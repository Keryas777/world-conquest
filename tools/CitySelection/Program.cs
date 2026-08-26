using System.Globalization;
using System.IO.Compression;

record City(long Id, string Name, string Country, double Lat, double Lon, long Population);
record CountryConfig(string Code, string Name, int Quota);

static class Geo
{
    public static double Km(City a, City b)
    {
        const double r = 6371.0088;
        static double Rad(double x) => x * Math.PI / 180.0;
        var dLat = Rad(b.Lat - a.Lat);
        var dLon = Rad(b.Lon - a.Lon);
        var la1 = Rad(a.Lat);
        var la2 = Rad(b.Lat);
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(la1) * Math.Cos(la2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

static class Selector
{
    public static List<City> Select(IReadOnlyList<City> candidates, int quota, double populationWeight)
    {
        if (quota >= candidates.Count) return candidates.OrderByDescending(c => c.Population).ToList();
        var maxPop = Math.Max(1L, candidates.Max(c => c.Population));
        double Pop(City c) => Math.Log10(c.Population + 10.0) / Math.Log10(maxPop + 10.0);

        var remaining = candidates.OrderByDescending(c => c.Population).ToList();
        var selected = new List<City> { remaining[0] };
        remaining.RemoveAt(0);
        var nearest = remaining.ToDictionary(c => c.Id, c => Geo.Km(c, selected[0]));

        while (selected.Count < quota && remaining.Count > 0)
        {
            var maxNearest = Math.Max(1.0, remaining.Max(c => nearest[c.Id]));
            City? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var c in remaining)
            {
                var spatial = Math.Min(1.0, nearest[c.Id] / maxNearest);
                var score = populationWeight * Pop(c) + (1 - populationWeight) * spatial;
                if (score > bestScore || (Math.Abs(score - bestScore) < 1e-12 && c.Population > (best?.Population ?? -1)))
                {
                    best = c;
                    bestScore = score;
                }
            }
            if (best is null) break;
            selected.Add(best);
            remaining.Remove(best);
            nearest.Remove(best.Id);
            foreach (var c in remaining)
                nearest[c.Id] = Math.Min(nearest[c.Id], Geo.Km(c, best));
        }
        return selected;
    }
}

static class Results
{
    public static (double Mean, double Min, double Max) NearestStats(IReadOnlyList<City> cities)
    {
        var values = new List<double>();
        for (var i = 0; i < cities.Count; i++)
        {
            var d = double.PositiveInfinity;
            for (var j = 0; j < cities.Count; j++)
                if (i != j) d = Math.Min(d, Geo.Km(cities[i], cities[j]));
            if (!double.IsInfinity(d)) values.Add(d);
        }
        return values.Count == 0 ? (0, 0, 0) : (values.Average(), values.Min(), values.Max());
    }

    public static void WriteCsv(string path, IEnumerable<City> cities)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path);
        w.WriteLine("geoname_id,name,country,latitude,longitude,population");
        foreach (var c in cities.OrderBy(c => c.Country).ThenByDescending(c => c.Population))
            w.WriteLine($"{c.Id},\"{c.Name.Replace("\"", "\"\"")}\",{c.Country},{c.Lat.ToString(CultureInfo.InvariantCulture)},{c.Lon.ToString(CultureInfo.InvariantCulture)},{c.Population}");
    }

    public static void WriteSvg(string path, IReadOnlyDictionary<string, List<City>> groups, double weight)
    {
        var all = groups.Values.SelectMany(x => x).ToList();
        var minLon = all.Min(c => c.Lon); var maxLon = all.Max(c => c.Lon);
        var minLat = all.Min(c => c.Lat); var maxLat = all.Max(c => c.Lat);
        const int width = 1000, height = 900, pad = 45;
        double X(double lon) => pad + (lon - minLon) / Math.Max(.001, maxLon - minLon) * (width - 2 * pad);
        double Y(double lat) => height - pad - (lat - minLat) / Math.Max(.001, maxLat - minLat) * (height - 2 * pad);
        var colors = new Dictionary<string, string> { ["FR"] = "#3b82f6", ["BE"] = "#f59e0b", ["LU"] = "#ef4444" };
        using var w = new StreamWriter(path);
        w.WriteLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        w.WriteLine("<rect width=\"100%\" height=\"100%\" fill=\"#f7f7f7\"/>");
        w.WriteLine($"<text x=\"45\" y=\"30\" font-family=\"sans-serif\" font-size=\"20\">City selection — population weight {weight:P0}</text>");
        foreach (var (country, cities) in groups)
            foreach (var c in cities)
            {
                var title = System.Security.SecurityElement.Escape($"{c.Name} — {c.Population:N0}");
                w.WriteLine($"<circle cx=\"{X(c.Lon):F1}\" cy=\"{Y(c.Lat):F1}\" r=\"4\" fill=\"{colors[country]}\" fill-opacity=\".75\"><title>{title}</title></circle>");
            }
        w.WriteLine("</svg>");
    }
}

static class Program
{
    public static async Task Main()
    {
        var configs = new[]
        {
            new CountryConfig("FR", "France", 100),
            new CountryConfig("BE", "Belgium", 32),
            new CountryConfig("LU", "Luxembourg", 8)
        };
        var weights = new[] { .30, .50, .70, 1.00 };
        const long minPopulation = 500;
        var cacheDir = Path.Combine("data", "raw", "geonames");
        var outDir = Path.Combine("data", "generated", "city-selection");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(outDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("world-conquest-city-selection-lab/0.1");
        var candidates = new Dictionary<string, List<City>>();

        foreach (var cfg in configs)
        {
            var zipPath = Path.Combine(cacheDir, cfg.Code + ".zip");
            if (!File.Exists(zipPath))
            {
                var url = $"https://download.geonames.org/export/dump/{cfg.Code}.zip";
                Console.WriteLine($"Downloading {url}");
                await using var input = await http.GetStreamAsync(url);
                await using var output = File.Create(zipPath);
                await input.CopyToAsync(output);
            }

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(cfg.Code + ".txt") ?? throw new InvalidOperationException($"Missing {cfg.Code}.txt");
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
                cities.Add(new City(id, f[1], cfg.Code, lat, lon, pop));
            }
            candidates[cfg.Code] = cities;
            Console.WriteLine($"{cfg.Name}: {cities.Count:N0} candidates >= {minPopulation:N0}");
        }

        using var report = new StreamWriter(Path.Combine(outDir, "report.md"));
        report.WriteLine("# City-selection laboratory\n");
        report.WriteLine($"GeoNames populated places (`feature class P`), minimum population {minPopulation:N0}.\n");
        report.WriteLine("| Population weight | Country | Selected | Mean nearest-neighbor km | Min km | Max km |");
        report.WriteLine("|---:|---|---:|---:|---:|---:|");

        foreach (var weight in weights)
        {
            var groups = new Dictionary<string, List<City>>();
            foreach (var cfg in configs)
            {
                var selected = Selector.Select(candidates[cfg.Code], cfg.Quota, weight);
                groups[cfg.Code] = selected;
                var s = Results.NearestStats(selected);
                report.WriteLine($"| {weight:P0} | {cfg.Name} | {selected.Count} | {s.Mean:F1} | {s.Min:F1} | {s.Max:F1} |");
                Console.WriteLine($"weight={weight:P0} {cfg.Name}: {selected.Count} cities; nearest mean={s.Mean:F1} km min={s.Min:F1} max={s.Max:F1}");
            }
            var label = ((int)Math.Round(weight * 100)).ToString("D3");
            Results.WriteCsv(Path.Combine(outDir, $"selected-pop-{label}.csv"), groups.Values.SelectMany(x => x));
            Results.WriteSvg(Path.Combine(outDir, $"selected-pop-{label}.svg"), groups, weight);
        }
        Console.WriteLine($"Outputs written to {outDir}");
    }
}
