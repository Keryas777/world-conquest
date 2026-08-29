using System.Text;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

static class VoronoiLab
{
    sealed record Pt(double X, double Y);

    static readonly GeometryFactory GeometryFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    // Geographic territory and diplomatic owner are deliberately separate.
    // These overseas territories keep their own geometry/city selection but
    // belong to France for ownership, diplomacy and war.
    static readonly HashSet<string> FrenchOwnedTerritories =
        new(StringComparer.OrdinalIgnoreCase) { "GF", "GP", "MQ", "RE", "YT" };

    static string OwnerCode(string territoryCode) =>
        FrenchOwnedTerritories.Contains(territoryCode) ? "FR" : territoryCode;

    // World Conquest political model: Crimea is an independent starting territory.
    const string CrimeaTerritoryCode = "XC";
    static readonly HashSet<long> CrimeaCityIds = new()
    {
        694423,  // Sevastopol
        706524,  // Kerch
        692315,  // Sudak
        693805,  // Simferopol
        688105   // Yevpatoriya
    };

    static readonly HashSet<long> MissingIslandCityIds = new()
    {
        400747,    // Abū Mūsá
        12382279,  // Sansha
        1684606,   // Taganak
        7552914,   // Nangan
        11496092,  // Hoàng Sa
        13535608,  // Bạch Long Vĩ
        13512695   // Thổ Châu
    };

    static string TerritoryCode(City city) =>
        CrimeaCityIds.Contains(city.Id) ? CrimeaTerritoryCode : city.Country;

    static readonly Dictionary<string, string[]> TerritoryNameAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["GF"] = new[] { "French Guiana", "Guyane française", "Guyane" },
            ["GP"] = new[] { "Guadeloupe" },
            ["MQ"] = new[] { "Martinique" },
            ["RE"] = new[] { "Réunion", "Reunion" },
            ["YT"] = new[] { "Mayotte" },
            ["TW"] = new[] { "Taiwan" },
            ["XK"] = new[] { "Kosovo" }
        };

    public static async Task GenerateWorldAsync(
        HttpClient http,
        string outDir,
        IReadOnlyDictionary<string, List<City>> worldSelection,
        double populationWeight = .60)
    {
        var sourceCities = worldSelection.Values.SelectMany(x => x).ToList();
        var selected = sourceCities
            .GroupBy(TerritoryCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var quotas = selected.ToDictionary(x => x.Key, x => x.Value.Count, StringComparer.OrdinalIgnoreCase);
        var cities = selected.Values.SelectMany(x => x).ToList();

        Console.WriteLine($"Worldwide Voronoi input: {cities.Count:N0} selected cities in {selected.Count} territories.");

        var referenceLat = cities.Average(c => c.Lat) * Math.PI / 180.0;
        var cosLat = Math.Cos(referenceLat);

        // Canonical initial territories: each country's cities partition only that
        // country's current Natural Earth 10m territory. This guarantees that at T0
        // the union of a country's cells reconstructs its present-day border/coastline.
        var countryTerritories = await LoadCountryTerritoriesAsync(http, quotas.Keys);
        await ApplyMissingIslandPatchesAsync(http, countryTerritories, cities);
        var clippedCells = Enumerable.Repeat<Geometry>(GeometryFactory.CreatePolygon(), cities.Count).ToList();
        var voronoiCandidates = new HashSet<(int A, int B)>();

        var globalIndexById = cities
            .Select((city, index) => (city.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        foreach (var (countryCode, countryCities) in selected)
        {
            if (!countryTerritories.TryGetValue(countryCode, out var countryGeometry))
                throw new InvalidOperationException($"Missing Natural Earth territory for {countryCode}.");

            var countryPts = countryCities
                .Select(city => new Pt(city.Lon * cosLat, city.Lat))
                .ToList();

            var env = countryGeometry.EnvelopeInternal;
            var minX = env.MinX * cosLat - .5;
            var maxX = env.MaxX * cosLat + .5;
            var minY = env.MinY - .5;
            var maxY = env.MaxY + .5;

            for (var localIndex = 0; localIndex < countryCities.Count; localIndex++)
            {
                var poly = new List<Pt>
                {
                    new(minX, minY), new(maxX, minY),
                    new(maxX, maxY), new(minX, maxY)
                };

                for (var j = 0; j < countryPts.Count && poly.Count > 0; j++)
                {
                    if (localIndex == j) continue;
                    poly = ClipCloserTo(poly, countryPts[localIndex], countryPts[j]);
                }

                // Record mathematical Voronoi neighbours before any country clipping.
                // This is much more reliable than trying to rediscover adjacency from
                // separately clipped polygons afterwards.
                for (var j = 0; j < countryPts.Count; j++)
                {
                    if (localIndex == j) continue;
                    if (!SharesVoronoiEdge(poly, countryPts[localIndex], countryPts[j])) continue;

                    var aGlobal = globalIndexById[countryCities[localIndex].Id];
                    var bGlobal = globalIndexById[countryCities[j].Id];
                    voronoiCandidates.Add((Math.Min(aGlobal, bGlobal), Math.Max(aGlobal, bGlobal)));
                }

                var rawCell = ToPolygon(poly, cosLat);
                Geometry clipped;
                try
                {
                    clipped = rawCell.Intersection(countryGeometry);
                }
                catch
                {
                    clipped = rawCell.Buffer(0).Intersection(countryGeometry.Buffer(0));
                }

                if (!clipped.IsValid) clipped = clipped.Buffer(0);
                clippedCells[globalIndexById[countryCities[localIndex].Id]] = clipped;
            }
        }

        // Validate same-country mathematical neighbours against the clipped
        // geometries, then discover cross-border contacts separately.
        var adjacency = new HashSet<(int A, int B)>();

        foreach (var pair in voronoiCandidates)
        {
            if (ShareBoundarySegment(clippedCells[pair.A], clippedCells[pair.B]))
                adjacency.Add(pair);
        }

        // Cross-border adjacency: only compare cells from countries whose
        // Natural Earth envelopes touch. This avoids an O(n²) world scan.
        var indicesByCountry = cities
            .Select((city, index) => (Country: TerritoryCode(city), index))
            .GroupBy(x => x.Country, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.index).ToArray(), StringComparer.OrdinalIgnoreCase);
        var countryCodes = selected.Keys.ToArray();

        for (var ci = 0; ci < countryCodes.Length; ci++)
        {
            var codeA = countryCodes[ci];
            var envA = new Envelope(countryTerritories[codeA].EnvelopeInternal);
            envA.ExpandBy(1e-4);

            for (var cj = ci + 1; cj < countryCodes.Length; cj++)
            {
                var codeB = countryCodes[cj];
                if (!envA.Intersects(countryTerritories[codeB].EnvelopeInternal)) continue;

                foreach (var i in indicesByCountry[codeA])
                {
                    var a = clippedCells[i];
                    if (a.IsEmpty) continue;
                    foreach (var j in indicesByCountry[codeB])
                    {
                        var b = clippedCells[j];
                        if (b.IsEmpty) continue;
                        if (ShareBoundarySegment(a, b))
                            adjacency.Add((Math.Min(i, j), Math.Max(i, j)));
                    }
                }
            }
        }

        var degree = new int[cities.Count];
        foreach (var (a, b) in adjacency)
        {
            degree[a]++;
            degree[b]++;
        }

        var suspicious = cities
            .Select((city, index) => new { city, index, degree = degree[index] })
            .Where(x => x.degree <= 1)
            .ToList();

        if (suspicious.Count > 0)
        {
            Console.WriteLine(
                $"Voronoi adjacency warning: {suspicious.Count} cell(s) have <= 1 neighbour: " +
                string.Join(", ", suspicious.Take(20).Select(x => $"{x.city.Name}({x.degree})")) +
                (suspicious.Count > 20 ? "…" : ""));
        }

        var cellPayload = cities.Select((city, i) => new
        {
            id = city.Id,
            name = city.Name,
            country = city.Country,
            territoryCode = TerritoryCode(city),
            ownerCode = OwnerCode(TerritoryCode(city)),
            lat = city.Lat,
            lon = city.Lon,
            population = city.Population,
            geometry = GeometryToGeoJson(clippedCells[i])
        }).ToList();

        var edgePayload = adjacency
            .OrderBy(x => x.A).ThenBy(x => x.B)
            .Select(x => new
            {
                a = cities[x.A].Id,
                b = cities[x.B].Id,
                foreign = OwnerCode(TerritoryCode(cities[x.A])) != OwnerCode(TerritoryCode(cities[x.B])),
                aOwner = OwnerCode(TerritoryCode(cities[x.A])),
                bOwner = OwnerCode(TerritoryCode(cities[x.B])),
                km = Math.Round(Geo.Km(cities[x.A], cities[x.B]), 1)
            }).ToList();

        var jsonCells = JsonSerializer.Serialize(cellPayload);
        var jsonEdges = JsonSerializer.Serialize(edgePayload);
        var quotaText = $"{cities.Count:N0} villes · {selected.Count} pays/territoires";

        var html = """
<!doctype html><html lang="fr"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="refresh" content="0; url=territory-lab.html">
<title>World Conquest — Voronoï Lab</title>
<style>body{font-family:system-ui;background:#071022;color:#eef3ff;padding:2rem}a{color:#9ecbff}</style>
</head><body>
<p>Le laboratoire Voronoï mondial est désormais intégré au Territory Lab.</p>
<p><a href="territory-lab.html">Ouvrir le Territory Lab</a></p>
</body></html>
""";

        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "voronoi.html"), html);
        await File.WriteAllTextAsync(
            Path.Combine(outDir, "voronoi-graph.json"),
            JsonSerializer.Serialize(
                new
                {
                    populationWeight,
                    quotas,
                    referenceLat = referenceLat * 180.0 / Math.PI,
                    generation = "worldwide country-constrained Voronoi formula C",
                    borderSource = "Natural Earth 1:10m admin-0 countries",
                    terrestrialAdjacencyCount = adjacency.Count,
                    cells = cellPayload,
                    edges = edgePayload
                }));

        var foreignEdges = edgePayload.Count(e => e.foreign);

        await WriteTerritoryAuditAsync(
            outDir,
            cities,
            clippedCells,
            adjacency,
            degree,
            countryTerritories);

        Console.WriteLine(
            $"Voronoi lab: {cities.Count} country-constrained cells, " +
            $"{edgePayload.Count} terrestrial adjacencies, {foreignEdges} cross-border adjacencies");
    }

    static async Task WriteTerritoryAuditAsync(
        string outDir,
        IReadOnlyList<City> cities,
        IReadOnlyList<Geometry> cells,
        IReadOnlySet<(int A, int B)> adjacency,
        IReadOnlyList<int> degree,
        IReadOnlyDictionary<string, Geometry> countryTerritories)
    {
        var empty = new List<object>();
        var invalid = new List<object>();
        var seedOutside = new List<object>();
        var seedDiagnostics = new List<object>();
        var lowDegree = new List<object>();

        for (var i = 0; i < cities.Count; i++)
        {
            var city = cities[i];
            var geometry = cells[i];

            var seed = GeometryFactory.CreatePoint(new Coordinate(city.Lon, city.Lat));
            var territory = countryTerritories[TerritoryCode(city)];
            var seedInTerritory = territory.Covers(seed);
            var distanceToTerritory = seedInTerritory ? 0.0 : territory.Distance(seed);

            if (geometry.IsEmpty || geometry.Dimension != Dimension.Surface || geometry.Area <= 0)
                empty.Add(new
                {
                    city.Id,
                    city.Name,
                    city.Country,
                    territoryCode = TerritoryCode(city),
                    city.Lat,
                    city.Lon,
                    seedInTerritory,
                    distanceToTerritoryDegrees = Math.Round(distanceToTerritory, 6)
                });

            if (!geometry.IsEmpty && !geometry.IsValid)
                invalid.Add(new { city.Id, city.Name, city.Country });

            if (!geometry.IsEmpty && !geometry.Covers(seed))
            {
                var distanceToCell = geometry.Distance(seed);
                var classification = seedInTerritory
                    ? "seed_inside_territory_but_outside_cell"
                    : "seed_outside_territory";

                var diagnostic = new
                {
                    city.Id,
                    city.Name,
                    city.Country,
                    city.Lat,
                    city.Lon,
                    classification,
                    seedInTerritory,
                    distanceToTerritoryDegrees = Math.Round(distanceToTerritory, 6),
                    distanceToCellDegrees = Math.Round(distanceToCell, 6),
                    cellAreaDegrees2 = Math.Round(geometry.Area, 8)
                };

                seedOutside.Add(diagnostic);
                seedDiagnostics.Add(diagnostic);
            }

            if (degree[i] <= 1)
                lowDegree.Add(new
                {
                    city.Id,
                    city.Name,
                    city.Country,
                    territoryCode = TerritoryCode(city),
                    degree = degree[i],
                    city.Lat,
                    city.Lon
                });
        }

        var idDuplicates = cities
            .GroupBy(x => x.Id)
            .Where(g => g.Count() > 1)
            .Select(g => new
            {
                id = g.Key,
                occurrences = g.Count(),
                cities = g.Select(x => new { x.Name, x.Country }).ToArray()
            })
            .ToArray();

        var selfEdges = adjacency.Count(x => x.A == x.B);
        var invalidEdgeIndices = adjacency.Count(x =>
            x.A < 0 || x.B < 0 || x.A >= cities.Count || x.B >= cities.Count);

        var degreeHistogram = degree
            .GroupBy(x => x)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count());

        var components = new List<List<int>>();
        var neighbours = Enumerable.Range(0, cities.Count)
            .Select(_ => new List<int>())
            .ToArray();

        foreach (var (a, b) in adjacency)
        {
            if (a < 0 || b < 0 || a >= cities.Count || b >= cities.Count || a == b) continue;
            neighbours[a].Add(b);
            neighbours[b].Add(a);
        }

        var visited = new bool[cities.Count];
        for (var start = 0; start < cities.Count; start++)
        {
            if (visited[start]) continue;
            var component = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            visited[start] = true;

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                component.Add(current);
                foreach (var next in neighbours[current])
                {
                    if (visited[next]) continue;
                    visited[next] = true;
                    stack.Push(next);
                }
            }

            components.Add(component);
        }

        var componentSummary = components
            .OrderByDescending(x => x.Count)
            .Select((component, index) => new
            {
                component = index + 1,
                cellCount = component.Count,
                countries = component
                    .Select(i => cities[i].Country)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToArray(),
                sampleCities = component
                    .Take(8)
                    .Select(i => new { cities[i].Id, cities[i].Name, cities[i].Country })
                    .ToArray()
            })
            .ToArray();

        var territoryCoverage = cities
            .Select((city, index) => (city, index))
            .GroupBy(x => TerritoryCode(x.city), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var union = UnaryUnionOp.Union(g.Select(x => cells[x.index]).Where(x => !x.IsEmpty).ToArray());
                var target = countryTerritories[g.Key];
                var targetArea = Math.Max(1e-12, target.Area);
                var unionArea = Math.Max(0.0, union.Area);

                // For the audit we only need a robust coverage diagnostic.
                // Overlay Difference can throw on tiny non-noded intersections
                // created by floating-point clipping. Use area delta as the
                // primary metric and keep topology overlay out of the audit path.
                var areaDeltaRatio = Math.Abs(unionArea - target.Area) / targetArea;

                return new
                {
                    territoryCode = g.Key,
                    cityCount = g.Count(),
                    targetAreaDegrees2 = Math.Round(target.Area, 8),
                    unionAreaDegrees2 = Math.Round(unionArea, 8),
                    areaDeltaRatio = Math.Round(areaDeltaRatio, 8)
                };
            })
            .ToArray();

        var coverageWarnings = territoryCoverage
            .Where(x => x.areaDeltaRatio > 1e-6)
            .OrderByDescending(x => x.areaDeltaRatio)
            .ToArray();

        var seedOutsideTerritoryCount = seedDiagnostics.Count(x =>
        {
            var json = JsonSerializer.Serialize(x);
            return json.Contains("\"classification\":\"seed_outside_territory\"", StringComparison.Ordinal);
        });
        var seedInsideTerritoryOutsideCellCount = seedDiagnostics.Count - seedOutsideTerritoryCount;

        var emptyOutsideTerritoryCount = empty.Count(x =>
        {
            var json = JsonSerializer.Serialize(x);
            return json.Contains("\"seedInTerritory\":false", StringComparison.Ordinal);
        });
        var emptyInsideTerritoryCount = empty.Count - emptyOutsideTerritoryCount;

        var maxDegree = degree.Count == 0 ? 0 : degree.Max();
        var meanDegree = degree.Count == 0 ? 0 : degree.Average();

        var audit = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            status = "diagnostic",
            summary = new
            {
                cellCount = cities.Count,
                territoryCount = cities.Select(TerritoryCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                edgeCount = adjacency.Count,
                meanDegree = Math.Round(meanDegree, 3),
                maxDegree,
                zeroNeighbourCount = degree.Count(x => x == 0),
                oneNeighbourCount = degree.Count(x => x == 1),
                emptyGeometryCount = empty.Count,
                invalidGeometryCount = invalid.Count,
                seedOutsideCellCount = seedOutside.Count,
                seedOutsideTerritoryCount,
                seedInsideTerritoryOutsideCellCount,
                emptyCellSeedOutsideTerritoryCount = emptyOutsideTerritoryCount,
                emptyCellSeedInsideTerritoryCount = emptyInsideTerritoryCount,
                duplicateCityIdCount = idDuplicates.Length,
                selfEdgeCount = selfEdges,
                invalidEdgeIndexCount = invalidEdgeIndices,
                connectedComponentCount = components.Count,
                coverageWarningCount = coverageWarnings.Length
            },
            degreeHistogram,
            zeroOrOneNeighbourCities = lowDegree,
            emptyGeometries = empty,
            invalidGeometries = invalid,
            seedsOutsideCells = seedOutside,
            seedDiagnosticSummary = new
            {
                outsideTerritory = seedOutsideTerritoryCount,
                insideTerritoryButOutsideCell = seedInsideTerritoryOutsideCellCount,
                emptyCellSeedOutsideTerritory = emptyOutsideTerritoryCount,
                emptyCellSeedInsideTerritory = emptyInsideTerritoryCount
            },
            duplicateCityIds = idDuplicates,
            connectedComponents = componentSummary,
            territoryCoverageWarnings = coverageWarnings
        };

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "territory-audit.json"),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));

        var md = new StringBuilder();
        md.AppendLine("# World Conquest — territorial graph audit");
        md.AppendLine();
        md.AppendLine("**Status: DIAGNOSTIC.** This report measures the generated worldwide Voronoi/adjacency graph; it does not automatically classify every low-degree island as an error.");
        md.AppendLine();
        md.AppendLine($"- Cells: **{cities.Count:N0}**");
        md.AppendLine($"- Territories: **{audit.summary.territoryCount:N0}**");
        md.AppendLine($"- Terrestrial edges: **{adjacency.Count:N0}**");
        md.AppendLine($"- Mean degree: **{meanDegree:F2}**");
        md.AppendLine($"- Max degree: **{maxDegree}**");
        md.AppendLine($"- 0-neighbour cells: **{audit.summary.zeroNeighbourCount:N0}**");
        md.AppendLine($"- 1-neighbour cells: **{audit.summary.oneNeighbourCount:N0}**");
        md.AppendLine($"- Empty geometries: **{empty.Count:N0}**");
        md.AppendLine($"- Invalid geometries: **{invalid.Count:N0}**");
        md.AppendLine($"- Seeds outside their cell: **{seedOutside.Count:N0}**");
        md.AppendLine($"  - already outside Natural Earth territory: **{seedOutsideTerritoryCount:N0}**");
        md.AppendLine($"  - inside territory but outside final cell: **{seedInsideTerritoryOutsideCellCount:N0}**");
        md.AppendLine($"- Empty-cell seeds outside territory: **{emptyOutsideTerritoryCount:N0}**");
        md.AppendLine($"- Empty-cell seeds inside territory: **{emptyInsideTerritoryCount:N0}**");
        md.AppendLine($"- Duplicate city ids: **{idDuplicates.Length:N0}**");
        md.AppendLine($"- Connected terrestrial components: **{components.Count:N0}**");
        md.AppendLine($"- Territory coverage warnings: **{coverageWarnings.Length:N0}**");
        md.AppendLine();
        md.AppendLine("## Cells with 0 or 1 terrestrial neighbour");
        md.AppendLine();
        foreach (dynamic x in lowDegree)
            md.AppendLine($"- {x.Name} ({x.Country}) — degree {x.degree} — id {x.Id}");
        md.AppendLine();
        md.AppendLine("## Seeds outside their generated cell");
        md.AppendLine();
        if (seedOutside.Count == 0) md.AppendLine("- None.");
        else foreach (dynamic x in seedOutside) md.AppendLine($"- {x.Name} ({x.Country}) — id {x.Id}");
        md.AppendLine();
        md.AppendLine("## Territory coverage warnings");
        md.AppendLine();
        if (coverageWarnings.Length == 0) md.AppendLine("- None.");
        else
        {
            foreach (var x in coverageWarnings.Take(100))
                md.AppendLine($"- {x.territoryCode}: area delta {x.areaDeltaRatio:P6}");
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "territory-audit.md"), md.ToString());

        Console.WriteLine(
            $"Territory audit: degree<=1={lowDegree.Count}, empty={empty.Count}, invalid={invalid.Count}, " +
            $"seedOutside={seedOutside.Count}, components={components.Count}, coverageWarnings={coverageWarnings.Length}.");
    }

    static Polygon ToPolygon(List<Pt> poly, double cosLat)
    {
        if (poly.Count < 3) return GeometryFactory.CreatePolygon();

        var coords = poly
            .Select(p => new Coordinate(p.X / cosLat, p.Y))
            .ToList();

        if (!coords[0].Equals2D(coords[^1]))
            coords.Add(new Coordinate(coords[0]));

        return GeometryFactory.CreatePolygon(coords.ToArray());
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
            var polygons = Enumerable.Range(0, multi.NumGeometries)
                .Select(i => PolygonCoordinates((Polygon)multi.GetGeometryN(i)))
                .ToArray();
            return new { type = "MultiPolygon", coordinates = polygons };
        }

        // Intersection can exceptionally return a geometry collection.
        var polygonParts = Enumerable.Range(0, geometry.NumGeometries)
            .Select(i => geometry.GetGeometryN(i))
            .OfType<Polygon>()
            .ToArray();

        if (polygonParts.Length == 1)
            return new { type = "Polygon", coordinates = PolygonCoordinates(polygonParts[0]) };

        if (polygonParts.Length > 1)
            return new
            {
                type = "MultiPolygon",
                coordinates = polygonParts.Select(PolygonCoordinates).ToArray()
            };

        return null;
    }

    static async Task ApplyMissingIslandPatchesAsync(
        HttpClient http,
        IDictionary<string, Geometry> territories,
        IReadOnlyList<City> cities)
    {
        var targets = cities.Where(c => MissingIslandCityIds.Contains(c.Id)).ToArray();
        if (targets.Length == 0) return;

        var cacheDir = Path.Combine("data", "raw", "natural-earth");
        Directory.CreateDirectory(cacheDir);
        var fileName = "ne_10m_minor_islands.geojson";
        var path = Path.Combine(cacheDir, fileName);
        if (!File.Exists(path))
        {
            var url =
                "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/" + fileName;
            var json = await http.GetStringAsync(url);
            await File.WriteAllTextAsync(path, json);
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var islandGeometries = document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => ParseGeoJsonGeometry(feature.GetProperty("geometry")))
            .Where(g => g is not null && !g.IsEmpty)
            .Cast<Geometry>()
            .ToArray();

        foreach (var city in targets)
        {
            var territoryCode = TerritoryCode(city);
            if (!territories.TryGetValue(territoryCode, out var territory)) continue;

            var seed = GeometryFactory.CreatePoint(new Coordinate(city.Lon, city.Lat));
            if (territory.Covers(seed)) continue;

            var matches = islandGeometries
                .Select(g => new { Geometry = g, Distance = g.Distance(seed) })
                .Where(x => x.Geometry.Covers(seed) || x.Distance <= 0.08)
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Geometry.Area)
                .ToArray();

            if (matches.Length == 0)
            {
                Console.WriteLine(
                    $"Minor-island patch unresolved: {city.Name} ({territoryCode}) — no Natural Earth island near seed.");
                continue;
            }

            // Use every polygon effectively touching the seed cluster. This keeps
            // archipelago fragments together without inventing synthetic land.
            var bestDistance = matches[0].Distance;
            var patchParts = matches
                .Where(x => x.Distance <= Math.Max(0.01, bestDistance + 0.01))
                .Select(x => x.Geometry)
                .ToArray();

            var patch = UnaryUnionOp.Union(patchParts);
            Geometry merged;
            try
            {
                merged = UnaryUnionOp.Union(new[] { territory, patch });
            }
            catch
            {
                merged = UnaryUnionOp.Union(new[] { territory.Buffer(0), patch.Buffer(0) });
            }

            if (!merged.IsValid) merged = merged.Buffer(0);
            territories[territoryCode] = merged;

            Console.WriteLine(
                $"Minor-island patch: {city.Name} ({territoryCode}) +{patchParts.Length} polygon(s), " +
                $"nearest={bestDistance:F4}°.");
        }
    }

    static async Task<Dictionary<string, Geometry>> LoadCountryTerritoriesAsync(
        HttpClient http,
        IEnumerable<string> countryCodes)
    {
        var wanted = countryCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partsByCode = wanted.ToDictionary(
            code => code,
            _ => new List<Geometry>(),
            StringComparer.OrdinalIgnoreCase);
        var cacheDir = Path.Combine("data", "raw", "natural-earth");
        Directory.CreateDirectory(cacheDir);

        // Do not stop at the first layer that resolves a territory. Natural Earth
        // splits some islands, overseas units and disputed areas across countries,
        // map-units and subunits. Accumulate every matching polygon from all three
        // layers, then union them per territory.
        var sources = new[]
        {
            "ne_10m_admin_0_countries_iso.geojson",
            "ne_10m_admin_0_map_units.geojson",
            "ne_10m_admin_0_map_subunits.geojson",
            "ne_10m_admin_0_scale_rank_minor_islands.geojson"
        };

        foreach (var fileName in sources)
        {
            var path = Path.Combine(cacheDir, fileName);
            if (!File.Exists(path))
            {
                var url =
                    "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/" + fileName;
                var json = await http.GetStringAsync(url);
                await File.WriteAllTextAsync(path, json);
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var matchedFeatures = 0;

            foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                var codes = ReadTerritoryCodes(properties, wanted).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (codes.Length == 0) continue;

                var geometry = ParseGeoJsonGeometry(feature.GetProperty("geometry"));
                if (geometry is null || geometry.IsEmpty) continue;

                foreach (var code in codes)
                    partsByCode[code].Add(geometry);

                matchedFeatures++;
            }

            Console.WriteLine(
                $"Natural Earth territory source {fileName}: {matchedFeatures:N0} matching feature(s).");
        }

        if (wanted.Contains(CrimeaTerritoryCode))
        {
            var disputedFile = "ne_10m_admin_0_disputed_areas.geojson";
            var disputedPath = Path.Combine(cacheDir, disputedFile);
            if (!File.Exists(disputedPath))
            {
                var url =
                    "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/" + disputedFile;
                var json = await http.GetStringAsync(url);
                await File.WriteAllTextAsync(disputedPath, json);
            }

            using var disputedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(disputedPath));
            foreach (var feature in disputedDocument.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                string? name = null;
                foreach (var key in new[] { "NAME", "NAME_LONG", "SUBUNIT", "BRK_NAME" })
                {
                    if (!properties.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                        continue;
                    name = value.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) break;
                }

                if (!string.Equals(name, "Crimea", StringComparison.OrdinalIgnoreCase))
                    continue;

                var geometry = ParseGeoJsonGeometry(feature.GetProperty("geometry"));
                if (geometry is not null && !geometry.IsEmpty)
                    partsByCode[CrimeaTerritoryCode].Add(geometry);
            }
        }

        var result = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in wanted.OrderBy(x => x))
        {
            var parts = partsByCode[code];
            if (parts.Count == 0) continue;

            Geometry geometry;
            try
            {
                geometry = UnaryUnionOp.Union(parts);
            }
            catch
            {
                geometry = UnaryUnionOp.Union(parts.Select(x => x.Buffer(0)).ToArray());
            }

            if (!geometry.IsValid) geometry = geometry.Buffer(0);
            result[code] = geometry;
        }

        var missing = wanted.Where(code => !result.ContainsKey(code)).OrderBy(x => x).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                "Natural Earth territory mapping missing for: " + string.Join(", ", missing));

        Console.WriteLine($"Natural Earth territories loaded from merged sources: {result.Count}/{wanted.Count}.");
        return result;
    }

    static IEnumerable<string> ReadTerritoryCodes(
        JsonElement properties,
        IReadOnlySet<string> wanted)
    {
        var direct = ReadIso2(properties);
        if (direct is not null && wanted.Contains(direct))
            yield return direct;

        string? ReadName(string key)
        {
            if (!properties.TryGetProperty(key, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        var names = new[]
        {
            ReadName("NAME"), ReadName("NAME_LONG"), ReadName("ADMIN"),
            ReadName("GEOUNIT"), ReadName("SUBUNIT"), ReadName("BRK_NAME"),
            ReadName("FORMAL_EN")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        foreach (var (code, aliases) in TerritoryNameAliases)
        {
            if (!wanted.Contains(code)) continue;
            if (names.Any(name => aliases.Any(alias =>
                    string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))))
                yield return code;
        }

        // Natural Earth often encodes Taiwan/Kosovo with non-ISO admin-0 codes.
        if (properties.TryGetProperty("ADM0_A3", out var a3) && a3.ValueKind == JsonValueKind.String)
        {
            var code = a3.GetString() switch
            {
                "TWN" => "TW",
                "KOS" => "XK",
                _ => null
            };
            if (code is not null && wanted.Contains(code))
                yield return code;
        }
    }

    static string? ReadIso2(JsonElement properties)
    {
        foreach (var key in new[] { "ISO_A2", "ISO_A2_EH", "WB_A2", "POSTAL" })
        {
            if (!properties.TryGetProperty(key, out var value)) continue;
            var code = value.GetString();
            if (!string.IsNullOrWhiteSpace(code) && code != "-99")
                return code;
        }

        return null;
    }

    static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("type", out var typeElement)) return null;
        if (!geometry.TryGetProperty("coordinates", out var coordinates)) return null;

        return typeElement.GetString() switch
        {
            "Polygon" => ParsePolygon(coordinates),
            "MultiPolygon" => GeometryFactory.CreateMultiPolygon(
                coordinates.EnumerateArray().Select(ParsePolygon).ToArray()),
            _ => null
        };
    }

    static Polygon ParsePolygon(JsonElement polygonCoordinates)
    {
        var rings = polygonCoordinates.EnumerateArray()
            .Select(ParseRing)
            .ToArray();

        if (rings.Length == 0)
            return GeometryFactory.CreatePolygon();

        return GeometryFactory.CreatePolygon(
            rings[0],
            rings.Skip(1).ToArray());
    }

    static LinearRing ParseRing(JsonElement ringCoordinates)
    {
        var coords = ringCoordinates.EnumerateArray()
            .Select(point =>
            {
                var values = point.EnumerateArray().ToArray();
                return new Coordinate(values[0].GetDouble(), values[1].GetDouble());
            })
            .ToList();

        if (coords.Count > 0 && !coords[0].Equals2D(coords[^1]))
            coords.Add(new Coordinate(coords[0]));

        return GeometryFactory.CreateLinearRing(coords.ToArray());
    }

    static bool SharesVoronoiEdge(List<Pt> poly, Pt site, Pt other)
    {
        if (poly.Count < 2) return false;

        var a = other.X - site.X;
        var b = other.Y - site.Y;
        var c = (other.X * other.X + other.Y * other.Y
               - site.X * site.X - site.Y * site.Y) / 2.0;
        var scale = Math.Max(1.0, Math.Sqrt(a * a + b * b));
        var tolerance = 1e-7 * scale;

        for (var i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];

            var dp = Math.Abs(a * p.X + b * p.Y - c);
            var dq = Math.Abs(a * q.X + b * q.Y - c);
            if (dp > tolerance || dq > tolerance) continue;

            var len = Math.Sqrt(
                (p.X - q.X) * (p.X - q.X) +
                (p.Y - q.Y) * (p.Y - q.Y));

            if (len > 1e-6) return true;
        }

        return false;
    }

    static bool ShareBoundarySegment(Geometry a, Geometry b)
    {
        if (a.IsEmpty || b.IsEmpty) return false;

        const double tolerance = 1e-5; // about one metre in latitude

        var envA = new Envelope(a.EnvelopeInternal);
        envA.ExpandBy(tolerance);
        if (!envA.Intersects(b.EnvelopeInternal)) return false;

        try
        {
            var shared = a.Boundary.Intersection(b.Boundary);
            if (!shared.IsEmpty && shared.Length > 1e-6) return true;
        }
        catch
        {
            // Fall through to the tolerance-based test below.
        }

        // Natural Earth borders and independently clipped cells can differ by tiny
        // floating-point amounts. A buffered boundary overlap recovers real shared
        // segments while rejecting ordinary point-only corner contacts.
        try
        {
            var overlap = a.Boundary.Buffer(tolerance).Intersection(b.Boundary.Buffer(tolerance));
            return !overlap.IsEmpty && overlap.Area > 5e-10;
        }
        catch
        {
            return false;
        }
    }

    static List<Pt> ClipCloserTo(List<Pt> poly, Pt site, Pt other)
    {
        if (poly.Count == 0) return poly;

        var a = other.X - site.X;
        var b = other.Y - site.Y;
        var c = (other.X * other.X + other.Y * other.Y - site.X * site.X - site.Y * site.Y) / 2.0;

        static double Eval(Pt p, double a, double b, double c) => a * p.X + b * p.Y - c;
        const double eps = 1e-10;

        var result = new List<Pt>();
        var prev = poly[^1];
        var prevValue = Eval(prev, a, b, c);
        var prevInside = prevValue <= eps;

        foreach (var curr in poly)
        {
            var currValue = Eval(curr, a, b, c);
            var currInside = currValue <= eps;

            if (currInside != prevInside)
            {
                var denom = prevValue - currValue;
                if (Math.Abs(denom) > 1e-15)
                {
                    var t = prevValue / denom;
                    result.Add(new Pt(
                        prev.X + (curr.X - prev.X) * t,
                        prev.Y + (curr.Y - prev.Y) * t));
                }
            }

            if (currInside) result.Add(curr);
            prev = curr;
            prevValue = currValue;
            prevInside = currInside;
        }

        return result;
    }

    static async Task<List<City>> LoadAsync(HttpClient http, string code, long minPopulation)
    {
        var cacheDir = Path.Combine("data", "raw", "geonames");
        Directory.CreateDirectory(cacheDir);
        var zipPath = Path.Combine(cacheDir, code + ".zip");

        if (!File.Exists(zipPath))
        {
            var url = $"https://download.geonames.org/export/dump/{code}.zip";
            await using var input = await http.GetStreamAsync(url);
            await using var output = File.Create(zipPath);
            await input.CopyToAsync(output);
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(code + ".txt") ?? throw new InvalidOperationException($"Missing {code}.txt");
        using var reader = new StreamReader(entry.Open());
        var cities = new List<City>();

        while (await reader.ReadLineAsync() is { } line)
        {
            var f = line.Split('\t');
            if (f.Length < 15 || f[6] != "P") continue;
            if (!long.TryParse(f[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop) || pop < minPopulation) continue;
            if (!long.TryParse(f[0], out var id)) continue;
            if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            cities.Add(new City(id, f[1], code, lat, lon, pop));
        }

        return cities;
    }
}
