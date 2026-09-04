using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Triangulate;

static class HybridFrontierLab
{
    sealed record Cell(long Id, string Name, string TerritoryCode, string OwnerCode, double Lat, double Lon, Geometry Geometry);
    sealed record Edge(long A, long B, bool Foreign);

    static readonly GeometryFactory GeometryFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly HashSet<string> TargetTerritories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "FR", "BE", "LU", "DE", "CH", "IT", "ES"
        };

    static readonly int[] CorridorWidthsKm = { 10, 25, 50 };
    const double CoastalGuardKm = 3.0;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        if (!File.Exists(graphPath))
            throw new InvalidOperationException("Hybrid frontier lab requires voronoi-graph.json to be generated first.");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;

        var cells = root.GetProperty("cells").EnumerateArray()
            .Select(ParseCell)
            .ToArray();

        var edges = root.GetProperty("edges").EnumerateArray()
            .Select(e => new Edge(
                e.GetProperty("a").GetInt64(),
                e.GetProperty("b").GetInt64(),
                e.TryGetProperty("foreign", out var foreign) && foreign.GetBoolean()))
            .ToArray();

        var targetCells = cells
            .Where(c => TargetTerritories.Contains(c.TerritoryCode))
            .ToArray();

        var byId = targetCells.ToDictionary(c => c.Id);
        var targetEdges = edges
            .Where(e => e.Foreign && byId.ContainsKey(e.A) && byId.ContainsKey(e.B))
            .ToArray();

        if (targetCells.Length == 0 || targetEdges.Length == 0)
            throw new InvalidOperationException("Hybrid frontier lab target region is empty or has no foreign adjacencies.");

        var regionalLand = SafeUnion(targetCells.Select(c => c.Geometry));
        if (regionalLand.IsEmpty)
            throw new InvalidOperationException("Hybrid frontier lab could not reconstruct the regional land mask.");

        var globalVoronoi = BuildGlobalVoronoi(targetCells, regionalLand.EnvelopeInternal);
        var currentOwnerRegions = BuildOwnerRegions(targetCells, targetCells.ToDictionary(c => c.Id, c => c.Geometry));

        var variants = new Dictionary<string, object>();
        foreach (var widthKm in CorridorWidthsKm)
        {
            var corridor = BuildLandBorderCorridor(targetEdges, byId, regionalLand, widthKm, CoastalGuardKm);
            var hybridCells = new Dictionary<long, Geometry>();

            foreach (var cell in targetCells)
            {
                var outside = SafeDifference(cell.Geometry, corridor);
                var inside = globalVoronoi.TryGetValue(cell.Id, out var pureCell)
                    ? SafeIntersection(pureCell, corridor)
                    : GeometryFactory.CreatePolygon();

                var hybrid = SafeUnion(new[] { outside, inside });
                hybrid = SafeIntersection(hybrid, regionalLand);
                if (!hybrid.IsValid) hybrid = hybrid.Buffer(0);
                hybridCells[cell.Id] = hybrid;
            }

            var ownerRegions = BuildOwnerRegions(targetCells, hybridCells);
            var changes = ownerRegions.ToDictionary(
                x => x.Key,
                x =>
                {
                    var baseline = currentOwnerRegions[x.Key];
                    var diff = SafeSymDifference(baseline, x.Value);
                    return baseline.Area <= 0 ? 0 : diff.Area / baseline.Area;
                },
                StringComparer.OrdinalIgnoreCase);

            variants[$"hybrid{widthKm}"] = new
            {
                corridorKm = widthKm,
                coastalGuardKm = CoastalGuardKm,
                corridor = GeometryToGeoJson(corridor),
                ownerRegions = OwnerRegionPayload(ownerRegions),
                changedAreaRatioByOwner = changes
            };
        }

        var payload = new
        {
            status = "experimental",
            description = "Hybrid cellular political-frontier lab. Existing cells are preserved outside a land-border corridor; a global Voronoi partition is used only inside that corridor. Coastlines are protected by a coastal guard band.",
            targetTerritories = TargetTerritories.OrderBy(x => x).ToArray(),
            corridorWidthsKm = CorridorWidthsKm,
            coastalGuardKm = CoastalGuardKm,
            cellCount = targetCells.Length,
            foreignAdjacencyCount = targetEdges.Length,
            current = new
            {
                ownerRegions = OwnerRegionPayload(currentOwnerRegions)
            },
            variants,
            cities = targetCells.Select(c => new
            {
                id = c.Id,
                c.Name,
                territoryCode = c.TerritoryCode,
                ownerCode = c.OwnerCode,
                lat = c.Lat,
                lon = c.Lon
            }).ToArray()
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "hybrid-frontier-lab.json"),
            JsonSerializer.Serialize(payload));

        Console.WriteLine(
            $"Hybrid frontier lab: {targetCells.Length} cells, {targetEdges.Length} foreign adjacencies, " +
            $"corridors {string.Join("/", CorridorWidthsKm)} km, coastal guard {CoastalGuardKm:0.#} km.");
    }

    static Cell ParseCell(JsonElement element)
    {
        var geometry = ParseGeoJsonGeometry(element.GetProperty("geometry"))
            ?? throw new InvalidOperationException($"Missing geometry for cell {element.GetProperty("id").GetInt64()}.");

        return new Cell(
            element.GetProperty("id").GetInt64(),
            element.GetProperty("name").GetString() ?? "?",
            element.GetProperty("territoryCode").GetString() ?? element.GetProperty("country").GetString() ?? "?",
            element.GetProperty("ownerCode").GetString() ?? element.GetProperty("territoryCode").GetString() ?? "?",
            element.GetProperty("lat").GetDouble(),
            element.GetProperty("lon").GetDouble(),
            geometry);
    }

    static Dictionary<long, Geometry> BuildGlobalVoronoi(IReadOnlyList<Cell> cells, Envelope clipEnvelope)
    {
        var siteIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var sites = new List<Coordinate>(cells.Count);

        foreach (var cell in cells)
        {
            var coordinate = new Coordinate(cell.Lon, cell.Lat);
            var key = CoordinateKey(coordinate);
            if (!siteIds.TryAdd(key, cell.Id))
                throw new InvalidOperationException($"Duplicate Voronoi site coordinate in hybrid lab: {cell.Name} ({cell.Id}).");
            sites.Add(coordinate);
        }

        var builder = new VoronoiDiagramBuilder
        {
            ClipEnvelope = clipEnvelope,
            Tolerance = 0.0
        };
        builder.SetSites(sites);
        var diagram = builder.GetDiagram(GeometryFactory);

        var result = new Dictionary<long, Geometry>();
        for (var i = 0; i < diagram.NumGeometries; i++)
        {
            var face = diagram.GetGeometryN(i);
            if (face.UserData is not Coordinate site)
                continue;

            if (!siteIds.TryGetValue(CoordinateKey(site), out var id))
                continue;

            result[id] = face;
        }

        if (result.Count != cells.Count)
            throw new InvalidOperationException(
                $"Hybrid Voronoi site mapping incomplete: {result.Count}/{cells.Count} cells reconstructed.");

        return result;
    }

    static Geometry BuildLandBorderCorridor(
        IReadOnlyList<Edge> edges,
        IReadOnlyDictionary<long, Cell> byId,
        Geometry regionalLand,
        double widthKm,
        double coastalGuardKm)
    {
        var widthDegrees = widthKm / 111.32;
        var corridorParts = new List<Geometry>(edges.Count);

        foreach (var edge in edges)
        {
            var a = byId[edge.A].Geometry;
            var b = byId[edge.B].Geometry;

            var aBand = SafeBuffer(a.Boundary, widthDegrees);
            var bBand = SafeBuffer(b.Boundary, widthDegrees);
            var overlap = SafeIntersection(aBand, bBand);
            if (!overlap.IsEmpty)
                corridorParts.Add(overlap);
        }

        var corridor = SafeUnion(corridorParts);
        corridor = SafeIntersection(corridor, regionalLand);

        // Keep the coastline and the exact land/sea meeting point stable. The
        // experimental deformation is therefore prevented from reaching the
        // exterior boundary of the regional land mask.
        if (coastalGuardKm > 0)
        {
            var coastalGuard = SafeBuffer(regionalLand.Boundary, coastalGuardKm / 111.32);
            corridor = SafeDifference(corridor, coastalGuard);
        }

        if (!corridor.IsValid) corridor = corridor.Buffer(0);
        return corridor;
    }

    static Dictionary<string, Geometry> BuildOwnerRegions(
        IReadOnlyList<Cell> cells,
        IReadOnlyDictionary<long, Geometry> geometryById)
    {
        return cells
            .GroupBy(c => c.OwnerCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => SafeUnion(g.Select(c => geometryById[c.Id])),
                StringComparer.OrdinalIgnoreCase);
    }

    static object[] OwnerRegionPayload(IReadOnlyDictionary<string, Geometry> ownerRegions) =>
        ownerRegions
            .OrderBy(x => x.Key)
            .Select(x => (object)new
            {
                ownerCode = x.Key,
                geometry = GeometryToGeoJson(x.Value)
            })
            .ToArray();

    static string CoordinateKey(Coordinate c) =>
        $"{Math.Round(c.X, 9):F9}|{Math.Round(c.Y, 9):F9}";

    static Geometry SafeUnion(IEnumerable<Geometry> geometries)
    {
        var array = geometries.Where(g => g is not null && !g.IsEmpty).ToArray();
        if (array.Length == 0) return GeometryFactory.CreatePolygon();
        if (array.Length == 1) return array[0];

        try
        {
            var result = UnaryUnionOp.Union(array);
            return result.IsValid ? result : result.Buffer(0);
        }
        catch
        {
            var result = OverlayNGRobust.Union(array);
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
        if (!geometry.TryGetProperty("type", out var typeElement)) return null;
        if (!geometry.TryGetProperty("coordinates", out var coordinates)) return null;

        var type = typeElement.GetString();
        if (string.Equals(type, "Polygon", StringComparison.OrdinalIgnoreCase))
            return ParsePolygon(coordinates);

        if (string.Equals(type, "MultiPolygon", StringComparison.OrdinalIgnoreCase))
        {
            var polygons = coordinates.EnumerateArray()
                .Select(ParsePolygon)
                .Where(p => p is not null && !p.IsEmpty)
                .Cast<Polygon>()
                .ToArray();
            return GeometryFactory.CreateMultiPolygon(polygons);
        }

        return null;
    }

    static Polygon ParsePolygon(JsonElement polygonCoordinates)
    {
        var rings = polygonCoordinates.EnumerateArray()
            .Select(ParseRing)
            .Where(r => r is not null)
            .Cast<LinearRing>()
            .ToArray();

        if (rings.Length == 0) return GeometryFactory.CreatePolygon();
        return GeometryFactory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    static LinearRing? ParseRing(JsonElement ringCoordinates)
    {
        var coordinates = ringCoordinates.EnumerateArray()
            .Select(point =>
            {
                var xy = point.EnumerateArray().ToArray();
                return new Coordinate(xy[0].GetDouble(), xy[1].GetDouble());
            })
            .ToList();

        if (coordinates.Count < 3) return null;
        if (!coordinates[0].Equals2D(coordinates[^1]))
            coordinates.Add(new Coordinate(coordinates[0]));
        if (coordinates.Count < 4) return null;

        return GeometryFactory.CreateLinearRing(coordinates.ToArray());
    }

    static object? GeometryToGeoJson(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;

        object Coordinates(Coordinate[] coords) =>
            coords.Select(c => new[]
            {
                Math.Round(c.X, 5),
                Math.Round(c.Y, 5)
            }).ToArray();

        object PolygonCoordinates(Polygon polygon)
        {
            var rings = new List<object> { Coordinates(polygon.ExteriorRing.Coordinates) };
            for (var i = 0; i < polygon.NumInteriorRings; i++)
                rings.Add(Coordinates(polygon.GetInteriorRingN(i).Coordinates));
            return rings;
        }

        if (geometry is Polygon polygon)
            return new { type = "Polygon", coordinates = PolygonCoordinates(polygon) };

        if (geometry is MultiPolygon multi)
        {
            return new
            {
                type = "MultiPolygon",
                coordinates = Enumerable.Range(0, multi.NumGeometries)
                    .Select(i => PolygonCoordinates((Polygon)multi.GetGeometryN(i)))
                    .ToArray()
            };
        }

        var polygonParts = Enumerable.Range(0, geometry.NumGeometries)
            .Select(i => geometry.GetGeometryN(i))
            .OfType<Polygon>()
            .ToArray();

        if (polygonParts.Length == 1)
            return new { type = "Polygon", coordinates = PolygonCoordinates(polygonParts[0]) };

        if (polygonParts.Length > 1)
        {
            return new
            {
                type = "MultiPolygon",
                coordinates = polygonParts.Select(PolygonCoordinates).ToArray()
            };
        }

        return null;
    }
}
