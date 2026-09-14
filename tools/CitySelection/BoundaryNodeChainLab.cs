using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record Node(double Lon, double Lat, string Source, string Detail);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var cells = document.RootElement.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();

        // C4.8: identify topological Voronoi vertices directly from the cell polygons.
        // A useful border junction is a single coordinate shared by at least three cells,
        // with both BE and LU represented (for example BE+BE+LU or BE+LU+LU).
        // This is the topological event "same-owner internal Voronoi edge reaches the
        // international border" without relying on fragile line-line intersections.
        const string ownerA = "BE";
        const string ownerB = "LU";
        const string pairKey = "BE-LU";

        var pairCells = cells.Where(c => c.OwnerCode == ownerA || c.OwnerCode == ownerB).ToArray();
        var regionA = SafeUnion(pairCells.Where(c => c.OwnerCode == ownerA).Select(c => c.Geometry));
        var regionB = SafeUnion(pairCells.Where(c => c.OwnerCode == ownerB).Select(c => c.Geometry));
        var borderComponents = MergeLines(EnumerateLines(SafeIntersection(regionA.Boundary, regionB.Boundary)));
        if (borderComponents.Count == 0)
            throw new InvalidOperationException("C4.8: no BE-LU border component found.");

        var borderUnion = SafeUnion(borderComponents.Cast<Geometry>());
        var buckets = new Dictionary<string, VertexBucket>(StringComparer.Ordinal);

        foreach (var cell in pairCells)
        {
            foreach (var coordinate in EnumerateVertices(cell.Geometry))
            {
                var key = NodeKey(coordinate.X, coordinate.Y);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new VertexBucket(coordinate.X, coordinate.Y);
                    buckets.Add(key, bucket);
                }
                bucket.CellOwners[cell.Id] = cell.OwnerCode;
            }
        }

        var nodes = new List<Node>();
        var mixedCandidates = 0;
        foreach (var bucket in buckets.Values)
        {
            var owners = bucket.CellOwners.Values.ToArray();
            if (owners.Length < 3)
                continue;

            var beCount = owners.Count(x => x == ownerA);
            var luCount = owners.Count(x => x == ownerB);
            if (beCount == 0 || luCount == 0)
                continue;
            if (Math.Max(beCount, luCount) < 2)
                continue;

            mixedCandidates++;
            var point = Factory.CreatePoint(new Coordinate(bucket.Lon, bucket.Lat));
            if (point.Distance(borderUnion) > NodeTolerance)
                continue;

            AddNode(nodes, new Node(
                bucket.Lon,
                bucket.Lat,
                "junction",
                $"{ownerA}:{beCount} {ownerB}:{luCount}"));
        }

        var (anchorA, anchorB) = FindOuterAnchors(borderComponents);
        AddOrPromoteAnchor(nodes, anchorA);
        AddOrPromoteAnchor(nodes, anchorB);

        var ordered = nodes
            .OrderBy(n => AxisPosition(anchorA, anchorB, new Coordinate(n.Lon, n.Lat)))
            .ToArray();

        var chain = Factory.CreateLineString(ordered.Select(n => new Coordinate(n.Lon, n.Lat)).ToArray());
        var junctionCount = ordered.Count(n => n.Source == "junction");

        Console.WriteLine(
            $"C4.8 {pairKey}: {junctionCount} mixed Voronoi vertex/vertices + 2 anchor(s), " +
            $"{mixedCandidates} mixed topological candidate(s), {borderComponents.Count} real-border component(s).");

        var payload = new
        {
            status = "experimental",
            description = "C4.8 BE-LU experiment. Useful junctions are topological Voronoi vertices shared by at least three cells with both BE and LU represented (BE+BE+LU or BE+LU+LU), plus the two outer real-border endpoints. No territory surface is modified.",
            targetOwners = new[] { ownerA, ownerB },
            pairCount = 1,
            pairs = new[]
            {
                new
                {
                    pair = pairKey,
                    owners = new[] { ownerA, ownerB },
                    junctionCount,
                    anchorCount = 2,
                    nodeCount = ordered.Length,
                    mixedCandidateCount = mixedCandidates,
                    realBorderComponentCount = borderComponents.Count,
                    components = borderComponents.Select(line => new
                    {
                        baseline = LineToGeoJson(line),
                        baselineVertexCount = line.NumPoints
                    }).ToArray(),
                    nodes = ordered.Select(n => new
                    {
                        lon = n.Lon,
                        lat = n.Lat,
                        source = n.Source,
                        detail = n.Detail
                    }).ToArray(),
                    baseline = MultiLineToGeoJson(borderComponents),
                    c4 = LineToGeoJson(chain)
                }
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "c4-boundary-node-chain-lab.json"),
            JsonSerializer.Serialize(payload));
    }

    sealed class VertexBucket
    {
        public VertexBucket(double lon, double lat)
        {
            Lon = lon;
            Lat = lat;
        }

        public double Lon { get; }
        public double Lat { get; }
        public Dictionary<long, string> CellOwners { get; } = new();
    }

    static IEnumerable<Coordinate> EnumerateVertices(Geometry geometry)
    {
        if (geometry.IsEmpty)
            yield break;

        if (geometry is Polygon polygon)
        {
            foreach (var c in EnumerateRingVertices(polygon.ExteriorRing))
                yield return c;
            for (var i = 0; i < polygon.NumInteriorRings; i++)
                foreach (var c in EnumerateRingVertices(polygon.GetInteriorRingN(i)))
                    yield return c;
            yield break;
        }

        if (geometry is GeometryCollection collection)
        {
            for (var i = 0; i < collection.NumGeometries; i++)
                foreach (var c in EnumerateVertices(collection.GetGeometryN(i)))
                    yield return c;
        }
    }

    static IEnumerable<Coordinate> EnumerateRingVertices(LineString ring)
    {
        var coordinates = ring.Coordinates;
        var count = coordinates.Length;
        if (count > 1 && coordinates[0].Equals2D(coordinates[^1]))
            count--;
        for (var i = 0; i < count; i++)
            yield return coordinates[i];
    }

    static (Coordinate A, Coordinate B) FindOuterAnchors(IReadOnlyList<LineString> borderComponents)
    {
        var endpoints = borderComponents
            .SelectMany(line => new[]
            {
                line.GetCoordinateN(0),
                line.GetCoordinateN(line.NumPoints - 1)
            })
            .ToArray();

        if (endpoints.Length < 2)
            throw new InvalidOperationException("C4.8: border has fewer than two endpoints.");

        var bestA = endpoints[0];
        var bestB = endpoints[1];
        var bestDistance = -1.0;
        for (var i = 0; i < endpoints.Length; i++)
        for (var j = i + 1; j < endpoints.Length; j++)
        {
            var distance = Distance(endpoints[i].X, endpoints[i].Y, endpoints[j].X, endpoints[j].Y);
            if (distance <= bestDistance) continue;
            bestDistance = distance;
            bestA = endpoints[i];
            bestB = endpoints[j];
        }

        return (bestA, bestB);
    }

    static void AddOrPromoteAnchor(List<Node> nodes, Coordinate anchor)
    {
        var index = nodes.FindIndex(n => Distance(n.Lon, n.Lat, anchor.X, anchor.Y) <= NodeTolerance);
        if (index >= 0)
        {
            nodes[index] = new Node(anchor.X, anchor.Y, "anchor", "real-border endpoint");
            return;
        }
        nodes.Add(new Node(anchor.X, anchor.Y, "anchor", "real-border endpoint"));
    }

    static void AddNode(List<Node> nodes, Node candidate)
    {
        if (nodes.Any(n => Distance(n.Lon, n.Lat, candidate.Lon, candidate.Lat) <= NodeTolerance))
            return;
        nodes.Add(candidate);
    }

    static string NodeKey(double lon, double lat) =>
        $"{Math.Round(lon, 5):F5},{Math.Round(lat, 5):F5}";

    static double AxisPosition(Coordinate start, Coordinate end, Coordinate point)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length2 = dx * dx + dy * dy;
        if (length2 <= 1e-18) return 0;
        return ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / length2;
    }

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
