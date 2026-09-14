using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record Edge(long A, long B, bool Foreign);
    sealed record Node(double Lon, double Lat, string Source);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly HashSet<string> TargetOwners =
        new(StringComparer.OrdinalIgnoreCase) { "FR", "BE", "LU" };

    // Numerical tolerance only: used to match coordinates generated from the same clipped topology.
    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;

        var cells = root.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();
        var byId = cells.ToDictionary(c => c.Id);
        var edges = root.GetProperty("edges").EnumerateArray()
            .Select(e => new Edge(
                e.GetProperty("a").GetInt64(),
                e.GetProperty("b").GetInt64(),
                e.TryGetProperty("foreign", out var foreign) && foreign.GetBoolean()))
            .Where(e => byId.ContainsKey(e.A) && byId.ContainsKey(e.B))
            .ToArray();

        var pairKeys = edges
            .Where(e => e.Foreign)
            .Select(e => PairKey(byId[e.A].OwnerCode, byId[e.B].OwnerCode))
            .Where(pair =>
            {
                var (a, b) = SplitPair(pair);
                return TargetOwners.Contains(a) && TargetOwners.Contains(b);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ownerRegions = TargetOwners.ToDictionary(
            owner => owner,
            owner => SafeUnion(cells
                .Where(c => c.OwnerCode.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Geometry)),
            StringComparer.OrdinalIgnoreCase);

        var pairs = new List<object>();

        foreach (var pairKey in pairKeys)
        {
            var (ownerA, ownerB) = SplitPair(pairKey);

            // The current real A-B border is the reference line. C4.5 does not derive a border
            // from foreign Voronoi edges. It only asks where INTERNAL Voronoi edges terminate on
            // this real border — exactly the junctions visible in the Territory Lab.
            var borderComponents = MergeLines(
                SharedLinework(ownerRegions[ownerA], ownerRegions[ownerB])
                    .Where(line => !line.IsEmpty));
            if (borderComponents.Count == 0)
                continue;

            var borderUnion = SafeUnion(borderComponents.Cast<Geometry>());
            var junctions = new List<Node>();
            var internalEdgeCount = 0;

            foreach (var edge in edges.Where(e => !e.Foreign))
            {
                var a = byId[edge.A];
                var b = byId[edge.B];
                if (!a.OwnerCode.Equals(b.OwnerCode, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!a.OwnerCode.Equals(ownerA, StringComparison.OrdinalIgnoreCase) &&
                    !a.OwnerCode.Equals(ownerB, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var line in SharedLinework(a.Geometry, b.Geometry))
                {
                    if (line.IsEmpty || line.NumPoints < 2)
                        continue;

                    internalEdgeCount++;
                    AddEndpointIfOnBorder(line.GetCoordinateN(0), a.OwnerCode, borderUnion, junctions);
                    AddEndpointIfOnBorder(line.GetCoordinateN(line.NumPoints - 1), a.OwnerCode, borderUnion, junctions);
                }
            }

            var componentPayload = new List<object>();
            var allC4Lines = new List<LineString>();
            var allNodes = new List<object>();
            var usedJunctionKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var baseline in borderComponents)
            {
                var componentNodes = junctions
                    .Where(n => Factory.CreatePoint(new Coordinate(n.Lon, n.Lat)).Distance(baseline) <= NodeTolerance)
                    .ToList();

                // The real border endpoints only close the chain. They are visually distinguished
                // from actual Voronoi/border junctions.
                var start = baseline.GetCoordinateN(0);
                var end = baseline.GetCoordinateN(baseline.NumPoints - 1);
                AddNode(componentNodes, new Node(start.X, start.Y, "anchor"));
                AddNode(componentNodes, new Node(end.X, end.Y, "anchor"));

                var ordered = componentNodes
                    .Select(n => new
                    {
                        Node = n,
                        Position = ProjectPosition(baseline, new Coordinate(n.Lon, n.Lat))
                    })
                    .OrderBy(x => x.Position)
                    .ToArray();

                if (ordered.Length < 2)
                    continue;

                var coordinates = ordered
                    .Select(x => new Coordinate(x.Node.Lon, x.Node.Lat))
                    .ToArray();
                var c4 = Factory.CreateLineString(coordinates);
                allC4Lines.Add(c4);

                foreach (var item in ordered)
                {
                    allNodes.Add(new
                    {
                        lon = item.Node.Lon,
                        lat = item.Node.Lat,
                        source = item.Node.Source,
                        position = item.Position
                    });
                    if (item.Node.Source != "anchor")
                        usedJunctionKeys.Add(NodeKey(item.Node));
                }

                componentPayload.Add(new
                {
                    baseline = LineToGeoJson(baseline),
                    c4 = LineToGeoJson(c4),
                    baselineVertexCount = baseline.NumPoints,
                    c4NodeCount = coordinates.Length,
                    junctionCount = ordered.Count(x => x.Node.Source != "anchor"),
                    maxDeviationDegrees = MaxDeviation(baseline, c4)
                });
            }

            Console.WriteLine(
                $"C4.5 {pairKey}: {usedJunctionKeys.Count} Voronoi/real-border junction(s), " +
                $"{borderComponents.Count} real-border component(s), {internalEdgeCount} internal edge part(s) inspected.");

            pairs.Add(new
            {
                pair = pairKey,
                owners = new[] { ownerA, ownerB },
                junctionCount = usedJunctionKeys.Count,
                realBorderComponentCount = borderComponents.Count,
                internalEdgePartCount = internalEdgeCount,
                components = componentPayload,
                nodes = allNodes,
                baseline = MultiLineToGeoJson(borderComponents),
                c4 = MultiLineToGeoJson(allC4Lines)
            });
        }

        var payload = new
        {
            status = "experimental",
            description = "C4.5 boundary-node chain experiment. Nodes are only junctions where an internal Voronoi edge of either adjacent owner terminates on the current real A-B border. Those junctions are ordered along the real border and connected by straight segments; real-border endpoints are anchors only. No territory surfaces are recomposed.",
            targetOwners = TargetOwners.OrderBy(x => x).ToArray(),
            pairCount = pairs.Count,
            pairs
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "c4-boundary-node-chain-lab.json"),
            JsonSerializer.Serialize(payload));
    }

    static void AddEndpointIfOnBorder(
        Coordinate coordinate,
        string owner,
        Geometry border,
        List<Node> nodes)
    {
        var point = Factory.CreatePoint(coordinate);
        if (point.Distance(border) > NodeTolerance)
            return;

        AddNode(nodes, new Node(coordinate.X, coordinate.Y, $"junction-{owner}"));
    }

    static List<LineString> MergeLines(IEnumerable<LineString> lines)
    {
        var values = lines.ToArray();
        if (values.Length == 0)
            return new List<LineString>();

        var merger = new LineMerger();
        merger.Add(values);
        return merger.GetMergedLineStrings().Cast<LineString>()
            .OrderByDescending(x => x.Length)
            .ToList();
    }

    static IEnumerable<LineString> SharedLinework(Geometry a, Geometry b)
    {
        var shared = SafeIntersection(a.Boundary, b.Boundary);
        return EnumerateLines(shared);
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
                foreach (var linePart in EnumerateLines(collection.GetGeometryN(i)))
                    yield return linePart;
        }
    }

    static void AddNode(List<Node> nodes, Node candidate)
    {
        var existing = nodes.FindIndex(n => Distance(n, candidate) <= NodeTolerance);
        if (existing < 0)
        {
            nodes.Add(candidate);
            return;
        }

        if (nodes[existing].Source == "anchor" && candidate.Source != "anchor")
            nodes[existing] = candidate;
    }

    static string NodeKey(Node node) => $"{Math.Round(node.Lon, 5):F5},{Math.Round(node.Lat, 5):F5}";

    static double Distance(Node a, Node b)
    {
        var dx = a.Lon - b.Lon;
        var dy = a.Lat - b.Lat;
        return Math.Sqrt(dx * dx + dy * dy);
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
            if (length2 <= 1e-18)
                continue;

            var t = Math.Clamp(((point.X - a.X) * vx + (point.Y - a.Y) * vy) / length2, 0.0, 1.0);
            var px = a.X + t * vx;
            var py = a.Y + t * vy;
            var dx = point.X - px;
            var dy = point.Y - py;
            var distance = dx * dx + dy * dy;
            var segmentLength = Math.Sqrt(length2);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPosition = cumulative + t * segmentLength;
            }
            cumulative += segmentLength;
        }
        return bestPosition;
    }

    static double MaxDeviation(LineString baseline, LineString c4)
    {
        var max = 0.0;
        foreach (var coordinate in baseline.Coordinates)
            max = Math.Max(max, Factory.CreatePoint(coordinate).Distance(c4));
        return max;
    }

    static Geometry SafeIntersection(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return Factory.CreateGeometryCollection();
        try
        {
            var result = a.Intersection(b);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            return OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection);
        }
    }

    static Geometry SafeUnion(IEnumerable<Geometry> geometries)
    {
        var values = geometries.Where(g => !g.IsEmpty).ToArray();
        if (values.Length == 0)
            return Factory.CreateGeometryCollection();
        if (values.Length == 1)
            return values[0];
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
        if (geometry.ValueKind == JsonValueKind.Null)
            return null;
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon")
            return ParsePolygon(coordinates);
        if (type == "MultiPolygon")
            return Factory.CreateMultiPolygon(coordinates.EnumerateArray().Select(ParsePolygon).ToArray());
        return null;
    }

    static Polygon ParsePolygon(JsonElement coordinates)
    {
        var rings = coordinates.EnumerateArray().Select(ParseRing).Where(x => x is not null).Cast<LinearRing>().ToArray();
        return rings.Length == 0 ? Factory.CreatePolygon() : Factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    static LinearRing? ParseRing(JsonElement ring)
    {
        var points = ring.EnumerateArray().Select(p =>
        {
            var xy = p.EnumerateArray().ToArray();
            return new Coordinate(xy[0].GetDouble(), xy[1].GetDouble());
        }).ToList();
        if (points.Count < 3)
            return null;
        if (!points[0].Equals2D(points[^1]))
            points.Add(new Coordinate(points[0]));
        return points.Count < 4 ? null : Factory.CreateLinearRing(points.ToArray());
    }

    static object LineToGeoJson(LineString line) => new
    {
        type = "LineString",
        coordinates = line.Coordinates.Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) }).ToArray()
    };

    static object MultiLineToGeoJson(IEnumerable<LineString> lines) => new
    {
        type = "MultiLineString",
        coordinates = lines.Select(line => line.Coordinates.Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) }).ToArray()).ToArray()
    };

    static string PairKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}-{b}" : $"{b}-{a}";

    static (string A, string B) SplitPair(string pair)
    {
        var separator = pair.IndexOf('-');
        return (pair[..separator], pair[(separator + 1)..]);
    }
}
