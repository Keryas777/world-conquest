using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

static class BoundaryNodeChainLab
{
    sealed record Cell(long Id, string OwnerCode, Geometry Geometry);
    sealed record Node(double Lon, double Lat, string Source, string OwnerCode);

    static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    const double NodeTolerance = 1e-5;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var cells = document.RootElement.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();

        // C4.7: test Jérôme's intended rule on the problematic BE-LU border.
        // Nodes = the two real border endpoints + intersections where an INTERNAL
        // Voronoi edge (between two cells of the same owner) reaches that border.
        const string ownerA = "BE";
        const string ownerB = "LU";
        const string pairKey = "BE-LU";

        var cellsA = cells.Where(c => c.OwnerCode == ownerA).ToArray();
        var cellsB = cells.Where(c => c.OwnerCode == ownerB).ToArray();
        var regionA = SafeUnion(cellsA.Select(c => c.Geometry));
        var regionB = SafeUnion(cellsB.Select(c => c.Geometry));

        var borderComponents = MergeLines(SharedLinework(regionA, regionB));
        if (borderComponents.Count == 0)
            throw new InvalidOperationException("C4.7: no BE-LU border component found.");

        var borderUnion = SafeUnion(borderComponents.Cast<Geometry>());
        var internalEdges = BuildInternalEdges(cellsA)
            .Concat(BuildInternalEdges(cellsB))
            .ToArray();

        var nodes = new List<Node>();
        foreach (var edge in internalEdges)
        {
            var hit = SafeIntersection(edge.Line, borderUnion);
            foreach (var coordinate in EnumerateIntersectionPoints(hit))
                AddNode(nodes, new Node(coordinate.X, coordinate.Y, "junction", edge.OwnerCode));
        }

        var (anchorA, anchorB) = FindOuterAnchors(borderComponents);
        AddOrPromoteAnchor(nodes, anchorA);
        AddOrPromoteAnchor(nodes, anchorB);

        // Order only for this visual experiment: project on the axis joining the
        // two true endpoints. This does not modify territory geometry.
        var ordered = nodes
            .OrderBy(n => AxisPosition(anchorA, anchorB, new Coordinate(n.Lon, n.Lat)))
            .ToArray();

        var chain = Factory.CreateLineString(ordered.Select(n => new Coordinate(n.Lon, n.Lat)).ToArray());
        var junctionCount = ordered.Count(n => n.Source == "junction");

        Console.WriteLine(
            $"C4.7 {pairKey}: {junctionCount} internal-edge junction(s) + 2 anchor(s), " +
            $"{internalEdges.Length} internal Voronoi edge(s), {borderComponents.Count} real-border component(s).");

        var payload = new
        {
            status = "experimental",
            description = "C4.7 BE-LU experiment. Nodes are only the two outer real-border endpoints plus intersections where same-owner internal Voronoi edges meet the BE-LU border. No territory surface is modified.",
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
                    internalEdgeCount = internalEdges.Length,
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
                        owner = n.OwnerCode
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

    sealed record InternalEdge(string OwnerCode, LineString Line);

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
                    if (!line.IsEmpty && line.Length > NodeTolerance)
                        yield return new InternalEdge(a.OwnerCode, line);
            }
        }
    }

    static IEnumerable<Coordinate> EnumerateIntersectionPoints(Geometry geometry)
    {
        if (geometry.IsEmpty)
            yield break;

        if (geometry is Point point)
        {
            yield return point.Coordinate;
            yield break;
        }

        // Defensive fallback for the unlikely case of a tiny overlap caused by
        // numerical precision: keep only the overlap endpoints as candidate junctions.
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
                foreach (var coordinate in EnumerateIntersectionPoints(collection.GetGeometryN(i)))
                    yield return coordinate;
        }
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
            throw new InvalidOperationException("C4.7: border has fewer than two endpoints.");

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
            nodes[index] = new Node(anchor.X, anchor.Y, "anchor", nodes[index].OwnerCode);
            return;
        }
        nodes.Add(new Node(anchor.X, anchor.Y, "anchor", "real-border"));
    }

    static void AddNode(List<Node> nodes, Node candidate)
    {
        if (nodes.Any(n => Distance(n.Lon, n.Lat, candidate.Lon, candidate.Lat) <= NodeTolerance))
            return;
        nodes.Add(candidate);
    }

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

    static IEnumerable<LineString> SharedLinework(Geometry a, Geometry b) =>
        EnumerateLines(SafeIntersection(a.Boundary, b.Boundary));

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
