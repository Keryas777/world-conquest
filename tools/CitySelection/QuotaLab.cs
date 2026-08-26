using System.Text;

record CountryFacts(string Code, string Name, double AreaKm2, long Population, string Profile);
record QuotaFormula(string Id, string Label, double AreaExponent, double PopulationExponent, int Minimum, int Maximum);

static class QuotaLab
{
    const int FranceReferenceQuota = 85;

    static readonly CountryFacts[] Countries =
    {
        new("FR","France",551695,68600000,"référence"),
        new("BE","Belgique",30528,11800000,"petit / dense"),
        new("LU","Luxembourg",2586,680000,"micro / dense"),
        new("DE","Allemagne",357588,84500000,"moyen / dense"),
        new("NL","Pays-Bas",41850,18000000,"petit / très dense"),
        new("CH","Suisse",41285,9000000,"petit / dense"),
        new("ES","Espagne",505990,48600000,"moyen / peu dense"),
        new("IT","Italie",301340,59000000,"moyen / dense"),
        new("GB","Royaume-Uni",243610,69000000,"moyen / dense"),
        new("PL","Pologne",312696,37600000,"moyen"),
        new("IS","Islande",103000,400000,"grand / très peu dense"),
        new("NO","Norvège",385207,5600000,"long / peu dense"),
        new("GR","Grèce",131957,10400000,"fragmenté / insulaire"),
        new("US","États-Unis",9833517,340000000,"géant / peuplé"),
        new("CA","Canada",9984670,41000000,"géant / très peu dense"),
        new("MX","Mexique",1964375,130000000,"grand / peuplé"),
        new("AR","Argentine",2780400,46000000,"grand / peu dense"),
        new("CL","Chili",756102,20000000,"long / peu dense"),
        new("BR","Brésil",8515767,212000000,"géant / peuplé"),
        new("RU","Russie",17098246,144000000,"géant / très étendu"),
        new("CN","Chine",9596961,1410000000,"géant / très peuplé"),
        new("IN","Inde",3287263,1430000000,"grand / extrêmement peuplé"),
        new("BD","Bangladesh",148460,173000000,"petit / extrêmement dense"),
        new("JP","Japon",377975,124000000,"insulaire / dense"),
        new("ID","Indonésie",1904569,282000000,"archipel / très peuplé"),
        new("SG","Singapour",735,6000000,"micro / extrêmement dense"),
        new("AU","Australie",7692024,27000000,"géant / très peu dense"),
        new("NZ","Nouvelle-Zélande",268838,5300000,"insulaire / peu dense"),
        new("IL","Israël",22145,10000000,"petit / dense"),
        new("SA","Arabie saoudite",2149690,35000000,"grand / désertique"),
        new("EG","Égypte",1002450,116000000,"grand / population concentrée"),
        new("NG","Nigeria",923768,232000000,"grand / très peuplé"),
        new("KE","Kenya",580367,56000000,"grand / moyen"),
        new("CD","RDC",2344858,111000000,"très grand / peu équipé"),
        new("ZA","Afrique du Sud",1221037,64000000,"grand / moyen"),
        new("MC","Monaco",2.02,39000,"micro-État")
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
            var raw = FranceReferenceQuota
                * Math.Pow(c.AreaKm2 / france.AreaKm2, f.AreaExponent)
                * Math.Pow((double)c.Population / france.Population, f.PopulationExponent);
            return Math.Clamp((int)Math.Round(raw), f.Minimum, f.Maximum);
        }

        Directory.CreateDirectory(outDir);
        await using (var csv = new StreamWriter(Path.Combine(outDir, "quota-comparison.csv"), false, Encoding.UTF8))
        {
            await csv.WriteLineAsync("code,name,profile,area_km2,population," + string.Join(',', Formulas.Select(f => "formula_" + f.Id)));
            foreach (var c in Countries)
            {
                var q = Formulas.Select(f => Quota(c, f).ToString());
                await csv.WriteLineAsync($"{c.Code},\"{c.Name.Replace("\"", "\"\"")}\",\"{c.Profile}\",{c.AreaKm2:F1},{c.Population},{string.Join(',', q)}");
            }
        }

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>World Conquest — quotas mondiaux</title>");
        html.Append("<style>body{font-family:system-ui;background:#071022;color:#eef3ff;margin:0;padding:20px}a{color:#79aaff}.wrap{max-width:1250px;margin:auto}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin:18px 0}.card{background:#101a31;border:1px solid #263553;border-radius:14px;padding:14px}.muted{color:#9eabc5}.callout{background:#172541;border:1px solid #38527d;border-radius:14px;padding:14px;margin:16px 0}table{width:100%;border-collapse:collapse;background:#101a31;border-radius:14px;overflow:hidden}th,td{padding:9px 7px;border-bottom:1px solid #263553;text-align:right;font-size:14px}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2),th:nth-child(3),td:nth-child(3){text-align:left}th{position:sticky;top:0;background:#172541}tr.hi{background:#172541}td.pick{background:#1b3153}small{color:#9eabc5}@media(max-width:700px){body{padding:12px}.tablewrap{overflow-x:auto}table{min-width:900px}}</style>");
        html.Append($"<div class=\"wrap\"><p><a href=\"./\">← Maillage des villes</a></p><h1>🌍 Test mondial du nombre de villes</h1><p class=\"muted\">Toutes les courbes sont recalibrées sur <b>France = {FranceReferenceQuota} villes</b>. Les superficies et populations sont des valeurs de laboratoire arrondies : ce test sert à comparer le comportement des formules sur des profils de pays très différents, pas encore à figer la base mondiale.</p>");
        html.Append("<div class=\"callout\"><b>À surveiller :</b> les formules B et C sont nos candidates principales. On vérifie surtout qu'elles ne surchargent ni les petits pays denses, ni les géants très étendus, et qu'elles ne sous-représentent pas excessivement les pays très peuplés.</div>");
        html.Append("<div class=\"cards\">");
        foreach (var f in Formulas)
            html.Append($"<div class=\"card\"><b>{f.Id} — {f.Label}</b><br><small>surface^{f.AreaExponent:0.00} × population^{f.PopulationExponent:0.00}</small></div>");
        html.Append("</div><div class=\"tablewrap\"><table><thead><tr><th>Code</th><th>Pays</th><th>Profil</th><th>Surface</th><th>Population</th>");
        foreach (var f in Formulas) html.Append($"<th>{f.Id}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var c in Countries)
        {
            var hi = c.Code is "FR" or "BE" or "LU" ? " class=\"hi\"" : "";
            html.Append($"<tr{hi}><td>{c.Code}</td><td>{c.Name}</td><td>{c.Profile}</td><td>{c.AreaKm2:N0} km²</td><td>{c.Population / 1_000_000.0:N1} M</td>");
            foreach (var f in Formulas)
            {
                var css = f.Id is "B" or "C" ? " class=\"pick\"" : "";
                html.Append($"<td{css}><b>{Quota(c, f)}</b></td>");
            }
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div><p class=\"muted\">Étape suivante après choix de la courbe : récupérer des données mondiales propres, calculer le quota de chaque pays, puis sélectionner réellement ses villes avec le maillage 60 % population / 40 % couverture.</p></div></html>");
        await File.WriteAllTextAsync(Path.Combine(outDir, "quotas.html"), html.ToString());

        Console.WriteLine($"World quota comparison (France={FranceReferenceQuota}):");
        foreach (var c in Countries)
            Console.WriteLine($"{c.Code,-2} " + string.Join(" ", Formulas.Select(f => $"{f.Id}={Quota(c,f),3}")));
    }
}
