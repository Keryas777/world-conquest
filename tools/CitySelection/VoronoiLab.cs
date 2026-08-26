using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

static class VoronoiLab
{
    sealed record Pt(double X, double Y);

    public static async Task GenerateAsync(
        HttpClient http,
        string outDir,
        IReadOnlyDictionary<string, List<City>> alreadyLoaded,
        double populationWeight = .60)
    {
        var quotas = new Dictionary<string, int>
        {
            ["FR"] = 85, ["BE"] = 18, ["LU"] = 3,
            ["DE"] = 75, ["CH"] = 20, ["IT"] = 55, ["ES"] = 65
        };

        const long minPopulation = 500;
        var selected = new Dictionary<string, List<City>>();

        foreach (var (code, quota) in quotas)
        {
            List<City> candidates;
            if (alreadyLoaded.TryGetValue(code, out var loaded)) candidates = loaded;
            else candidates = await LoadAsync(http, code, minPopulation);

            selected[code] = Selector.Select(candidates, quota, populationWeight);
        }

        var cities = selected.Values.SelectMany(x => x).ToList();
        var referenceLat = cities.Average(c => c.Lat) * Math.PI / 180.0;
        var cosLat = Math.Cos(referenceLat);
        var pts = cities.Select(c => new Pt(c.Lon * cosLat, c.Lat)).ToList();

        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        var padX = Math.Max(1.5, (maxX - minX) * .08);
        var padY = Math.Max(1.5, (maxY - minY) * .08);
        minX -= padX; maxX += padX; minY -= padY; maxY += padY;

        var cells = new List<List<Pt>>(cities.Count);
        var adjacency = new HashSet<(int A, int B)>();

        for (var i = 0; i < pts.Count; i++)
        {
            var poly = new List<Pt>
            {
                new(minX, minY), new(maxX, minY),
                new(maxX, maxY), new(minX, maxY)
            };

            for (var j = 0; j < pts.Count && poly.Count > 0; j++)
            {
                if (i == j) continue;
                poly = ClipCloserTo(poly, pts[i], pts[j]);
            }

            cells.Add(poly);

            const double eps = 1e-7;
            for (var j = 0; j < pts.Count; j++)
            {
                if (i == j) continue;
                var a = pts[j].X - pts[i].X;
                var b = pts[j].Y - pts[i].Y;
                var c = (pts[j].X * pts[j].X + pts[j].Y * pts[j].Y
                       - pts[i].X * pts[i].X - pts[i].Y * pts[i].Y) / 2.0;
                var scale = Math.Max(1.0, Math.Sqrt(a * a + b * b));
                var tol = eps * scale;

                for (var k = 0; k < poly.Count; k++)
                {
                    var p = poly[k];
                    var q = poly[(k + 1) % poly.Count];
                    var dp = Math.Abs(a * p.X + b * p.Y - c);
                    var dq = Math.Abs(a * q.X + b * q.Y - c);
                    var len = Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2));
                    if (dp <= tol && dq <= tol && len > 1e-6)
                    {
                        adjacency.Add((Math.Min(i, j), Math.Max(i, j)));
                        break;
                    }
                }
            }
        }

        var cellPayload = cities.Select((city, i) => new
        {
            id = city.Id,
            name = city.Name,
            country = city.Country,
            lat = city.Lat,
            lon = city.Lon,
            population = city.Population,
            polygon = cells[i].Select(p => new[] { p.Y, p.X / cosLat })
        }).ToList();

        var edgePayload = adjacency
            .OrderBy(x => x.A).ThenBy(x => x.B)
            .Select(x => new
            {
                a = cities[x.A].Id,
                b = cities[x.B].Id,
                foreign = cities[x.A].Country != cities[x.B].Country,
                km = Math.Round(Geo.Km(cities[x.A], cities[x.B]), 1)
            }).ToList();

        var jsonCells = JsonSerializer.Serialize(cellPayload);
        var jsonEdges = JsonSerializer.Serialize(edgePayload);
        var quotaText = string.Join(" · ", quotas.Select(x => $"{x.Key} {x.Value}"));

        var html = $$"""
<!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>World Conquest — Voronoï Lab</title>
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
<style>
html,body,#map{height:100%;margin:0}body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;background:#071022;color:#eef3ff}
.top{position:absolute;z-index:1000;top:10px;left:10px;right:10px;background:rgba(7,16,34,.94);border:1px solid #2a3555;border-radius:14px;padding:10px 12px;box-shadow:0 8px 24px #0006}.top b{font-size:15px}.muted{font-size:11px;color:#aab5cf;margin-top:3px}
.legend{position:absolute;z-index:1000;right:10px;bottom:20px;background:rgba(7,16,34,.94);border:1px solid #2a3555;border-radius:12px;padding:9px;font-size:11px}
.leaflet-popup-content-wrapper,.leaflet-popup-tip{background:#111b31;color:#eef3ff}
</style></head><body>
<div id="map"></div>
<div class="top"><b>🕸️ Voronoï — maillage implicite des villes</b><div class="muted">{{quotaText}} · 60/40 · Voronoï × terres émergées · Natural Earth 50m</div></div>
<div class="legend">Clique une ville : ses voisins directs apparaissent.<br><span style="color:#ffcf5a">— voisin étranger</span> · <span style="color:#9ca3af">— voisin interne</span></div>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>\n<script src="https://cdn.jsdelivr.net/npm/@turf/turf@7/turf.min.js"></script>
<script>
const cells={{jsonCells}}, edges={{jsonEdges}};
const colors={FR:'#3178ff',BE:'#ff9d19',LU:'#ef3d5c',DE:'#8b5cf6',CH:'#f43f5e',IT:'#22c55e',ES:'#facc15'};
const byId=new Map(cells.map(c=>[c.id,c]));
const neighbors=new Map(cells.map(c=>[c.id,[]]));
for(const e of edges){neighbors.get(e.a).push({...e,other:e.b});neighbors.get(e.b).push({...e,other:e.a});}
const map=L.map('map').setView([48,5],5);
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:13,attribution:'&copy; OpenStreetMap contributors'}).addTo(map);

const cellLayers=[];
const highlight=L.layerGroup().addTo(map);
const cityLayers=[];
let landFeature=null;
let maskReady=false;

function rawCellFeature(c){
  const ring=c.polygon.map(p=>[p[1],p[0]]);
  if(ring.length && (ring[0][0]!==ring[ring.length-1][0] || ring[0][1]!==ring[ring.length-1][1])) ring.push([...ring[0]]);
  return turf.polygon([ring],{id:c.id});
}
function addCellGeometry(geometry,c){
  const layer=L.geoJSON(geometry,{style:{
    color:'#64748b',weight:.8,opacity:.78,
    fillColor:colors[c.country]||'#aaa',fillOpacity:.16
  },interactive:false}).addTo(map);
  cellLayers.push(layer);
}
function drawRawCells(){
  for(const c of cells) addCellGeometry(rawCellFeature(c),c);
}
async function drawLandClippedCells(){
  try{
    const response=await fetch('https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_50m_land.geojson',{cache:'force-cache'});
    if(!response.ok) throw new Error('Natural Earth HTTP '+response.status);
    const landGeo=await response.json();
    landFeature=turf.combine(landGeo).features[0];
    for(const c of cells){
      const clipped=turf.intersect(turf.featureCollection([rawCellFeature(c),landFeature]));
      if(clipped) addCellGeometry(clipped,c);
    }
    maskReady=true;
  }catch(err){
    console.error('Land mask failed, raw Voronoi fallback:',err);
    drawRawCells();
    document.querySelector('.muted').textContent='{{quotaText}} · 60/40 · masque terrestre indisponible — Voronoï brut';
  }
}

function edgeIsTerrestrial(e){
  if(!maskReady || !landFeature) return true;
  const a=byId.get(e.a), b=byId.get(e.b);
  if(!a || !b) return false;
  let sea=0;
  const samples=31;
  for(let i=1;i<samples-1;i++){
    const t=i/(samples-1);
    const lon=a.lon+(b.lon-a.lon)*t;
    const lat=a.lat+(b.lat-a.lat)*t;
    if(!turf.booleanPointInPolygon(turf.point([lon,lat]),landFeature)) sea++;
  }
  return sea<=1;
}

for(const c of cells){
  const list=neighbors.get(c.id)||[];
  const marker=L.circleMarker([c.lat,c.lon],{radius:5,color:'#fff',weight:1,fillColor:colors[c.country]||'#aaa',fillOpacity:.95})
   .bindPopup(()=>{
      const terrestrial=list.filter(edgeIsTerrestrial);
      const rejected=list.filter(e=>!edgeIsTerrestrial(e));
      const foreign=terrestrial.filter(e=>e.foreign);
      const rows=terrestrial.slice().sort((a,b)=>a.km-b.km).map(e=>{
        const o=byId.get(e.other);
        return '<span style="color:'+(e.foreign?'#ffcf5a':'#cbd5e1')+'">'+o.name+' ('+o.country+') — '+e.km+' km</span>';
      }).join('<br>');
      const seaRows=rejected.length
        ? '<br><span style="color:#7dd3fc">Mer écartée : '+rejected.map(e=>byId.get(e.other).name).join(', ')+'</span>'
        : '';
      return '<b>'+c.name+'</b> ('+c.country+')<br>'+Number(c.population).toLocaleString('fr-FR')+
        ' hab.<br><b>'+terrestrial.length+'</b> voisin(s) terrestre(s), <b>'+foreign.length+
        '</b> étranger(s)'+seaRows+'<br>'+rows;
   })
   .on('click',()=>{
      highlight.clearLayers();
      for(const e of list){
        if(!edgeIsTerrestrial(e)) continue;
        const o=byId.get(e.other);
        L.polyline([[c.lat,c.lon],[o.lat,o.lon]],{color:e.foreign?'#ffcf5a':'#9ca3af',weight:e.foreign?3:2,opacity:.95}).addTo(highlight);
      }
   }).addTo(map);
  cityLayers.push(marker);
}
drawLandClippedCells();
function applyZoomStyle(){
  const z=map.getZoom();
  const radius =
    z<=5 ? 5 :
    z===6 ? 7 :
    z===7 ? 10 :
    z===8 ? 14 :
    z===9 ? 18 :
    z===10 ? 23 :
    z===11 ? 28 :
    z===12 ? 34 : 40;
  const cellWeight =
    z<=5 ? .8 :
    z===6 ? 1 :
    z===7 ? 1.3 :
    z===8 ? 1.7 :
    z===9 ? 2.2 :
    z===10 ? 2.8 :
    z===11 ? 3.5 :
    z===12 ? 4.2 : 5;
  const cellOpacity = z<=5?.72:z===6?.80:z===7?.86:z===8?.90:z===9?.93:.96;
  const fillOpacity = z<=5?.12:z===6?.14:z===7?.16:z===8?.18:z===9?.20:z===10?.22:z===11?.24:z===12?.26:.28;
  for(const marker of cityLayers) marker.setRadius(radius);
  for(const layer of cellLayers) layer.setStyle({weight:cellWeight,opacity:cellOpacity,fillOpacity});
}
map.on('zoomend',applyZoomStyle);
map.fitBounds(cells.map(c=>[c.lat,c.lon]),{padding:[20,20]});
applyZoomStyle();
</script></body></html>
""";

        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "voronoi.html"), html);
        await File.WriteAllTextAsync(
            Path.Combine(outDir, "voronoi-graph.json"),
            JsonSerializer.Serialize(
                new { populationWeight, quotas, referenceLat = referenceLat * 180.0 / Math.PI, cells = cellPayload, edges = edgePayload },
                new JsonSerializerOptions { WriteIndented = true }));

        var foreignEdges = edgePayload.Count(e => e.foreign);
        Console.WriteLine($"Voronoi lab: {cities.Count} cells, {edgePayload.Count} adjacencies, {foreignEdges} cross-border adjacencies");
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
