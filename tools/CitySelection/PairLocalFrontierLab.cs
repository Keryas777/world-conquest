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

    static readonly GeometryFactory GeometryFactory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    static readonly HashSet<string> TargetTerritories = new(StringComparer.OrdinalIgnoreCase) { "FR", "BE", "LU", "DE", "CH", "IT", "ES" };
    const double WidthMultiplier = 1.50;
    const double CoastalGuardKm = 3.0;
    const double InvariantAreaTolerance = 1e-7;

    public static async Task GenerateAsync(string outDir)
    {
        var graphPath = Path.Combine(outDir, "voronoi-graph.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(graphPath));
        var root = document.RootElement;
        var cells = root.GetProperty("cells").EnumerateArray().Select(ParseCell).ToArray();
        var edges = root.GetProperty("edges").EnumerateArray().Select(e => new Edge(e.GetProperty("a").GetInt64(), e.GetProperty("b").GetInt64(), e.TryGetProperty("foreign", out var foreign) && foreign.GetBoolean())).ToArray();
        var targetCells = cells.Where(c => TargetTerritories.Contains(c.TerritoryCode)).ToArray();
        var byId = targetCells.ToDictionary(c => c.Id);
        var targetEdges = edges.Where(e => e.Foreign && byId.ContainsKey(e.A) && byId.ContainsKey(e.B)).ToArray();
        var regionalLand = SafeUnion(targetCells.Select(c => c.Geometry));
        var baselineByCell = targetCells.ToDictionary(c => c.Id, c => c.Geometry);
        var baselineOwners = BuildOwnerRegions(targetCells, baselineByCell);
        var ownerRegions = baselineOwners.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        Geometry usedCorridor = GeometryFactory.CreatePolygon();
        var pairMetrics = new List<object>();
        var pairKeys = targetEdges.Select(e => PairKey(byId[e.A].OwnerCode, byId[e.B].OwnerCode)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var pairKey in pairKeys)
        {
            var (ownerA, ownerB) = SplitPair(pairKey);
            if (!baselineOwners.ContainsKey(ownerA) || !baselineOwners.ContainsKey(ownerB)) continue;
            var pairEdges = targetEdges.Where(e => PairKey(byId[e.A].OwnerCode, byId[e.B].OwnerCode).Equals(pairKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            var widthKm = Math.Clamp(BaseWidthKm(Math.Min(baselineOwners[ownerA].Area, baselineOwners[ownerB].Area)) * WidthMultiplier, 0.05, 25.0 * WidthMultiplier);
            var corridor = BuildPairCorridor(pairEdges, byId, regionalLand, widthKm);
            var exclusiveCorridor = SafeDifference(corridor, usedCorridor);
            usedCorridor = SafeUnion(new[] { usedCorridor, exclusiveCorridor });
            if (exclusiveCorridor.IsEmpty) continue;
            var pairBaseline = SafeUnion(new[] { baselineOwners[ownerA], baselineOwners[ownerB] });
            var pairDomain = SafeIntersection(exclusiveCorridor, pairBaseline);
            if (pairDomain.IsEmpty) continue;
            var influenceKm = Math.Max(75.0, widthKm * 3.0);
            var influenceZone = SafeBuffer(pairDomain, influenceKm / 111.32);
            var pairCells = targetCells.Where(c => c.OwnerCode.Equals(ownerA, StringComparison.OrdinalIgnoreCase) || c.OwnerCode.Equals(ownerB, StringComparison.OrdinalIgnoreCase)).Where(c => !SafeIntersection(c.Geometry, influenceZone).IsEmpty).ToArray();
            foreach (var owner in new[] { ownerA, ownerB })
            {
                if (pairCells.Any(c => c.OwnerCode.Equals(owner, StringComparison.OrdinalIgnoreCase))) continue;
                var nearest = targetCells.Where(c => c.OwnerCode.Equals(owner, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Geometry.Distance(pairDomain)).ThenBy(c => c.Id).FirstOrDefault();
                if (nearest is not null) pairCells = pairCells.Append(nearest).ToArray();
            }
            Console.WriteLine($"C3 pair {pairKey}: {pairCells.Length} local candidate sites, width={widthKm:F1} km, influence={influenceKm:F1} km.");
            var pairVoronoi = BuildVoronoi(pairCells, pairDomain.EnvelopeInternal);
            Geometry AssignedTo(string owner) => SafeUnion(pairCells.Where(c => c.OwnerCode.Equals(owner, StringComparison.OrdinalIgnoreCase)).Select(c => pairVoronoi.TryGetValue(c.Id, out var face) ? SafeIntersection(face, pairDomain) : GeometryFactory.CreatePolygon()));

            // Canonicalize the pair domain into an exact two-owner partition. Numerical
            // overlay noise must not create slivers shared by both owners.
            var insideA = AssignedTo(ownerA);
            var insideB = SafeDifference(pairDomain, insideA);
            var assigned = SafeUnion(new[] { insideA, insideB });
            var localGap = SafeDifference(pairDomain, assigned);
            var localOverlap = SafeIntersection(insideA, insideB);

            // Recompose only the domain that is actually contested by this pair. The old
            // implementation removed the whole corridor, even outside A∪B, which could cut
            // unrelated pieces. Reject any pair update that would create extra polygon
            // components for either owner; leaving that pair on its current geometry is a
            // deliberate local fallback, not a global owner reset.
            var candidateA = SafeUnion(new[] { SafeDifference(ownerRegions[ownerA], pairDomain), insideA });
            var candidateB = SafeUnion(new[] { SafeDifference(ownerRegions[ownerB], pairDomain), insideB });
            var baselineComponentsA = PolygonComponentCount(baselineOwners[ownerA]);
            var baselineComponentsB = PolygonComponentCount(baselineOwners[ownerB]);
            var candidateComponentsA = PolygonComponentCount(candidateA);
            var candidateComponentsB = PolygonComponentCount(candidateB);
            var topologyRejected = candidateComponentsA > baselineComponentsA || candidateComponentsB > baselineComponentsB;

            if (!topologyRejected)
            {
                ownerRegions[ownerA] = candidateA;
                ownerRegions[ownerB] = candidateB;
            }
            else
            {
                Console.WriteLine($"C3 pair {pairKey}: topology guard rejected update ({ownerA} {candidateComponentsA}/{baselineComponentsA}, {ownerB} {candidateComponentsB}/{baselineComponentsB} components).");
            }

            pairMetrics.Add(new { pair = pairKey, widthKm, influenceKm, candidateSiteCount = pairCells.Length, corridorArea = exclusiveCorridor.Area, localGapArea = localGap.Area, localOverlapArea = localOverlap.Area, topologyRejected, candidateComponents = new { ownerA = candidateComponentsA, ownerB = candidateComponentsB }, competingOwners = new[] { ownerA, ownerB } });
        }

        var finalUnion = SafeUnion(ownerRegions.Values);
        var gap = SafeDifference(regionalLand, finalUnion);
        var outside = SafeDifference(finalUnion, regionalLand);
        var overlapArea = PairwiseOverlapArea(ownerRegions);
        var topology = ownerRegions.OrderBy(x => x.Key).Select(x => { var baselineCount = PolygonComponentCount(baselineOwners[x.Key]); var finalCount = PolygonComponentCount(x.Value); return new { ownerCode = x.Key, baselineComponents = baselineCount, finalComponents = finalCount, newComponents = Math.Max(0, finalCount - baselineCount) }; }).ToArray();
        var coverageSatisfied = gap.Area <= InvariantAreaTolerance && outside.Area <= InvariantAreaTolerance;
        var overlapSatisfied = overlapArea <= InvariantAreaTolerance;
        var topologySatisfied = topology.All(x => x.newComponents == 0);
        var invariantsSatisfied = coverageSatisfied && overlapSatisfied && topologySatisfied;
        var payload = new { status = "experimental", description = "C3 pair-local frontier experiment. Each terrestrial owner-pair keeps an exclusive corridor and only nearby cities owned by that pair may compete inside it. Pair corridors are made mutually exclusive deterministically before recomposition, and pair updates that would create extra owner components are rejected locally.", widthMultiplier = WidthMultiplier, coastalGuardKm = CoastalGuardKm, targetTerritories = TargetTerritories.OrderBy(x => x).ToArray(), cellCount = targetCells.Length, foreignAdjacencyCount = targetEdges.Length, pairCount = pairKeys.Length, current = new { ownerRegions = OwnerRegionPayload(baselineOwners) }, c3 = new { ownerRegions = OwnerRegionPayload(ownerRegions), corridor = GeometryToGeoJson(usedCorridor), pairMetrics, invariants = new { satisfied = invariantsSatisfied, coverageSatisfied, overlapSatisfied, topologySatisfied, gapArea = gap.Area, outsideArea = outside.Area, overlapArea, topology } }, cities = targetCells.Select(c => new { id = c.Id, territoryCode = c.TerritoryCode, ownerCode = c.OwnerCode, lat = c.Lat, lon = c.Lon }).ToArray() };
        await File.WriteAllTextAsync(Path.Combine(outDir, "c3-pair-local-frontier-lab.json"), JsonSerializer.Serialize(payload));
        Console.WriteLine($"C3 pair-local: pairs={pairKeys.Length}, gap={gap.Area:F8}, overlap={overlapArea:F8}, new-components={topology.Sum(x => x.newComponents)}, rejected-pairs={pairMetrics.Count(x => (bool)x.GetType().GetProperty("topologyRejected")!.GetValue(x)! )}, invariants={(invariantsSatisfied ? "ok" : "FAILED")}.");
        if (!invariantsSatisfied) throw new InvalidOperationException($"C3 geometry invariants failed: coverage={coverageSatisfied}, overlap={overlapSatisfied}, topology={topologySatisfied}.");
    }

    static Geometry BuildPairCorridor(IReadOnlyList<Edge> edges, IReadOnlyDictionary<long, Cell> byId, Geometry regionalLand, double widthKm)
    {
        var parts = new List<Geometry>(); var d = widthKm / 111.32;
        foreach (var edge in edges) { var a = byId[edge.A].Geometry; var b = byId[edge.B].Geometry; var overlap = SafeIntersection(SafeBuffer(a.Boundary, d), SafeBuffer(b.Boundary, d)); if (!overlap.IsEmpty) parts.Add(overlap); }
        var corridor = SafeIntersection(SafeUnion(parts), regionalLand); var coastalGuard = SafeBuffer(regionalLand.Boundary, CoastalGuardKm / 111.32); return SafeDifference(corridor, coastalGuard);
    }

    static Dictionary<long, Geometry> BuildVoronoi(IReadOnlyList<Cell> cells, Envelope envelope)
    {
        if (cells.Count == 0) return new();
        var snapGrids = new[] { 0.0, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4 };
        foreach (var grid in snapGrids)
        {
            var representatives = new Dictionary<string, Cell>(StringComparer.Ordinal); var snappedById = new Dictionary<long, Coordinate>();
            foreach (var cell in cells.OrderBy(c => c.Id)) { var point = grid <= 0 ? new Coordinate(cell.Lon, cell.Lat) : new Coordinate(Snap(cell.Lon, grid), Snap(cell.Lat, grid)); if (representatives.TryAdd(Key(point), cell)) snappedById[cell.Id] = point; }
            var ids = new Dictionary<string, long>(StringComparer.Ordinal); var sites = new List<Coordinate>();
            foreach (var cell in representatives.Values.OrderBy(c => c.Id)) { var point = snappedById[cell.Id]; ids[Key(point)] = cell.Id; sites.Add(point); }
            if (sites.Count == 1) return new() { [representatives.Values.Single().Id] = GeometryFactory.ToGeometry(envelope) };
            try
            {
                var builder = new VoronoiDiagramBuilder { ClipEnvelope = envelope, Tolerance = grid <= 0 ? 0.0 : grid }; builder.SetSites(sites); var diagram = builder.GetDiagram(GeometryFactory); var result = new Dictionary<long, Geometry>();
                for (var i = 0; i < diagram.NumGeometries; i++) { var face = diagram.GetGeometryN(i); if (face.UserData is Coordinate site && ids.TryGetValue(Key(site), out var id)) result[id] = face; }
                if (grid > 0) Console.WriteLine($"C3 Voronoi recovered on {grid:G}° snap grid ({sites.Count} sites from {cells.Count} local candidates).");
                return result;
            }
            catch (NetTopologySuite.Triangulate.QuadEdge.LocateFailureException) { }
        }
        Console.WriteLine($"C3 Voronoi: NTS Delaunay failed for {cells.Count} sites; using deterministic half-plane fallback.");
        return BuildVoronoiByHalfPlanes(cells, envelope);
    }

    static Dictionary<long, Geometry> BuildVoronoiByHalfPlanes(IReadOnlyList<Cell> cells, Envelope envelope)
    {
        var margin = Math.Max(envelope.Width, envelope.Height) + 1.0;
        var clip = new Envelope(envelope); clip.ExpandBy(margin);
        var result = new Dictionary<long, Geometry>();
        foreach (var site in cells.OrderBy(c => c.Id))
        {
            var polygon = new List<Coordinate> { new(clip.MinX, clip.MinY), new(clip.MaxX, clip.MinY), new(clip.MaxX, clip.MaxY), new(clip.MinX, clip.MaxY) };
            foreach (var other in cells.OrderBy(c => c.Id))
            {
                if (other.Id == site.Id) continue;
                var dx = other.Lon - site.Lon; var dy = other.Lat - site.Lat;
                if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) continue;
                var mx = (site.Lon + other.Lon) * 0.5; var my = (site.Lat + other.Lat) * 0.5;
                polygon = ClipHalfPlane(polygon, dx, dy, dx * mx + dy * my);
                if (polygon.Count < 3) break;
            }
            if (polygon.Count >= 3)
            {
                var ring = polygon.Select(p => new Coordinate(p)).ToList(); ring.Add(new Coordinate(ring[0]));
                result[site.Id] = GeometryFactory.CreatePolygon(GeometryFactory.CreateLinearRing(ring.ToArray()));
            }
        }
        return result;
    }

    static List<Coordinate> ClipHalfPlane(IReadOnlyList<Coordinate> input, double a, double b, double c)
    {
        var output = new List<Coordinate>(); if (input.Count == 0) return output;
        bool Inside(Coordinate p) => a * p.X + b * p.Y <= c + 1e-12;
        Coordinate Intersect(Coordinate p, Coordinate q) { var vx = q.X - p.X; var vy = q.Y - p.Y; var denom = a * vx + b * vy; if (Math.Abs(denom) < 1e-15) return new Coordinate(p); var t = (c - a * p.X - b * p.Y) / denom; return new Coordinate(p.X + t * vx, p.Y + t * vy); }
        var previous = input[^1]; var previousInside = Inside(previous);
        foreach (var current in input) { var currentInside = Inside(current); if (currentInside) { if (!previousInside) output.Add(Intersect(previous, current)); output.Add(new Coordinate(current)); } else if (previousInside) output.Add(Intersect(previous, current)); previous = current; previousInside = currentInside; }
        return output;
    }

    static double Snap(double value, double grid) => Math.Round(value / grid) * grid;
    static double PairwiseOverlapArea(IReadOnlyDictionary<string, Geometry> owners) { var values = owners.OrderBy(x => x.Key).ToArray(); var area = 0.0; for (var i = 0; i < values.Length; i++) for (var j = i + 1; j < values.Length; j++) area += SafeIntersection(values[i].Value, values[j].Value).Area; return area; }
    static int PolygonComponentCount(Geometry geometry) { if (geometry.IsEmpty) return 0; if (geometry is Polygon) return 1; if (geometry is MultiPolygon multi) return multi.NumGeometries; return Enumerable.Range(0, geometry.NumGeometries).Count(i => geometry.GetGeometryN(i) is Polygon); }
    static string PairKey(string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? $"{a}-{b}" : $"{b}-{a}";
    static (string A, string B) SplitPair(string pair) { var separator = pair.IndexOf('-'); return (pair[..separator], pair[(separator + 1)..]); }
    static double BaseWidthKm(double smallerOwnerAreaDegrees2) { if (smallerOwnerAreaDegrees2 < 0.5) return 5; if (smallerOwnerAreaDegrees2 < 2.0) return 10; if (smallerOwnerAreaDegrees2 < 8.0) return 15; if (smallerOwnerAreaDegrees2 < 20.0) return 20; return 25; }
    static string Key(Coordinate c) => $"{Math.Round(c.X, 9):F9}|{Math.Round(c.Y, 9):F9}";
    static Cell ParseCell(JsonElement e) { var geometry = ParseGeoJsonGeometry(e.GetProperty("geometry")) ?? GeometryFactory.CreatePolygon(); return new Cell(e.GetProperty("id").GetInt64(), e.GetProperty("territoryCode").GetString() ?? "?", e.GetProperty("ownerCode").GetString() ?? "?", e.GetProperty("lat").GetDouble(), e.GetProperty("lon").GetDouble(), geometry); }
    static Dictionary<string, Geometry> BuildOwnerRegions(IReadOnlyList<Cell> cells, IReadOnlyDictionary<long, Geometry> geometryById) => cells.GroupBy(c => c.OwnerCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => SafeUnion(g.Select(c => geometryById[c.Id])), StringComparer.OrdinalIgnoreCase);
    static object[] OwnerRegionPayload(IReadOnlyDictionary<string, Geometry> owners) => owners.OrderBy(x => x.Key).Select(x => (object)new { ownerCode = x.Key, geometry = GeometryToGeoJson(x.Value) }).ToArray();
    static Geometry SafeUnion(IEnumerable<Geometry> geometries) { var values = geometries.Where(g => !g.IsEmpty).ToArray(); if (values.Length == 0) return GeometryFactory.CreatePolygon(); if (values.Length == 1) return values[0]; try { var r = UnaryUnionOp.Union(values); return r.IsValid ? r : r.Buffer(0); } catch { var r = OverlayNGRobust.Union(values); return r.IsValid ? r : r.Buffer(0); } }
    static Geometry SafeIntersection(Geometry a, Geometry b) { if (a.IsEmpty || b.IsEmpty) return GeometryFactory.CreatePolygon(); try { var r = a.Intersection(b); return r.IsValid ? r : r.Buffer(0); } catch { var r = OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection); return r.IsValid ? r : r.Buffer(0); } }
    static Geometry SafeDifference(Geometry a, Geometry b) { if (a.IsEmpty) return GeometryFactory.CreatePolygon(); if (b.IsEmpty) return a; try { var r = a.Difference(b); return r.IsValid ? r : r.Buffer(0); } catch { var r = OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference); return r.IsValid ? r : r.Buffer(0); } }
    static Geometry SafeBuffer(Geometry geometry, double distance) { try { var r = geometry.Buffer(distance); return r.IsValid ? r : r.Buffer(0); } catch { var r = geometry.Buffer(0).Buffer(distance); return r.IsValid ? r : r.Buffer(0); } }
    static Geometry? ParseGeoJsonGeometry(JsonElement geometry) { if (geometry.ValueKind == JsonValueKind.Null) return null; var type = geometry.GetProperty("type").GetString(); var coordinates = geometry.GetProperty("coordinates"); if (type == "Polygon") return ParsePolygon(coordinates); if (type == "MultiPolygon") return GeometryFactory.CreateMultiPolygon(coordinates.EnumerateArray().Select(ParsePolygon).ToArray()); return null; }
    static Polygon ParsePolygon(JsonElement coordinates) { var rings = coordinates.EnumerateArray().Select(ParseRing).Where(x => x is not null).Cast<LinearRing>().ToArray(); return rings.Length == 0 ? GeometryFactory.CreatePolygon() : GeometryFactory.CreatePolygon(rings[0], rings.Skip(1).ToArray()); }
    static LinearRing? ParseRing(JsonElement ring) { var points = ring.EnumerateArray().Select(p => { var xy = p.EnumerateArray().ToArray(); return new Coordinate(xy[0].GetDouble(), xy[1].GetDouble()); }).ToList(); if (points.Count < 3) return null; if (!points[0].Equals2D(points[^1])) points.Add(new Coordinate(points[0])); return points.Count < 4 ? null : GeometryFactory.CreateLinearRing(points.ToArray()); }
    static object? GeometryToGeoJson(Geometry geometry) { if (geometry.IsEmpty) return null; object Coordinates(Coordinate[] coordinates) => coordinates.Select(p => new[] { Math.Round(p.X, 5), Math.Round(p.Y, 5) }).ToArray(); object PolygonCoordinates(Polygon polygon) { var rings = new List<object> { Coordinates(polygon.ExteriorRing.Coordinates) }; for (var i = 0; i < polygon.NumInteriorRings; i++) rings.Add(Coordinates(polygon.GetInteriorRingN(i).Coordinates)); return rings; } if (geometry is Polygon polygon) return new { type = "Polygon", coordinates = PolygonCoordinates(polygon) }; if (geometry is MultiPolygon multiPolygon) return new { type = "MultiPolygon", coordinates = Enumerable.Range(0, multiPolygon.NumGeometries).Select(i => PolygonCoordinates((Polygon)multiPolygon.GetGeometryN(i))).ToArray() }; var polygons = Enumerable.Range(0, geometry.NumGeometries).Select(i => geometry.GetGeometryN(i)).OfType<Polygon>().ToArray(); if (polygons.Length == 1) return new { type = "Polygon", coordinates = PolygonCoordinates(polygons[0]) }; if (polygons.Length > 1) return new { type = "MultiPolygon", coordinates = polygons.Select(PolygonCoordinates).ToArray() }; return null; }
}
