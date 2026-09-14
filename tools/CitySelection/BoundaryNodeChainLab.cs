using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record Node(double Lon, double Lat, string Source, long CellId);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;
        var cells = root.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();

        // C4.6 is intentionally a diagnostic on one border only.
        // Goal: verify the exact junctions Jérôme marked in Territory Lab before drawing anything.
        const string ownerA = "FR";
        const string ownerB = "LU";
        var pairKey = "FR-LU";

        var ownerARegion = SafeUnion(cells.Where(c => c.OwnerCode == ownerA).Select(c => c.Geometry));
        var ownerBRegion = SafeUnion(cells.Where(c => c.OwnerCode == ownerB).Select(c => c.Geometry));
        var borderComponents = MergeLines(SharedLinework(ownerARegion, ownerBRegion));
        if (borderComponents.Count == 0)
            throw new InvalidOperationException("C4.6: no FR-LU border component found.");

        var borderUnion = SafeUnion(borderComponents.Cast<Geometry>());
        var junctions = new List<Node>();
        var inspectedCells = 0;

        foreach (var cell in cells.Where(c => c.OwnerCode == ownerA || c.OwnerCode == ownerB))
        {
            var hit = SafeIntersection(cell.Geometry.Boundary, borderUnion);
            if (hit.IsEmpty)
                continue;

            inspectedCells++;
            foreach (var coordinate in EnumerateBorderContactEndpoints(hit))
                AddNode(junctions, new Node(coordinate.X, coordinate.Y, cell.OwnerCode, cell.Id));
        }

        var components = new List<object>();
        var payloadNodes = new List<object>();
        var trueJunctionCount = 0;

        foreach (var baseline in borderComponents)
        {
            var start = baseline.GetCoordinateN(0);
            var end = baseline.GetCoordinateN(baseline.NumPoints - 1);
            var componentNodes = junctions
                .Where(n => Factory.CreatePoint(new Coordinate(n.Lon, n.Lat)).Distance(baseline) <= NodeTolerance)
                .ToList();

            foreach (var n in componentNodes)
            {
                var isAnchor = Distance(n.Lon, n.Lat, start.X, start.Y) <= NodeTolerance ||
                               Distance(n.Lon, n.Lat, end.X, end.Y) <= NodeTolerance;
                if (!isAnchor)
                    trueJunctionCount++;
                payloadNodes.Add(new
                {
                    lon = n.Lon,
                    lat = n.Lat,
                    source = isAnchor ? "anchor" : "junction",
                    owner = n.Source,
                    cellId = n.CellId,
                    position = ProjectPosition(baseline, new Coordinate(n.Lon, n.Lat))
                });
            }

            components.Add(new
            {
                baseline = LineToGeoJson(baseline),
                baselineVertexCount = baseline.NumPoints
            });
        }

        // Count unique non-anchor coordinates after cell-side deduplication.
        trueJunctionCount = payloadNodes
            .Cast<dynamic>()
            .Count();
        var uniqueJunctions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in junctions)
        {
            var p = new Coordinate(n.Lon, n.Lat);
            var isAnchor = borderComponents.Any(b =>
                Distance(p.X, p.Y, b.GetCoordinateN(0).X, b.GetCoordinateN(0).Y) <= NodeTolerance ||
                Distance(p.X, p.Y, b.GetCoordinateN(b.NumPoints - 1).X, b.GetCoordinateN(b.NumPoints - 1).Y) <= NodeTolerance);
            if (!isAnchor)
                uniqueJunctions.Add(NodeKey(n.Lon, n.Lat));
        }

        Console.WriteLine($"C4.6 {pairKey}: {uniqueJunctions.Count} cell-border junction(s), {inspectedCells} border-touching cell(s), {borderComponents.Count} border component(s).");

        var payload = new
        {
            status = "experimental",
            description = "C4.6 diagnostic only. Junctions are extracted directly from each FR/LU Voronoi cell boundary where it touches the current real FR-LU border. No replacement frontier is drawn and no territory surface is modified.",
            targetOwners = new[] { ownerA, ownerB },
            pairCount = 1,
            pairs = new[]
            {
                new
                {
                    pair = pairKey,
                    owners = new[] { ownerA, ownerB },
                    junctionCount = uniqueJunctions.Count,
                    realBorderComponentCount = borderComponents.Count,
                    inspectedCellCount = inspectedCells,
                    components,
                    nodes = payloadNodes,
                    baseline = MultiLineToGeoJson(borderComponents)
                }
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "c4-boundary-node-chain-lab.json"),
            JsonSerializer.Serialize(payload));
    }

    static IEnumerable<Coordinate> EnumerateBorderContactEndpoints(Geometry geometry)
    {
        if (geometry.IsEmpty)
            yield break;
        if (geometry is Point point)
        {
            yield return point.Coordinate;
            yield break;
        }
        if (geometry is LineString line)
        {
            if (line.NumPoints > 0)
                yield return line.GetCoordinateN(0);
            if (line.NumPoints > 1)
                yield return line.GetCoordinateN(line.NumPoints - 1);
            yield break;
        }
        if (geometry is GeometryCollection collection)
        {
            for (var i = 0; i < collection.NumGeometries; i++)
                foreach (var c in EnumerateBorderContactEndpoints(collection.GetGeometryN(i)))
                    yield return c;
        }
    }

    static void AddNode(List<Node> nodes, Node candidate)
    {
        if (nodes.Any(n => Distance(n.Lon, n.Lat, candidate.Lon, candidate.Lat) <= NodeTolerance))
            return;
        nodes.Add(candidate);
    }

    static string NodeKey(double lon, double lat) => $"{Math.Round(lon, 5):F5},{Math.Round(lat, 5):F5}";
    static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
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

    static IEnumerable<LineString> SharedLinework(Geometry a, Geometry b) => EnumerateLines(SafeIntersection(a.Boundary, b.Boundary));

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

    static double ProjectPosition(LineString line, Coordinate point)
    {
        var coords = line.Coordinates;
        var bestDistance = double.PositiveInfinity;
        var bestPosition = 0.0;
        var cumulative = 0.0;
        for (var i = 0; i < coords.Length - 1; i++)
        {
            var a = coords[i];
            var b = coords[i + 1];
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var length2 = vx * vx + vy * vy;
            if (length2 <= 1e-18) continue;
            var t = Math.Clamp(((point.X - a.X) * vx + (point.Y - a.Y) * vy) / length2, 0.0, 1.0);
            var px = a.X + t * vx;
            var py = a.Y + t * vy;
            var dx = point.X - px;
            var dy = point.Y - py;
            var d = dx * dx + dy * dy;
            var seg = Math.Sqrt(length2);
            if (d < bestDistance) { bestDistance = d; bestPosition = cumulative + t * seg; }
            cumulative += seg;
        }
        return bestPosition;
    }

    static Geometry SafeIntersection(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty) return Factory.CreateGeometryCollection();
        try { return a.Intersection(b); }
        catch { return OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection); }
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
        return new Cell(element.GetProperty("id").GetInt64(), element.GetProperty("ownerCode").GetString() ?? "?", geometry);
    }

    static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (geometry.ValueKind == JsonValueKind.Null) return null;
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon") return ParsePolygon(coordinates);
        if (type == "MultiPolygon") return Factory.CreateMultiPolygon(coordinates.EnumerateArray().Select(ParsePolygon).ToArray());
        return null;
    }

    static Polygon ParsePolygon(JsonElement coordinates)
    {
        var rings = coordinates.EnumerateArray().Select(ParseRing).Where(x => x is not null).Cast<LinearRing>().ToArray();
        return rings.Length == 0 ? Factory.CreatePolygon() : Factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    static LinearRing? ParseRing(JsonElement ring)
    {
        var points = ring.EnumerateArray().Select(p => { var xy = p.EnumerateArray().ToArray(); return new Coordinate(xy[0].GetDouble(), xy[1].GetDouble()); }).ToList();
        if (points.Count < 3) return null;
        if (!points[0].Equals2D(points[^1])) points.Add(new Coordinate(points[0]));
        return points.Count < 4 ? null : Factory.CreateLinearRing(points.ToArray());
    }

    static object LineToGeoJson(LineString line) => new { type = "LineString", coordinates = line.Coordinates.Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) }).ToArray() };
    static object MultiLineToGeoJson(IEnumerable<LineString> lines) => new { type = "MultiLineString", coordinates = lines.Select(line => line.Coordinates.Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) }).ToArray()).ToArray() };
}
