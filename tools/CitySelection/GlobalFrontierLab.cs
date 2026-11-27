using System.Diagnostics;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Distance;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Union;

static partial class GlobalFrontierLab
{
    private sealed record Cell(long Id, string Territory, string Owner, Geometry Geometry,
        string Name, string Country, double Latitude, double Longitude, long Population);
    private sealed record Edge(long A, long B, bool Foreign);
    private sealed record BorderPair(string Key, string A, string B, int EdgeCount, LineString[] Pieces,
        LineString[] MergedBorderLines, Coordinate[] SourcePieceEndpoints, bool LineMergeFallback);
    private sealed record InternalEdge(string Territory, long CellA, long CellB, LineString Line, double LengthKm);
    private sealed record Junction(long CellA, long CellB, string Territory, Coordinate Endpoint,
        Coordinate BorderPoint, double DistanceMeters, double AngleDegrees, double EdgeLengthKm, string Classification);
    private sealed record Point2(double X, double Y);

    private static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private static readonly (string Key, string Label)[] Methods =
    {
        ("ref40", "Référence 40 · Douglas–Peucker · 0,040° (C4.28)"),
        ("ref85", "Référence 85 · Douglas–Peucker · 0,085° (C4.28)"),
        ("protected", "Mode « Angles protégés » · DP 10,5 km, restauration à 6,3 km (C4.27)"),
        ("adaptive", "Adaptatif · DP 6,5 km puis 3,8 km (C4.26)")
    };

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        if (!File.Exists(graphPath))
            throw new FileNotFoundException("Global frontier screening requires voronoi-graph.json.", graphPath);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;
        var cells = root.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();
        var byId = cells.ToDictionary(c => c.Id);
        var edges = root.GetProperty("edges").EnumerateArray().Select(e => new Edge(
            e.GetProperty("a").GetInt64(), e.GetProperty("b").GetInt64(),
            e.TryGetProperty("foreign", out var f) && f.GetBoolean())).ToArray();

        var groups = new Dictionary<string, (string A, string B, int Count, int SharedLineCount, int MissingLineCount, int FailedCount, List<Geometry> Pieces)>(StringComparer.Ordinal);
        foreach (var edge in edges.Where(e => e.Foreign))
        {
            if (!byId.TryGetValue(edge.A, out var a) || !byId.TryGetValue(edge.B, out var b)) continue;
            if (a.Owner == b.Owner || a.Territory == b.Territory) continue;
            var (codeA, codeB) = StringComparer.Ordinal.Compare(a.Territory, b.Territory) <= 0
                ? (a.Territory, b.Territory) : (b.Territory, a.Territory);
            var key = codeA + "-" + codeB;
            if (!groups.TryGetValue(key, out var group))
                group = (codeA, codeB, 0, 0, 0, 0, new List<Geometry>());
            group.Count++;

            try
            {
                var shared = a.Geometry.Boundary.Intersection(b.Geometry.Boundary);
                var found = false;
                foreach (var line in EnumerateLines(shared))
                    if (!line.IsEmpty && line.Length > 1e-10)
                    {
                        foreach (var piece in SplitAtAntimeridian(line)) group.Pieces.Add(piece);
                        group.SharedLineCount++;
                        found = true;
                    }
                if (!found) group.MissingLineCount++;
            }
            catch
            {
                group.FailedCount++;
            }
            groups[key] = group;
        }

        var internalEdgesByTerritory = BuildInternalEdges(byId, edges)
            .GroupBy(edge => edge.Territory, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var pairs = groups.OrderBy(x => x.Key, StringComparer.Ordinal).Select(item =>
        {
            var merger = new LineMerger();
            var pieces = item.Value.Pieces.OfType<LineString>()
                .Where(line => !line.IsEmpty && line.NumPoints >= 2)
                .ToArray();
            foreach (var piece in pieces) merger.Add(piece);
            var mergedLines = merger.GetMergedLineStrings().Cast<LineString>()
                .SelectMany(SplitAtAntimeridian)
                .Where(line => !line.IsEmpty && line.NumPoints >= 2)
                .OrderBy(line => line.EnvelopeInternal.MinX)
                .ThenBy(line => line.EnvelopeInternal.MinY)
                .ThenByDescending(line => line.Length)
                .ToArray();
            var lineMergeFallback = mergedLines.Length == 0 && pieces.Length > 0;
            var lines = lineMergeFallback ? pieces : mergedLines;
            var endpointMap = new Dictionary<string, Coordinate>(StringComparer.Ordinal);
            foreach (var piece in pieces)
            foreach (var endpoint in new[] { piece.Coordinates[0], piece.Coordinates[^1] })
            {
                if (Math.Abs(Math.Abs(endpoint.X) - 180.0) < 1e-10) continue;
                endpointMap.TryAdd($"{endpoint.X:R},{endpoint.Y:R}", new Coordinate(endpoint));
            }
            var sourcePieceEndpoints = endpointMap.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray();
            return new BorderPair(item.Key, item.Value.A, item.Value.B, item.Value.Count, pieces, lines, sourcePieceEndpoints, lineMergeFallback);
        }).ToArray();

        var stopwatch = Stopwatch.StartNew();
        var outputPairs = new List<object>(pairs.Length);
        var summary = new Dictionary<string, object>(StringComparer.Ordinal);
        var methodLines = new Dictionary<string, List<LineString[]>>(StringComparer.Ordinal);
        var methodSourceEndpointDisplacement = new Dictionary<string, List<SourceEndpointDisplacement>>(StringComparer.Ordinal);

        foreach (var method in Methods)
        {
            var methodWatch = Stopwatch.StartNew();
            var resultByPair = new List<LineString[]>(pairs.Length);
            long inputVertices = 0, outputVertices = 0;
            long sourcePieceEndpointCount = 0, sourcePieceEndpointsWithinOneMeter = 0, sourcePieceEndpointsWithinHundredMeters = 0;
            var maximumSourcePieceEndpointDisplacementMeters = 0.0;
            var lengthsBefore = new List<double>();
            var lengthsAfter = new List<double>();
            var maxDeviation = 0.0;
            var deviationTotal = 0.0;
            long deviationSamples = 0;
            var invalidLines = 0;
            var displacementByPair = new List<SourceEndpointDisplacement>(pairs.Length);
            foreach (var pair in pairs)
            {
                var simplified = pair.MergedBorderLines.Select(line => Simplify(line, method.Key)).ToArray();
                resultByPair.Add(simplified);
                foreach (var line in pair.MergedBorderLines)
                {
                    inputVertices += line.NumPoints;
                    lengthsBefore.Add(LengthKm(line.Coordinates));
                }
                foreach (var line in simplified)
                {
                    outputVertices += line.NumPoints;
                    lengthsAfter.Add(LengthKm(line.Coordinates));
                    if (!line.IsSimple || !line.IsValid) invalidLines++;
                }

                for (var i = 0; i < pair.MergedBorderLines.Length; i++)
                {
                    var stats = Deviation(pair.MergedBorderLines[i], simplified[i]);
                    maxDeviation = Math.Max(maxDeviation, stats.MaxMeters);
                    deviationTotal += stats.SumMeters;
                    deviationSamples += stats.Samples;
                }

                var displacement = MeasureSourceEndpointDisplacement(pair.SourcePieceEndpoints, simplified);
                displacementByPair.Add(displacement);
                sourcePieceEndpointCount += displacement.EndpointCount;
                sourcePieceEndpointsWithinOneMeter += displacement.WithinOneMeter;
                sourcePieceEndpointsWithinHundredMeters += displacement.WithinHundredMeters;
                maximumSourcePieceEndpointDisplacementMeters = Math.Max(maximumSourcePieceEndpointDisplacementMeters, displacement.MaximumDisplacementMeters ?? 0);
            }
            methodWatch.Stop();
            methodLines[method.Key] = resultByPair;
            methodSourceEndpointDisplacement[method.Key] = displacementByPair;
            summary[method.Key] = new
            {
                label = method.Label,
                pairCount = pairs.Length,
                inputVertices,
                outputVertices,
                verticesRemoved = inputVertices - outputVertices,
                vertexReductionRatio = inputVertices == 0 ? 0 : (double)(inputVertices - outputVertices) / inputVertices,
                totalLengthBeforeKm = lengthsBefore.Sum(),
                totalLengthAfterKm = lengthsAfter.Sum(),
                maxDeviationMeters = maxDeviation,
                meanSourceVertexDeviationMeters = deviationSamples == 0 ? 0 : deviationTotal / deviationSamples,
                sourcePieceEndpointCount,
                sourcePieceEndpointsWithinOneMeter,
                sourcePieceEndpointRetentionWithinOneMeter = sourcePieceEndpointCount == 0 ? 1 : (double)sourcePieceEndpointsWithinOneMeter / sourcePieceEndpointCount,
                sourcePieceEndpointsWithinHundredMeters,
                maximumSourcePieceEndpointDisplacementMeters,
                invalidOrSelfIntersectingLineCount = invalidLines,
                elapsedMilliseconds = methodWatch.ElapsedMilliseconds
            };
        }

        for (var pairIndex = 0; pairIndex < pairs.Length; pairIndex++)
        {
            var pair = pairs[pairIndex];
            var variants = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var method in Methods)
            {
                var lines = methodLines[method.Key][pairIndex];
                var displacement = methodSourceEndpointDisplacement[method.Key][pairIndex];
                variants[method.Key] = new
                {
                    geometry = ToMultiLineGeoJson(lines),
                    vertexCount = lines.Sum(line => line.NumPoints),
                    lengthKm = lines.Sum(line => LengthKm(line.Coordinates)),
                    maxDeviationMeters = pair.MergedBorderLines.Select((line, index) => Deviation(line, lines[index]).MaxMeters).DefaultIfEmpty(0).Max(),
                    nonSimpleLineComponentCount = lines.Count(line => !line.IsSimple || !line.IsValid),
                    sourcePieceEndpointCount = displacement.EndpointCount,
                    sourcePieceEndpointsWithinOneMeter = displacement.WithinOneMeter,
                    sourcePieceEndpointsWithinHundredMeters = displacement.WithinHundredMeters,
                    maximumSourcePieceEndpointDisplacementMeters = displacement.MaximumDisplacementMeters,
                    identicalToOriginal = GeometryCoordinatesEqual(pair.MergedBorderLines, lines)
                };
            }

            var junctions = DetectJunctions(pair, internalEdgesByTerritory);

            outputPairs.Add(new
            {
                key = pair.Key,
                territories = new[] { pair.A, pair.B },
                crossOwnerVoronoiEdgeCount = pair.EdgeCount,
                sharedLineIntersectionCount = groups[pair.Key].SharedLineCount,
                missingSharedLineIntersectionCount = groups[pair.Key].MissingLineCount,
                failedSharedLineIntersectionCount = groups[pair.Key].FailedCount,
                sourceLineMergeFallback = pair.LineMergeFallback,
                sourceLineComponentCount = pair.MergedBorderLines.Length,
                junctionDetection = new
                {
                    endpointDistanceToleranceMeters = 500,
                    transverseAngleMinimumDegrees = 20,
                    shortEdgeLengthThresholdKm = .5,
                    nearbyPairDistanceMeters = 5000,
                    thresholdsStatus = "experimental screen; not selected for the game"
                },
                junctionSummary = new
                {
                    nearBorderEndpoints = junctions.NearEndpoints.Length,
                    transverse = junctions.NearEndpoints.Count(x => x.Classification == "transverse"),
                    boundaryParallel = junctions.NearEndpoints.Count(x => x.Classification == "boundary-parallel"),
                    shortTransverse = junctions.NearEndpoints.Count(x => x.Classification == "short-transverse"),
                    nearbyDistinctIncidencePairs = junctions.NearbyPairs.Length
                },
                junctions = junctions.NearEndpoints.Select(SerializeJunction).ToArray(),
                nearbyFusionCandidates = junctions.NearbyPairs,
                source = new
                {
                    geometry = ToMultiLineGeoJson(pair.MergedBorderLines),
                    vertexCount = pair.MergedBorderLines.Sum(line => line.NumPoints),
                    lengthKm = pair.MergedBorderLines.Sum(line => LengthKm(line.Coordinates)),
                    sourcePieceEndpointCount = pair.SourcePieceEndpoints.Length
                },
                identicalMethodPairs = IdenticalMethodPairs(pairIndex, methodLines),
                variants
            });
        }

        stopwatch.Stop();
        var payload = new
        {
            status = "screening_only",
            source = new
            {
                graphPath = "docs/voronoi-graph.json",
                cellCount = cells.Length,
                territoryCount = cells.Select(c => c.Territory).Distinct(StringComparer.Ordinal).Count(),
                ownerCount = cells.Select(c => c.Owner).Distinct(StringComparer.Ordinal).Count(),
                graphEdgeCount = edges.Length,
                foreignEdgeCount = edges.Count(e => e.Foreign),
                territoryPairCount = pairs.Length
            },
            methods = summary,
            comparability = new
            {
                identicalByPair = Methods.Select(m => m.Key).ToDictionary(
                    key => key,
                    key => pairs.Select((_, i) => methodLines[key][i])
                        .Zip(pairs.Select((_, i) => methodLines[Methods[0].Key][i]))
                        .Count(x => GeometryCoordinatesEqual(x.First, x.Second))),
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
            },
            limitations = new[]
            {
                "This campaign simplifies real shared Voronoi-border linework, but does not rebuild territory polygons or cells.",
                "Area gained/lost, shared-border area reconciliation, cell validity after reconstruction, coverage, gaps, overlaps and lost cell adjacencies are therefore not measured.",
                "Reference 40/85 retain the historical C4.28 degree-coordinate Douglas–Peucker operation; their distances vary with latitude.",
                "Junction fusion is not applied: proximity alone cannot prove that the incident Voronoi connections are redundant.",
                "Source piece endpoint displacement is a linework diagnostic; these endpoints are not all proven topological junctions, and the test does not preserve cell adjacency."
            },
            pairs = outputPairs
        };

        var outputPath = Path.Combine(outDir, "global-frontier-results.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"Global frontier screening: cells={cells.Length}, territories={payload.source.territoryCount}, foreign edges={payload.source.foreignEdgeCount}, border pairs={pairs.Length}.");
        Console.WriteLine($"Screening result: {outputPath} ({new FileInfo(outputPath).Length:N0} bytes), {stopwatch.Elapsed.TotalSeconds:F1}s.");

        await GenerateReconstructionAsync(outDir, cells, edges);
    }

    private sealed record SourceEndpointDisplacement(int EndpointCount, int WithinOneMeter,
        int WithinHundredMeters, double? MaximumDisplacementMeters);

    private static SourceEndpointDisplacement MeasureSourceEndpointDisplacement(IReadOnlyList<Coordinate> endpoints,
        IReadOnlyList<LineString> simplifiedLines)
    {
        if (endpoints.Count == 0) return new SourceEndpointDisplacement(0, 0, 0, 0);
        if (simplifiedLines.Count == 0) return new SourceEndpointDisplacement(endpoints.Count, 0, 0, null);
        var oneMeter = 0;
        var hundredMeters = 0;
        var maximum = 0.0;
        foreach (var endpoint in endpoints)
        {
            var nearest = NearestBorderPoint(endpoint, simplifiedLines);
            if (nearest is null) return new SourceEndpointDisplacement(endpoints.Count, 0, 0, null);
            var distance = HaversineKm(endpoint, nearest.Value.Point) * 1000.0;
            if (distance <= 1.0) oneMeter++;
            if (distance <= 100.0) hundredMeters++;
            maximum = Math.Max(maximum, distance);
        }
        return new SourceEndpointDisplacement(endpoints.Count, oneMeter, hundredMeters, maximum);
    }

    private static InternalEdge[] BuildInternalEdges(IReadOnlyDictionary<long, Cell> byId, Edge[] edges)
    {
        var output = new List<InternalEdge>();
        foreach (var edge in edges.Where(e => !e.Foreign))
        {
            if (!byId.TryGetValue(edge.A, out var a) || !byId.TryGetValue(edge.B, out var b) ||
                a.Territory != b.Territory || a.Owner != b.Owner) continue;
            try
            {
                foreach (var line in EnumerateLines(a.Geometry.Boundary.Intersection(b.Geometry.Boundary)))
                {
                    if (line.IsEmpty || line.NumPoints < 2 || line.Length <= 1e-10) continue;
                    var cellA = Math.Min(edge.A, edge.B);
                    var cellB = Math.Max(edge.A, edge.B);
                    output.Add(new InternalEdge(a.Territory, cellA, cellB, line, LengthKm(line.Coordinates)));
                }
            }
            catch
            {
                // A missing internal edge is counted indirectly by the original graph's adjacency audit.
            }
        }
        return output.ToArray();
    }

    private static (Junction[] NearEndpoints, object[] NearbyPairs) DetectJunctions(
        BorderPair pair,
        IReadOnlyDictionary<string, InternalEdge[]> internalEdgesByTerritory)
    {
        if (pair.MergedBorderLines.Length == 0) return (Array.Empty<Junction>(), Array.Empty<object>());
        var borderEnvelope = new Envelope();
        var maximumLatitude = 0.0;
        foreach (var line in pair.MergedBorderLines)
        {
            borderEnvelope.ExpandToInclude(line.EnvelopeInternal);
            maximumLatitude = Math.Max(maximumLatitude, line.Coordinates.Select(c => Math.Abs(c.Y)).DefaultIfEmpty(0).Max());
        }
        var longitudePad = Math.Min(180.0, .005 / Math.Max(.01, Math.Cos(maximumLatitude * Math.PI / 180.0)));
        borderEnvelope.ExpandBy(longitudePad, .005);
        var candidates = new List<Junction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var territory in new[] { pair.A, pair.B })
        {
            if (!internalEdgesByTerritory.TryGetValue(territory, out var internalEdges)) continue;
            foreach (var edge in internalEdges)
            {
                if (!borderEnvelope.Intersects(edge.Line.EnvelopeInternal)) continue;
                var coordinates = edge.Line.Coordinates;
                foreach (var endpointIndex in new[] { 0, coordinates.Length - 1 })
                {
                    var endpoint = coordinates[endpointIndex];
                    var nearest = NearestBorderPoint(endpoint, pair.MergedBorderLines);
                    if (nearest is null) continue;
                    var distanceMeters = HaversineKm(endpoint, nearest.Value.Point) * 1000.0;
                    if (distanceMeters > 500.0) continue;
                    var edgeTangent = EndpointTangent(edge.Line, endpointIndex == 0);
                    var angle = AngleBetween(edgeTangent, nearest.Value.Tangent);
                    var classification = angle < 20.0 ? "boundary-parallel" : edge.LengthKm < .5 ? "short-transverse" : "transverse";
                    var id = $"{edge.CellA}:{edge.CellB}:{Math.Round(nearest.Value.Point.X, 5):F5}:{Math.Round(nearest.Value.Point.Y, 5):F5}";
                    if (!seen.Add(id)) continue;
                    candidates.Add(new Junction(edge.CellA, edge.CellB, territory, new Coordinate(endpoint),
                        nearest.Value.Point, distanceMeters, angle, edge.LengthKm, classification));
                }
            }
        }

        var nearby = new List<object>();
        for (var i = 0; i < candidates.Count; i++)
        for (var j = i + 1; j < candidates.Count; j++)
        {
            var left = candidates[i];
            var right = candidates[j];
            if (left.CellA == right.CellA && left.CellB == right.CellB) continue;
            var distanceMeters = HaversineKm(left.BorderPoint, right.BorderPoint) * 1000.0;
            if (distanceMeters > 5000.0) continue;
            var sharesCell = left.CellA == right.CellA || left.CellA == right.CellB || left.CellB == right.CellA || left.CellB == right.CellB;
            nearby.Add(new
            {
                a = new[] { left.BorderPoint.X, left.BorderPoint.Y },
                b = new[] { right.BorderPoint.X, right.BorderPoint.Y },
                distanceMeters,
                cellsA = new[] { left.CellA, left.CellB },
                cellsB = new[] { right.CellA, right.CellB },
                territoryA = left.Territory,
                territoryB = right.Territory,
                oppositeTerritorySides = left.Territory != right.Territory,
                incidentCellOverlap = sharesCell,
                fusionCandidate = left.Territory != right.Territory,
                fusionApplied = false
            });
        }
        return (candidates.ToArray(), nearby.ToArray());
    }

    private static object SerializeJunction(Junction junction) => new
    {
        cellA = junction.CellA,
        cellB = junction.CellB,
        territory = junction.Territory,
        endpoint = new[] { junction.Endpoint.X, junction.Endpoint.Y },
        borderPoint = new[] { junction.BorderPoint.X, junction.BorderPoint.Y },
        junction.DistanceMeters,
        junction.AngleDegrees,
        junction.EdgeLengthKm,
        junction.Classification
    };

    private static (Coordinate Point, Point2 Tangent)? NearestBorderPoint(Coordinate point, IReadOnlyList<LineString> lines)
    {
        var latitude = point.Y * Math.PI / 180.0;
        Point2 Project(Coordinate c) => new(NormalizeLongitudeDelta(c.X - point.X) * 111320.0 * Math.Cos(latitude), (c.Y - point.Y) * 111320.0);
        var p = Project(point);
        var best = double.PositiveInfinity;
        Coordinate? closest = null;
        Point2? tangent = null;
        foreach (var line in lines)
        {
            var coordinates = line.Coordinates;
            for (var i = 1; i < coordinates.Length; i++)
            {
                var a = Project(coordinates[i - 1]);
                var b = Project(coordinates[i]);
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var length2 = dx * dx + dy * dy;
                if (length2 <= 1e-12) continue;
                var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0.0, 1.0);
                var projected = new Point2(a.X + t * dx, a.Y + t * dy);
                var distance = Math.Sqrt(Square(p.X - projected.X) + Square(p.Y - projected.Y));
                if (distance >= best) continue;
                best = distance;
                closest = new Coordinate(
                    coordinates[i - 1].X + (coordinates[i].X - coordinates[i - 1].X) * t,
                    coordinates[i - 1].Y + (coordinates[i].Y - coordinates[i - 1].Y) * t);
                tangent = new Point2(dx, dy);
            }
        }
        if (closest is null || tangent is null) return null;
        return (closest, tangent);
    }

    private static Point2 EndpointTangent(LineString line, bool atStart)
    {
        var coordinates = line.Coordinates;
        var latitude = coordinates.Average(c => c.Y) * Math.PI / 180.0;
        var a = atStart ? coordinates[0] : coordinates[^2];
        var b = atStart ? coordinates[1] : coordinates[^1];
        return new Point2(NormalizeLongitudeDelta(b.X - a.X) * 111320.0 * Math.Cos(latitude), (b.Y - a.Y) * 111320.0);
    }

    private static double AngleBetween(Point2 a, Point2 b)
    {
        var na = Math.Sqrt(Square(a.X) + Square(a.Y));
        var nb = Math.Sqrt(Square(b.X) + Square(b.Y));
        if (na <= 1e-12 || nb <= 1e-12) return 0;
        var cosine = Math.Clamp(Math.Abs((a.X * b.X + a.Y * b.Y) / (na * nb)), 0.0, 1.0);
        return Math.Acos(cosine) * 180.0 / Math.PI;
    }

    public static void RunSelfTests()
    {
        var pointOnly = Factory.CreatePoint(new Coordinate(0, 0));
        if (EnumerateLines(pointOnly).Any()) throw new InvalidOperationException("Point-only geometry must not recurse as linework.");

        var shortWiggle = Factory.CreateLineString(new[]
        {
            new Coordinate(0, 0), new Coordinate(.01, .0001), new Coordinate(.02, 0),
            new Coordinate(.03, .0001), new Coordinate(.04, 0)
        });
        foreach (var method in Methods)
        {
            var first = Simplify(shortWiggle, method.Key);
            var second = Simplify(shortWiggle, method.Key);
            if (first.NumPoints < 2 || first.Coordinates[0].Distance(shortWiggle.Coordinates[0]) > 1e-12 ||
                first.Coordinates[^1].Distance(shortWiggle.Coordinates[^1]) > 1e-12 ||
                !GeometryCoordinatesEqual(new[] { first }, new[] { second }))
                throw new InvalidOperationException($"Global frontier self-test failed: {method.Key} endpoint or determinism invariant.");
        }

        var discriminating = Factory.CreateLineString(new[]
        {
            new Coordinate(0, 0), new Coordinate(1, .05), new Coordinate(2, 0)
        });
        if (Simplify(discriminating, "ref40").NumPoints != 3 || Simplify(discriminating, "ref85").NumPoints != 2 ||
            Simplify(discriminating, "protected").NumPoints != 2 || Simplify(discriminating, "adaptive").NumPoints != 3)
            throw new InvalidOperationException("Global frontier self-test failed: the four method parameters are not distinguishable.");
        if (HaversineKm(new Coordinate(179.9, 0), new Coordinate(-179.9, 0)) > 23.0)
            throw new InvalidOperationException("Global frontier self-test failed: antimeridian length was not normalized.");
        var seamParts = SplitAtAntimeridian(Factory.CreateLineString(new[]
        {
            new Coordinate(179.9, 0), new Coordinate(-179.9, 0)
        })).ToArray();
        if (seamParts.Length != 2 || seamParts[0].Coordinates[^1].X != 180.0 ||
            seamParts[1].Coordinates[0].X != -180.0)
            throw new InvalidOperationException("Global frontier self-test failed: antimeridian linework was not split consistently.");
        var seamBorder = Factory.CreateLineString(new[] { new Coordinate(179.8, 0), new Coordinate(180, 0) });
        var seamNearest = NearestBorderPoint(new Coordinate(-179.9, 0), new[] { seamBorder });
        if (seamNearest is null || HaversineKm(new Coordinate(-179.9, 0), seamNearest.Value.Point) > 12.0)
            throw new InvalidOperationException("Global frontier self-test failed: antimeridian junction lookup used the long way around.");

        RunReconstructionSelfTests();

        Console.WriteLine("Global frontier self-tests passed: point-only geometry, endpoints, determinism, parameter separation and antimeridian handling.");
    }

    private static LineString Simplify(LineString line, string method)
    {
        var coordinates = line.Coordinates;
        if (coordinates.Length < 3) return (LineString)line.Copy();
        var latitude = coordinates.Average(c => c.Y) * Math.PI / 180.0;
        Point2 Metric(Coordinate c) => new(c.X * 111320.0 * Math.Cos(latitude), c.Y * 111320.0);

        return Factory.CreateLineString(method switch
        {
            "ref40" => DouglasPeucker(coordinates, .040, c => new Point2(c.X, c.Y)),
            "ref85" => DouglasPeucker(coordinates, .085, c => new Point2(c.X, c.Y)),
            "protected" => ProtectCorners(coordinates, Metric),
            "adaptive" => Adaptive(coordinates, Metric),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown global frontier method.")
        });
    }

    private static Coordinate[] DouglasPeucker(Coordinate[] points, double tolerance, Func<Coordinate, Point2> project)
    {
        return DouglasPeuckerIndices(points, tolerance, project).Select(i => new Coordinate(points[i])).ToArray();
    }

    private static int[] DouglasPeuckerIndices(Coordinate[] points, double tolerance, Func<Coordinate, Point2> project)
    {
        if (points.Length < 3) return Enumerable.Range(0, points.Length).ToArray();
        var retained = new bool[points.Length];
        retained[0] = true;
        retained[^1] = true;
        var pending = new Stack<(int Start, int End)>();
        pending.Push((0, points.Length - 1));
        while (pending.Count > 0)
        {
            var (start, end) = pending.Pop();
            var best = 0.0;
            var split = -1;
            var a = project(points[start]);
            var b = project(points[end]);
            for (var i = start + 1; i < end; i++)
            {
                var distance = PointSegmentDistance(project(points[i]), a, b);
                if (distance > best) { best = distance; split = i; }
            }
            if (best <= tolerance || split < 0) continue;
            retained[split] = true;
            pending.Push((split, end));
            pending.Push((start, split));
        }
        return Enumerable.Range(0, points.Length).Where(i => retained[i]).ToArray();
    }

    private static Coordinate[] ProtectCorners(Coordinate[] points, Func<Coordinate, Point2> project)
    {
        const double tolerance = 10500.0;
        const double restoreAt = 6300.0;
        var anchors = DouglasPeuckerIndices(points, tolerance, project);
        var output = new List<Coordinate> { new(points[anchors[0]]) };
        for (var i = 0; i < anchors.Length - 1; i++)
        {
            var from = anchors[i];
            var to = anchors[i + 1];
            var max = 0.0;
            var index = -1;
            if (to > from)
            {
                var a = project(points[from]);
                var b = project(points[to]);
                for (var j = from + 1; j < to; j++)
                {
                    var d = PointSegmentDistance(project(points[j]), a, b);
                    if (d > max) { max = d; index = j; }
                }
            }
            if (index >= 0 && max >= restoreAt) output.Add(new Coordinate(points[index]));
            output.Add(new Coordinate(points[to]));
        }
        return output.ToArray();
    }

    private static Coordinate[] Adaptive(Coordinate[] points, Func<Coordinate, Point2> project)
    {
        var anchors = DouglasPeuckerIndices(points, 6500.0, project);
        var output = new List<Coordinate>();
        for (var i = 0; i < anchors.Length - 1; i++)
        {
            var from = anchors[i];
            var to = anchors[i + 1];
            var local = DouglasPeuckerIndices(points[from..(to + 1)], 3800.0, project);
            output.AddRange(local[..^1].Select(index => new Coordinate(points[from + index])));
        }
        output.Add(new Coordinate(points[^1]));
        return output.ToArray();
    }

    private static (double MaxMeters, double SumMeters, long Samples) Deviation(LineString source, LineString simplified)
    {
        if (source.IsEmpty || simplified.IsEmpty) return (0, 0, 0);
        var latitude = source.Coordinates.Average(c => c.Y) * Math.PI / 180.0;
        Point2 Project(Coordinate c) => new(c.X * 111320.0 * Math.Cos(latitude), c.Y * 111320.0);
        var target = simplified.Coordinates.Select(Project).ToArray();
        var max = 0.0;
        var sum = 0.0;
        foreach (var coordinate in source.Coordinates)
        {
            var p = Project(coordinate);
            var nearest = Enumerable.Range(1, Math.Max(0, target.Length - 1))
                .Select(i => PointSegmentDistance(p, target[i - 1], target[i]))
                .DefaultIfEmpty(0).Min();
            max = Math.Max(max, nearest);
            sum += nearest;
        }
        return (max, sum, source.NumPoints);
    }

    private static double PointSegmentDistance(Point2 p, Point2 a, Point2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length2 = dx * dx + dy * dy;
        if (length2 == 0) return Math.Sqrt(Square(p.X - a.X) + Square(p.Y - a.Y));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0.0, 1.0);
        return Math.Sqrt(Square(p.X - a.X - t * dx) + Square(p.Y - a.Y - t * dy));
    }

    private static double Square(double value) => value * value;

    private static double LengthKm(IReadOnlyList<Coordinate> coordinates)
    {
        var length = 0.0;
        for (var i = 1; i < coordinates.Count; i++) length += HaversineKm(coordinates[i - 1], coordinates[i]);
        return length;
    }

    private static double HaversineKm(Coordinate a, Coordinate b)
    {
        const double radius = 6371.0088;
        static double Rad(double degrees) => degrees * Math.PI / 180.0;
        var lat1 = Rad(a.Y);
        var lat2 = Rad(b.Y);
        var dLat = lat2 - lat1;
        var dLon = Rad(NormalizeLongitudeDelta(b.X - a.X));
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double NormalizeLongitudeDelta(double delta) => ((delta + 540.0) % 360.0) - 180.0;

    private static bool GeometryCoordinatesEqual(IReadOnlyList<LineString> a, IReadOnlyList<LineString> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].NumPoints != b[i].NumPoints) return false;
            for (var j = 0; j < a[i].NumPoints; j++)
                if (!a[i].GetCoordinateN(j).Equals2D(b[i].GetCoordinateN(j))) return false;
        }
        return true;
    }

    private static string[] IdenticalMethodPairs(int pairIndex, IReadOnlyDictionary<string, List<LineString[]>> methods)
    {
        var identical = new List<string>();
        for (var i = 0; i < Methods.Length; i++)
        for (var j = i + 1; j < Methods.Length; j++)
            if (GeometryCoordinatesEqual(methods[Methods[i].Key][pairIndex], methods[Methods[j].Key][pairIndex]))
                identical.Add(Methods[i].Key + "=" + Methods[j].Key);
        return identical.ToArray();
    }

    private static object ToMultiLineGeoJson(IEnumerable<LineString> lines) => new
    {
        type = "MultiLineString",
        coordinates = lines.Select(line => line.Coordinates.Select(c => new[] { c.X, c.Y }).ToArray()).ToArray()
    };

    private static IEnumerable<LineString> EnumerateLines(Geometry geometry)
    {
        var pending = new Stack<Geometry>();
        pending.Push(geometry);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.IsEmpty) continue;
            if (current is LineString line)
            {
                yield return line;
                continue;
            }
            if (current is not GeometryCollection collection) continue;
            for (var i = collection.NumGeometries - 1; i >= 0; i--)
                pending.Push(collection.GetGeometryN(i));
        }
    }

    private static IEnumerable<LineString> SplitAtAntimeridian(LineString line)
    {
        var coordinates = line.Coordinates;
        if (coordinates.Length < 2) yield break;
        var current = new List<Coordinate> { new(coordinates[0]) };
        for (var i = 1; i < coordinates.Length; i++)
        {
            var previous = coordinates[i - 1];
            var next = coordinates[i];
            var rawDelta = next.X - previous.X;
            if (Math.Abs(rawDelta) <= 180.0)
            {
                current.Add(new Coordinate(next));
                continue;
            }

            var wrappedDelta = NormalizeLongitudeDelta(rawDelta);
            if (Math.Abs(wrappedDelta) < 1e-12)
            {
                // +180 and -180 are the same meridian; keep the segment on one side.
                current.Add(new Coordinate(previous.X, next.Y));
                continue;
            }

            var unwrappedEnd = previous.X + wrappedDelta;
            var seam = wrappedDelta > 0 ? 180.0 : -180.0;
            var fraction = Math.Clamp((seam - previous.X) / (unwrappedEnd - previous.X), 0.0, 1.0);
            var seamLatitude = previous.Y + (next.Y - previous.Y) * fraction;
            current.Add(new Coordinate(seam, seamLatitude));
            if (current.Count >= 2) yield return Factory.CreateLineString(current.ToArray());
            var otherSeam = seam > 0 ? -180.0 : 180.0;
            current = new List<Coordinate> { new(otherSeam, seamLatitude), new(next) };
        }
        if (current.Count >= 2) yield return Factory.CreateLineString(current.ToArray());
    }

    private static Cell ParseCell(JsonElement element)
    {
        var geometry = ParseGeoJsonGeometry(element.GetProperty("geometry")) ?? Factory.CreatePolygon();
        return new Cell(
            element.GetProperty("id").GetInt64(),
            element.GetProperty("territoryCode").GetString() ?? "",
            element.GetProperty("ownerCode").GetString() ?? "",
            geometry,
            element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            element.TryGetProperty("country", out var country) ? country.GetString() ?? "" : "",
            element.TryGetProperty("lat", out var lat) ? lat.GetDouble() : 0,
            element.TryGetProperty("lon", out var lon) ? lon.GetDouble() : 0,
            element.TryGetProperty("population", out var population) ? population.GetInt64() : 0);
    }

    private static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (geometry.ValueKind != JsonValueKind.Object || !geometry.TryGetProperty("type", out var typeElement) ||
            !geometry.TryGetProperty("coordinates", out var coordinates)) return null;
        var type = typeElement.GetString();
        return type switch
        {
            "Polygon" => ParsePolygon(coordinates),
            "MultiPolygon" => Factory.CreateMultiPolygon(coordinates.EnumerateArray().Select(ParsePolygon).ToArray()),
            _ => null
        };
    }

    private static Polygon ParsePolygon(JsonElement polygon)
    {
        var rings = polygon.EnumerateArray().Select(ParseRing).ToArray();
        if (rings.Length == 0) return Factory.CreatePolygon();
        return Factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    private static LinearRing ParseRing(JsonElement ring) => Factory.CreateLinearRing(
        ring.EnumerateArray().Select(point => new Coordinate(point[0].GetDouble(), point[1].GetDouble())).ToArray());
}

