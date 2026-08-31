using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Triangulate;

static class AdaptiveHybridFrontierLab
{
    sealed record Cell(long Id, string TerritoryCode, string OwnerCode, double Lat, double Lon, Geometry Geometry);
    sealed record Edge(long A, long B, bool Foreign);

    static readonly GeometryFactory GeometryFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly HashSet<string> TargetTerritories =
        new(StringComparer.OrdinalIgnoreCase) { "FR", "BE", "LU", "DE", "CH", "IT", "ES" };

    const double CoastalGuardKm = 3.0;
    const double MaxChangedAreaRatio = 0.35;
    const int MaxGuardrailIterations = 8;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;

        var cells = root.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();
        var edges = root.GetProperty("edges").EnumerateArray()
            .Select(e => new Edge(e.GetProperty("a").GetInt64(), e.GetProperty("b").GetInt64(),
                e.TryGetProperty("foreign", out var f) && f.GetBoolean()))
            .ToArray();

        var targetCells = cells.Where(c => TargetTerritories.Contains(c.TerritoryCode)).ToArray();
        var byId = targetCells.ToDictionary(c => c.Id);
        var targetEdges = edges.Where(e => e.Foreign && byId.ContainsKey(e.A) && byId.ContainsKey(e.B)).ToArray();
        var regionalLand = SafeUnion(targetCells.Select(c => c.Geometry));
        var globalVoronoi = BuildGlobalVoronoi(targetCells, regionalLand.EnvelopeInternal);
        var baselineByCell = targetCells.ToDictionary(c => c.Id, c => c.Geometry);
        var baselineOwners = BuildOwnerRegions(targetCells, baselineByCell);

        var ownerScale = baselineOwners.Keys.ToDictionary(x => x, _ => 1.0, StringComparer.OrdinalIgnoreCase);
        Dictionary<long, Geometry> finalCells = baselineByCell;
        Dictionary<string, Geometry> finalOwners = baselineOwners;
        Dictionary<string, double> finalChanges = baselineOwners.Keys.ToDictionary(x => x, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        Geometry finalCorridor = GeometryFactory.CreatePolygon();
        Dictionary<string, double> finalWidths = new(StringComparer.OrdinalIgnoreCase);
        var iterations = 0;

        for (var iteration = 0; iteration < MaxGuardrailIterations; iteration++)
        {
            iterations = iteration + 1;
            var (corridor, widths) = BuildAdaptiveCorridor(targetEdges, byId, baselineOwners, regionalLand, ownerScale);
            var hybridCells = new Dictionary<long, Geometry>();

            foreach (var cell in targetCells)
            {
                var outside = SafeDifference(cell.Geometry, corridor);
                var inside = globalVoronoi.TryGetValue(cell.Id, out var pure)
                    ? SafeIntersection(pure, corridor)
                    : GeometryFactory.CreatePolygon();
                var hybrid = SafeIntersection(SafeUnion(new[] { outside, inside }), regionalLand);
                hybridCells[cell.Id] = hybrid.IsValid ? hybrid : hybrid.Buffer(0);
            }

            var ownerRegions = BuildOwnerRegions(targetCells, hybridCells);
            var changes = ownerRegions.ToDictionary(
                x => x.Key,
                x =>
                {
                    var baseline = baselineOwners[x.Key];
                    var diff = SafeSymDifference(baseline, x.Value);
                    return baseline.Area <= 0 ? 0 : diff.Area / baseline.Area;
                }, StringComparer.OrdinalIgnoreCase);

            finalCells = hybridCells;
            finalOwners = ownerRegions;
            finalChanges = changes;
            finalCorridor = corridor;
            finalWidths = widths;

            var offenders = changes.Where(x => x.Value > MaxChangedAreaRatio).ToArray();
            if (offenders.Length == 0) break;

            foreach (var offender in offenders)
            {
                var correction = Math.Sqrt(MaxChangedAreaRatio / offender.Value);
                ownerScale[offender.Key] = Math.Max(0.20, ownerScale[offender.Key] * correction);
            }
        }

        var guardrailSatisfied = finalChanges.All(x => x.Value <= MaxChangedAreaRatio + 1e-9);
        var widthValues = finalWidths.Values.ToArray();

        var payload = new
        {
            status = "experimental",
            description = "C2 adaptive hybrid political-frontier lab. Corridor width varies by the smaller adjacent owner and is iteratively reduced when territorial deformation exceeds the guardrail.",
            targetTerritories = TargetTerritories.OrderBy(x => x).ToArray(),
            cellCount = targetCells.Length,
            foreignAdjacencyCount = targetEdges.Length,
            current = new { ownerRegions = OwnerRegionPayload(baselineOwners) },
            variants = new Dictionary<string, object>
            {
                ["adaptive"] = new
                {
                    label = "C2 adaptatif",
                    coastalGuardKm = CoastalGuardKm,
                    maxChangedAreaRatio = MaxChangedAreaRatio,
                    guardrailSatisfied,
                    guardrailIterations = iterations,
                    minAppliedCorridorKm = widthValues.Length == 0 ? 0 : widthValues.Min(),
                    maxAppliedCorridorKm = widthValues.Length == 0 ? 0 : widthValues.Max(),
                    corridor = GeometryToGeoJson(finalCorridor),
                    ownerRegions = OwnerRegionPayload(finalOwners),
                    changedAreaRatioByOwner = finalChanges,
                    ownerScaleFactors = ownerScale,
                    appliedWidthsKmByOwnerPair = finalWidths
                }
            },
            cities = targetCells.Select(c => new { id = c.Id, territoryCode = c.TerritoryCode, ownerCode = c.OwnerCode, lat = c.Lat, lon = c.Lon }).ToArray()
        };

        await File.WriteAllTextAsync(Path.Combine(outDir, "hybrid-frontier-lab.json"), JsonSerializer.Serialize(payload));
        Console.WriteLine($"Adaptive hybrid C2: {targetCells.Length} cells, guardrail={(guardrailSatisfied ? "ok" : "not-satisfied")}, iterations={iterations}, max-change={finalChanges.Values.Max():P1}.");
    }

    static (Geometry Corridor, Dictionary<string, double> Widths) BuildAdaptiveCorridor(
        IReadOnlyList<Edge> edges,
        IReadOnlyDictionary<long, Cell> byId,
        IReadOnlyDictionary<string, Geometry> ownerRegions,
        Geometry regionalLand,
        IReadOnlyDictionary<string, double> ownerScale)
    {
        var parts = new List<Geometry>();
        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            var aCell = byId[edge.A];
            var bCell = byId[edge.B];
            var smallerArea = Math.Min(ownerRegions[aCell.OwnerCode].Area, ownerRegions[bCell.OwnerCode].Area);
            var baseWidth = BaseWidthKm(smallerArea);
            var widthKm = baseWidth * Math.Min(ownerScale[aCell.OwnerCode], ownerScale[bCell.OwnerCode]);
            widthKm = Math.Clamp(widthKm, 2.0, 25.0);

            var pairKey = string.Compare(aCell.OwnerCode, bCell.OwnerCode, StringComparison.OrdinalIgnoreCase) < 0
                ? $"{aCell.OwnerCode}-{bCell.OwnerCode}"
                : $"{bCell.OwnerCode}-{aCell.OwnerCode}";
            widths[pairKey] = widths.TryGetValue(pairKey, out var old) ? Math.Min(old, widthKm) : widthKm;

            var d = widthKm / 111.32;
            var overlap = SafeIntersection(SafeBuffer(aCell.Geometry.Boundary, d), SafeBuffer(bCell.Geometry.Boundary, d));
            if (!overlap.IsEmpty) parts.Add(overlap);
        }

        var corridor = SafeIntersection(SafeUnion(parts), regionalLand);
        var coastalGuard = SafeBuffer(regionalLand.Boundary, CoastalGuardKm / 111.32);
        corridor = SafeDifference(corridor, coastalGuard);
        return (corridor.IsValid ? corridor : corridor.Buffer(0), widths);
    }

    static double BaseWidthKm(double smallerOwnerAreaDegrees2)
    {
        if (smallerOwnerAreaDegrees2 < 0.5) return 5;
        if (smallerOwnerAreaDegrees2 < 2.0) return 10;
        if (smallerOwnerAreaDegrees2 < 8.0) return 15;
        if (smallerOwnerAreaDegrees2 < 20.0) return 20;
        return 25;
    }

    static Cell ParseCell(JsonElement e)
    {
        var geometry = ParseGeoJsonGeometry(e.GetProperty("geometry")) ?? GeometryFactory.CreatePolygon();
        return new Cell(
            e.GetProperty("id").GetInt64(),
            e.GetProperty("territoryCode").GetString() ?? "?",
            e.GetProperty("ownerCode").GetString() ?? "?",
            e.GetProperty("lat").GetDouble(), e.GetProperty("lon").GetDouble(), geometry);
    }

    static Dictionary<long, Geometry> BuildGlobalVoronoi(IReadOnlyList<Cell> cells, Envelope envelope)
    {
        var ids = new Dictionary<string, long>();
        var sites = new List<Coordinate>();
        foreach (var c in cells) { var p = new Coordinate(c.Lon, c.Lat); ids[Key(p)] = c.Id; sites.Add(p); }
        var b = new VoronoiDiagramBuilder { ClipEnvelope = envelope, Tolerance = 0.0 };
        b.SetSites(sites);
        var diagram = b.GetDiagram(GeometryFactory);
        var result = new Dictionary<long, Geometry>();
        for (var i = 0; i < diagram.NumGeometries; i++)
        {
            var face = diagram.GetGeometryN(i);
            if (face.UserData is Coordinate site && ids.TryGetValue(Key(site), out var id)) result[id] = face;
        }
        return result;
    }

    static string Key(Coordinate c) => $"{Math.Round(c.X, 9):F9}|{Math.Round(c.Y, 9):F9}";

    static Dictionary<string, Geometry> BuildOwnerRegions(IReadOnlyList<Cell> cells, IReadOnlyDictionary<long, Geometry> geometryById) =>
        cells.GroupBy(c => c.OwnerCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SafeUnion(g.Select(c => geometryById[c.Id])), StringComparer.OrdinalIgnoreCase);

    static object[] OwnerRegionPayload(IReadOnlyDictionary<string, Geometry> owners) => owners.OrderBy(x => x.Key)
        .Select(x => (object)new { ownerCode = x.Key, geometry = GeometryToGeoJson(x.Value) }).ToArray();

    static Geometry SafeUnion(IEnumerable<Geometry> geometries)
    {
        var a = geometries.Where(g => !g.IsEmpty).ToArray();
        if (a.Length == 0) return GeometryFactory.CreatePolygon();
        if (a.Length == 1) return a[0];
        try { var r = UnaryUnionOp.Union(a); return r.IsValid ? r : r.Buffer(0); }
        catch { var r = UnaryUnionOp.Union(a.Select(g => g.Buffer(0)).ToArray()); return r.IsValid ? r : r.Buffer(0); }
    }

    static Geometry SafeIntersection(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty) return GeometryFactory.CreatePolygon();
        try { var r = a.Intersection(b); return r.IsValid ? r : r.Buffer(0); }
        catch { var r = a.Buffer(0).Intersection(b.Buffer(0)); return r.IsValid ? r : r.Buffer(0); }
    }

    static Geometry SafeDifference(Geometry a, Geometry b)
    {
        if (a.IsEmpty) return GeometryFactory.CreatePolygon(); if (b.IsEmpty) return a;
        try { var r = a.Difference(b); return r.IsValid ? r : r.Buffer(0); }
        catch { var r = a.Buffer(0).Difference(b.Buffer(0)); return r.IsValid ? r : r.Buffer(0); }
    }

    static Geometry SafeSymDifference(Geometry a, Geometry b)
    {
        try { var r = a.SymmetricDifference(b); return r.IsValid ? r : r.Buffer(0); }
        catch { var r = a.Buffer(0).SymmetricDifference(b.Buffer(0)); return r.IsValid ? r : r.Buffer(0); }
    }

    static Geometry SafeBuffer(Geometry g, double d)
    {
        try { var r = g.Buffer(d); return r.IsValid ? r : r.Buffer(0); }
        catch { var r = g.Buffer(0).Buffer(d); return r.IsValid ? r : r.Buffer(0); }
    }

    static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (geometry.ValueKind == JsonValueKind.Null) return null;
        var type = geometry.GetProperty("type").GetString(); var coords = geometry.GetProperty("coordinates");
        if (type == "Polygon") return ParsePolygon(coords);
        if (type == "MultiPolygon") return GeometryFactory.CreateMultiPolygon(coords.EnumerateArray().Select(ParsePolygon).ToArray());
        return null;
    }

    static Polygon ParsePolygon(JsonElement coords)
    {
        var rings = coords.EnumerateArray().Select(ParseRing).Where(x => x is not null).Cast<LinearRing>().ToArray();
        return rings.Length == 0 ? GeometryFactory.CreatePolygon() : GeometryFactory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    static LinearRing? ParseRing(JsonElement ring)
    {
        var pts = ring.EnumerateArray().Select(p => { var xy = p.EnumerateArray().ToArray(); return new Coordinate(xy[0].GetDouble(), xy[1].GetDouble()); }).ToList();
        if (pts.Count < 3) return null; if (!pts[0].Equals2D(pts[^1])) pts.Add(new Coordinate(pts[0]));
        return pts.Count < 4 ? null : GeometryFactory.CreateLinearRing(pts.ToArray());
    }

    static object? GeometryToGeoJson(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;
        object Coordinates(Coordinate[] c) => c.Select(p => new[] { Math.Round(p.X, 5), Math.Round(p.Y, 5) }).ToArray();
        object Poly(Polygon p)
        {
            var rings = new List<object> { Coordinates(p.ExteriorRing.Coordinates) };
            for (var i = 0; i < p.NumInteriorRings; i++) rings.Add(Coordinates(p.GetInteriorRingN(i).Coordinates));
            return rings;
        }
        if (geometry is Polygon p) return new { type = "Polygon", coordinates = Poly(p) };
        if (geometry is MultiPolygon m) return new { type = "MultiPolygon", coordinates = Enumerable.Range(0, m.NumGeometries).Select(i => Poly((Polygon)m.GetGeometryN(i))).ToArray() };
        var polygons = Enumerable.Range(0, geometry.NumGeometries).Select(i => geometry.GetGeometryN(i)).OfType<Polygon>().ToArray();
        if (polygons.Length == 1) return new { type = "Polygon", coordinates = Poly(polygons[0]) };
        if (polygons.Length > 1) return new { type = "MultiPolygon", coordinates = polygons.Select(Poly).ToArray() };
        return null;
    }
}
