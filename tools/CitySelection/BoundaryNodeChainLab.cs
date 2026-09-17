using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record InternalEdge(string OwnerCode, long CellA, long CellB, LineString Line);
    sealed record Hit(string OwnerCode, long CellA, long CellB, Coordinate Point, LineString Edge);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var cells = document.RootElement.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();

        // C4.11 is diagnostic only. Instead of projecting nearby Voronoi endpoints
        // onto the BE-LU border, intersect every same-owner internal Voronoi edge
        // with the real reconstructed BE-LU border. Only exact geometric contacts
        // are retained; no distance threshold and no replacement frontier are used.
        const string ownerA = "BE";
        const string ownerB = "LU";
        const string pairKey = "BE-LU";

        var cellsA = cells.Where(c => c.OwnerCode == ownerA).ToArray();
        var cellsB = cells.Where(c => c.OwnerCode == ownerB).ToArray();
        var regionA = SafeUnion(cellsA.Select(c => c.Geometry));
        var regionB = SafeUnion(cellsB.Select(c => c.Geometry));
        var borderComponents = MergeLines(EnumerateLines(SafeIntersection(regionA.Boundary, regionB.Boundary)));
        if (borderComponents.Count == 0)
            throw new InvalidOperationException("C4.11: no BE-LU border component found.");

        var borderUnion = SafeUnion(borderComponents.Cast<Geometry>());
        var internalEdges = BuildInternalEdges(cellsA)
            .Concat(BuildInternalEdges(cellsB))
            .ToArray();

        var rawHits = new List<Hit>();
        foreach (var edge in internalEdges)
        {
            var intersection = SafeIntersection(edge.Line, borderUnion);
            foreach (var point in EnumeratePointCoordinates(intersection))
                rawHits.Add(new Hit(edge.OwnerCode, edge.CellA, edge.CellB, new Coordinate(point), edge.Line));
        }

        // A Voronoi vertex can be represented by several incident internal edges.
        // Collapse coincident contacts so the diagnostic counts physical junctions,
        // not edge incidences.
        var groups = new List<List<Hit>>();
        foreach (var hit in rawHits.OrderBy(h => h.Point.X).ThenBy(h => h.Point.Y))
        {
            var group = groups.FirstOrDefault(g => ApproxKm(g[0].Point, hit.Point) <= 0.01);
            if (group is null)
                groups.Add(new List<Hit> { hit });
            else
                group.Add(hit);
        }

        var junctions = groups.Select((group, index) =>
        {
            var x = group.Average(h => h.Point.X);
            var y = group.Average(h => h.Point.Y);
            return new
            {
                id = index + 1,
                point = new[] { Math.Round(x, 6), Math.Round(y, 6) },
                incidenceCount = group.Count,
                owners = group.Select(h => h.OwnerCode).Distinct().OrderBy(x => x).ToArray(),
                edges = group.Select(h => new
                {
                    owner = h.OwnerCode,
                    cellA = h.CellA,
                    cellB = h.CellB,
                    geometry = LineToGeoJson(h.Edge)
                }).ToArray()
            };
        }).ToArray();

        Console.WriteLine($"C4.11 {pairKey}: inspected {internalEdges.Length} internal edge(s), exact incidences={rawHits.Count}, unique junctions={junctions.Length}.");

        var payload = new
        {
            status = "experimental",
            description = "C4.11 diagnostic only. Exact geometric intersections between same-owner internal Voronoi edges (BE or LU) and the reconstructed BE-LU border. No projection, distance threshold, replacement frontier or territory surface modification.",
            targetOwners = new[] { ownerA, ownerB },
            pairCount = 1,
            pairs = new[]
            {
                new
                {
                    pair = pairKey,
                    owners = new[] { ownerA, ownerB },
                    internalEdgeCount = internalEdges.Length,
                    exactIncidenceCount = rawHits.Count,
                    uniqueJunctionCount = junctions.Length,
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
