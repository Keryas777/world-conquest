using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

static class WorldSelectionLab
{
    sealed record CountryInfo(string Code, string Name, double AreaKm2, long Population);
    sealed record Formula(string Id, double AreaExponent, double PopulationExponent);

    static readonly Formula[] Formulas =
    {
        // Formula C is the current worldwide working hypothesis.
        new("C", .40, .20)
    };

    const int FranceReferenceQuota = 85;
    const long MinPopulation = 500;
    const double PopulationWeight = .60;
    const double CrossBorderDiagnosticKm = 25.0;

    public static async Task<Dictionary<string, List<City>>> GenerateAsync(HttpClient http, string outDir)
    {
        var countries = await LoadCountryInfoAsync(http);
        if (!countries.TryGetValue("FR", out var france))
            throw new InvalidOperationException("France missing from GeoNames countryInfo.");

        var candidates = await LoadGlobalCitiesAsync(http, countries.Keys);
        Console.WriteLine($"World selection lab: {candidates.Values.Sum(x => x.Count):N0} candidates in {candidates.Count} countries.");

        var summary = new List<object>();
        Dictionary<string, List<City>>? finalSelection = null;

        foreach (var formula in Formulas)
        {
            var selected = new Dictionary<string, List<City>>(StringComparer.OrdinalIgnoreCase);
            var quotas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var country in countries.Values.OrderBy(c => c.Code))
            {
                if (!candidates.TryGetValue(country.Code, out var pool) || pool.Count == 0)
                    continue;

                var raw = FranceReferenceQuota
                    * Math.Pow(country.AreaKm2 / france.AreaKm2, formula.AreaExponent)
                    * Math.Pow((double)Math.Max(1, country.Population) / Math.Max(1, france.Population), formula.PopulationExponent);
                var quota = Math.Clamp((int)Math.Round(raw), 1, 450);
                quota = Math.Min(quota, pool.Count);

                quotas[country.Code] = quota;
                selected[country.Code] = Selector.Select(pool, quota, PopulationWeight);
            }

            var beforeSpacing = selected.Values.SelectMany(x => x).ToList();
            var initialClosePairs = FindCrossBorderPairs(beforeSpacing, CrossBorderDiagnosticKm);
            var spacingChanges = ApplyCrossBorderSpacing(selected, candidates, CrossBorderDiagnosticKm);
            var all = selected.Values.SelectMany(x => x).ToList();
            var closePairs = FindCrossBorderPairs(all, CrossBorderDiagnosticKm);

            var countryRows = selected
                .OrderBy(x => x.Key)
                .Select(x =>
                {
                    var info = countries[x.Key];
                    var stats = Results.NearestStats(x.Value);
                    return new
                    {
                        code = x.Key,
                        name = info.Name,
                        areaKm2 = info.AreaKm2,
                        population = info.Population,
                        quota = quotas[x.Key],
                        selected = x.Value.Count,
                        nearestMeanKm = Math.Round(stats.Mean, 1),
                        nearestMinKm = Math.Round(stats.Min, 1),
                        nearestMaxKm = Math.Round(stats.Max, 1)
                    };
                }).ToList();

            var payload = new
            {
                formula = formula.Id,
                status = "experimental",
                franceReferenceQuota = FranceReferenceQuota,
                populationWeight = PopulationWeight,
                coverageWeight = 1 - PopulationWeight,
                minimumPopulation = MinPopulation,
                crossBorderDiagnosticKm = CrossBorderDiagnosticKm,
                countryCount = selected.Count,
                cityCount = all.Count,
                initialCloseCrossBorderPairCount = initialClosePairs.Count,
                closeCrossBorderPairCount = closePairs.Count,
                spacingReplacementCount = spacingChanges.Count,
                spacingReplacements = spacingChanges,
                countries = countryRows,
                closeCrossBorderPairs = closePairs.Select(x => new
                {
                    aId = x.A.Id, aName = x.A.Name, aCountry = x.A.Country,
                    bId = x.B.Id, bName = x.B.Name, bCountry = x.B.Country,
                    km = Math.Round(x.Km, 2)
                })
            };

            await File.WriteAllTextAsync(
                Path.Combine(outDir, $"world-selection-{formula.Id}.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

            Results.WriteCsv(
                Path.Combine(outDir, $"world-selected-{formula.Id}.csv"),
                all);

            await WriteMarkdownAsync(
                Path.Combine(outDir, $"world-selection-{formula.Id}.md"),
                formula.Id, countryRows, all.Count, closePairs);

            Console.WriteLine(
                $"World formula {formula.Id}: {selected.Count} countries, {all.Count:N0} cities, " +
                $"{initialClosePairs.Count} -> {closePairs.Count} cross-border pair(s) < {CrossBorderDiagnosticKm:0} km, " +
                $"{spacingChanges.Count} replacement(s).");

            foreach (var pair in closePairs.Take(40))
                Console.WriteLine(
                    $"  {pair.A.Name} ({pair.A.Country}) <-> {pair.B.Name} ({pair.B.Country}) : {pair.Km:F1} km");

            summary.Add(new
            {
                formula = formula.Id,
                countries = selected.Count,
                cities = all.Count,
                closeCrossBorderPairs25Km = closePairs.Count
            });
            finalSelection = selected;
        }

        await File.WriteAllTextAsync(
            Path.Combine(outDir, "world-selection-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        return finalSelection ?? throw new InvalidOperationException("World selection produced no result.");
    }

    static async Task<Dictionary<string, CountryInfo>> LoadCountryInfoAsync(HttpClient http)
    {
        var cacheDir = Path.Combine("data", "raw", "geonames");
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, "countryInfo.txt");

        if (!File.Exists(path))
        {
            var text = await http.GetStringAsync("https://download.geonames.org/export/dump/countryInfo.txt");
            await File.WriteAllTextAsync(path, text);
        }

        var result = new Dictionary<string, CountryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 9) continue;
            var code = f[0];
            var name = f[4];
            if (!double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var area) || area <= 0) continue;
            if (!long.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop) || pop <= 0) continue;
            result[code] = new CountryInfo(code, name, area, pop);
        }
        return result;
    }

    static async Task<Dictionary<string, List<City>>> LoadGlobalCitiesAsync(
        HttpClient http,
        IEnumerable<string> allowedCountries)
    {
        var allowed = allowedCountries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cacheDir = Path.Combine("data", "raw", "geonames");
        Directory.CreateDirectory(cacheDir);
        var zipPath = Path.Combine(cacheDir, "cities500.zip");

        if (!File.Exists(zipPath))
        {
            var url = "https://download.geonames.org/export/dump/cities500.zip";
            Console.WriteLine($"Downloading {url}");
            await using var input = await http.GetStreamAsync(url);
            await using var output = File.Create(zipPath);
            await input.CopyToAsync(output);
        }

        var result = new Dictionary<string, List<City>>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("cities500.txt")
            ?? throw new InvalidOperationException("Missing cities500.txt.");
        using var reader = new StreamReader(entry.Open());

        while (await reader.ReadLineAsync() is { } line)
        {
            var f = line.Split('\t');
            if (f.Length < 15 || f[6] != "P") continue;
            var code = f[8];
            if (!allowed.Contains(code)) continue;
            if (!long.TryParse(f[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop) || pop < MinPopulation) continue;
            if (!long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;

            if (!result.TryGetValue(code, out var list))
                result[code] = list = new List<City>();
            list.Add(new City(id, f[1], code, lat, lon, pop));
        }

        return result;
    }

    static List<object> ApplyCrossBorderSpacing(
        Dictionary<string, List<City>> selected,
        IReadOnlyDictionary<string, List<City>> candidates,
        double thresholdKm)
    {
        var changes = new List<object>();
        var unresolved = new HashSet<(long A, long B)>();
        const int maxPasses = 2000;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var all = selected.Values.SelectMany(x => x).ToList();
            var conflicts = FindCrossBorderPairs(all, thresholdKm);
            var conflict = conflicts.FirstOrDefault(x =>
            {
                var key = (Math.Min(x.A.Id, x.B.Id), Math.Max(x.A.Id, x.B.Id));
                return !unresolved.Contains(key);
            });

            if (conflict.A is null || conflict.B is null) break;

            // Try replacing the less populous endpoint first. If that country's
            // quota cannot be preserved with a valid candidate, try the other side
            // before classifying this particular pair as irreducible.
            var attempts = conflict.A.Population >= conflict.B.Population
                ? new[] { (Keep: conflict.A, Remove: conflict.B), (Keep: conflict.B, Remove: conflict.A) }
                : new[] { (Keep: conflict.B, Remove: conflict.A), (Keep: conflict.A, Remove: conflict.B) };

            var resolved = false;
            foreach (var attempt in attempts)
            {
                var keep = attempt.Keep;
                var remove = attempt.Remove;
                var countryList = selected[remove.Country];
                var selectedIds = all.Select(x => x.Id).ToHashSet();

                City? replacement = null;
                double bestScore = double.NegativeInfinity;
                var maxPop = Math.Max(1L, candidates[remove.Country].Max(x => x.Population));

                foreach (var candidate in candidates[remove.Country])
                {
                    if (selectedIds.Contains(candidate.Id)) continue;
                    if (all.Any(x => x.Id != remove.Id && x.Country != candidate.Country &&
                                     Geo.Km(candidate, x) < thresholdKm)) continue;

                    var sameCountry = countryList.Where(x => x.Id != remove.Id).ToList();
                    var nearest = sameCountry.Count == 0 ? 1000.0 : sameCountry.Min(x => Geo.Km(candidate, x));
                    var popScore = Math.Log10(candidate.Population + 10.0) / Math.Log10(maxPop + 10.0);
                    var coverageScore = Math.Min(1.0, nearest / 250.0);
                    var score = PopulationWeight * popScore + (1 - PopulationWeight) * coverageScore;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        replacement = candidate;
                    }
                }

                if (replacement is null) continue;

                countryList.RemoveAll(x => x.Id == remove.Id);
                countryList.Add(replacement);
                changes.Add(new
                {
                    removedId = remove.Id,
                    removedName = remove.Name,
                    country = remove.Country,
                    keptId = keep.Id,
                    keptName = keep.Name,
                    replacementId = replacement.Id,
                    replacementName = replacement.Name,
                    originalConflictKm = Math.Round(conflict.Km, 2)
                });
                resolved = true;
                break;
            }

            if (!resolved)
            {
                var key = (Math.Min(conflict.A.Id, conflict.B.Id), Math.Max(conflict.A.Id, conflict.B.Id));
                unresolved.Add(key);
                Console.WriteLine(
                    $"25 km spacing irreducible: {conflict.A.Name} ({conflict.A.Country}) / " +
                    $"{conflict.B.Name} ({conflict.B.Country}) : {conflict.Km:F1} km");
            }
        }

        var remaining = FindCrossBorderPairs(selected.Values.SelectMany(x => x).ToList(), thresholdKm);
        Console.WriteLine(
            $"25 km spacing final: {changes.Count} replacement(s), " +
            $"{remaining.Count} remaining pair(s), {unresolved.Count} classified irreducible.");

        return changes;
    }

    static List<(City A, City B, double Km)> FindCrossBorderPairs(IReadOnlyList<City> cities, double thresholdKm)
    {
        // Spatial bucketing avoids an O(n²) world scan.
        const double cellDegrees = .25;
        var buckets = new Dictionary<(int X, int Y), List<City>>();
        static int B(double v) => (int)Math.Floor(v / cellDegrees);

        foreach (var city in cities)
        {
            var key = (B(city.Lon), B(city.Lat));
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<City>();
            list.Add(city);
        }

        var result = new List<(City A, City B, double Km)>();
        var seen = new HashSet<(long, long)>();

        foreach (var city in cities)
        {
            var bx = B(city.Lon);
            var by = B(city.Lat);
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
            {
                if (!buckets.TryGetValue((bx + dx, by + dy), out var others)) continue;
                foreach (var other in others)
                {
                    if (city.Country == other.Country || city.Id == other.Id) continue;
                    var lo = Math.Min(city.Id, other.Id);
                    var hi = Math.Max(city.Id, other.Id);
                    if (!seen.Add((lo, hi))) continue;
                    var km = Geo.Km(city, other);
                    if (km < thresholdKm)
                        result.Add((city, other, km));
                }
            }
        }

        return result.OrderBy(x => x.Km).ToList();
    }

    static async Task WriteMarkdownAsync(
        string path,
        string formula,
        IEnumerable<object> countryRows,
        int totalCities,
        IReadOnlyList<(City A, City B, double Km)> closePairs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Worldwide city-selection laboratory — formula {formula}");
        sb.AppendLine();
        sb.AppendLine("**Status: EXPERIMENTAL.** This report does not validate the quota formula or the 25 km rule.");
        sb.AppendLine();
        sb.AppendLine($"Selected cities: **{totalCities:N0}**");
        sb.AppendLine($"Cross-border pairs below 25 km: **{closePairs.Count:N0}**");
        sb.AppendLine();
        sb.AppendLine("## Closest cross-border pairs");
        sb.AppendLine();
        foreach (var p in closePairs.Take(100))
            sb.AppendLine($"- {p.A.Name} ({p.A.Country}) <-> {p.B.Name} ({p.B.Country}): {p.Km:F1} km");
        await File.WriteAllTextAsync(path, sb.ToString());
    }
}
