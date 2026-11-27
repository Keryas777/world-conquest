using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Noding;
using NetTopologySuite.Operation.Distance;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Operation.Valid;
using NetTopologySuite.Precision;

static partial class GlobalFrontierLab
{
    private static long _robustIntersectionFallbackCount;
    private static long _robustDifferenceFallbackCount;
    private static long _precisionReducedOverlayFallbackCount;

    private sealed record TerritorySource(string Code, string Owner, Cell[] Cells, Geometry Geometry);
    private sealed record TerritoryBorder(string Key, string A, string B, string OwnerA, string OwnerB,
        LineString[] SourceLines);
    private sealed record TerritoryOverlap(string A, string B, double AreaKm2);
    private sealed record PolygonizationResult(Dictionary<string, Geometry> Territories,
        int FaceCount, int UnassignedFaceCount, int ExcludedWaterFaceCount,
        string[] MissingTerritories, string[] InvalidTerritories, bool FullUnionNodingFallback = false)
    {
        public int MissingTerritoryCount => MissingTerritories.Length;
        public int InvalidTerritoryCount => InvalidTerritories.Length;
    }
    private sealed record CellBuild(Cell Cell, Geometry Geometry, double SourceAreaKm2,
        double RebuiltAreaKm2, double GainedAreaKm2, double LostAreaKm2,
        bool SourceSeedCovered, bool RebuiltSeedCovered, double SeedDistanceMeters,
        bool LocalFallback, bool GeometryRepaired, bool Valid);

    private static void RunReconstructionSelfTests()
    {
        Polygon Rect(double x0, double y0, double x1, double y1) => Factory.CreatePolygon(new[]
        {
            new Coordinate(x0, y0), new Coordinate(x1, y0), new Coordinate(x1, y1),
            new Coordinate(x0, y1), new Coordinate(x0, y0)
        });
        var leftCell = new Cell(1, "AA", "AA", Rect(0, 0, 2, 2), "left", "AA", 1, 1, 1);
        var rightCell = new Cell(2, "BB", "BB", Rect(2, 0, 4, 2), "right", "BB", 1, 3, 1);
        var testLine = Factory.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(1, 1) });
        var testPoint = Factory.CreatePoint(new Coordinate(0, 0));
        if (SphericalAreaKm2(testLine) != 0 || SphericalAreaKm2(testPoint) != 0 ||
            PolygonComponentCount(testLine) != 0 || PolygonHoleCount(testLine) != 0 ||
            GeometryToGeoJsonObject(testLine) is not null)
            throw new InvalidOperationException("Reconstruction self-test failed: non-polygon geometries must terminate safely and report no polygon area/components.");
        var source = new Dictionary<string, TerritorySource>(StringComparer.Ordinal)
        {
            ["AA"] = new("AA", "AA", new[] { leftCell }, leftCell.Geometry),
            ["BB"] = new("BB", "BB", new[] { rightCell }, rightCell.Geometry)
        };
        var borders = BuildTerritoryBorders(source);
        if (borders.Count != 1 || borders[0].Key != "AA-BB")
            throw new InvalidOperationException("Reconstruction self-test failed: a shared territorial edge was not detected exactly once.");
        var land = UnaryUnionOp.Union(source.Values.Select(value => value.Geometry));
        var coast = EnumerateLines(land.Boundary).ToArray();
        foreach (var method in Methods)
        {
            var lineMap = borders.ToDictionary(border => border.Key, border => border.SourceLines, StringComparer.Ordinal);
            var first = BuildTerritoriesForMethod(method.Key, borders, lineMap, new HashSet<string>(), coast, source);
            var second = BuildTerritoriesForMethod(method.Key, borders, lineMap, new HashSet<string>(), coast, source);
            if (first.MissingTerritoryCount != 0 || first.InvalidTerritoryCount != 0 || first.UnassignedFaceCount != 0 ||
                GetTerritoryAdjacencies(first.Territories).Count != 1 ||
                first.FullUnionNodingFallback || second.FullUnionNodingFallback ||
                source.Keys.Any(code => !first.Territories[code].EqualsTopologically(second.Territories[code])))
                throw new InvalidOperationException($"Reconstruction self-test failed: {method.Key} did not preserve shared topology and deterministic faces.");
        }

        var corner = new Dictionary<string, TerritorySource>(StringComparer.Ordinal)
        {
            ["AA"] = new("AA", "AA", Array.Empty<Cell>(), Rect(0, 0, 1, 1)),
            ["BB"] = new("BB", "BB", Array.Empty<Cell>(), Rect(1, 1, 2, 2))
        };
        if (BuildTerritoryBorders(corner).Count != 0)
            throw new InvalidOperationException("Reconstruction self-test failed: point-only contact was counted as a shared border.");

        var testCities = new Dictionary<string, List<City>>(StringComparer.Ordinal)
        {
            ["AA"] = new()
            {
                new City(11, "west", "AA", .5, .5, 1),
                new City(12, "east", "AA", .5, 1.5, 1)
            }
        };
        var testRawCells = VoronoiLab.BuildRawWorldCells(testCities,
            new Dictionary<string, Geometry>(StringComparer.Ordinal) { ["AA"] = Rect(0, 0, 2, 1) });
        if (testRawCells.Count != 2 || testCities["AA"].Any(city =>
                !testRawCells[city.Id].Covers(Factory.CreatePoint(new Coordinate(city.Lon, city.Lat)))))
            throw new InvalidOperationException("Reconstruction self-test failed: Delaunay-backed Voronoi cells did not retain both seed cities.");

        var microSegmentA = Factory.CreatePolygon(new[]
        {
            new Coordinate(9.4, 4.0),
            new Coordinate(9.45289060135816, 4.07696919910027),
            new Coordinate(9.45289060135816, 4.076969199100268),
            new Coordinate(9.6, 4.2), new Coordinate(9.6, 4.5),
            new Coordinate(9.4, 4.5), new Coordinate(9.4, 4.0)
        });
        var microSegmentB = Factory.CreatePolygon(new[]
        {
            new Coordinate(9.45, 4.05), new Coordinate(9.7, 4.05),
            new Coordinate(9.7, 4.3), new Coordinate(9.45, 4.3), new Coordinate(9.45, 4.05)
        });
        var robustDifference = OverlayAfterPrecisionReduction(microSegmentA, microSegmentB, SpatialFunction.Difference);
        if (robustDifference.IsEmpty || !robustDifference.IsValid)
            throw new InvalidOperationException("Reconstruction self-test failed: a near-zero edge difference was not resolved as a valid polygon.");

        var crossingLines = Factory.CreateGeometryCollection(new Geometry[]
        {
            Factory.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(2, 0), new Coordinate(2, 2), new Coordinate(0, 2), new Coordinate(0, 0) }),
            Factory.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(2, 2) }),
            Factory.CreateLineString(new[] { new Coordinate(0, 2), new Coordinate(2, 0) })
        });
        var nodedCrossing = NodeLinework(crossingLines);
        var crossingPolygonizer = new Polygonizer();
        crossingPolygonizer.Add(nodedCrossing);
        if (crossingPolygonizer.GetPolygons().Count != 4)
            throw new InvalidOperationException("Reconstruction self-test failed: indexed line noding did not split crossing boundaries into four valid faces.");
    }

    private static async Task GenerateReconstructionAsync(string outDir, Cell[] cells, Edge[] edges)
    {
        var timer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();
        Console.WriteLine("Global reconstruction: building canonical territory masks from the published cells.");
        var territories = cells
            .GroupBy(cell => cell.Territory, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var members = group.OrderBy(cell => cell.Id).ToArray();
                Geometry geometry;
                try { geometry = UnaryUnionOp.Union(members.Select(cell => cell.Geometry)); }
                catch { geometry = UnaryUnionOp.Union(members.Select(cell => cell.Geometry.Buffer(0))); }
                if (!geometry.IsValid) geometry = geometry.Buffer(0);
                return new TerritorySource(group.Key, members[0].Owner, members, geometry);
            }, StringComparer.Ordinal);
        Console.WriteLine($"Global reconstruction phase: canonical territory masks={territories.Count}, {phaseTimer.Elapsed.TotalSeconds:F1}s.");
        phaseTimer.Restart();

        var selectedCities = territories.ToDictionary(item => item.Key,
            item => item.Value.Cells.Select(cell => new City(cell.Id, cell.Name, cell.Country,
                cell.Latitude, cell.Longitude, cell.Population)).ToList(), StringComparer.Ordinal);
        var rawVoronoiCells = VoronoiLab.BuildRawWorldCells(selectedCities,
            territories.ToDictionary(item => item.Key, item => item.Value.Geometry, StringComparer.Ordinal));
        if (rawVoronoiCells.Count != cells.Length)
            throw new InvalidOperationException($"Raw Voronoi reconstruction returned {rawVoronoiCells.Count} cells for {cells.Length} source cities.");
        Console.WriteLine($"Global reconstruction phase: raw territory-local Voronoi cells={rawVoronoiCells.Count}, {phaseTimer.Elapsed.TotalSeconds:F1}s.");
        phaseTimer.Restart();

        var land = UnaryUnionOp.Union(territories.Values.Select(value => value.Geometry));
        if (!land.IsValid) land = land.Buffer(0);
        var borders = BuildTerritoryBorders(territories);
        var sourceAdjacencies = borders.Select(border => border.Key).ToHashSet(StringComparer.Ordinal);
        var coast = EnumerateLines(land.Boundary).Where(line => line.NumPoints >= 2).ToArray();
        var directBorderKeys = borders.Select(border => border.Key).ToHashSet(StringComparer.Ordinal);

        var sourceCellAreaSum = cells.Sum(cell => SphericalAreaKm2(cell.Geometry));
        var sourceCellUnionArea = SphericalAreaKm2(land);
        var sourceTerritoryAreaSum = territories.Values.Sum(value => SphericalAreaKm2(value.Geometry));
        var sourceValidCells = cells.Count(cell => cell.Geometry.IsValid);
        var sourceEmptyCells = cells.Count(cell => cell.Geometry.IsEmpty);
        var sourceEmptyTerritories = territories.Values.Count(value => value.Geometry.IsEmpty);
        var sourceInvalidTerritories = territories.Values.Count(value => !value.Geometry.IsValid);
        var sourceInvalidCellDetails = cells.Where(cell => !cell.Geometry.IsValid || cell.Geometry.IsEmpty)
            .OrderBy(cell => cell.Id)
            .Select(cell =>
            {
                var validation = cell.Geometry.IsEmpty ? null : new IsValidOp(cell.Geometry).ValidationError;
                var coordinate = validation?.Coordinate;
                return new
                {
                    id = cell.Id,
                    name = cell.Name,
                    territory = cell.Territory,
                    owner = cell.Owner,
                    empty = cell.Geometry.IsEmpty,
                    invalid = !cell.Geometry.IsValid,
                    geometryType = cell.Geometry.GeometryType,
                    validityError = validation?.Message,
                    validityErrorType = validation?.ErrorType.ToString(),
                    validityErrorCoordinate = coordinate is null ? null : new[] { coordinate.X, coordinate.Y }
                };
            })
            .ToArray();
        var sourceSeedAudit = cells.OrderBy(cell => cell.Id).Select(cell =>
        {
            var seed = Factory.CreatePoint(new Coordinate(cell.Longitude, cell.Latitude));
            return new
            {
                cell.Id,
                cell.Name,
                territory = cell.Territory,
                owner = cell.Owner,
                latitude = cell.Latitude,
                longitude = cell.Longitude,
                seedInsideTerritory = territories[cell.Territory].Geometry.Covers(seed),
                seedCoveredByCell = cell.Geometry.Covers(seed)
            };
        }).ToArray();
        var sourceSeedAnomalies = sourceSeedAudit
            .Where(item => !item.seedInsideTerritory || !item.seedCoveredByCell)
            .ToArray();
        var sourceTerritoryOverlaps = BuildTerritoryOverlapDiagnostics(territories.ToDictionary(
            item => item.Key, item => item.Value.Geometry, StringComparer.Ordinal));
        var edgeDiagnostics = BuildMissingCellEdgeDiagnostics(cells, edges, territories, directBorderKeys);
        Console.WriteLine($"Global reconstruction phase: land, shared borders and edge diagnostics={borders.Count} borders / {edgeDiagnostics.Length} missing direct cell edges, {phaseTimer.Elapsed.TotalSeconds:F1}s.");
        var graphDigest = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(outDir, "voronoi-graph.json")))).ToLowerInvariant();
        var matchesPreviouslyAuditedGraph = graphDigest == "1a472b806de5fcc68df5271da2e1f4c477fc2657bee7eae94574fe14f19278c7";

        var methodSummaries = new Dictionary<string, object>(StringComparer.Ordinal);
        var reconstructionMethods = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var method in Methods.OrderBy(method => method.Key switch
                 {
                     "adaptive" => 0,
                     "ref40" => 1,
                     _ => 2
                 }))
        {
            var methodTimer = Stopwatch.StartNew();
            var robustIntersectionStart = System.Threading.Interlocked.Read(ref _robustIntersectionFallbackCount);
            var robustDifferenceStart = System.Threading.Interlocked.Read(ref _robustDifferenceFallbackCount);
            var precisionReducedStart = System.Threading.Interlocked.Read(ref _precisionReducedOverlayFallbackCount);
            var sourceLines = borders.ToDictionary(border => border.Key, border => border.SourceLines,
                StringComparer.Ordinal);
            var restoredBorders = new HashSet<string>(StringComparer.Ordinal);
            var locallyRestoredBorders = new HashSet<string>(StringComparer.Ordinal);
            var build = BuildTerritoriesForMethod(method.Key, borders, sourceLines, restoredBorders, coast, territories);
            var changed = ChangedTerritoryAdjacencies(sourceAdjacencies, GetTerritoryAdjacencies(build.Territories));
            var initialPolygonization = build;
            var initialAdjacencyChanges = changed.ToArray();
            PolygonizationResult? recoveryPolygonization = null;
            string[] recoveryAdjacencyChanges = Array.Empty<string>();

            // First attempt a local source-contour fallback around territories whose simplified
            // linework loses a face or changes an adjacency. Shared border linework is restored
            // once for both territories, never independently per polygon.
            var affectedTerritories = changed.SelectMany(pair => new[] { PairA(pair), PairB(pair) })
                .Concat(build.MissingTerritories).Concat(build.InvalidTerritories)
                .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var globalSourceFallback = false;
            if (affectedTerritories.Count > 0)
            {
                foreach (var border in borders.Where(border => affectedTerritories.Contains(border.A) || affectedTerritories.Contains(border.B)))
                    if (restoredBorders.Add(border.Key))
                    {
                        locallyRestoredBorders.Add(border.Key);
                        sourceLines[border.Key] = border.SourceLines;
                    }
                if (restoredBorders.Count == borders.Count)
                {
                    // A retry with every original border would have no simplified edge left to
                    // test. Keep the source masks directly and record this as a world fallback.
                    globalSourceFallback = true;
                    build = new PolygonizationResult(
                        territories.ToDictionary(item => item.Key, item => item.Value.Geometry, StringComparer.Ordinal),
                        build.FaceCount, build.UnassignedFaceCount, build.ExcludedWaterFaceCount,
                        Array.Empty<string>(), Array.Empty<string>());
                    changed = Array.Empty<string>();
                }
                else
                {
                    // Recheck after restoring shared source contours around affected territories.
                    // Keep the retry bounded: the world-wide robust union proved prohibitively slow
                    // on this graph, so an incomplete local result falls back to the complete source
                    // mask network below.
                    build = BuildTerritoriesForMethod(method.Key, borders, sourceLines, restoredBorders, coast, territories,
                        allowFullUnionNodingFallback: false);
                    changed = ChangedTerritoryAdjacencies(sourceAdjacencies, GetTerritoryAdjacencies(build.Territories));
                    recoveryPolygonization = build;
                    recoveryAdjacencyChanges = changed.ToArray();
                }
            }

            if (build.MissingTerritoryCount > 0 || build.InvalidTerritoryCount > 0 || changed.Length > 0 ||
                build.UnassignedFaceCount > 0)
            {
                // If the local retry cannot close the graph, preserve the complete source mask
                // directly. A third world-wide polygonization of unchanged source contours is
                // redundant and used to dominate the experimental run time.
                restoredBorders.UnionWith(borders.Select(border => border.Key));
                globalSourceFallback = true;
                build = new PolygonizationResult(
                    territories.ToDictionary(item => item.Key, item => item.Value.Geometry, StringComparer.Ordinal),
                    build.FaceCount, build.UnassignedFaceCount, build.ExcludedWaterFaceCount,
                    Array.Empty<string>(), Array.Empty<string>());
                changed = Array.Empty<string>();
            }

            var emptyClipTerritories = globalSourceFallback
                ? new HashSet<string>(StringComparer.Ordinal)
                : FindEmptyClipTerritories(cells, rawVoronoiCells, build.Territories);
            if (emptyClipTerritories.Count > 0 && !globalSourceFallback)
            {
                foreach (var border in borders.Where(border => emptyClipTerritories.Contains(border.A) || emptyClipTerritories.Contains(border.B)))
                    if (restoredBorders.Add(border.Key)) locallyRestoredBorders.Add(border.Key);
                build = BuildTerritoriesForMethod(method.Key, borders, sourceLines, restoredBorders, coast, territories,
                    allowFullUnionNodingFallback: false);
                recoveryPolygonization = build;
                recoveryAdjacencyChanges = ChangedTerritoryAdjacencies(sourceAdjacencies,
                    GetTerritoryAdjacencies(build.Territories));
                emptyClipTerritories = FindEmptyClipTerritories(cells, rawVoronoiCells, build.Territories);
                if (emptyClipTerritories.Count > 0)
                {
                    // A per-territory polygon replacement would break shared-border identity.
                    // Revert the whole territorial mask network as one coherent fallback.
                    globalSourceFallback = true;
                    restoredBorders.UnionWith(borders.Select(border => border.Key));
                    build = new PolygonizationResult(
                        territories.ToDictionary(item => item.Key, item => item.Value.Geometry, StringComparer.Ordinal),
                        build.FaceCount, build.UnassignedFaceCount, build.ExcludedWaterFaceCount,
                        Array.Empty<string>(), Array.Empty<string>());
                }
            }

            var clips = new List<CellBuild>(cells.Length);
            foreach (var sourceCell in cells.OrderBy(cell => cell.Id))
            {
                var targetTerritory = build.Territories[sourceCell.Territory];
                var rawCell = rawVoronoiCells[sourceCell.Id];
                // If the simplified shared network fails its world topology guardrail,
                // retain the existing cell geometry as an explicit, measurable rollback.
                var geometry = globalSourceFallback
                    ? (Geometry)sourceCell.Geometry.Copy()
                    : SafeIntersection(rawCell, targetTerritory);
                var fallback = false;
                var geometryRepaired = false;
                if (geometry.IsEmpty)
                {
                    // A city cell may not disappear. Restore that territory and all of its
                    // incident shared borders, then retry this method as a coherent network.
                    geometry = sourceCell.Geometry;
                    fallback = true;
                }
                if (!geometry.IsValid)
                {
                    geometry = geometry.Buffer(0);
                    geometryRepaired = true;
                }
                if (geometry.IsEmpty || !geometry.IsValid)
                {
                    geometry = sourceCell.Geometry.Buffer(0);
                    fallback = true;
                    geometryRepaired = true;
                }
                var sourceSeed = Factory.CreatePoint(new Coordinate(sourceCell.Longitude, sourceCell.Latitude));
                var sourceCovered = sourceCell.Geometry.Covers(sourceSeed);
                var rebuiltCovered = geometry.Covers(sourceSeed);
                var distanceMeters = rebuiltCovered ? 0 : HaversineKm(
                    new Coordinate(sourceCell.Longitude, sourceCell.Latitude),
                    DistanceOp.NearestPoints(geometry, sourceSeed)[0]) * 1000.0;
                var before = SphericalAreaKm2(sourceCell.Geometry);
                var after = SphericalAreaKm2(geometry);
                var comparisonSource = sourceCell.Geometry.IsValid
                    ? sourceCell.Geometry
                    : GeometryFixer.Fix(sourceCell.Geometry);
                var overlapArea = SphericalAreaKm2(SafeIntersection(geometry, comparisonSource));
                var gained = Math.Max(0, after - overlapArea);
                var lost = Math.Max(0, before - overlapArea);
                clips.Add(new CellBuild(sourceCell, geometry, before, after, gained, lost,
                    sourceCovered, rebuiltCovered, distanceMeters, fallback, geometryRepaired, geometry.IsValid));
            }

            var emptyCells = clips.Count(item => item.Geometry.IsEmpty);
            var invalidCells = clips.Count(item => !item.Valid);
            var cellFallbacks = clips.Count(item => item.LocalFallback);
            var cellGeometryRepairs = clips.Count(item => item.GeometryRepaired);
            var rebuiltCellUnionByTerritory = clips.GroupBy(item => item.Cell.Territory, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => UnaryUnionOp.Union(group.Select(item => item.Geometry)), StringComparer.Ordinal);
            var cellCoverageGaps = new Dictionary<string, double>(StringComparer.Ordinal);
            var cellOverlaps = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var territory in territories)
            {
                var cellUnion = rebuiltCellUnionByTerritory[territory.Key];
                var targetArea = SphericalAreaKm2(build.Territories[territory.Key]);
                var coveredArea = SphericalAreaKm2(SafeIntersection(build.Territories[territory.Key], cellUnion));
                cellCoverageGaps[territory.Key] = Math.Max(0, targetArea - coveredArea);
                var sum = clips.Where(item => item.Cell.Territory == territory.Key).Sum(item => item.RebuiltAreaKm2);
                cellOverlaps[territory.Key] = Math.Max(0, sum - SphericalAreaKm2(cellUnion));
            }

            var rebuiltWorld = UnaryUnionOp.Union(rebuiltCellUnionByTerritory.Values);
            var landOverlapArea = SphericalAreaKm2(SafeIntersection(rebuiltWorld, land));
            var worldGained = Math.Max(0, SphericalAreaKm2(rebuiltWorld) - landOverlapArea);
            var worldLost = Math.Max(0, SphericalAreaKm2(land) - landOverlapArea);
            var sourceCellAdjacencies = edges.Select(edge => CellPairKey(edge.A, edge.B)).ToHashSet(StringComparer.Ordinal);
            var rebuiltAdjacencies = BuildCellAdjacencies(clips);
            var lostCellAdjacencies = sourceCellAdjacencies.Except(rebuiltAdjacencies).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var createdCellAdjacencies = rebuiltAdjacencies.Except(sourceCellAdjacencies).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var rebuiltTerritoryAdjacencies = GetTerritoryAdjacencies(build.Territories)
                .ToHashSet(StringComparer.Ordinal);
            var territoryAdjacencyChanges = ChangedTerritoryAdjacencies(sourceAdjacencies, rebuiltTerritoryAdjacencies);
            var rebuiltTerritoryOverlaps = BuildTerritoryOverlapDiagnostics(build.Territories);
            var coastDeviationKm = BoundarySymmetricDifferenceLengthKm(land, rebuiltWorld);
            var sourceSeedCoveredCount = clips.Count(item => item.SourceSeedCovered);
            var rebuiltSeedCoveredCount = clips.Count(item => item.RebuiltSeedCovered);

            var territoryMetrics = territories.Values.OrderBy(value => value.Code, StringComparer.Ordinal)
                .Select(territory =>
                {
                    var before = SphericalAreaKm2(territory.Geometry);
                    var after = SphericalAreaKm2(build.Territories[territory.Code]);
                    var overlap = globalSourceFallback
                        ? before
                        : SphericalAreaKm2(SafeIntersection(build.Territories[territory.Code], territory.Geometry));
                    return new
                    {
                        territory = territory.Code,
                        owner = territory.Owner,
                        cellCount = territory.Cells.Length,
                        areaBeforeKm2 = before,
                        areaAfterKm2 = after,
                        gainedKm2 = Math.Max(0, after - overlap),
                        lostKm2 = Math.Max(0, before - overlap),
                        relativeAreaChange = before == 0 ? 0 : (after - before) / before,
                        cellCoverageGapKm2 = cellCoverageGaps[territory.Code],
                        cellOverlapKm2 = cellOverlaps[territory.Code]
                    };
                }).ToArray();

            var features = clips.Select(item => (object)new
            {
                type = "Feature",
                properties = new
                {
                    id = item.Cell.Id,
                    name = item.Cell.Name,
                    country = item.Cell.Country,
                    territoryCode = item.Cell.Territory,
                    ownerCode = item.Cell.Owner,
                    latitude = item.Cell.Latitude,
                    longitude = item.Cell.Longitude,
                    population = item.Cell.Population,
                    areaBeforeKm2 = item.SourceAreaKm2,
                    areaAfterKm2 = item.RebuiltAreaKm2,
                    gainedKm2 = item.GainedAreaKm2,
                    lostKm2 = item.LostAreaKm2,
                    relativeAreaChange = item.SourceAreaKm2 == 0 ? 0 : (item.RebuiltAreaKm2 - item.SourceAreaKm2) / item.SourceAreaKm2,
                    sourceSeedCovered = item.SourceSeedCovered,
                    rebuiltSeedCovered = item.RebuiltSeedCovered,
                    seedDistanceMeters = item.SeedDistanceMeters,
                    localFallback = item.LocalFallback,
                    geometryRepaired = item.GeometryRepaired,
                    globalSourceMaskFallback = globalSourceFallback,
                    valid = item.Valid
                },
                geometry = GeometryToGeoJsonObject(item.Geometry)
            }).ToArray();

            var methodSummary = new
            {
                method = method.Key,
                label = method.Label,
                cellCount = clips.Count,
                territoryCount = build.Territories.Count,
                emptyCellCount = emptyCells,
                invalidCellCount = invalidCells,
                invalidTerritoryCount = build.InvalidTerritoryCount,
                missingTerritoryCount = build.MissingTerritoryCount,
                unassignedPolygonFaceCount = build.UnassignedFaceCount,
                excludedWaterFaceCount = build.ExcludedWaterFaceCount,
                polygonizedFaceCount = build.FaceCount,
                initialPolygonization = new
                {
                    faceCount = initialPolygonization.FaceCount,
                    assignedTerritoryCount = initialPolygonization.Territories.Count,
                    missingTerritories = initialPolygonization.MissingTerritories,
                    invalidTerritories = initialPolygonization.InvalidTerritories,
                    unassignedFaceCount = initialPolygonization.UnassignedFaceCount,
                    excludedWaterFaceCount = initialPolygonization.ExcludedWaterFaceCount,
                    changedTerritoryAdjacencies = initialAdjacencyChanges
                },
                recoveryPolygonization = recoveryPolygonization is null ? null : new
                {
                    faceCount = recoveryPolygonization.FaceCount,
                    assignedTerritoryCount = recoveryPolygonization.Territories.Count,
                    missingTerritories = recoveryPolygonization.MissingTerritories,
                    invalidTerritories = recoveryPolygonization.InvalidTerritories,
                    unassignedFaceCount = recoveryPolygonization.UnassignedFaceCount,
                    excludedWaterFaceCount = recoveryPolygonization.ExcludedWaterFaceCount,
                    changedTerritoryAdjacencies = recoveryAdjacencyChanges
                },
                worldGainedKm2 = worldGained,
                worldLostKm2 = worldLost,
                cellGainedKm2 = clips.Sum(item => item.GainedAreaKm2),
                cellLostKm2 = clips.Sum(item => item.LostAreaKm2),
                relativeCellAreaChangeMaximum = clips.Select(item => item.SourceAreaKm2 == 0 ? 0 : Math.Abs(item.RebuiltAreaKm2 - item.SourceAreaKm2) / item.SourceAreaKm2).DefaultIfEmpty(0).Max(),
                sourceSeedCoveredCount,
                rebuiltSeedCoveredCount,
                seedCoverageWorsenedCount = clips.Count(item => item.SourceSeedCovered && !item.RebuiltSeedCovered),
                seedCoverageImprovedCount = clips.Count(item => !item.SourceSeedCovered && item.RebuiltSeedCovered),
                coastBoundarySymmetricDifferenceKm = coastDeviationKm,
                sourceLandComponentCount = PolygonComponentCount(land),
                rebuiltLandComponentCount = PolygonComponentCount(rebuiltWorld),
                sourceLandHoleCount = PolygonHoleCount(land),
                rebuiltLandHoleCount = PolygonHoleCount(rebuiltWorld),
                sourceTerritoryHoleCount = territories.Values.Sum(value => PolygonHoleCount(value.Geometry)),
                rebuiltTerritoryHoleCount = build.Territories.Values.Sum(PolygonHoleCount),
                sourceTerritoryAdjacencyCount = sourceAdjacencies.Count,
                rebuiltTerritoryAdjacencyCount = rebuiltTerritoryAdjacencies.Count,
                rebuiltTerritoryOverlapPairCount = rebuiltTerritoryOverlaps.Length,
                rebuiltTerritoryOverlapPairwiseAreaKm2 = rebuiltTerritoryOverlaps.Sum(item => item.AreaKm2),
                rebuiltTerritoryOverlaps,
                territoryAdjacencyLost = ChangedTerritoryAdjacencies(sourceAdjacencies, rebuiltTerritoryAdjacencies)
                    .Where(key => key.StartsWith("lost:", StringComparison.Ordinal))
                    .Select(key => key[5..]).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                territoryAdjacencyCreated = ChangedTerritoryAdjacencies(sourceAdjacencies, rebuiltTerritoryAdjacencies)
                    .Where(key => key.StartsWith("created:", StringComparison.Ordinal))
                    .Select(key => key[8..]).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                sourceCellAdjacencyCount = sourceCellAdjacencies.Count,
                rebuiltCellAdjacencyCount = rebuiltAdjacencies.Count,
                cellAdjacencyLostCount = lostCellAdjacencies.Length,
                cellAdjacencyCreatedCount = createdCellAdjacencies.Length,
                cellAdjacencyLost = lostCellAdjacencies,
                cellAdjacencyCreated = createdCellAdjacencies,
                locallyRestoredBorderCount = locallyRestoredBorders.Count,
                locallyRestoredBorders = locallyRestoredBorders.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                sourceContourBorderCount = restoredBorders.Count,
                globalSourceContourFallback = globalSourceFallback,
                globalSourceContourFallbackReason = globalSourceFallback
                    ? "Direct and locally restored polygonization did not close the territory graph; the complete source territorial masks and source cell geometries were restored."
                    : null,
                indexedLineNodingApplied = true,
                fullUnionNodingFallback = build.FullUnionNodingFallback,
                cellFallbackCount = cellFallbacks,
                cellGeometryRepairCount = cellGeometryRepairs,
                sourceMaskCellFallbackCount = globalSourceFallback ? clips.Count : 0,
                topologyValidated = !globalSourceFallback &&
                    clips.Count == cells.Length && emptyCells == 0 && invalidCells == 0 &&
                    build.Territories.Count == territories.Count && build.MissingTerritoryCount == 0 &&
                    build.InvalidTerritoryCount == 0 && build.UnassignedFaceCount == 0 &&
                    lostCellAdjacencies.Length == 0 && createdCellAdjacencies.Length == 0 &&
                    territoryAdjacencyChanges.Length == 0 && rebuiltTerritoryOverlaps.Length == 0 &&
                    cellCoverageGaps.Values.Sum() <= 1e-4 && cellOverlaps.Values.Sum() <= 1e-4 &&
                    coastDeviationKm <= 0.01 &&
                    clips.All(item => !item.SourceSeedCovered || item.RebuiltSeedCovered),
                topologyValidationFailureReasons = BuildTopologyValidationFailureReasons(
                    globalSourceFallback, clips.Count, cells.Length, emptyCells, invalidCells,
                    territories.Count, build, lostCellAdjacencies.Length, createdCellAdjacencies.Length,
                    territoryAdjacencyChanges, rebuiltTerritoryOverlaps.Length,
                    cellCoverageGaps.Values.Sum(), cellOverlaps.Values.Sum(), coastDeviationKm,
                    clips.Count(item => item.SourceSeedCovered && !item.RebuiltSeedCovered)),
                deterministicOrdering = "territories, border keys, cells and adjacency pairs sorted ordinally / by stable id",
                territoryMetrics,
                elapsedMilliseconds = methodTimer.ElapsedMilliseconds,
                robustIntersectionFallbackCount = System.Threading.Interlocked.Read(ref _robustIntersectionFallbackCount) - robustIntersectionStart,
                robustDifferenceFallbackCount = System.Threading.Interlocked.Read(ref _robustDifferenceFallbackCount) - robustDifferenceStart,
                precisionReducedOverlayFallbackCount = System.Threading.Interlocked.Read(ref _precisionReducedOverlayFallbackCount) - precisionReducedStart,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
            };

            var filename = $"global-frontier-reconstruction-{method.Key}.geojson";
            var output = Path.Combine(outDir, filename);
            var geometryText = JsonSerializer.Serialize(features.Select(feature => feature));
            var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(geometryText))).ToLowerInvariant();
            var metadataWithHash = JsonNode.Parse(JsonSerializer.Serialize(methodSummary)!);
            metadataWithHash!["geometrySha256"] = sha;
            var text = JsonSerializer.Serialize(new
            {
                type = "FeatureCollection",
                name = $"World Conquest reconstructed cells · {method.Key}",
                metadata = metadataWithHash,
                features
            });
            await File.WriteAllTextAsync(output, text);
            methodSummaries[method.Key] = methodSummary;
            reconstructionMethods[method.Key] = new
            {
                file = filename,
                sizeBytes = new FileInfo(output).Length,
                geometrySha256 = sha,
                artifactSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant()
            };
            Console.WriteLine($"Reconstructed {method.Key}: cells={clips.Count}, territories={build.Territories.Count}, valid={clips.Count(item => item.Valid)}/{clips.Count}, locally restored borders={locallyRestoredBorders.Count}, source-contour borders={restoredBorders.Count}, indexed line noding=true, full-union noding fallback={build.FullUnionNodingFallback}, global fallback={globalSourceFallback}, {methodTimer.Elapsed.TotalSeconds:F1}s, {new FileInfo(output).Length:N0} bytes.");
        }

        var summary = new
        {
            schema = "world-conquest.global-frontier-reconstruction.v1",
            generatedUtc = DateTime.UtcNow.ToString("O"),
            source = new
            {
                cellCount = cells.Length,
                territoryCount = territories.Count,
                politicalOwnerCount = cells.Select(cell => cell.Owner).Distinct(StringComparer.Ordinal).Count(),
                graphEdgeCount = edges.Length,
                graphSha256 = graphDigest,
                sourceValidCellCount = sourceValidCells,
                sourceEmptyCellCount = sourceEmptyCells,
                sourceInvalidCellCount = cells.Count(cell => !cell.Geometry.IsValid),
                sourceInvalidCells = sourceInvalidCellDetails,
                sourceInvalidTerritoryCount = sourceInvalidTerritories,
                sourceEmptyTerritoryCount = sourceEmptyTerritories,
                sourceSeedInsideTerritoryCount = sourceSeedAudit.Count(item => item.seedInsideTerritory),
                sourceSeedOutsideTerritoryCount = sourceSeedAudit.Count(item => !item.seedInsideTerritory),
                sourceSeedCoveredByCellCount = sourceSeedAudit.Count(item => item.seedCoveredByCell),
                sourceSeedOutsideCellCount = sourceSeedAudit.Count(item => !item.seedCoveredByCell),
                sourceSeedInsideTerritoryOutsideCellCount = sourceSeedAudit.Count(item => item.seedInsideTerritory && !item.seedCoveredByCell),
                sourceSeedAnomalies,
                sourceLandAreaKm2 = sourceCellUnionArea,
                sumCellAreaKm2 = sourceCellAreaSum,
                cellOverlapAreaKm2 = Math.Max(0, sourceCellAreaSum - sourceCellUnionArea),
                sumTerritoryAreaKm2 = sourceTerritoryAreaSum,
                territoryOverlapAreaKm2 = Math.Max(0, sourceTerritoryAreaSum - sourceCellUnionArea),
                sourceTerritoryOverlapPairwiseAreaKm2 = sourceTerritoryOverlaps.Sum(item => item.AreaKm2),
                sourceTerritoryOverlapPairCount = sourceTerritoryOverlaps.Length,
                sourceTerritoryOverlaps,
                landComponentCount = PolygonComponentCount(land),
                landHoleCount = PolygonHoleCount(land),
                territoryHoleCount = territories.Values.Sum(value => PolygonHoleCount(value.Geometry)),
                territoryAdjacencyCount = sourceAdjacencies.Count,
                borderPairCount = borders.Count,
                coastlineLineComponentCount = coast.Length,
                historicalSelectionCount = 9894,
                currentSelectedCityCount = cells.Length,
                additionalSelectedCity = new { id = 13157454L, name = "Al-Juwain", country = "SA", latitude = 17.53756, longitude = 42.85038, population = 3500 },
                saudiSelectedCountChange = "105 to 106 between Sep 5 and Sep 14 artifacts; selection snapshots were generated from live GeoNames candidates, with no archived cities500.zip to identify the exact upstream row change",
                matchedVerifiedTerritoryAudit = matchesPreviouslyAuditedGraph ? new
                {
                    emptyCells = 0,
                    invalidCells = 0,
                    zeroNeighbourCells = 138,
                    oneNeighbourCells = 153,
                    seedOutsideNaturalEarthTerritory = 373,
                    seedInsideTerritoryOutsideCell = 0,
                    connectedComponents = 207,
                    coverageWarnings = 0,
                    auditRun = "Sep 14 territory audit; source graph SHA-256 matches exactly",
                    currentDirectNtsAuditNote = sourceInvalidCellDetails.Length > 0
                        ? "The current direct NTS validity check found source cell(s) not reported by the historical audit; current direct metrics and IDs above are authoritative for this reconstruction run."
                        : "Current direct NTS validity check agrees with the historical empty/invalid-cell counts."
                } : null,
                missingDirectCellBoundaryEdgeCount = edgeDiagnostics.Length,
                missingDirectCellBoundaryEdges = edgeDiagnostics,
                jurisdictionModel = "territoryCode defines geometry; ownerCode defines political ownership",
                seedAuditReference = "Current graph digest was already matched to the Sep 14 territory audit; this run rechecks geometry directly."
            },
            junctions = new
            {
                detectedNearbyCandidateCount = ReadFusionCandidateCount(outDir),
                belgiumLuxembourgReference = ReadBelgiumLuxembourgReference(outDir),
                fusionAppliedCount = 0,
                fusionRule = "Merge only after moving both junctions to one point on their original shared contour and rebuilding the incident cells without losing any source cell adjacency, creating invalid/empty cells, or changing territory adjacency. Proximity alone is insufficient.",
                fusionRuleStatus = "The rule is expressed as a topological acceptance test, but no junction is automatically moved in this run. The 23 candidates remain diagnostic until the movement operation passes that test."
            },
            methods = methodSummaries,
            files = reconstructionMethods,
            elapsedMilliseconds = timer.ElapsedMilliseconds,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, "global-frontier-reconstruction-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Global reconstruction completed: {territories.Count} territories, {cells.Length} city cells, missing direct cell-line edges={edgeDiagnostics.Length}, {timer.Elapsed.TotalMinutes:F1} min.");
    }

    private static List<TerritoryBorder> BuildTerritoryBorders(IReadOnlyDictionary<string, TerritorySource> territories)
    {
        var values = territories.Values.OrderBy(value => value.Code, StringComparer.Ordinal).ToArray();
        var borders = new List<TerritoryBorder>();
        for (var i = 0; i < values.Length; i++)
        for (var j = i + 1; j < values.Length; j++)
        {
            var a = values[i];
            var b = values[j];
            if (!a.Geometry.EnvelopeInternal.Intersects(b.Geometry.EnvelopeInternal)) continue;
            Geometry shared;
            try { shared = a.Geometry.Boundary.Intersection(b.Geometry.Boundary); }
            catch { continue; }
            var pieces = EnumerateLines(shared)
                .Where(line => !line.IsEmpty && line.NumPoints >= 2 && line.Length > 1e-10)
                .SelectMany(SplitAtAntimeridian)
                .Where(line => line.NumPoints >= 2)
                .ToArray();
            var merger = new LineMerger();
            foreach (var piece in pieces) merger.Add(piece);
            var merged = merger.GetMergedLineStrings().Cast<LineString>()
                .Where(line => !line.IsEmpty && line.NumPoints >= 2 && line.Length > 1e-10)
                .OrderBy(line => line.EnvelopeInternal.MinX)
                .ThenBy(line => line.EnvelopeInternal.MinY)
                .ThenByDescending(line => line.Length)
                .ToArray();
            var lines = merged.Length == 0 && pieces.Length > 0 ? pieces : merged;
            if (lines.Length == 0) continue;
            borders.Add(new TerritoryBorder(TerritoryPairKey(a.Code, b.Code), a.Code, b.Code,
                a.Owner, b.Owner, lines));
        }
        return borders;
    }

    private static PolygonizationResult BuildTerritoriesForMethod(string method,
        IReadOnlyList<TerritoryBorder> borders,
        IReadOnlyDictionary<string, LineString[]> linesByBorder,
        IReadOnlySet<string> restoredBorders,
        IReadOnlyList<LineString> coast,
        IReadOnlyDictionary<string, TerritorySource> source,
        bool allowFullUnionNodingFallback = false)
    {
        var nodedCoast = NodeCoastlinesAtBorderEndpoints(coast, borders);
        var linework = new List<Geometry>(nodedCoast.Length + borders.Count * 2);
        linework.AddRange(nodedCoast);
        foreach (var border in borders)
        {
            var lines = restoredBorders.Contains(border.Key)
                ? border.SourceLines
                : linesByBorder.TryGetValue(border.Key, out var provided) ? provided : border.SourceLines;
            foreach (var line in lines)
            {
                var simplified = restoredBorders.Contains(border.Key) ? (LineString)line.Copy() : Simplify(line, method);
                if (simplified.NumPoints >= 2 && simplified.Length > 1e-10) linework.Add(simplified);
            }
        }

        PolygonizationResult? direct = null;
        try
        {
            Console.WriteLine($"Global reconstruction {method}: polygonizing {linework.Count:N0} shared/coast line components after indexed robust noding.");
            direct = PolygonizeTerritoryLinework(Factory.CreateGeometryCollection(linework.ToArray()), source, false);

            if (direct is not null && direct.MissingTerritoryCount == 0 && direct.InvalidTerritoryCount == 0 &&
                direct.UnassignedFaceCount == 0 && direct.Territories.Count == source.Count)
            {
                var expectedAdjacencies = borders.Select(border => border.Key).ToHashSet(StringComparer.Ordinal);
                var directAdjacencies = GetTerritoryAdjacencies(direct.Territories);
                var adjacencyChanges = ChangedTerritoryAdjacencies(expectedAdjacencies, directAdjacencies);
                if (adjacencyChanges.Length == 0)
                {
                    Console.WriteLine($"Global reconstruction {method}: indexed-noded polygonization accepted; all {expectedAdjacencies.Count} territorial adjacencies preserved.");
                    return direct;
                }
                Console.WriteLine($"Global reconstruction {method}: indexed-noded result faces={direct.FaceCount}, adjacency changes={adjacencyChanges.Length}.");
            }
            else if (direct is not null)
                Console.WriteLine($"Global reconstruction {method}: indexed-noded result faces={direct.FaceCount}, territories={direct.Territories.Count}/{source.Count}, missing={direct.MissingTerritoryCount}, invalid={direct.InvalidTerritoryCount}, unassigned={direct.UnassignedFaceCount}.");
        }
        catch (Exception error)
        {
            Console.WriteLine($"Global reconstruction {method}: indexed-noded polygonization raised {error.GetType().Name}.");
        }

        if (!allowFullUnionNodingFallback)
        {
            if (direct is not null)
                Console.WriteLine($"Global reconstruction {method}: returning the measured indexed-noded result for local contour recovery; full-union noding is disabled for this pass.");
            return direct ?? new PolygonizationResult(new Dictionary<string, Geometry>(StringComparer.Ordinal),
                0, 0, 0, source.Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray(), Array.Empty<string>());
        }

        Console.WriteLine($"Global reconstruction {method}: applying full-union noding to {linework.Count:N0} line components after local contour recovery.");
        Geometry noded;
        try { noded = UnaryUnionOp.Union(linework); }
        catch
        {
            noded = UnaryUnionOp.Union(linework.Select(line => line.Buffer(0)));
        }
        return PolygonizeTerritoryLinework(noded, source, true);
    }

    private static LineString[] NodeCoastlinesAtBorderEndpoints(IReadOnlyList<LineString> coast,
        IReadOnlyList<TerritoryBorder> borders)
    {
        if (coast.Count == 0 || borders.Count == 0) return coast.Select(line => (LineString)line.Copy()).ToArray();
        const double exactContactToleranceDegrees = 1e-12;
        var index = new STRtree<int>();
        for (var i = 0; i < coast.Count; i++) index.Insert(coast[i].EnvelopeInternal, i);
        index.Build();
        var inserts = coast.Select(_ => new Dictionary<int, List<(double T, Coordinate Point)>>()).ToArray();
        var nodeKeys = coast.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string CoordinateKey(Coordinate coordinate) =>
            $"{Math.Round(coordinate.X, 12):F12}|{Math.Round(coordinate.Y, 12):F12}";

        foreach (var border in borders)
        foreach (var line in border.SourceLines)
        foreach (var endpoint in new[] { line.GetCoordinateN(0), line.GetCoordinateN(line.NumPoints - 1) })
        {
            if (!seen.Add(CoordinateKey(endpoint))) continue;
            var point = Factory.CreatePoint(endpoint);
            var envelope = new Envelope(endpoint);
            envelope.ExpandBy(exactContactToleranceDegrees);
            foreach (var coastIndex in index.Query(envelope))
            {
                var coastline = coast[coastIndex];
                var nearest = DistanceOp.NearestPoints(point, coastline);
                if (nearest.Length < 2 || nearest[0].Distance(nearest[1]) > exactContactToleranceDegrees) continue;
                var coordinates = coastline.Coordinates;
                nodeKeys[coastIndex].Add(CoordinateKey(endpoint));
                if (coordinates.Any(coordinate => coordinate.Distance(endpoint) <= exactContactToleranceDegrees)) continue;

                var bestDistance = double.PositiveInfinity;
                var bestSegment = -1;
                var bestT = 0.0;
                for (var segment = 0; segment < coordinates.Length - 1; segment++)
                {
                    var a = coordinates[segment];
                    var b = coordinates[segment + 1];
                    var dx = b.X - a.X;
                    var dy = b.Y - a.Y;
                    var lengthSquared = dx * dx + dy * dy;
                    if (lengthSquared <= 1e-24) continue;
                    var t = Math.Clamp(((endpoint.X - a.X) * dx + (endpoint.Y - a.Y) * dy) / lengthSquared, 0.0, 1.0);
                    var x = a.X + t * dx;
                    var y = a.Y + t * dy;
                    var distance = Math.Sqrt((endpoint.X - x) * (endpoint.X - x) + (endpoint.Y - y) * (endpoint.Y - y));
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    bestSegment = segment;
                    bestT = t;
                }
                if (bestSegment < 0 || bestT <= 1e-12 || bestT >= 1.0 - 1e-12 || bestDistance > exactContactToleranceDegrees) continue;
                if (!inserts[coastIndex].TryGetValue(bestSegment, out var points))
                    inserts[coastIndex][bestSegment] = points = new List<(double T, Coordinate Point)>();
                if (!points.Any(item => item.Point.Distance(endpoint) <= exactContactToleranceDegrees))
                    points.Add((bestT, new Coordinate(endpoint)));
            }
        }

        var output = new List<LineString>(coast.Count + seen.Count);
        for (var lineIndex = 0; lineIndex < coast.Count; lineIndex++)
        {
            var line = coast[lineIndex];
            if (inserts[lineIndex].Count == 0 && nodeKeys[lineIndex].Count == 0)
            {
                output.Add((LineString)line.Copy());
                continue;
            }
            var coordinates = line.Coordinates;
            var rebuilt = new List<Coordinate>(coordinates.Length + inserts[lineIndex].Values.Sum(points => points.Count));
            for (var segment = 0; segment < coordinates.Length - 1; segment++)
            {
                rebuilt.Add(new Coordinate(coordinates[segment]));
                if (!inserts[lineIndex].TryGetValue(segment, out var points)) continue;
                rebuilt.AddRange(points.OrderBy(item => item.T).Select(item => item.Point));
            }
            rebuilt.Add(new Coordinate(coordinates[^1]));

            var chunkStart = 0;
            for (var coordinateIndex = 1; coordinateIndex < rebuilt.Count; coordinateIndex++)
            {
                if (!nodeKeys[lineIndex].Contains(CoordinateKey(rebuilt[coordinateIndex]))) continue;
                if (coordinateIndex > chunkStart)
                    output.Add(Factory.CreateLineString(rebuilt.Skip(chunkStart).Take(coordinateIndex - chunkStart + 1).ToArray()));
                chunkStart = coordinateIndex;
            }
            if (chunkStart < rebuilt.Count - 1)
                output.Add(Factory.CreateLineString(rebuilt.Skip(chunkStart).ToArray()));
        }
        return output.ToArray();
    }

    private static PolygonizationResult PolygonizeTerritoryLinework(Geometry linework,
        IReadOnlyDictionary<string, TerritorySource> source,
        bool fullUnionNodingFallback)
    {
        var polygonizer = new Polygonizer();
        polygonizer.Add(NodeLinework(linework));
        var faces = polygonizer.GetPolygons().Cast<Geometry>()
            .Where(face => !face.IsEmpty && face.Area > 1e-14)
            .ToArray();
        var territoryIndex = new STRtree<string>();
        foreach (var item in source.Values) territoryIndex.Insert(item.Geometry.EnvelopeInternal, item.Code);
        territoryIndex.Build();
        var assigned = source.Keys.ToDictionary(key => key, _ => new List<Geometry>(), StringComparer.Ordinal);
        var unassigned = 0;
        var excludedWater = 0;
        foreach (var face in faces)
        {
            var point = face.InteriorPoint;
            var candidates = territoryIndex.Query(point.EnvelopeInternal)
                .Where(code => source[code].Geometry.Covers(point))
                .ToArray();
            if (candidates.Length == 0)
            {
                // Simplified borders may move a face point across its original border. Use
                // the face's largest source overlap, with stable code ordering as a tie-break.
                candidates = territoryIndex.Query(face.EnvelopeInternal).ToArray();
                var bestCode = "";
                var bestOverlap = 0.0;
                foreach (var code in candidates.OrderBy(code => code, StringComparer.Ordinal))
                {
                    try
                    {
                        var overlap = face.Intersection(source[code].Geometry).Area;
                        if (overlap > bestOverlap) { bestOverlap = overlap; bestCode = code; }
                    }
                    catch { }
                }
                if (bestCode.Length == 0)
                {
                    if (bestOverlap > 1e-14) unassigned++;
                    else excludedWater++;
                    continue;
                }
                assigned[bestCode].Add(face);
                continue;
            }

            var target = candidates.Length == 1 ? candidates[0] : candidates
                .OrderByDescending(code => FaceOverlapArea(face, source[code].Geometry))
                .ThenBy(code => code, StringComparer.Ordinal)
                .First();
            assigned[target].Add(face);
        }

        var output = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        foreach (var territory in source.Values)
        {
            var parts = assigned[territory.Code];
            if (parts.Count == 0) continue;
            Geometry merged;
            try { merged = UnaryUnionOp.Union(parts); }
            catch { merged = UnaryUnionOp.Union(parts.Select(part => part.Buffer(0))); }
            if (!merged.IsValid) merged = merged.Buffer(0);
            output[territory.Code] = merged;
        }
        var missing = source.Keys.Where(code => !output.ContainsKey(code)).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        var invalid = output.Where(item => !item.Value.IsValid).Select(item => item.Key)
            .OrderBy(code => code, StringComparer.Ordinal).ToArray();
        return new PolygonizationResult(output, faces.Length, unassigned, excludedWater, missing, invalid,
            fullUnionNodingFallback);
    }

    private static string[] BuildTopologyValidationFailureReasons(bool globalSourceFallback,
        int rebuiltCellCount, int sourceCellCount, int emptyCellCount, int invalidCellCount,
        int sourceTerritoryCount, PolygonizationResult build,
        int lostCellAdjacencyCount, int createdCellAdjacencyCount,
        IReadOnlyCollection<string> territoryAdjacencyChanges, int territoryOverlapPairCount,
        double cellCoverageGapKm2, double cellOverlapKm2, double coastlineDifferenceKm,
        int lostCoveredSeedCount)
    {
        var reasons = new List<string>();
        if (globalSourceFallback) reasons.Add("global source-mask/cell fallback was used");
        if (rebuiltCellCount != sourceCellCount) reasons.Add($"cell count changed from {sourceCellCount} to {rebuiltCellCount}");
        if (emptyCellCount > 0) reasons.Add($"{emptyCellCount} empty cell(s)");
        if (invalidCellCount > 0) reasons.Add($"{invalidCellCount} invalid cell(s)");
        if (build.Territories.Count != sourceTerritoryCount)
            reasons.Add($"territory count changed from {sourceTerritoryCount} to {build.Territories.Count}");
        if (build.MissingTerritoryCount > 0)
            reasons.Add($"{build.MissingTerritoryCount} territory polygon(s) missing");
        if (build.InvalidTerritoryCount > 0) reasons.Add($"{build.InvalidTerritoryCount} invalid territory polygon(s)");
        if (build.UnassignedFaceCount > 0) reasons.Add($"{build.UnassignedFaceCount} polygon face(s) unassigned");
        if (lostCellAdjacencyCount > 0) reasons.Add($"{lostCellAdjacencyCount} cell adjacency(ies) lost");
        if (createdCellAdjacencyCount > 0) reasons.Add($"{createdCellAdjacencyCount} cell adjacency(ies) created");
        if (territoryAdjacencyChanges.Count > 0) reasons.Add($"{territoryAdjacencyChanges.Count} territory adjacency(ies) changed");
        if (territoryOverlapPairCount > 0) reasons.Add($"{territoryOverlapPairCount} territory overlap pair(s)");
        if (cellCoverageGapKm2 > 1e-4) reasons.Add($"cell coverage gaps total {cellCoverageGapKm2:F6} km²");
        if (cellOverlapKm2 > 1e-4) reasons.Add($"cell overlaps total {cellOverlapKm2:F6} km²");
        if (coastlineDifferenceKm > 0.01) reasons.Add($"coastline symmetric difference {coastlineDifferenceKm:F3} km");
        if (lostCoveredSeedCount > 0) reasons.Add($"{lostCoveredSeedCount} previously covered city seed(s) became uncovered");
        return reasons.ToArray();
    }

    private static Geometry NodeLinework(Geometry linework)
    {
        var strings = EnumerateLines(linework)
            .Where(line => !line.IsEmpty && line.NumPoints >= 2)
            .Select(line => (ISegmentString)new NodedSegmentString(
                line.Coordinates.Select(point => new Coordinate(point)).ToArray(), null!))
            .ToList();
        if (strings.Count == 0) return Factory.CreateMultiLineString(Array.Empty<LineString>());

        var intersector = new IntersectionAdder(new RobustLineIntersector());
        var noder = new MCIndexNoder(intersector);
        noder.ComputeNodes(strings);
        var substrings = NodedSegmentString.GetNodedSubstrings(strings);
        var result = substrings
            .Select(item => Factory.CreateLineString(item.Coordinates.Select(point => new Coordinate(point)).ToArray()))
            .Where(line => !line.IsEmpty && line.NumPoints >= 2 && line.Length > 1e-14)
            .ToArray();
        return Factory.CreateMultiLineString(result);
    }

    private static List<string> GetTerritoryAdjacencies(IReadOnlyDictionary<string, Geometry> territories)
    {
        var values = territories.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var output = new List<string>();
        for (var i = 0; i < values.Length; i++)
        for (var j = i + 1; j < values.Length; j++)
        {
            if (!values[i].Value.EnvelopeInternal.Intersects(values[j].Value.EnvelopeInternal)) continue;
            try
            {
                var shared = values[i].Value.Boundary.Intersection(values[j].Value.Boundary);
                if (EnumerateLines(shared).Any(line => line.Length > 1e-10))
                    output.Add(TerritoryPairKey(values[i].Key, values[j].Key));
            }
            catch { }
        }
        return output;
    }

    private static TerritoryOverlap[] BuildTerritoryOverlapDiagnostics(
        IReadOnlyDictionary<string, Geometry> territories)
    {
        const double reportAreaThresholdKm2 = 1e-6;
        var values = territories.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var index = new STRtree<int>();
        for (var i = 0; i < values.Length; i++)
            if (!values[i].Value.IsEmpty) index.Insert(values[i].Value.EnvelopeInternal, i);
        index.Build();

        var output = new List<TerritoryOverlap>();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].Value.IsEmpty) continue;
            foreach (var j in index.Query(values[i].Value.EnvelopeInternal))
            {
                if (j <= i) continue;
                var overlap = SafeIntersection(values[i].Value, values[j].Value);
                var areaKm2 = SphericalAreaKm2(overlap);
                if (areaKm2 <= reportAreaThresholdKm2) continue;
                output.Add(new TerritoryOverlap(values[i].Key, values[j].Key, areaKm2));
            }
        }
        return output.OrderByDescending(item => item.AreaKm2)
            .ThenBy(item => item.A, StringComparer.Ordinal)
            .ThenBy(item => item.B, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> FindEmptyClipTerritories(Cell[] cells,
        IReadOnlyDictionary<long, Geometry> rawCells,
        IReadOnlyDictionary<string, Geometry> territories)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (!territories.TryGetValue(cell.Territory, out var territory) ||
                !rawCells.TryGetValue(cell.Id, out var raw))
            {
                output.Add(cell.Territory);
                continue;
            }

            var seed = Factory.CreatePoint(new Coordinate(cell.Longitude, cell.Latitude));
            if (territory.Covers(seed)) continue;
            if (SafeIntersection(raw, territory).IsEmpty) output.Add(cell.Territory);
        }
        return output;
    }

    private static string[] ChangedTerritoryAdjacencies(IReadOnlyCollection<string> source, IReadOnlyCollection<string> rebuilt)
    {
        return source.Except(rebuilt, StringComparer.Ordinal).Select(key => "lost:" + key)
            .Concat(rebuilt.Except(source, StringComparer.Ordinal).Select(key => "created:" + key))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
    }

    private static object[] BuildMissingCellEdgeDiagnostics(Cell[] cells, Edge[] edges,
        IReadOnlyDictionary<string, TerritorySource> territories, IReadOnlySet<string> directTerritorialBorders)
    {
        var byId = cells.ToDictionary(cell => cell.Id);
        var output = new List<object>();
        foreach (var edge in edges.Where(edge => edge.Foreign).OrderBy(edge => edge.A).ThenBy(edge => edge.B))
        {
            if (!byId.TryGetValue(edge.A, out var a) || !byId.TryGetValue(edge.B, out var b) ||
                a.Owner == b.Owner || a.Territory == b.Territory) continue;
            Geometry shared;
            try { shared = a.Geometry.Boundary.Intersection(b.Geometry.Boundary); }
            catch { continue; }
            if (EnumerateLines(shared).Any(line => !line.IsEmpty && line.Length > 1e-10)) continue;

            var nearest = DistanceOp.NearestPoints(a.Geometry.Boundary, b.Geometry.Boundary);
            var distanceMeters = nearest.Length < 2 ? double.PositiveInfinity : HaversineKm(nearest[0], nearest[1]) * 1000.0;
            var latitude = nearest.Length < 2 ? 0 : (nearest[0].Y + nearest[1].Y) / 2.0;
            var bufferedArea = 0.0;
            try
            {
                bufferedArea = a.Geometry.Boundary.Buffer(1e-5)
                    .Intersection(b.Geometry.Boundary.Buffer(1e-5)).Area * 111.32 * 111.32 *
                    Math.Max(.01, Math.Cos(latitude * Math.PI / 180.0));
            }
            catch { }
            var aggregateExists = directTerritorialBorders.Contains(TerritoryPairKey(a.Territory, b.Territory));
            var pointOnly = !shared.IsEmpty && shared.Dimension == Dimension.Point;
            var classification = pointOnly ? "point-only-contact"
                : bufferedArea > 0 && distanceMeters <= 1.5 ? "precision-or-quantization-contact"
                : bufferedArea > 0 ? "tolerance-buffer-contact"
                : aggregateExists ? "cell-boundary-mismatch-territorial-border-exists"
                : "potential-false-adjacency-no-territorial-line";
            output.Add(new
            {
                cellA = a.Id,
                cellB = b.Id,
                territoryA = a.Territory,
                territoryB = b.Territory,
                ownerA = a.Owner,
                ownerB = b.Owner,
                intersectionType = shared.IsEmpty ? "empty" : shared.GeometryType,
                pointOnlyContact = pointOnly,
                nearestBoundaryDistanceMeters = distanceMeters,
                approximateBufferedOverlapKm2 = bufferedArea,
                aggregateTerritorialBorderExists = aggregateExists,
                classification
            });
        }
        return output.ToArray();
    }

    private static HashSet<string> BuildCellAdjacencies(IReadOnlyList<CellBuild> cells)
    {
        var index = new STRtree<int>();
        for (var i = 0; i < cells.Count; i++) index.Insert(cells[i].Geometry.EnvelopeInternal, i);
        index.Build();
        var output = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < cells.Count; i++)
        {
            var envelope = new Envelope(cells[i].Geometry.EnvelopeInternal);
            envelope.ExpandBy(2e-5);
            foreach (var j in index.Query(envelope))
            {
                if (j <= i) continue;
                if (SharesBoundaryWithinSourceTolerance(cells[i].Geometry, cells[j].Geometry))
                    output.Add(CellPairKey(cells[i].Cell.Id, cells[j].Cell.Id));
            }
        }
        return output;
    }

    private static bool SharesBoundaryWithinSourceTolerance(Geometry a, Geometry b)
    {
        try
        {
            var shared = a.Boundary.Intersection(b.Boundary);
            if (EnumerateLines(shared).Any(line => line.Length > 1e-6)) return true;
        }
        catch { }
        try
        {
            return a.Boundary.Buffer(1e-5).Intersection(b.Boundary.Buffer(1e-5)).Area > 5e-10;
        }
        catch { return false; }
    }

    private static Geometry SafeIntersection(Geometry a, Geometry b)
    {
        try { return a.Intersection(b); }
        catch
        {
            System.Threading.Interlocked.Increment(ref _robustIntersectionFallbackCount);
            try { return OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection); }
            catch { return OverlayAfterPrecisionReduction(a, b, SpatialFunction.Intersection); }
        }
    }

    private static Geometry SafeDifference(Geometry a, Geometry b)
    {
        try { return a.Difference(b); }
        catch
        {
            System.Threading.Interlocked.Increment(ref _robustDifferenceFallbackCount);
            try { return OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference); }
            catch { return OverlayAfterPrecisionReduction(a, b, SpatialFunction.Difference); }
        }
    }

    private static Geometry OverlayAfterPrecisionReduction(Geometry a, Geometry b, SpatialFunction operation)
    {
        System.Threading.Interlocked.Increment(ref _precisionReducedOverlayFallbackCount);
        Exception? lastError = null;
        // Remove sub-micrometre duplicate edges first; only broaden the grid if
        // the robust overlay still cannot node the repaired contours.
        foreach (var scale in new[] { 1e12, 1e10, 1e9 })
        {
            try
            {
                var precision = new PrecisionModel(scale);
                var repairedA = GeometryPrecisionReducer.Reduce(GeometryFixer.Fix(a), precision);
                var repairedB = GeometryPrecisionReducer.Reduce(GeometryFixer.Fix(b), precision);
                var result = OverlayNGRobust.Overlay(repairedA, repairedB, operation);
                if (!result.IsValid)
                    throw new InvalidOperationException($"Precision-reduced {operation} returned invalid geometry at scale {scale:G}.");
                return result;
            }
            catch (Exception error)
            {
                lastError = error;
            }
        }
        throw new InvalidOperationException("The geometry overlay remained invalid after robust noding and three topology-preserving precision retries.", lastError);
    }

    private static double FaceOverlapArea(Geometry face, Geometry territory)
    {
        try { return face.Intersection(territory).Area; }
        catch { return 0; }
    }

    private static double SphericalAreaKm2(Geometry geometry)
    {
        if (geometry.IsEmpty) return 0;
        if (geometry is Polygon polygon)
        {
            var area = SphericalRingAreaKm2(polygon.ExteriorRing);
            for (var i = 0; i < polygon.NumInteriorRings; i++) area -= SphericalRingAreaKm2(polygon.GetInteriorRingN(i));
            return Math.Max(0, area);
        }
        // Points and lines have no area. In NTS, a non-collection geometry
        // reports NumGeometries == 1 and GetGeometryN(0) returns itself, so
        // recursing over every Geometry would overflow the stack.
        if (geometry is not GeometryCollection) return 0;
        var sum = 0.0;
        for (var i = 0; i < geometry.NumGeometries; i++) sum += SphericalAreaKm2(geometry.GetGeometryN(i));
        return sum;
    }

    private static double SphericalRingAreaKm2(LineString ring)
    {
        const double earthRadiusKm = 6371.0088;
        var points = ring.Coordinates;
        if (points.Length < 4) return 0;
        var sum = 0.0;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var delta = NormalizeLongitudeDelta(points[i + 1].X - points[i].X) * Math.PI / 180.0;
            var latA = points[i].Y * Math.PI / 180.0;
            var latB = points[i + 1].Y * Math.PI / 180.0;
            sum += delta * (2.0 + Math.Sin(latA) + Math.Sin(latB));
        }
        return Math.Abs(sum) * earthRadiusKm * earthRadiusKm / 2.0;
    }

    private static double BoundarySymmetricDifferenceLengthKm(Geometry before, Geometry after)
    {
        try
        {
            var changed = before.Boundary.SymmetricDifference(after.Boundary);
            return EnumerateLines(changed).Sum(line => LengthKm(line.Coordinates));
        }
        catch { return double.PositiveInfinity; }
    }

    private static int PolygonComponentCount(Geometry geometry)
    {
        if (geometry.IsEmpty) return 0;
        if (geometry is Polygon) return 1;
        if (geometry is not GeometryCollection) return 0;
        var count = 0;
        for (var i = 0; i < geometry.NumGeometries; i++) count += PolygonComponentCount(geometry.GetGeometryN(i));
        return count;
    }

    private static int PolygonHoleCount(Geometry geometry)
    {
        if (geometry.IsEmpty) return 0;
        if (geometry is Polygon polygon) return polygon.NumInteriorRings;
        if (geometry is not GeometryCollection) return 0;
        var count = 0;
        for (var i = 0; i < geometry.NumGeometries; i++) count += PolygonHoleCount(geometry.GetGeometryN(i));
        return count;
    }

    private static object? GeometryToGeoJsonObject(Geometry geometry)
    {
        var polygons = new List<Polygon>();
        CollectPolygons(geometry, polygons);
        if (polygons.Count == 0) return null;
        object Coordinates(Polygon polygon)
        {
            var rings = new List<double[][]>
            {
                polygon.ExteriorRing.Coordinates.Select(point => new[] { point.X, point.Y }).ToArray()
            };
            for (var i = 0; i < polygon.NumInteriorRings; i++)
                rings.Add(polygon.GetInteriorRingN(i).Coordinates.Select(point => new[] { point.X, point.Y }).ToArray());
            return rings.ToArray();
        }
        if (polygons.Count == 1) return new { type = "Polygon", coordinates = Coordinates(polygons[0]) };
        return new { type = "MultiPolygon", coordinates = polygons.Select(Coordinates).ToArray() };
    }

    private static void CollectPolygons(Geometry geometry, ICollection<Polygon> output)
    {
        if (geometry.IsEmpty) return;
        if (geometry is Polygon polygon) { output.Add(polygon); return; }
        if (geometry is not GeometryCollection) return;
        for (var i = 0; i < geometry.NumGeometries; i++) CollectPolygons(geometry.GetGeometryN(i), output);
    }

    private static string TerritoryPairKey(string a, string b) =>
        StringComparer.Ordinal.Compare(a, b) <= 0 ? a + "-" + b : b + "-" + a;

    private static string CellPairKey(long a, long b) => a <= b ? a + ":" + b : b + ":" + a;

    private static string PairA(string key)
    {
        var value = key[(key.IndexOf(':') + 1)..];
        if (value.StartsWith("lost:", StringComparison.Ordinal)) value = value[5..];
        if (value.StartsWith("created:", StringComparison.Ordinal)) value = value[8..];
        return value[..value.IndexOf('-')];
    }

    private static string PairB(string key)
    {
        var value = key[(key.IndexOf(':') + 1)..];
        if (value.StartsWith("lost:", StringComparison.Ordinal)) value = value[5..];
        if (value.StartsWith("created:", StringComparison.Ordinal)) value = value[8..];
        return value[(value.IndexOf('-') + 1)..];
    }

    private static int ReadFusionCandidateCount(string outDir)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "global-frontier-results.json")));
            return document.RootElement.GetProperty("pairs").EnumerateArray()
                .Sum(pair => pair.GetProperty("nearbyFusionCandidates").GetArrayLength());
        }
        catch { return 0; }
    }

    private static object ReadBelgiumLuxembourgReference(string outDir)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "global-frontier-results.json")));
            var pair = document.RootElement.GetProperty("pairs").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("key").GetString() == "BE-LU");
            if (pair.ValueKind == JsonValueKind.Undefined)
                return new { detected = false, referenceAnchor = new[] { 5.88546, 49.64353 }, matchingCandidate = (object?)null };
            var candidate = pair.GetProperty("nearbyFusionCandidates").EnumerateArray().FirstOrDefault(item =>
            {
                var a = item.GetProperty("cellsA").EnumerateArray().Select(value => value.GetInt64()).OrderBy(value => value).ToArray();
                var b = item.GetProperty("cellsB").EnumerateArray().Select(value => value.GetInt64()).OrderBy(value => value).ToArray();
                return a.SequenceEqual(new long[] { 2787315, 2792442 }) &&
                       b.SequenceEqual(new long[] { 2960316, 2960634 });
            });
            return new
            {
                detected = pair.GetProperty("nearbyFusionCandidates").GetArrayLength() > 0,
                referenceAnchor = new[] { 5.88546, 49.64353 },
                validatedVisualJunctionDistanceMeters = 4380.0,
                matchingCandidate = candidate.ValueKind == JsonValueKind.Undefined ? null : (object)new
                {
                    distanceMeters = candidate.GetProperty("distanceMeters").GetDouble(),
                    cellsA = candidate.GetProperty("cellsA").EnumerateArray().Select(value => value.GetInt64()).ToArray(),
                    cellsB = candidate.GetProperty("cellsB").EnumerateArray().Select(value => value.GetInt64()).ToArray(),
                    borderPoints = new[] { candidate.GetProperty("a").Clone(), candidate.GetProperty("b").Clone() }
                }
            };
        }
        catch { return new { detected = false, referenceAnchor = new[] { 5.88546, 49.64353 }, matchingCandidate = (object?)null }; }
    }
}
