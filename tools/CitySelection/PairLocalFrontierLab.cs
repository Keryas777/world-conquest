using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Triangulate;

static class PairLocalFrontierLab
{
    sealed record Cell(long Id, string TerritoryCode, string OwnerCode, double Lat, double Lon, Geometry Geometry);
    sealed record Edge(long A, long B, bool Foreign);

    static readonly GeometryFactory GeometryFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly HashSet<string> TargetTerritories =
        new(StringComparer.OrdinalIgnoreCase) { "FR", "BE", "LU", "DE", "CH", "IT", "ES" };

    const double WidthMultiplier = 1.50;
    const double CoastalGuardKm = 3.0;
    const double InvariantAreaTolerance = 1e-7;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;

        var cells = root.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();
        var edges = root.GetProperty("edges").EnumerateArray()
            .Select(e => new Edge(
                e.GetProperty("a").GetInt64(),
                e.GetProperty("b").GetInt64(),
                e.TryGetProperty("foreign", out var foreign) && foreign.GetBoolean()))
            .ToArray();

        var targetCells = cells.Where(c => TargetTerritories.Contains(c.TerritoryCode)).ToArray();
        var byId = targetCells.ToDictionary(c => c.Id);
        var targetEdges = edges.Where(e => e.Foreign && byId.ContainsKey(e.A) && byId.ContainsKey(e.B)).ToArray();
        var regionalLand = SafeUnion(targetCells.Select(c => c.Geometry));
        var baselineByCell = targetCells.ToDictionary(c => c.Id, c => c.Geometry);
        var baselineOwners = BuildOwnerRegions(targetCells, baselineByCell);
        var ownerRegions = baselineOwners.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        Geometry usedCorridor = GeometryFactory.CreatePolygon();
        var pairMetrics = new List<object>();
        var pairKeys = targetEdges
            .Select(e => PairKey(byId[e.A].OwnerCode, byId[e.B].OwnerCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var pairKey in pairKeys)
        {
            var (ownerA, ownerB) = SplitPair(pairKey);
            if (!baselineOwners.ContainsKey(ownerA) || !baselineOwners.ContainsKey(ownerB))
                continue;

            var pairEdges = targetEdges
                .Where(e => PairKey(byId[e.A].OwnerCode, byId[e.B].OwnerCode).Equals(pairKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var widthKm = Math.Clamp(
                BaseWidthKm(Math.Min(baselineOwners[ownerA].Area, baselineOwners[ownerB].Area)) * WidthMultiplier,
                0.05,
                25.0 * WidthMultiplier);

            var corridor = BuildPairCorridor(pairEdges, byId, regionalLand, widthKm);
            var exclusiveCorridor = SafeDifference(corridor, usedCorridor);
            usedCorridor = SafeUnion(new[] { usedCorridor, exclusiveCorridor });
            if (exclusiveCorridor.IsEmpty)
                continue;

            var pairBaseline = SafeUnion(new[] { baselineOwners[ownerA], baselineOwners[ownerB] });
            var pairDomain = SafeIntersection(exclusiveCorridor, pairBaseline);
            if (pairDomain.IsEmpty)
                continue;

            var pairCells = targetCells
                .Where(c => c.OwnerCode.Equals(ownerA, StringComparison.OrdinalIgnoreCase) ||
                            c.OwnerCode.Equals(ownerB, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var pairVoronoi = BuildVoronoi(pairCells, pairDomain.EnvelopeInternal);

            Geometry AssignedTo(string owner) => SafeUnion(pairCells
                .Where(c => c.OwnerCode.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(c => pairVoronoi.TryGetValue(c.Id, out var face)
                    ? SafeIntersection(face, pairDomain)
                    : GeometryFactory.CreatePolygon()));

            var insideA = AssignedTo(ownerA);
            var insideB = AssignedTo(ownerB);
            var assigned = SafeUnion(new[] { insideA, insideB });
            var localGap = SafeDifference(pairDomain, assigned);
            var localOverlap = SafeIntersection(insideA, insideB);

            ownerRegions[ownerA] = SafeUnion(new[] { SafeDifference(ownerRegions[ownerA], exclusiveCorridor), insideA });
            ownerRegions[ownerB] = SafeUnion(new[] { SafeDifference(ownerRegions[ownerB], exclusiveCorridor), insideB });

            pairMetrics.Add(new
            {
                pair = pairKey,
                widthKm,
                corridorArea = exclusiveCorridor.Area,
                localGapArea = localGap.Area,
                localOverlapArea = localOverlap.Area,
                competingOwners = new[] { ownerA, ownerB }
            });
        }

        var finalUnion = SafeUnion(ownerRegions.Values);
        var gap = SafeDifference(regionalLand, finalUnion);
        var outside = SafeDifference(finalUnion, regionalLand);
        var overlapArea = PairwiseOverlapArea(ownerRegions);

        var topology = ownerRegions.OrderBy(x => x.Key).Select(x =>
        {
            var baselineCount = PolygonComponentCount(baselineOwners[x.Key]);
            var finalCount = PolygonComponentCount(x.Value);
            return new
            {
                ownerCode = x.Key,
                baselineComponents = baselineCount,
                finalComponents = finalCount,
                newComponents = Math.Max(0, finalCount - baselineCount)
            };
        }).ToArray();

        var coverageSatisfied = gap.Area <= InvariantAreaTolerance && outside.Area <= InvariantAreaTolerance;
        var overlapSatisfied = overlapArea <= InvariantAreaTolerance;
        var topologySatisfied = topology.All(x => x.newComponents == 0);
        var invariantsSatisfied = coverageSatisfied && overlapSatisfied && topologySatisfied;

        var payload = new
        {
            status = "experimental",
            description = "C3 pair-local frontier experiment. Each terrestrial owner-pair keeps an exclusive corridor and only cities owned by that pair may compete inside it. Pair corridors are made mutually exclusive deterministically before recomposition.",
            widthMultiplier = WidthMultiplier,
            coastalGuardKm = CoastalGuardKm,
            targetTerritories = TargetTerritories.OrderBy(x => x).ToArray(),
            cellCount = targetCells.Length,
            foreignAdjacencyCount = targetEdges.Length,
            pairCount = pairKeys.Length,
            current = new { ownerRegions = OwnerRegionPayload(baselineOwners) },
            c3 = new
            {
                ownerRegions = OwnerRegionPayload(ownerRegions),
                corridor = GeometryToGeoJson(usedCorridor),
                pairMetrics,
                invariants = new
                {
                    satisfied = invariantsSatisfied,
                    coverageSatisfied,
                    overlapSatisfied,
                    topologySatisfied,
                    gapArea = gap.Area,
                    outsideArea = outside.Area,
                    overlapArea,
                    topology
                }
            },
            cities = targetCells.Select(c => new
            {
                id = c.Id,
                territoryCode = c.TerritoryCode,
                ownerCode = c.OwnerCode,
                lat = c.Lat,
                lon = c.Lon
            }).ToArray()
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "c3-pair-local-frontier-lab.json"),
            JsonSerializer.Serialize(payload));

        Console.WriteLine(
            $"C3 pair-local: pairs={pairKeys.Length}, gap={gap.Area:F8}, overlap={overlapArea:F8}, " +
            $"new-components={topology.Sum(x => x.newComponents)}, invariants={(invariantsSatisfied ? "ok" : "FAILED")}.");

        if (!invariantsSatisfied)
            throw new InvalidOperationException(
                $"C3 geometry invariants failed: coverage={coverageSatisfied}, overlap={overlapSatisfied}, topology={topologySatisfied}.");
    }

    static Geometry BuildPairCorridor(
        IReadOnlyList<Edge> edges,
        IReadOnlyDictionary<long, Cell> byId,
        Geometry regionalLand,
        double widthKm)
    {
        var parts = new List<Geometry>();
        var d = widthKm / 111.32;
        foreach (var edge in edges)
        {
            var a = byId[edge.A].Geometry;
            var b = byId[edge.B].Geometry;
            var overlap = SafeIntersection(SafeBuffer(a.Boundary, d), SafeBuffer(b.Boundary, d));
            if (!overlap.IsEmpty)
                parts.Add(overlap);
        }

        var corridor = SafeIntersection(SafeUnion(parts), regionalLand);
        var coastalGuard = SafeBuffer(regionalLand.Boundary, CoastalGuardKm / 111.32);
        return SafeDifference(corridor, coastalGuard);
    }

    static Dictionary<long, Geometry> BuildVoronoi(IReadOnlyList<Cell> cells, Envelope envelope)
    {
        var ids = new Dictionary<string, long>();
        var sites = new List<Coordinate>();
        foreach (var cell in cells)
        {
            var point = new Coordinate(cell.Lon, cell.Lat);
            ids[Key(point)] = cell.Id;
            sites.Add(point);
        }

        var builder = new VoronoiDiagramBuilder { ClipEnvelope = envelope, Tolerance = 0.0 };
        builder.SetSites(sites);
        var diagram = builder.GetDiagram(GeometryFactory);
        var result = new Dictionary<long, Geometry>();
        for (var i = 0; i < diagram.NumGeometries; i++)
        {
            var face = diagram.GetGeometryN(i);
            if (face.UserData is Coordinate site && ids.TryGetValue(Key(site), out var id))
                result[id] = face;
        }
        return result;
    }

    static double PairwiseOverlapArea(IReadOnlyDictionary<string, Geometry> owners)
    {
        var values = owners.OrderBy(x => x.Key).ToArray();
        var area = 0.0;
        for (var i = 0; i < values.Length; i++)
            for (var j = i + 1; j < values.Length; j++)
                area += SafeIntersection(values[i].Value, values[j].Value).Area;
        return area;
    }

    static int PolygonComponentCount(Geometry geometry)
    {
        if (geometry.IsEmpty) return 0;
        if (geometry is Polygon) return 1;
        if (geometry is MultiPolygon multi) return multi.NumGeometries;
        return Enumerable.Range(0, geometry.NumGeometries).Count(i => geometry.GetGeometryN(i) is Polygon);
    }

    static string PairKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}-{b}" : $"{b}-{a}";

    static (string A, string B) SplitPair(string pair)
    {
        var separator = pair.IndexOf('-');
        return (pair[..separator], pair[(separator + 1)..]);
    }

    static double BaseWidthKm(double smallerOwnerAreaDegrees2)
    {
        if (smallerOwnerAreaDegrees2 < 0.5) return 5;
        if (smallerOwnerAreaDegrees2 < 2.0) return 10;
        if (smallerOwnerAreaDegrees2 < 8.0) return 15;
        if (smallerOwnerAreaDegrees2 < 20.0) return 20;
        return 25;
    }

    static string Key(Coordinate c) => $"{Math.Round(c.X, 9):F9}|{Math.Round(c.Y, 9):F9}";

    static Cell ParseCell(JsonElement e)
    {
        var geometry = ParseGeoJsonGeometry(e.GetProperty("geometry")) ?? GeometryFactory.CreatePolygon();
        return new Cell(
            e.GetProperty("id").GetInt64(),
            e.GetProperty("territoryCode").GetString() ?? "?",
            e.GetProperty("ownerCode").GetString() ?? "?",
            e.GetProperty("lat").GetDouble(),
            e.GetProperty("lon").GetDouble(),
            geometry);
    }

    static Dictionary<string, Geometry> BuildOwnerRegions(
        IReadOnlyList<Cell> cells,
        IReadOnlyDictionary<long, Geometry> geometryById) =>
        cells.GroupBy(c => c.OwnerCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SafeUnion(g.Select(c => geometryById[c.Id])), StringComparer.OrdinalIgnoreCase);

    static object[] OwnerRegionPayload(IReadOnlyDictionary<string, Geometry> owners) =>
        owners.OrderBy(x => x.Key).Select(x => (object)new { ownerCode = x.Key, geometry = GeometryToGeoJson(x.Value) }).ToArray();

    static Geometry SafeUnion(IEnumerable<Geometry> geometries)
    {
        var values = geometries.Where(g => !g.IsEmpty).ToArray();
        if (values.Length == 0) return GeometryFactory.CreatePolygon();
        if (values.Length == 1) return values[0];
        try
        {
            var result = UnaryUnionOp.Union(values);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            var result = OverlayNGRobust.Union(values);
            return result.IsValid ? result : result.Buffer(0);
        }
    }

    static Geometry SafeIntersection(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty) return GeometryFactory.CreatePolygon();
        try
        {
            var result = a.Intersection(b);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            var result = OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection);
            return result.IsValid ? result : result.Buffer(0);
        }
    }

    static Geometry SafeDifference(Geometry a, Geometry b)
    {
        if (a.IsEmpty) return GeometryFactory.CreatePolygon();
        if (b.IsEmpty) return a;
        try
        {
            var result = a.Difference(b);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            var result = OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference);
            return result.IsValid ? result : result.Buffer(0);
        }
    }

    static Geometry SafeBuffer(Geometry geometry, double distance)
    {
        try
        {
            var result = geometry.Buffer(distance);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            var result = geometry.Buffer(0).Buffer(distance);
            return result.IsValid ? result : result.Buffer(0);
        }
    }

    static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (geometry.ValueKind == JsonValueKind.Null) return null;
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon") return ParsePolygon(coordinates);
        if (type == "MultiPolygon") return GeometryFactory.CreateMultiPolygon(coordinates.EnumerateArray().Select(ParsePolygon).ToArray());
        return null;
    }

    static Polygon ParsePolygon(JsonElement coordinates)
    {
        var rings = coordinates.EnumerateArray().Select(ParseRing).Where(x => x is not null).Cast<LinearRing>().ToArray();
        return rings.Length == 0 ? GeometryFactory.CreatePolygon() : GeometryFactory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
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
        return points.Count < 4 ? null : GeometryFactory.CreateLinearRing(points.ToArray());
    }

    static object? GeometryToGeoJson(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;
        object Coordinates(Coordinate[] coordinates) => coordinates.Select(p => new[] { Math.Round(p.X, 5), Math.Round(p.Y, 5) }).ToArray();
        object PolygonCoordinates(Polygon polygon)
        {
            var rings = new List<object> { Coordinates(polygon.ExteriorRing.Coordinates) };
            for (var i = 0; i < polygon.NumInteriorRings; i++) rings.Add(Coordinates(polygon.GetInteriorRingN(i).Coordinates));
            return rings;
        }
        if (geometry is Polygon polygon) return new { type = "Polygon", coordinates = PolygonCoordinates(polygon) };
        if (geometry is MultiPolygon multiPolygon)
            return new { type = "MultiPolygon", coordinates = Enumerable.Range(0, multiPolygon.NumGeometries).Select(i => PolygonCoordinates((Polygon)multiPolygon.GetGeometryN(i))).ToArray() };
        var polygons = Enumerable.Range(0, geometry.NumGeometries).Select(i => geometry.GetGeometryN(i)).OfType<Polygon>().ToArray();
        if (polygons.Length == 1) return new { type = "Polygon", coordinates = PolygonCoordinates(polygons[0]) };
        if (polygons.Length > 1) return new { type = "MultiPolygon", coordinates = polygons.Select(PolygonCoordinates).ToArray() };
        return null;
    }
}
