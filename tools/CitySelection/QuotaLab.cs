using System.Text;

record CountryFacts(string Code, string Name, double AreaKm2, long Population);
record QuotaFormula(string Id, string Label, double AreaExponent, double PopulationExponent, int Minimum, int Maximum);

static class QuotaLab
{
    static readonly CountryFacts[] Countries =
    {
        new("FR","France",551695,68600000), new("BE","Belgique",30528,11800000), new("LU","Luxembourg",2586,680000),
        new("DE","Allemagne",357588,84500000), new("NL","Pays-Bas",41850,18000000), new("CH","Suisse",41285,9000000),
        new("ES","Espagne",505990,48600000), new("IT","Italie",301340,59000000), new("GB","Royaume-Uni",243610,69000000),
        new("PL","Pologne",312696,37600000), new("US","États-Unis",9833517,340000000), new("CA","Canada",9984670,41000000),
        new("RU","Russie",17098246,144000000), new("CN","Chine",9596961,1410000000), new("IN","Inde",3287263,1430000000),
        new("BR","Brésil",8515767,212000000), new("AU","Australie",7692024,27000000), new("BD","Bangladesh",148460,173000000),
        new("JP","Japon",377975,124000000), new("IL","Israël",22145,10000000), new("KE","Kenya",580367,56000000),
        new("CD","RDC",2344858,111000000), new("SG","Singapour",735,6000000), new("MC","Monaco",2.02,39000)
    };

    static readonly QuotaFormula[] Formulas =
    {
        new("A", "Territoire fort", .50, .10, 1, 450),
        new("B", "Territoire dominant", .45, .15, 1, 450),
        new("C", "Équilibre", .40, .20, 1, 450),
        new("D", "Population renforcée", .35, .25, 1, 450)
    };

    public static async Task GenerateAsync(HttpClient _, string outDir)
    {
        var france = Countries.Single(c => c.Code == "FR");
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
            foreach (var c in Countries)
            {
                var q = Formulas.Select(f => Quota(c, f).ToString());
                await csv.WriteLineAsync($"{c.Code},\"{c.Name.Replace("\"", "\"\"")}\",{c.AreaKm2:F1},{c.Population},{string.Join(',', q)}");
            }
        }

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>World Conquest — quotas</title>");
        html.Append("<style>body{font-family:system-ui;background:#071022;color:#eef3ff;margin:0;padding:20px}a{color:#79aaff}.wrap{max-width:1100px;margin:auto}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin:18px 0}.card{background:#101a31;border:1px solid #263553;border-radius:14px;padding:14px}.muted{color:#9eabc5}table{width:100%;border-collapse:collapse;background:#101a31;border-radius:14px;overflow:hidden}th,td{padding:10px 8px;border-bottom:1px solid #263553;text-align:right}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}th{position:sticky;top:0;background:#172541}tr.hi{background:#172541}small{color:#9eabc5}</style>");
        html.Append("<div class=\"wrap\"><p><a href=\"./\">← Maillage des villes</a></p><h1>🌍 Test mondial du nombre de villes</h1><p class=\"muted\">Toutes les formules sont calibrées pour donner exactement 100 à la France. Jeu de données de laboratoire : superficies et populations arrondies, utilisées uniquement pour comparer la forme des courbes. Plafond temporaire : 450 villes.</p>");
        html.Append("<div class=\"cards\">");
        foreach (var f in Formulas)
            html.Append($"<div class=\"card\"><b>{f.Id} — {f.Label}</b><br><small>surface^{f.AreaExponent:0.00} × population^{f.PopulationExponent:0.00}</small></div>");
        html.Append("</div><table><thead><tr><th>Code</th><th>Pays</th><th>Surface</th><th>Population</th>");
        foreach (var f in Formulas) html.Append($"<th>{f.Id}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var c in Countries)
        {
            var hi = c.Code is "FR" or "BE" or "LU" ? " class=\"hi\"" : "";
            html.Append($"<tr{hi}><td>{c.Code}</td><td>{c.Name}</td><td>{c.AreaKm2:N0} km²</td><td>{c.Population / 1_000_000.0:N1} M</td>");
            foreach (var f in Formulas) html.Append($"<td><b>{Quota(c, f)}</b></td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table><p class=\"muted\">On cherche ici une courbe mondiale crédible. Une fois la formule choisie, on recalculera réellement les villes de chaque pays avec GeoNames et le maillage 60/40.</p></div></html>");
        await File.WriteAllTextAsync(Path.Combine(outDir, "quotas.html"), html.ToString());

        Console.WriteLine("World quota comparison:");
        foreach (var c in Countries)
            Console.WriteLine($"{c.Code,-2} " + string.Join(" ", Formulas.Select(f => $"{f.Id}={Quota(c,f),3}")));
    }
}
