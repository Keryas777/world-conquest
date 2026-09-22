using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Distance;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record InternalEdge(string OwnerCode, long CellA, long CellB, LineString Line);
    sealed record Junction(string OwnerCode, long CellA, long CellB, Coordinate Point, LineString Edge);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var cells = document.RootElement.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();

        // C4.17 experimental prototype: derive one canonical BE-LU border from the two
        // independently clipped owner boundaries. The canonical line is diagnostic
        // only: gameplay/cell geometry is not modified.
        const string ownerA = "BE";
        const string ownerB = "LU";
        const string pairKey = "BE-LU";

        var cellsA = cells.Where(c => c.OwnerCode == ownerA).ToArray();
        var cellsB = cells.Where(c => c.OwnerCode == ownerB).ToArray();
        var regionA = SafeUnion(cellsA.Select(c => c.Geometry));
        var regionB = SafeUnion(cellsB.Select(c => c.Geometry));
        var borderComponents = MergeLines(EnumerateLines(SafeIntersection(regionA.Boundary, regionB.Boundary)));
        if (borderComponents.Count == 0) throw new InvalidOperationException("C4.17: no BE-LU border component found.");

        // The two owner boundaries are already sub-metre apart at the interesting
        // locations. Snap LU boundary vertices to the nearest BE boundary location
        // only when they are within 2 metres, then intersect the snapped LU boundary
        // with a 2 m buffer around BE. This tolerance reconciles source-coordinate
        // noise; it is not a Voronoi-junction acceptance threshold.
        const double canonicalToleranceKm = 0.002;
        var canonicalBorder = BuildCanonicalBorder(regionA.Boundary, regionB.Boundary, canonicalToleranceKm);
        var canonicalComponents = MergeLines(EnumerateLines(canonicalBorder));

        var internalEdges = BuildInternalEdges(cellsA).Concat(BuildInternalEdges(cellsB)).ToArray();
        var ownerRegions = new Dictionary<string, Geometry> { [ownerA] = regionA, [ownerB] = regionB };
        var crossOwnerEdges = BuildCrossOwnerEdges(cellsA, cellsB).ToArray();
        var endpointDiagnostics = internalEdges.SelectMany(edge =>
        {
            if (edge.Line.NumPoints == 0) return Array.Empty<object>();
            return new[] { edge.Line.GetCoordinateN(0), edge.Line.GetCoordinateN(edge.Line.NumPoints - 1) }
                .Select(endpoint =>
                {
                    var point = Factory.CreatePoint(new Coordinate(endpoint));
                    var nearest = crossOwnerEdges.Select(borderEdge =>
                    {
                        var pair = new DistanceOp(point, borderEdge).NearestPoints();
                        var borderPoint = pair.Length > 1 ? pair[1] : endpoint;
                        return new { borderEdge, borderPoint, distanceKm = ApproxKm(endpoint, borderPoint) };
                    }).OrderBy(x => x.distanceKm).First();
                    var canonicalPair = canonicalComponents.Select(line =>
                    {
                        var pair = new DistanceOp(point, line).NearestPoints();
                        var cp = pair.Length > 1 ? pair[1] : endpoint;
                        return new { point = cp, km = ApproxKm(endpoint, cp) };
                    }).OrderBy(x => x.km).FirstOrDefault();
                    var ownBoundary = ownerRegions[edge.OwnerCode].Boundary;
                    var oppositeCode = edge.OwnerCode == ownerA ? ownerB : ownerA;
                    var oppositeBoundary = ownerRegions[oppositeCode].Boundary;
                    var ownPair = new DistanceOp(point, ownBoundary).NearestPoints();
                    var oppositePair = new DistanceOp(point, oppositeBoundary).NearestPoints();
                    var ownPoint = ownPair.Length > 1 ? ownPair[1] : endpoint;
                    var oppositePoint = oppositePair.Length > 1 ? oppositePair[1] : endpoint;
                    return (object)new
                    {
                        owner = edge.OwnerCode,
                        oppositeOwner = oppositeCode,
                        cellA = edge.CellA,
                        cellB = edge.CellB,
                        endpoint = new[] { Math.Round(endpoint.X, 9), Math.Round(endpoint.Y, 9) },
                        nearestBorderPoint = new[] { Math.Round(nearest.borderPoint.X, 9), Math.Round(nearest.borderPoint.Y, 9) },
                        distanceKm = Math.Round(nearest.distanceKm, 6),
                        distanceMeters = Math.Round(nearest.distanceKm * 1000.0, 3),
                        ownBoundaryPoint = new[] { Math.Round(ownPoint.X, 9), Math.Round(ownPoint.Y, 9) },
                        ownBoundaryDistanceMeters = Math.Round(ApproxKm(endpoint, ownPoint) * 1000.0, 3),
                        oppositeBoundaryPoint = new[] { Math.Round(oppositePoint.X, 9), Math.Round(oppositePoint.Y, 9) },
                        oppositeBoundaryDistanceMeters = Math.Round(ApproxKm(endpoint, oppositePoint) * 1000.0, 3),
                        canonicalBorderPoint = canonicalPair is null ? null : new[] { Math.Round(canonicalPair.point.X, 9), Math.Round(canonicalPair.point.Y, 9) },
                        canonicalBorderDistanceMeters = canonicalPair is null ? (double?)null : Math.Round(canonicalPair.km * 1000.0, 3),
                        exactCover = nearest.borderEdge.Covers(point),
                        edge = LineToGeoJson(edge.Line),
                        borderEdge = LineToGeoJson(nearest.borderEdge)
                    };
                }).ToArray();
        }).Cast<dynamic>().OrderBy(x => (double)x.distanceKm).Take(12).ToArray();

        var junctions = Array.Empty<object>();
        // Border terminals are independent of Voronoi junctions. For this diagnostic,
        // take the two farthest endpoints among reconstructed BE-LU components.
        var terminalCandidates = borderComponents.SelectMany(l=>new[]{l.GetCoordinateN(0),l.GetCoordinateN(l.NumPoints-1)}).ToArray();
        var terminalPair = FarthestPair(terminalCandidates);
        var terminals = terminalPair.Select((p,i)=>new{id=i+1,point=new[]{Math.Round(p.X,6),Math.Round(p.Y,6)}}).ToArray();
        Console.WriteLine($"C4.17 {pairKey}: internal edges={internalEdges.Length}, cross-owner edges={crossOwnerEdges.Length}, exported nearest endpoint diagnostics={endpointDiagnostics.Length}, terminals={terminals.Length}.");

        var payload = new
        {
            status = "experimental",
            description = "C4.17 experimental diagnostic. Builds a canonical BE-LU border by reconciling sub-2m disagreement between independently clipped owner boundaries, then measures Voronoi endpoints against it. No gameplay/cell geometry modification.",
            targetOwners = new[] { ownerA, ownerB },
            pairCount = 1,
            pairs = new[]
            {
                new
                {
                    pair = pairKey,
                    owners = new[] { ownerA, ownerB },
                    internalEdgeCount = internalEdges.Length,
                    crossOwnerEdgeCount = crossOwnerEdges.Length,
                    canonicalBorderComponentCount = canonicalComponents.Count,
                    canonicalBorder = MultiLineToGeoJson(canonicalComponents),
                    diagnosticEndpointCount = endpointDiagnostics.Length,
                    endpointDiagnostics,
                    terminalCount = terminals.Length,
                    terminals,
                    realBorderComponentCount = borderComponents.Count,
                    junctions,
                    components = borderComponents.Select(line => new
                    {
                        baseline = LineToGeoJson(line),
                        baselineVertexCount = line.NumPoints
                    }).ToArray(),
                    baseline = MultiLineToGeoJson(borderComponents)
                }
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "c4-boundary-node-chain-lab.json"),
            JsonSerializer.Serialize(payload));
    }

    static Geometry BuildCanonicalBorder(Geometry aBoundary, Geometry bBoundary, double toleranceKm)
    {
        // Snap coordinates of B onto A where disagreement is only source precision.
        var editor = new NetTopologySuite.Geometries.Utilities.GeometryEditor(Factory);
        var snappedB = editor.Edit(bBoundary, new SnapToBoundaryOperation(aBoundary, toleranceKm));
        // Keep only the portion of the reconciled B boundary that is effectively on A.
        var toleranceDegrees = toleranceKm / 110.57;
        return SafeIntersection(snappedB, aBoundary.Buffer(toleranceDegrees));
    }

    sealed class SnapToBoundaryOperation : NetTopologySuite.Geometries.Utilities.GeometryEditor.CoordinateSequenceOperation
    {
        readonly Geometry _target; readonly double _toleranceKm;
        public SnapToBoundaryOperation(Geometry target, double toleranceKm) { _target=target; _toleranceKm=toleranceKm; }
        public override CoordinateSequence Edit(CoordinateSequence seq, Geometry geometry)
        {
            var copy = seq.Copy();
            for (var i=0;i<copy.Count;i++)
            {
                var c = copy.GetCoordinate(i);
                var p = Factory.CreatePoint(c);
                var pair = new DistanceOp(p,_target).NearestPoints();
                if (pair.Length>1 && ApproxKm(c,pair[1]) <= _toleranceKm)
                { copy.SetX(i,pair[1].X); copy.SetY(i,pair[1].Y); }
            }
            return copy;
        }
    }

    static IEnumerable<LineString> BuildCrossOwnerEdges(IReadOnlyList<Cell> aCells, IReadOnlyList<Cell> bCells)
    {
        foreach (var a in aCells) foreach (var b in bCells)
        {
            if (!a.Geometry.EnvelopeInternal.Intersects(b.Geometry.EnvelopeInternal)) continue;
            foreach (var line in EnumerateLines(SafeIntersection(a.Geometry.Boundary,b.Geometry.Boundary)))
                if (!line.IsEmpty && line.Length > NodeTolerance) yield return line;
        }
    }

    static Coordinate[] FarthestPair(IReadOnlyList<Coordinate> points)
    {
        if (points.Count < 2) return points.ToArray();
        var best = new[]{points[0],points[1]}; var bestKm=-1.0;
        for(var i=0;i<points.Count;i++) for(var j=i+1;j<points.Count;j++)
        { var km=ApproxKm(points[i],points[j]); if(km>bestKm){bestKm=km;best=new[]{points[i],points[j]};} }
        return best;
    }

    static IEnumerable<InternalEdge> BuildInternalEdges(IReadOnlyList<Cell> ownerCells)
    {
        for (var i = 0; i < ownerCells.Count; i++)
        {
            for (var j = i + 1; j < ownerCells.Count; j++)
            {
                var a = ownerCells[i];
                var b = ownerCells[j];
                if (!a.Geometry.EnvelopeInternal.Intersects(b.Geometry.EnvelopeInternal))
                    continue;

                var shared = SafeIntersection(a.Geometry.Boundary, b.Geometry.Boundary);
                foreach (var line in EnumerateLines(shared))
                {
                    if (!line.IsEmpty && line.Length > NodeTolerance)
                        yield return new InternalEdge(a.OwnerCode, a.Id, b.Id, line);
                }
            }
        }
    }

    static IEnumerable<Coordinate> EnumeratePointCoordinates(Geometry geometry)
    {
        if (geometry is Point point)
        {
            if (!point.IsEmpty) yield return point.Coordinate;
            yield break;
        }

        // Collinear overlap is not a single junction. Keep only its endpoints so it
        // remains visible diagnostically without manufacturing intermediate nodes.
        if (geometry is LineString line)
        {
            if (!line.IsEmpty && line.NumPoints > 0)
            {
                yield return line.GetCoordinateN(0);
                if (line.NumPoints > 1)
                    yield return line.GetCoordinateN(line.NumPoints - 1);
            }
            yield break;
        }

        if (geometry is GeometryCollection collection)
        {
            for (var i = 0; i < collection.NumGeometries; i++)
                foreach (var coordinate in EnumeratePointCoordinates(collection.GetGeometryN(i)))
                    yield return coordinate;
        }
    }

    static double ApproxKm(Coordinate a, Coordinate b)
    {
        var meanLatRadians = ((a.Y + b.Y) * 0.5) * Math.PI / 180.0;
        var dx = (a.X - b.X) * 111.32 * Math.Cos(meanLatRadians);
        var dy = (a.Y - b.Y) * 110.57;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static List<LineString> MergeLines(IEnumerable<LineString> lines)
    {
        var values = lines.Where(l => !l.IsEmpty).ToArray();
        if (values.Length == 0)
            return new List<LineString>();
        var merger = new LineMerger();
        merger.Add(values);
        return merger.GetMergedLineStrings().Cast<LineString>().OrderByDescending(x => x.Length).ToList();
    }

    static IEnumerable<LineString> EnumerateLines(Geometry geometry)
    {
        if (geometry is LineString line)
        {
            yield return line;
            yield break;
        }
        if (geometry is GeometryCollection collection)
        {
            for (var i = 0; i < collection.NumGeometries; i++)
                foreach (var part in EnumerateLines(collection.GetGeometryN(i)))
                    yield return part;
        }
    }

    static Geometry SafeIntersection(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty) return Factory.CreateGeometryCollection();
        try { return a.Intersection(b); }
        catch { return OverlayNGRobust.Overlay(a, b, NetTopologySuite.Operation.Overlay.SpatialFunction.Intersection); }
    }

    static Geometry SafeUnion(IEnumerable<Geometry> geometries)
    {
        var values = geometries.Where(g => !g.IsEmpty).ToArray();
        if (values.Length == 0) return Factory.CreateGeometryCollection();
        if (values.Length == 1) return values[0];
        return OverlayNGRobust.Union(values);
    }

    static Cell ParseCell(JsonElement element)
    {
        var geometry = ParseGeoJsonGeometry(element.GetProperty("geometry")) ?? Factory.CreatePolygon();
        return new Cell(
            element.GetProperty("id").GetInt64(),
            element.GetProperty("ownerCode").GetString() ?? "?",
            geometry);
    }

    static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (geometry.ValueKind == JsonValueKind.Null) return null;
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon") return ParsePolygon(coordinates);
        if (type == "MultiPolygon")
            return Factory.CreateMultiPolygon(coordinates.EnumerateArray().Select(ParsePolygon).ToArray());
        return null;
    }

    static Polygon ParsePolygon(JsonElement coordinates)
    {
        var rings = coordinates.EnumerateArray()
            .Select(ParseRing)
            .Where(x => x is not null)
            .Cast<LinearRing>()
            .ToArray();
        return rings.Length == 0
            ? Factory.CreatePolygon()
            : Factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    static LinearRing? ParseRing(JsonElement ring)
    {
        var points = ring.EnumerateArray().Select(p =>
        {
            var xy = p.EnumerateArray().ToArray();
            return new Coordinate(xy[0].GetDouble(), xy[1].GetDouble());
        }).ToList();
        if (points.Count < 3) return null;
        if (!points[0].Equals2D(points[^1])) points.Add(new Coordinate(points[0]));
        return points.Count < 4 ? null : Factory.CreateLinearRing(points.ToArray());
    }

    static object LineToGeoJson(LineString line) => new
    {
        type = "LineString",
        coordinates = line.Coordinates
            .Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) })
            .ToArray()
    };

    static object MultiLineToGeoJson(IEnumerable<LineString> lines) => new
    {
        type = "MultiLineString",
        coordinates = lines.Select(line => line.Coordinates
            .Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) })
            .ToArray()).ToArray()
    };
}
