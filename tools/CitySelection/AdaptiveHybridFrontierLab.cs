using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Triangulate;

static class AdaptiveHybridFrontierLab
{
    sealed record Cell(long Id, string TerritoryCode, string OwnerCode, double Lat, double Lon, Geometry Geometry);
    sealed record Edge(long A, long B, bool Foreign);
    sealed record AdaptiveProfile(string Key, string Label, double WidthMultiplier);
    sealed record AdaptiveRun(
        Geometry Corridor,
        Dictionary<string, Geometry> OwnerRegions,
        Dictionary<string, double> ChangedAreaRatioByOwner,
        Dictionary<string, double> PairScaleFactors,
        Dictionary<string, double> AppliedWidthsKmByOwnerPair,
        string[] DisabledPairs,
        string[] ProtectedOwners,
        int Iterations,
        bool GuardrailSatisfied);

    static readonly GeometryFactory GeometryFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly HashSet<string> TargetTerritories =
        new(StringComparer.OrdinalIgnoreCase) { "FR", "BE", "LU", "DE", "CH", "IT", "ES" };

    static readonly AdaptiveProfile[] Profiles =
    {
        new("adaptive", "C2.2 x1.00", 1.00),
        new("adaptive125", "C2.2 x1.25", 1.25),
        new("adaptive150", "C2.2 x1.50", 1.50),
        new("adaptive175", "C2.2 x1.75", 1.75),
        new("adaptive200", "C2.2 x2.00", 2.00)
    };

    const double CoastalGuardKm = 3.0;
    const double MaxChangedAreaRatio = 0.35;
    const int MaxSoftGuardrailIterations = 12;
    const double GuardrailEpsilon = 1e-9;
    const double MinActiveCorridorKm = 0.05;

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
                e.TryGetProperty("foreign", out var f) && f.GetBoolean()))
            .ToArray();

        var targetCells = cells.Where(c => TargetTerritories.Contains(c.TerritoryCode)).ToArray();
        var byId = targetCells.ToDictionary(c => c.Id);
        var targetEdges = edges.Where(e => e.Foreign && byId.ContainsKey(e.A) && byId.ContainsKey(e.B)).ToArray();
        var regionalLand = SafeUnion(targetCells.Select(c => c.Geometry));
        var globalVoronoi = BuildGlobalVoronoi(targetCells, regionalLand.EnvelopeInternal);
        var baselineByCell = targetCells.ToDictionary(c => c.Id, c => c.Geometry);
        var baselineOwners = BuildOwnerRegions(targetCells, baselineByCell);

        var variants = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            var run = RunAdaptiveVariant(
                targetCells, targetEdges, byId, baselineOwners,
                globalVoronoi, regionalLand, profile.WidthMultiplier);

            var widthValues = run.AppliedWidthsKmByOwnerPair.Values.ToArray();
            var changeValues = run.ChangedAreaRatioByOwner.Values.ToArray();

            variants[profile.Key] = new
            {
                label = profile.Label,
                widthMultiplier = profile.WidthMultiplier,
                coastalGuardKm = CoastalGuardKm,
                maxChangedAreaRatio = MaxChangedAreaRatio,
                hardGuardrail = true,
                guardrailMode = "pairwise",
                guardrailSatisfied = run.GuardrailSatisfied,
                guardrailIterations = run.Iterations,
                minAppliedCorridorKm = widthValues.Length == 0 ? 0 : widthValues.Min(),
                meanAppliedCorridorKm = widthValues.Length == 0 ? 0 : widthValues.Average(),
                maxAppliedCorridorKm = widthValues.Length == 0 ? 0 : widthValues.Max(),
                maxObservedChangedAreaRatio = changeValues.Length == 0 ? 0 : changeValues.Max(),
                meanObservedChangedAreaRatio = changeValues.Length == 0 ? 0 : changeValues.Average(),
                constrainedPairCount = run.PairScaleFactors.Count(x => x.Value < 0.999999),
                disabledPairCount = run.DisabledPairs.Length,
                disabledPairCodes = run.DisabledPairs,
                protectedOwnerCount = run.ProtectedOwners.Length,
                protectedOwnerCodes = run.ProtectedOwners,
                hardFallbackUsed = run.DisabledPairs.Length > 0 || run.ProtectedOwners.Length > 0,
                nearGuardrailOwnerCount = run.ChangedAreaRatioByOwner.Count(x => x.Value >= MaxChangedAreaRatio * 0.85),
                corridor = GeometryToGeoJson(run.Corridor),
                ownerRegions = OwnerRegionPayload(run.OwnerRegions),
                changedAreaRatioByOwner = run.ChangedAreaRatioByOwner,
                pairScaleFactors = run.PairScaleFactors,
                appliedWidthsKmByOwnerPair = run.AppliedWidthsKmByOwnerPair
            };

            Console.WriteLine(
                $"Adaptive hybrid {profile.Label}: guardrail={(run.GuardrailSatisfied ? "ok" : "FAILED")}, " +
                $"iterations={run.Iterations}, disabled-pairs={run.DisabledPairs.Length}, protected={run.ProtectedOwners.Length}, " +
                $"max-change={(changeValues.Length == 0 ? 0 : changeValues.Max()):P1}, " +
                $"mean-width={(widthValues.Length == 0 ? 0 : widthValues.Average()):F1} km.");
        }

        var payload = new
        {
            status = "experimental",
            description = "C2.2 adaptive hybrid political-frontier lab. The 35% deformation guardrail remains a hard invariant. Correction is applied per owner-pair; individual pairs are disabled as needed, and an owner whose own pairs are exhausted is locally protected from unrelated corridor spillover instead of disabling neighbouring experiments.",
            targetTerritories = TargetTerritories.OrderBy(x => x).ToArray(),
            cellCount = targetCells.Length,
            foreignAdjacencyCount = targetEdges.Length,
            current = new { ownerRegions = OwnerRegionPayload(baselineOwners) },
            variants,
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
            Path.Combine(outDir, "hybrid-frontier-lab.json"),
            JsonSerializer.Serialize(payload));
    }

    static AdaptiveRun RunAdaptiveVariant(
        IReadOnlyList<Cell> targetCells,
        IReadOnlyList<Edge> targetEdges,
        IReadOnlyDictionary<long, Cell> byId,
        IReadOnlyDictionary<string, Geometry> baselineOwners,
        IReadOnlyDictionary<long, Geometry> globalVoronoi,
        Geometry regionalLand,
        double widthMultiplier)
    {
        var pairKeys = targetEdges
            .Select(e => PairKey(byId[e.A].OwnerCode, byId[e.B].OwnerCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pairScale = pairKeys.ToDictionary(x => x, _ => 1.0, StringComparer.OrdinalIgnoreCase);
        var disabledPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var evaluation = EvaluateVariant(
            targetCells, targetEdges, byId, baselineOwners, globalVoronoi,
            regionalLand, pairScale, disabledPairs, protectedOwners, widthMultiplier);
        var iterations = 1;

        for (var iteration = 1;
             iteration < MaxSoftGuardrailIterations &&
             evaluation.Changes.Any(x => x.Value > MaxChangedAreaRatio + GuardrailEpsilon);
             iteration++)
        {
            var corrections = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var offender in evaluation.Changes.Where(x => x.Value > MaxChangedAreaRatio + GuardrailEpsilon))
            {
                var correction = Math.Sqrt(MaxChangedAreaRatio / offender.Value);
                foreach (var pair in pairKeys.Where(p => PairTouchesOwner(p, offender.Key) && !disabledPairs.Contains(p)))
                    corrections[pair] = corrections.TryGetValue(pair, out var old) ? Math.Min(old, correction) : correction;
            }

            foreach (var correction in corrections)
                pairScale[correction.Key] = Math.Max(0.001, pairScale[correction.Key] * correction.Value);

            evaluation = EvaluateVariant(
                targetCells, targetEdges, byId, baselineOwners, globalVoronoi,
                regionalLand, pairScale, disabledPairs, protectedOwners, widthMultiplier);
            iterations++;
        }

        while (evaluation.Changes.Any(x => x.Value > MaxChangedAreaRatio + GuardrailEpsilon))
        {
            var worstOwner = evaluation.Changes
                .Where(x => x.Value > MaxChangedAreaRatio + GuardrailEpsilon)
                .OrderByDescending(x => x.Value)
                .First().Key;

            var candidate = pairKeys
                .Where(p => PairTouchesOwner(p, worstOwner) && !disabledPairs.Contains(p))
                .OrderByDescending(p => evaluation.Widths.TryGetValue(p, out var width) ? width : 0)
                .FirstOrDefault();

            if (candidate is not null)
            {
                disabledPairs.Add(candidate);
                pairScale[candidate] = 0.0;
            }
            else if (!protectedOwners.Add(worstOwner))
            {
                throw new InvalidOperationException($"C2.2 hard guardrail cannot reduce remaining deformation for {worstOwner}.");
            }

            evaluation = EvaluateVariant(
                targetCells, targetEdges, byId, baselineOwners, globalVoronoi,
                regionalLand, pairScale, disabledPairs, protectedOwners, widthMultiplier);
            iterations++;

            if (disabledPairs.Count == pairKeys.Length &&
                protectedOwners.Count == baselineOwners.Count &&
                evaluation.Changes.Any(x => x.Value > MaxChangedAreaRatio + GuardrailEpsilon))
                throw new InvalidOperationException("C2.2 hard guardrail invariant failed even after disabling all pairs and protecting all owners.");
        }

        if (evaluation.Changes.Any(x => x.Value > MaxChangedAreaRatio + GuardrailEpsilon))
            throw new InvalidOperationException("C2.2 hard guardrail invariant failed.");

        return new AdaptiveRun(
            evaluation.Corridor,
            evaluation.OwnerRegions,
            evaluation.Changes,
            pairScale,
            evaluation.Widths,
            disabledPairs.OrderBy(x => x).ToArray(),
            protectedOwners.OrderBy(x => x).ToArray(),
            iterations,
            true);
    }

    static (Geometry Corridor,
        Dictionary<string, Geometry> OwnerRegions,
        Dictionary<string, double> Changes,
        Dictionary<string, double> Widths) EvaluateVariant(
        IReadOnlyList<Cell> targetCells,
        IReadOnlyList<Edge> targetEdges,
        IReadOnlyDictionary<long, Cell> byId,
        IReadOnlyDictionary<string, Geometry> baselineOwners,
        IReadOnlyDictionary<long, Geometry> globalVoronoi,
        Geometry regionalLand,
        IReadOnlyDictionary<string, double> pairScale,
        IReadOnlySet<string> disabledPairs,
        IReadOnlySet<string> protectedOwners,
        double widthMultiplier)
    {
        var (corridor, widths) = BuildAdaptiveCorridor(
            targetEdges, byId, baselineOwners, regionalLand,
            pairScale, disabledPairs, protectedOwners, widthMultiplier);

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
            },
            StringComparer.OrdinalIgnoreCase);

        return (corridor, ownerRegions, changes, widths);
    }

    static (Geometry Corridor, Dictionary<string, double> Widths) BuildAdaptiveCorridor(
        IReadOnlyList<Edge> edges,
        IReadOnlyDictionary<long, Cell> byId,
        IReadOnlyDictionary<string, Geometry> ownerRegions,
        Geometry regionalLand,
        IReadOnlyDictionary<string, double> pairScale,
        IReadOnlySet<string> disabledPairs,
        IReadOnlySet<string> protectedOwners,
        double widthMultiplier)
    {
        var parts = new List<Geometry>();
        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            var aCell = byId[edge.A];
            var bCell = byId[edge.B];
            var pairKey = PairKey(aCell.OwnerCode, bCell.OwnerCode);
            if (disabledPairs.Contains(pairKey))
                continue;

            var smallerArea = Math.Min(ownerRegions[aCell.OwnerCode].Area, ownerRegions[bCell.OwnerCode].Area);
            var requestedWidthKm = BaseWidthKm(smallerArea) * widthMultiplier;
            var scale = pairScale.TryGetValue(pairKey, out var value) ? value : 1.0;
            var widthKm = Math.Clamp(requestedWidthKm * scale, MinActiveCorridorKm, 25.0 * widthMultiplier);
            widths[pairKey] = widths.TryGetValue(pairKey, out var old) ? Math.Min(old, widthKm) : widthKm;

            var d = widthKm / 111.32;
            var overlap = SafeIntersection(
                SafeBuffer(aCell.Geometry.Boundary, d),
                SafeBuffer(bCell.Geometry.Boundary, d));
            if (!overlap.IsEmpty)
                parts.Add(overlap);
        }

        var corridor = SafeIntersection(SafeUnion(parts), regionalLand);
        var coastalGuard = SafeBuffer(regionalLand.Boundary, CoastalGuardKm / 111.32);
        corridor = SafeDifference(corridor, coastalGuard);

        if (protectedOwners.Count > 0)
        {
            // Local fallback only: keep a fully exhausted owner at its baseline shape without
            // disabling the remaining pairwise experiments elsewhere in the test region.
            var protectedGeometry = SafeUnion(
                protectedOwners
                    .Where(ownerRegions.ContainsKey)
                    .Select(code => ownerRegions[code]));
            corridor = SafeDifference(corridor, protectedGeometry);
        }

        return (corridor.IsValid ? corridor : corridor.Buffer(0), widths);
    }

    static string PairKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}-{b}" : $"{b}-{a}";

    static bool PairTouchesOwner(string pair, string owner) =>
        pair.StartsWith(owner + "-", StringComparison.OrdinalIgnoreCase) ||
        pair.EndsWith("-" + owner, StringComparison.OrdinalIgnoreCase);

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
            e.GetProperty("lat").GetDouble(),
            e.GetProperty("lon").GetDouble(),
            geometry);
    }

    static Dictionary<long, Geometry> BuildGlobalVoronoi(IReadOnlyList<Cell> cells, Envelope envelope)
    {
        var ids = new Dictionary<string, long>();
        var sites = new List<Coordinate>();
        foreach (var c in cells)
        {
            var p = new Coordinate(c.Lon, c.Lat);
            ids[Key(p)] = c.Id;
            sites.Add(p);
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

    static string Key(Coordinate c) => $"{Math.Round(c.X, 9):F9}|{Math.Round(c.Y, 9):F9}";

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

    static Geometry SafeSymDifference(Geometry a, Geometry b)
    {
        try
        {
            var result = a.SymmetricDifference(b);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            var result = OverlayNGRobust.Overlay(a, b, SpatialFunction.SymDifference);
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
