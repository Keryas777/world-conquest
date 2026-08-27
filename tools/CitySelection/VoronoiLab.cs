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
        var selected = worldSelection.ToDictionary(
            x => x.Key,
            x => x.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        var quotas = selected.ToDictionary(x => x.Key, x => x.Value.Count, StringComparer.OrdinalIgnoreCase);
        var cities = selected.Values.SelectMany(x => x).ToList();

        Console.WriteLine($"Worldwide Voronoi input: {cities.Count:N0} selected cities in {selected.Count} countries.");

        var referenceLat = cities.Average(c => c.Lat) * Math.PI / 180.0;
        var cosLat = Math.Cos(referenceLat);

        // Canonical initial territories: each country's cities partition only that
        // country's current Natural Earth 10m territory. This guarantees that at T0
        // the union of a country's cells reconstructs its present-day border/coastline.
        var countryTerritories = await LoadCountryTerritoriesAsync(http, quotas.Keys);
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
            .Select((city, index) => (city.Country, index))
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
            territoryCode = city.Country,
            ownerCode = OwnerCode(city.Country),
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
                foreign = OwnerCode(cities[x.A].Country) != OwnerCode(cities[x.B].Country),
                aOwner = OwnerCode(cities[x.A].Country),
                bOwner = OwnerCode(cities[x.B].Country),
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
        Console.WriteLine(
            $"Voronoi lab: {cities.Count} country-constrained cells, " +
            $"{edgePayload.Count} terrestrial adjacencies, {foreignEdges} cross-border adjacencies");
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

    static async Task<Dictionary<string, Geometry>> LoadCountryTerritoriesAsync(
        HttpClient http,
        IEnumerable<string> countryCodes)
    {
        var wanted = countryCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);
        var cacheDir = Path.Combine("data", "raw", "natural-earth");
        Directory.CreateDirectory(cacheDir);

        // Countries ISO handles almost everything. Map units/subunits provide
        // overseas and disputed geographic units that are intentionally absent
        // from the countries layer.
        var sources = new[]
        {
            "ne_10m_admin_0_countries_iso.geojson",
            "ne_10m_admin_0_map_units.geojson",
            "ne_10m_admin_0_map_subunits.geojson"
        };

        foreach (var fileName in sources)
        {
            var remaining = wanted.Where(code => !result.ContainsKey(code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (remaining.Count == 0) break;

            var path = Path.Combine(cacheDir, fileName);
            if (!File.Exists(path))
            {
                var url =
                    "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/" + fileName;
                var json = await http.GetStringAsync(url);
                await File.WriteAllTextAsync(path, json);
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var partsByCode = remaining.ToDictionary(
                code => code,
                _ => new List<Geometry>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                var codes = ReadTerritoryCodes(properties, remaining).ToArray();
                if (codes.Length == 0) continue;

                var geometry = ParseGeoJsonGeometry(feature.GetProperty("geometry"));
                if (geometry is null || geometry.IsEmpty) continue;

                foreach (var code in codes)
                    partsByCode[code].Add(geometry);
            }

            foreach (var code in remaining)
            {
                var parts = partsByCode[code];
                if (parts.Count == 0) continue;
                var geometry = UnaryUnionOp.Union(parts);
                if (!geometry.IsValid) geometry = geometry.Buffer(0);
                result[code] = geometry;
            }

            Console.WriteLine(
                $"Natural Earth territory source {fileName}: {result.Count}/{wanted.Count} resolved.");
        }

        var missing = wanted.Where(code => !result.ContainsKey(code)).OrderBy(x => x).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                "Natural Earth territory mapping missing for: " + string.Join(", ", missing));

        Console.WriteLine($"Natural Earth territories loaded: {result.Count}/{wanted.Count}.");
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
