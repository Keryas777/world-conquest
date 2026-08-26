using System.Text;
using System.Text.Json;

record CountryFacts(string Code, string Name, double AreaKm2, long Population);
record QuotaFormula(string Id, string Label, double AreaExponent, double PopulationExponent, int Minimum, int Maximum);

static class QuotaLab
{
    static readonly string[] FocusCodes =
    {
        "FR","BE","LU","DE","NL","CH","ES","IT","GB","PL",
        "US","CA","RU","CN","IN","BR","AU","BD","JP","IL","KE","CD","SG","MC"
    };

    static readonly QuotaFormula[] Formulas =
    {
        new("A", "Territoire fort", .50, .10, 1, 450),
        new("B", "Territoire dominant", .45, .15, 1, 450),
        new("C", "Équilibre", .40, .20, 1, 450),
        new("D", "Population renforcée", .35, .25, 1, 450)
    };

    public static async Task GenerateAsync(HttpClient http, string outDir)
    {
        using var stream = await http.GetStreamAsync("https://restcountries.com/v3.1/all?fields=name,cca2,population,area");
        using var doc = await JsonDocument.ParseAsync(stream);

        var countries = new List<CountryFacts>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var code = e.GetProperty("cca2").GetString();
            var name = e.GetProperty("name").GetProperty("common").GetString();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;
            var pop = e.GetProperty("population").GetInt64();
            var area = e.GetProperty("area").GetDouble();
            if (pop <= 0 || area <= 0) continue;
            countries.Add(new CountryFacts(code, name, area, pop));
        }

        var france = countries.Single(c => c.Code == "FR");
        int Quota(CountryFacts c, QuotaFormula f)
        {
            var raw = 100.0
                * Math.Pow(c.AreaKm2 / france.AreaKm2, f.AreaExponent)
                * Math.Pow((double)c.Population / france.Population, f.PopulationExponent);
            return Math.Clamp((int)Math.Round(raw), f.Minimum, f.Maximum);
        }

        Directory.CreateDirectory(outDir);
        await using (var csv = new StreamWriter(Path.Combine(outDir, "quota-comparison.csv"), false, Encoding.UTF8))
        {
            await csv.WriteLineAsync("code,name,area_km2,population," + string.Join(',', Formulas.Select(f => "formula_" + f.Id)));
            foreach (var c in countries.OrderBy(c => c.Name))
            {
                var q = Formulas.Select(f => Quota(c, f).ToString());
                await csv.WriteLineAsync($"{c.Code},\"{c.Name.Replace("\"", "\"\"")}\",{c.AreaKm2:F1},{c.Population},{string.Join(',', q)}");
            }
        }

        var focus = FocusCodes.Select(code => countries.Single(c => c.Code == code)).ToArray();
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>World Conquest — quotas</title>");
        html.Append("<style>body{font-family:system-ui;background:#071022;color:#eef3ff;margin:0;padding:20px}a{color:#79aaff}.wrap{max-width:1100px;margin:auto}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin:18px 0}.card{background:#101a31;border:1px solid #263553;border-radius:14px;padding:14px}.muted{color:#9eabc5}table{width:100%;border-collapse:collapse;background:#101a31;border-radius:14px;overflow:hidden}th,td{padding:10px 8px;border-bottom:1px solid #263553;text-align:right}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}th{position:sticky;top:0;background:#172541}tr.hi{background:#172541}small{color:#9eabc5}</style>");
        html.Append("<div class=\"wrap\"><p><a href=\"./\">← Maillage des villes</a></p><h1>🌍 Test mondial du nombre de villes</h1><p class=\"muted\">Toutes les formules sont calibrées pour donner exactement 100 à la France. Source automatique : REST Countries (population + superficie). Plafond temporaire : 450 villes.</p>");
        html.Append("<div class=\"cards\">");
        foreach (var f in Formulas)
            html.Append($"<div class=\"card\"><b>{f.Id} — {f.Label}</b><br><small>surface^{f.AreaExponent:0.00} × population^{f.PopulationExponent:0.00}</small></div>");
        html.Append("</div><table><thead><tr><th>Pays</th><th>Surface</th><th>Population</th>");
        foreach (var f in Formulas) html.Append($"<th>{f.Id}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var c in focus)
        {
            var hi = c.Code is "FR" or "BE" or "LU" ? " class=\"hi\"" : "";
            html.Append($"<tr{hi}><td>{c.Code}</td><td>{c.Name}</td><td>{c.AreaKm2:N0} km²</td><td>{c.Population / 1_000_000.0:N1} M</td>");
            foreach (var f in Formulas) html.Append($"<td><b>{Quota(c, f)}</b></td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table><p class=\"muted\">Le but n’est pas encore de choisir une formule définitive : on cherche une courbe mondiale crédible avant de recalculer le maillage réel avec les nouveaux quotas.</p></div></html>");
        await File.WriteAllTextAsync(Path.Combine(outDir, "quotas.html"), html.ToString());

        Console.WriteLine("World quota comparison:");
        foreach (var code in new[] { "FR", "BE", "LU", "DE", "US", "RU", "CN", "IN", "BD", "AU", "SG", "MC" })
        {
            var c = countries.Single(x => x.Code == code);
            Console.WriteLine($"{code,-2} " + string.Join(" ", Formulas.Select(f => $"{f.Id}={Quota(c,f),3}")));
        }
    }
}
