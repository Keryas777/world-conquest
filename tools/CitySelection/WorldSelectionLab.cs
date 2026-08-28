using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

static class WorldSelectionLab
{
    sealed record CountryInfo(string Code, string Name, double AreaKm2, long Population);
    sealed record Formula(string Id, double AreaExponent, double PopulationExponent);
    sealed record InternalSpacingShrinkResult(
        string Code,
        double ThresholdKm,
        object Before,
        object After,
        int FinalCount,
        int QuotaReduction,
        int ReplacementCount,
        int RemovalWithoutReplacementCount,
        int RemainingPairCountBelowThreshold,
        List<object> Replacements,
        List<object> RemovalsWithoutReplacement);

    static readonly Formula[] Formulas =
    {
        // Formula C is the current worldwide working hypothesis.
        new("C", .40, .20)
    };

    const int FranceReferenceQuota = 85;
    const long MinPopulation = 500;
    const double PopulationWeight = .60;
    const double CrossBorderDiagnosticKm = 25.0;
    const double InternalSpacingCoefficient = 0.15;
    const double InternalSpacingMaxKm = 25.0;

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

            var internalThresholds = selected.Keys.ToDictionary(
                code => code,
                code => Math.Min(
                    InternalSpacingMaxKm,
                    InternalSpacingCoefficient * Math.Sqrt(countries[code].AreaKm2)),
                StringComparer.OrdinalIgnoreCase);

            var internalSpacingChanges = ApplyInternalSpacingAdaptive(
                selected,
                candidates,
                internalThresholds,
                CrossBorderDiagnosticKm);

            var spacingChanges = ApplyCrossBorderSpacing(
                selected,
                candidates,
                CrossBorderDiagnosticKm,
                internalThresholds);

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

            var internalSpacing25Km = new[] { "FR", "EG", "DZ" }
                .Where(code => selected.ContainsKey(code) && candidates.ContainsKey(code))
                .Select(code => BuildInternalSpacingTest(code, selected[code], candidates[code], 25.0))
                .ToArray();

            var internalSpacing25KmVariableQuota = selected.Keys
                .Where(code => candidates.ContainsKey(code))
                .OrderBy(code => code)
                .Select(code => BuildInternalSpacingShrinkTest(code, selected[code], candidates[code], 25.0))
                .ToArray();

            var spacingByCode25 = internalSpacing25KmVariableQuota.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
            var scaled25KmExperiments = new[] { 0.15, 0.20, 0.25 }
                .Select(coefficient =>
                {
                    var rows = selected.Keys
                        .OrderBy(code => code)
                        .Select(code =>
                        {
                            var area = countries[code].AreaKm2;
                            var threshold = Math.Min(25.0, coefficient * Math.Sqrt(area));
                            if (Math.Abs(threshold - 25.0) < 1e-9)
                                return spacingByCode25[code];
                            return BuildInternalSpacingShrinkTest(code, selected[code], candidates[code], threshold);
                        })
                        .ToArray();

                    var finalCount = rows.Sum(x => x.FinalCount);
                    var reduced = rows
                        .Where(x => x.QuotaReduction > 0)
                        .OrderByDescending(x => x.QuotaReduction)
                        .ThenBy(x => x.Code)
                        .Select(x => new
                        {
                            code = x.Code,
                            areaKm2 = countries[x.Code].AreaKm2,
                            thresholdKm = Math.Round(x.ThresholdKm, 1),
                            initial = x.FinalCount + x.QuotaReduction,
                            final = x.FinalCount,
                            reduction = x.QuotaReduction
                        })
                        .ToArray();

                    return new
                    {
                        coefficient,
                        rule = "min(25 km, coefficient * sqrt(areaKm2))",
                        initialCityCount = all.Count,
                        finalCityCount = finalCount,
                        quotaReduction = all.Count - finalCount,
                        reducedCountryCount = reduced.Length,
                        reducedCountries = reduced
                    };
                })
                .ToArray();

            var adaptiveTotalCities = internalSpacing25KmVariableQuota.Sum(x => x.FinalCount);
            var adaptiveQuotaReduction = all.Count - adaptiveTotalCities;
            var adaptiveReducedCountries = internalSpacing25KmVariableQuota
                .Where(x => x.QuotaReduction > 0)
                .OrderByDescending(x => x.QuotaReduction)
                .ThenBy(x => x.Code)
                .Select(x => new
                {
                    code = x.Code,
                    initial = x.FinalCount + x.QuotaReduction,
                    final = x.FinalCount,
                    reduction = x.QuotaReduction
                })
                .ToArray();

            Console.WriteLine(
                $"Worldwide 25 km adaptive spacing: {all.Count:N0} -> {adaptiveTotalCities:N0} cities " +
                $"(-{adaptiveQuotaReduction:N0}), {adaptiveReducedCountries.Length} country/countries reduced.");
            foreach (var row in adaptiveReducedCountries.Take(40))
                Console.WriteLine($"  {row.code}: {row.initial} -> {row.final} (-{row.reduction})");

            var payload = new
            {
                formula = formula.Id,
                status = "experimental",
                franceReferenceQuota = FranceReferenceQuota,
                populationWeight = PopulationWeight,
                coverageWeight = 1 - PopulationWeight,
                minimumPopulation = MinPopulation,
                crossBorderDiagnosticKm = CrossBorderDiagnosticKm,
                internalSpacingRule = new
                {
                    status = "validated",
                    coefficient = InternalSpacingCoefficient,
                    maxKm = InternalSpacingMaxKm,
                    formula = "min(25 km, 0.15 * sqrt(areaKm2))",
                    changedCountryCount = internalSpacingChanges.Select(x => x.Country).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    removalCount = internalSpacingChanges.Count(x => x.Kind == "remove"),
                    replacementCount = internalSpacingChanges.Count(x => x.Kind == "replace")
                },
                countryCount = selected.Count,
                cityCount = all.Count,
                initialCloseCrossBorderPairCount = initialClosePairs.Count,
                closeCrossBorderPairCount = closePairs.Count,
                crossBorderSpacingStatus = closePairs.Count == 3 &&
                    closePairs.All(p =>
                        new[] { "AI", "MF", "SX" }.Contains(p.A.Country) &&
                        new[] { "AI", "MF", "SX" }.Contains(p.B.Country))
                    ? "validated_with_microterritory_exceptions"
                    : "review_required",
                acceptedCrossBorderExceptions = closePairs
                    .Where(p =>
                        new[] { "AI", "MF", "SX" }.Contains(p.A.Country) &&
                        new[] { "AI", "MF", "SX" }.Contains(p.B.Country))
                    .Select(p => new
                    {
                        aId = p.A.Id,
                        aName = p.A.Name,
                        aCountry = p.A.Country,
                        bId = p.B.Id,
                        bName = p.B.Name,
                        bCountry = p.B.Country,
                        km = Math.Round(p.Km, 2),
                        reason = "Accepted micro-territory exception: preserving the last city of each territory takes priority over the 25 km cross-border spacing rule."
                    })
                    .ToArray(),
                spacingReplacementCount = spacingChanges.Count,
                spacingReplacements = spacingChanges,
                internalSpacing25Km,
                internalSpacing25KmVariableQuota,
                scaled25KmExperiments,
                adaptive25KmSummary = new
                {
                    initialCityCount = all.Count,
                    finalCityCount = adaptiveTotalCities,
                    quotaReduction = adaptiveQuotaReduction,
                    reducedCountryCount = adaptiveReducedCountries.Length,
                    reducedCountries = adaptiveReducedCountries
                },
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

    static InternalSpacingShrinkResult BuildInternalSpacingShrinkTest(
        string code,
        IReadOnlyList<City> currentSelection,
        IReadOnlyList<City> candidates,
        double thresholdKm)
    {
        var before = currentSelection.ToList();
        var after = currentSelection.ToList();
        var candidatePool = candidates
            .Where(c => after.All(s => s.Id != c.Id))
            .OrderByDescending(c => c.Population)
            .ToList();

        var replacements = new List<object>();
        var removalsWithoutReplacement = new List<object>();

        for (var pass = 0; pass < 5000; pass++)
        {
            (City A, City B, double Km)? conflict = null;
            for (var i = 0; i < after.Count && conflict is null; i++)
            for (var j = i + 1; j < after.Count; j++)
            {
                var km = Geo.Km(after[i], after[j]);
                if (km < thresholdKm)
                {
                    conflict = (after[i], after[j], km);
                    break;
                }
            }

            if (conflict is null) break;

            var pair = conflict.Value;
            var remove = pair.A.Population <= pair.B.Population ? pair.A : pair.B;
            var keep = remove.Id == pair.A.Id ? pair.B : pair.A;

            City? replacement = null;
            foreach (var candidate in candidatePool)
            {
                if (after.Any(s => s.Id != remove.Id && Geo.Km(candidate, s) < thresholdKm))
                    continue;
                replacement = candidate;
                break;
            }

            after.RemoveAll(x => x.Id == remove.Id);

            if (replacement is not null)
            {
                after.Add(replacement);
                candidatePool.RemoveAll(x => x.Id == replacement.Id);
                candidatePool.Add(remove);
                candidatePool = candidatePool.OrderByDescending(x => x.Population).ToList();
                replacements.Add(new
                {
                    removedId = remove.Id,
                    removedName = remove.Name,
                    removedPopulation = remove.Population,
                    keptId = keep.Id,
                    keptName = keep.Name,
                    replacementId = replacement.Id,
                    replacementName = replacement.Name,
                    replacementPopulation = replacement.Population,
                    originalDistanceKm = Math.Round(pair.Km, 1)
                });
            }
            else
            {
                removalsWithoutReplacement.Add(new
                {
                    removedId = remove.Id,
                    removedName = remove.Name,
                    removedPopulation = remove.Population,
                    keptId = keep.Id,
                    keptName = keep.Name,
                    originalDistanceKm = Math.Round(pair.Km, 1)
                });
            }
        }

        object Stats(IReadOnlyList<City> cities)
        {
            var nearest = new List<double>();
            for (var i = 0; i < cities.Count; i++)
            {
                var d = double.PositiveInfinity;
                for (var j = 0; j < cities.Count; j++)
                    if (i != j) d = Math.Min(d, Geo.Km(cities[i], cities[j]));
                if (!double.IsInfinity(d)) nearest.Add(d);
            }
            nearest.Sort();

            double P(double q)
            {
                if (nearest.Count == 0) return 0;
                var pos = (nearest.Count - 1) * q;
                var lo = (int)Math.Floor(pos);
                var hi = (int)Math.Ceiling(pos);
                if (lo == hi) return nearest[lo];
                return nearest[lo] + (nearest[hi] - nearest[lo]) * (pos - lo);
            }

            return new
            {
                count = cities.Count,
                meanKm = Math.Round(nearest.Count == 0 ? 0 : nearest.Average(), 1),
                minKm = Math.Round(nearest.Count == 0 ? 0 : nearest.Min(), 1),
                p10Km = Math.Round(P(.10), 1),
                p25Km = Math.Round(P(.25), 1),
                medianKm = Math.Round(P(.50), 1),
                p75Km = Math.Round(P(.75), 1),
                p90Km = Math.Round(P(.90), 1),
                maxKm = Math.Round(nearest.Count == 0 ? 0 : nearest.Max(), 1)
            };
        }

        var remainingConflicts = 0;
        for (var i = 0; i < after.Count; i++)
        for (var j = i + 1; j < after.Count; j++)
            if (Geo.Km(after[i], after[j]) < thresholdKm) remainingConflicts++;

        return new InternalSpacingShrinkResult(
            Code: code,
            ThresholdKm: thresholdKm,
            Before: Stats(before),
            After: Stats(after),
            FinalCount: after.Count,
            QuotaReduction: before.Count - after.Count,
            ReplacementCount: replacements.Count,
            RemovalWithoutReplacementCount: removalsWithoutReplacement.Count,
            RemainingPairCountBelowThreshold: remainingConflicts,
            Replacements: replacements,
            RemovalsWithoutReplacement: removalsWithoutReplacement);
    }

    static object BuildInternalSpacingTest(
        string code,
        IReadOnlyList<City> currentSelection,
        IReadOnlyList<City> candidates,
        double thresholdKm)
    {
        var before = currentSelection.ToList();
        var after = currentSelection.ToList();
        var candidatePool = candidates
            .Where(c => after.All(s => s.Id != c.Id))
            .OrderByDescending(c => c.Population)
            .ToList();

        var replacements = new List<object>();
        var protectedPairs = new HashSet<(long, long)>();

        for (var pass = 0; pass < 5000; pass++)
        {
            (City A, City B, double Km)? conflict = null;
            for (var i = 0; i < after.Count && conflict is null; i++)
            for (var j = i + 1; j < after.Count; j++)
            {
                var km = Geo.Km(after[i], after[j]);
                if (km >= thresholdKm) continue;
                var key = (Math.Min(after[i].Id, after[j].Id), Math.Max(after[i].Id, after[j].Id));
                if (protectedPairs.Contains(key)) continue;
                conflict = (after[i], after[j], km);
                break;
            }

            if (conflict is null) break;

            var pair = conflict.Value;
            var remove = pair.A.Population <= pair.B.Population ? pair.A : pair.B;
            var keep = remove.Id == pair.A.Id ? pair.B : pair.A;

            City? replacement = null;
            foreach (var candidate in candidatePool)
            {
                if (after.Any(s => s.Id != remove.Id && Geo.Km(candidate, s) < thresholdKm))
                    continue;
                replacement = candidate;
                break;
            }

            if (replacement is null)
            {
                protectedPairs.Add((Math.Min(pair.A.Id, pair.B.Id), Math.Max(pair.A.Id, pair.B.Id)));
                continue;
            }

            after.RemoveAll(x => x.Id == remove.Id);
            after.Add(replacement);
            candidatePool.RemoveAll(x => x.Id == replacement.Id);
            candidatePool.Add(remove);
            candidatePool = candidatePool.OrderByDescending(x => x.Population).ToList();

            replacements.Add(new
            {
                removedId = remove.Id,
                removedName = remove.Name,
                removedPopulation = remove.Population,
                keptId = keep.Id,
                keptName = keep.Name,
                replacementId = replacement.Id,
                replacementName = replacement.Name,
                replacementPopulation = replacement.Population,
                originalDistanceKm = Math.Round(pair.Km, 1)
            });
        }

        object Stats(IReadOnlyList<City> cities)
        {
            var nearest = new List<double>();
            for (var i = 0; i < cities.Count; i++)
            {
                var d = double.PositiveInfinity;
                for (var j = 0; j < cities.Count; j++)
                    if (i != j) d = Math.Min(d, Geo.Km(cities[i], cities[j]));
                if (!double.IsInfinity(d)) nearest.Add(d);
            }
            nearest.Sort();

            double P(double q)
            {
                if (nearest.Count == 0) return 0;
                var pos = (nearest.Count - 1) * q;
                var lo = (int)Math.Floor(pos);
                var hi = (int)Math.Ceiling(pos);
                if (lo == hi) return nearest[lo];
                return nearest[lo] + (nearest[hi] - nearest[lo]) * (pos - lo);
            }

            return new
            {
                count = cities.Count,
                meanKm = Math.Round(nearest.Count == 0 ? 0 : nearest.Average(), 1),
                minKm = Math.Round(nearest.Count == 0 ? 0 : nearest.Min(), 1),
                p10Km = Math.Round(P(.10), 1),
                p25Km = Math.Round(P(.25), 1),
                medianKm = Math.Round(P(.50), 1),
                p75Km = Math.Round(P(.75), 1),
                p90Km = Math.Round(P(.90), 1),
                maxKm = Math.Round(nearest.Count == 0 ? 0 : nearest.Max(), 1)
            };
        }

        var remainingConflicts = 0;
        for (var i = 0; i < after.Count; i++)
        for (var j = i + 1; j < after.Count; j++)
            if (Geo.Km(after[i], after[j]) < thresholdKm) remainingConflicts++;

        return new
        {
            code,
            thresholdKm,
            before = Stats(before),
            after = Stats(after),
            replacementCount = replacements.Count,
            remainingPairCountBelowThreshold = remainingConflicts,
            replacements
        };
    }

    sealed record InternalSpacingChange(
        string Country,
        string Kind,
        long RemovedId,
        string RemovedName,
        long? ReplacementId,
        string? ReplacementName,
        double ConflictKm,
        double ThresholdKm);

    static List<InternalSpacingChange> ApplyInternalSpacingAdaptive(
        Dictionary<string, List<City>> selected,
        IReadOnlyDictionary<string, List<City>> candidates,
        IReadOnlyDictionary<string, double> thresholds,
        double crossBorderKm)
    {
        var changes = new List<InternalSpacingChange>();

        foreach (var code in selected.Keys.OrderBy(x => x).ToArray())
        {
            var after = selected[code];
            if (after.Count < 2) continue;

            var thresholdKm = thresholds[code];
            var candidatePool = candidates[code]
                .Where(c => after.All(s => s.Id != c.Id))
                .OrderByDescending(c => c.Population)
                .ToList();

            for (var pass = 0; pass < 5000; pass++)
            {
                (City A, City B, double Km)? conflict = null;
                for (var i = 0; i < after.Count && conflict is null; i++)
                for (var j = i + 1; j < after.Count; j++)
                {
                    var km = Geo.Km(after[i], after[j]);
                    if (km < thresholdKm)
                    {
                        conflict = (after[i], after[j], km);
                        break;
                    }
                }

                if (conflict is null) break;

                var pair = conflict.Value;
                var remove = pair.A.Population <= pair.B.Population ? pair.A : pair.B;

                City? replacement = null;
                foreach (var candidate in candidatePool)
                {
                    if (after.Any(s => s.Id != remove.Id && Geo.Km(candidate, s) < thresholdKm))
                        continue;

                    var conflictsForeign = selected
                        .Where(kv => !kv.Key.Equals(code, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(kv => kv.Value)
                        .Any(city => Geo.Km(candidate, city) < crossBorderKm);
                    if (conflictsForeign) continue;

                    replacement = candidate;
                    break;
                }

                after.RemoveAll(x => x.Id == remove.Id);

                if (replacement is null)
                {
                    changes.Add(new InternalSpacingChange(
                        code, "remove", remove.Id, remove.Name,
                        null, null, Math.Round(pair.Km, 2), Math.Round(thresholdKm, 2)));
                    continue;
                }

                after.Add(replacement);
                candidatePool.RemoveAll(x => x.Id == replacement.Id);
                candidatePool.Add(remove);
                candidatePool = candidatePool.OrderByDescending(x => x.Population).ToList();

                changes.Add(new InternalSpacingChange(
                    code, "replace", remove.Id, remove.Name,
                    replacement.Id, replacement.Name,
                    Math.Round(pair.Km, 2), Math.Round(thresholdKm, 2)));
            }
        }

        Console.WriteLine(
            $"Validated internal spacing 0.15: {changes.Count} change(s), " +
            $"{changes.Count(x => x.Kind == "remove")} removal(s), " +
            $"{changes.Count(x => x.Kind == "replace")} replacement(s).");

        return changes;
    }

    static List<object> ApplyCrossBorderSpacing(
        Dictionary<string, List<City>> selected,
        IReadOnlyDictionary<string, List<City>> candidates,
        double thresholdKm,
        IReadOnlyDictionary<string, double>? internalThresholds = null)
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
                    if (internalThresholds is not null &&
                        internalThresholds.TryGetValue(candidate.Country, out var internalKm) &&
                        countryList.Any(x => x.Id != remove.Id && Geo.Km(candidate, x) < internalKm)) continue;

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
                // No valid replacement exists on either side. Apply the same
                // adaptive-quota principle used for internal spacing: remove
                // the less important endpoint, but never delete the last city
                // of a territory.
                var removable = attempts
                    .Select(x => x.Remove)
                    .Where(city => selected[city.Country].Count > 1)
                    .OrderBy(city => city.Population)
                    .ThenBy(city => city.Id)
                    .FirstOrDefault();

                if (removable is not null)
                {
                    selected[removable.Country].RemoveAll(x => x.Id == removable.Id);
                    changes.Add(new
                    {
                        removedId = removable.Id,
                        removedName = removable.Name,
                        country = removable.Country,
                        keptId = removable.Id == conflict.A.Id ? conflict.B.Id : conflict.A.Id,
                        keptName = removable.Id == conflict.A.Id ? conflict.B.Name : conflict.A.Name,
                        replacementId = (long?)null,
                        replacementName = (string?)null,
                        originalConflictKm = Math.Round(conflict.Km, 2),
                        action = "remove_without_replacement"
                    });
                    Console.WriteLine(
                        $"25 km spacing quota reduction: removed {removable.Name} ({removable.Country}) " +
                        $"to resolve {conflict.Km:F1} km cross-border conflict.");
                    continue;
                }

                var key = (Math.Min(conflict.A.Id, conflict.B.Id), Math.Max(conflict.A.Id, conflict.B.Id));
                unresolved.Add(key);
                Console.WriteLine(
                    $"25 km spacing irreducible because both territories are at one city: " +
                    $"{conflict.A.Name} ({conflict.A.Country}) / " +
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
