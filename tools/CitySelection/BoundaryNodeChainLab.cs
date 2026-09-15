using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Distance;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record InternalEdge(string OwnerCode, long CellA, long CellB, LineString Line);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var cells = document.RootElement.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();

        // C4.9 is diagnostic only. We inspect the exact same-owner Voronoi edges
        // around BE-LU and measure how close their endpoints really are to the
        // reconstructed BE-LU border. No replacement frontier is produced.
        const string ownerA = "BE";
        const string ownerB = "LU";
        const string pairKey = "BE-LU";

        var cellsA = cells.Where(c => c.OwnerCode == ownerA).ToArray();
        var cellsB = cells.Where(c => c.OwnerCode == ownerB).ToArray();
        var regionA = SafeUnion(cellsA.Select(c => c.Geometry));
        var regionB = SafeUnion(cellsB.Select(c => c.Geometry));
        var borderComponents = MergeLines(EnumerateLines(SafeIntersection(regionA.Boundary, regionB.Boundary)));
        if (borderComponents.Count == 0)
            throw new InvalidOperationException("C4.9: no BE-LU border component found.");

        var borderUnion = SafeUnion(borderComponents.Cast<Geometry>());
        var internalEdges = BuildInternalEdges(cellsA)
            .Concat(BuildInternalEdges(cellsB))
            .ToArray();

        var candidates = new List<object>();
        var ranked = new List<(double Km, InternalEdge Edge, Coordinate Endpoint, Coordinate BorderPoint)>();

        foreach (var edge in internalEdges)
        {
            if (edge.Line.NumPoints == 0)
                continue;

            var endpoints = new[]
            {
                edge.Line.GetCoordinateN(0),
                edge.Line.GetCoordinateN(edge.Line.NumPoints - 1)
            };

            foreach (var endpoint in endpoints)
            {
                var point = Factory.CreatePoint(new Coordinate(endpoint));
                var nearest = new DistanceOp(point, borderUnion).NearestPoints();
                if (nearest.Length < 2)
                    continue;

                var borderPoint = nearest[1];
                var km = ApproxKm(endpoint, borderPoint);
                ranked.Add((km, edge, new Coordinate(endpoint), new Coordinate(borderPoint)));
            }
        }

        var top = ranked
            .OrderBy(x => x.Km)
            .ThenBy(x => x.Edge.OwnerCode, StringComparer.Ordinal)
            .ThenBy(x => x.Edge.CellA)
            .ThenBy(x => x.Edge.CellB)
            .Take(24)
            .ToArray();

        for (var i = 0; i < top.Length; i++)
        {
            var x = top[i];
            candidates.Add(new
            {
                rank = i + 1,
                owner = x.Edge.OwnerCode,
                cellA = x.Edge.CellA,
                cellB = x.Edge.CellB,
                distanceKm = Math.Round(x.Km, 4),
                endpoint = new[] { Math.Round(x.Endpoint.X, 6), Math.Round(x.Endpoint.Y, 6) },
                nearestBorder = new[] { Math.Round(x.BorderPoint.X, 6), Math.Round(x.BorderPoint.Y, 6) },
                edge = LineToGeoJson(x.Edge.Line)
            });
        }

        Console.WriteLine($"C4.9 {pairKey}: inspected {internalEdges.Length} internal edge(s), {ranked.Count} endpoint(s).");
        foreach (var x in top.Take(10))
            Console.WriteLine($"  {x.Edge.OwnerCode} cells {x.Edge.CellA}/{x.Edge.CellB}: endpoint-border distance={x.Km:F4} km at {x.Endpoint.X:F6},{x.Endpoint.Y:F6}");

        var payload = new
        {
            status = "experimental",
            description = "C4.9 diagnostic only. Same-owner Voronoi edge endpoints near the BE-LU border are ranked by true endpoint-to-border distance so the visually expected middle junction can be identified from the stored geometry. No replacement frontier or territory surface is produced.",
            targetOwners = new[] { ownerA, ownerB },
            pairCount = 1,
            pairs = new[]
            {
                new
                {
                    pair = pairKey,
                    owners = new[] { ownerA, ownerB },
                    internalEdgeCount = internalEdges.Length,
                    endpointCount = ranked.Count,
                    realBorderComponentCount = borderComponents.Count,
                    candidates,
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
