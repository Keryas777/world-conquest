using System.Text;
using System.Text.Json;

static class FrontierGraphLab
{
    // Phase 1 laboratory: derive cross-border attack candidates from the selected
    // city set. This deliberately does NOT alter movement/combat rules yet.
    // A foreign city is a candidate when it belongs to one of the country's
    // real land neighbours and is geographically close to a selected city.
    static readonly Dictionary<string, HashSet<string>> LandNeighbours = new()
    {
        ["FR"] = new(["BE","LU","DE","CH","IT","ES"]),
        ["BE"] = new(["FR","LU","DE"]),
        ["LU"] = new(["FR","BE","DE"]),
        ["DE"] = new(["FR","BE","LU","CH"]),
        ["CH"] = new(["FR","DE","IT"]),
        ["IT"] = new(["FR","CH"]),
        ["ES"] = new(["FR"])
    };

    record Edge(City A, City B, double Km);

    public static async Task GenerateAsync(
        HttpClient http,
        string outDir,
        IReadOnlyDictionary<string, List<City>> alreadyLoaded,
        double populationWeight = .60)
    {
        var quotas = new Dictionary<string,int>
        {
            ["FR"] = 85, ["BE"] = 18, ["LU"] = 3,
            ["DE"] = 75, ["CH"] = 20, ["IT"] = 55, ["ES"] = 65
        };
        const long minPopulation = 500;
        var selected = new Dictionary<string,List<City>>();

        foreach (var (code, quota) in quotas)
        {
            List<City> candidates;
            if (alreadyLoaded.TryGetValue(code, out var loaded)) candidates = loaded;
            else candidates = await LoadAsync(http, code, minPopulation);
            selected[code] = Selector.Select(candidates, quota, populationWeight);
        }

        // For each selected city, retain up to two closest cities in each
        // neighbouring country, but only within a generous 180 km lab radius.
        // This is intentionally permissive: the viewer exists to expose bad
        // links before we define the final border/Voronoi rule.
        var edges = new Dictionary<string,Edge>();
        foreach (var (country, cities) in selected)
        foreach (var other in LandNeighbours[country])
        {
            foreach (var a in cities)
            {
                foreach (var b in selected[other]
                    .Select(b => (City:b, Km:Geo.Km(a,b)))
                    .Where(x => x.Km <= 180)
                    .OrderBy(x => x.Km)
                    .Take(2))
                {
                    var lo = Math.Min(a.Id,b.City.Id); var hi = Math.Max(a.Id,b.City.Id);
                    var key = $"{lo}:{hi}";
                    if (!edges.ContainsKey(key)) edges[key] = new Edge(a,b.City,b.Km);
                }
            }
        }

        var citiesPayload = selected.Values.SelectMany(x=>x).Select(c=>new {
            id=c.Id,name=c.Name,country=c.Country,lat=c.Lat,lon=c.Lon,population=c.Population
        });
        var edgesPayload = edges.Values.OrderBy(e=>e.Km).Select(e=>new {
            a=e.A.Id,b=e.B.Id,km=Math.Round(e.Km,1),countries=$"{e.A.Country}-{e.B.Country}"
        });
        var jsonCities = JsonSerializer.Serialize(citiesPayload);
        var jsonEdges = JsonSerializer.Serialize(edgesPayload);
        var quotaText = string.Join(" · ", quotas.Select(x=>$"{x.Key} {x.Value}"));

        var html = $$"""
<!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><title>World Conquest — Frontier Graph Lab</title>
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"><style>
html,body,#map{height:100%;margin:0}body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;background:#071022;color:#eef3ff}.top{position:absolute;z-index:1000;top:10px;left:10px;right:10px;background:rgba(7,16,34,.94);border:1px solid #2a3555;border-radius:14px;padding:10px 12px;box-shadow:0 8px 24px #0006}.top b{font-size:15px}.muted{font-size:11px;color:#aab5cf;margin-top:3px}.leaflet-popup-content-wrapper,.leaflet-popup-tip{background:#111b31;color:#eef3ff}.legend{position:absolute;z-index:1000;right:10px;bottom:20px;background:rgba(7,16,34,.94);border:1px solid #2a3555;border-radius:12px;padding:9px;font-size:11px}.dot{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:4px}
</style></head><body><div id="map"></div><div class="top"><b>🕸️ Graphe frontalier — laboratoire 60/40</b><div class="muted">{{quotaText}} · traits = candidats d'attaque transfrontalière · rayon labo ≤ 180 km, max 2 voisins/pays/ville</div></div><div class="legend">Clique une ville pour voir ses liaisons.<br><span style="color:#ffcf5a">— liaison transfrontalière candidate</span></div>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script><script>
const cities={{jsonCities}}, edges={{jsonEdges}}; const colors={FR:'#3178ff',BE:'#ff9d19',LU:'#ef3d5c',DE:'#8b5cf6',CH:'#f43f5e',IT:'#22c55e',ES:'#facc15'}; const byId=new Map(cities.map(c=>[c.id,c]));
const map=L.map('map').setView([48,5],5);L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:13,attribution:'&copy; OpenStreetMap contributors'}).addTo(map);const lines=L.layerGroup().addTo(map),points=L.layerGroup().addTo(map);const edgeLines=[];
for(const e of edges){const a=byId.get(e.a),b=byId.get(e.b);const line=L.polyline([[a.lat,a.lon],[b.lat,b.lon]],{color:'#ffcf5a',weight:1.2,opacity:.30}).addTo(lines);edgeLines.push({e,line});}
for(const c of cities){const linked=edges.filter(e=>e.a===c.id||e.b===c.id);L.circleMarker([c.lat,c.lon],{radius:5,color:'#fff',weight:1,fillColor:colors[c.country]||'#aaa',fillOpacity:.9}).bindPopup(`<b>${c.name}</b> (${c.country})<br>${c.population.toLocaleString('fr-FR')} hab.<br><b>${linked.length}</b> liaison(s) candidate(s)<br>${linked.slice(0,10).map(e=>{const o=byId.get(e.a===c.id?e.b:e.a);return `${o.name} (${o.country}) — ${e.km} km`}).join('<br>')}`).on('click',()=>{for(const x of edgeLines)x.line.setStyle({opacity:(x.e.a===c.id||x.e.b===c.id)?.95:.08,weight:(x.e.a===c.id||x.e.b===c.id)?3:1});}).addTo(points)}
map.fitBounds(cities.map(c=>[c.lat,c.lon]),{padding:[20,20]});
</script></body></html>
""";
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir,"frontiers.html"),html);
        await File.WriteAllTextAsync(Path.Combine(outDir,"frontier-graph.json"),JsonSerializer.Serialize(new {populationWeight,quotas,cities=citiesPayload,edges=edgesPayload},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Frontier graph lab: {selected.Sum(x=>x.Value.Count)} cities, {edges.Count} cross-border candidate edges");
    }

    static async Task<List<City>> LoadAsync(HttpClient http,string code,long minPopulation)
    {
        var cacheDir=Path.Combine("data","raw","geonames");Directory.CreateDirectory(cacheDir);var zipPath=Path.Combine(cacheDir,code+".zip");
        if(!File.Exists(zipPath)){var url=$"https://download.geonames.org/export/dump/{code}.zip";await using var input=await http.GetStreamAsync(url);await using var output=File.Create(zipPath);await input.CopyToAsync(output);}
        using var archive=System.IO.Compression.ZipFile.OpenRead(zipPath);var entry=archive.GetEntry(code+".txt")??throw new InvalidOperationException($"Missing {code}.txt");using var reader=new StreamReader(entry.Open());var cities=new List<City>();
        while(await reader.ReadLineAsync() is { } line){var f=line.Split('\t');if(f.Length<15||f[6]!="P")continue;if(!long.TryParse(f[14],out var pop)||pop<minPopulation)continue;if(!long.TryParse(f[0],out var id))continue;if(!double.TryParse(f[4],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var lat))continue;if(!double.TryParse(f[5],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var lon))continue;cities.Add(new City(id,f[1],code,lat,lon,pop));}
        return cities;
    }
}
