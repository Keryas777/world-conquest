using System.Globalization;
using System.IO.Compression;
using System.Text;

static class PopulationThresholdLab
{
    public static async Task GenerateAsync(HttpClient http, string outDir)
    {
        var cacheDir = Path.Combine("data", "raw", "geonames");
        Directory.CreateDirectory(cacheDir);
        var zipPath = Path.Combine(cacheDir, "FR.zip");
        if (!File.Exists(zipPath))
        {
            await using var input = await http.GetStreamAsync("https://download.geonames.org/export/dump/FR.zip");
            await using var output = File.Create(zipPath);
            await input.CopyToAsync(output);
        }

        var populations = new List<long>();
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entry = archive.GetEntry("FR.txt") ?? throw new InvalidOperationException("Missing FR.txt");
            using var reader = new StreamReader(entry.Open());
            while (await reader.ReadLineAsync() is { } line)
            {
                var f = line.Split('\t');
                if (f.Length < 15 || f[6] != "P") continue;
                if (long.TryParse(f[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pop) && pop > 0)
                    populations.Add(pop);
            }
        }

        var thresholds = new long[] { 50000, 55000, 60000, 65000, 70000, 75000, 80000, 90000, 100000 };
        var rows = thresholds.Select(t => (Threshold: t, Count: populations.Count(p => p >= t))).ToList();

        using (var report = new StreamWriter(Path.Combine(outDir, "population-thresholds.md")))
        {
            report.WriteLine("# France — GeoNames population thresholds\n");
            report.WriteLine("GeoNames populated places (`feature class P`). Counts use the population field in the current FR dump.\n");
            report.WriteLine("| Minimum population | Places |\n|---:|---:|");
            foreach (var r in rows) report.WriteLine($"| {r.Threshold:N0} | {r.Count} |");
        }

        var json = string.Join(",", rows.Select(r => $"{{threshold:{r.Threshold},count:{r.Count}}}"));
        var html = $$"""
<!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>France — seuils de population</title>
<style>body{margin:0;background:#091126;color:#eef3ff;font-family:system-ui,-apple-system,sans-serif}.wrap{max-width:760px;margin:auto;padding:24px}h1{font-size:28px}.sub{color:#aebbd5}.card{background:#121b31;border:1px solid #2a3859;border-radius:18px;padding:18px;margin-top:20px}table{width:100%;border-collapse:collapse}th,td{padding:12px;border-bottom:1px solid #293653;text-align:right}th:first-child,td:first-child{text-align:left}.target{font-weight:800;color:#7db0ff}</style></head><body><div class="wrap"><h1>🇫🇷 France — seuils de population</h1><p class="sub">Comptage direct du dump GeoNames FR · lieux habités (feature class P)</p><div class="card"><table><thead><tr><th>Population minimale</th><th>Nombre de lieux</th></tr></thead><tbody id="rows"></tbody></table></div><p class="sub">Les lignes les plus proches de 80–90 sont mises en évidence. Ce tableau sert au calibrage du nombre de villes du jeu, pas à sélectionner les villes finales.</p></div><script>const data=[{{json}}];const nf=new Intl.NumberFormat('fr-FR');document.getElementById('rows').innerHTML=data.map(x=>`<tr class="${x.count>=80&&x.count<=90?'target':''}"><td>≥ ${nf.format(x.threshold)}</td><td>${nf.format(x.count)}</td></tr>`).join('');</script></body></html>
""";
        await File.WriteAllTextAsync(Path.Combine(outDir, "population-thresholds.html"), html, Encoding.UTF8);
        Console.WriteLine("France population threshold comparison written to " + Path.Combine(outDir, "population-thresholds.html"));
    }
}
