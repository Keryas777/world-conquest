using System.Text.Json;

static class Viewer
{
    public static void WriteHtml(
        string path,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, List<City>>> snapshots,
        IReadOnlyList<CountryConfig> configs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = snapshots.ToDictionary(
            x => x.Key.ToString(),
            x => x.Value.ToDictionary(
                g => g.Key,
                g => g.Value.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    country = c.Country,
                    lat = c.Lat,
                    lon = c.Lon,
                    population = c.Population
                }).ToArray()));

        var metrics = snapshots.ToDictionary(
            x => x.Key.ToString(),
            x => x.Value.ToDictionary(
                g => g.Key,
                g =>
                {
                    var s = Results.NearestStats(g.Value);
                    return new { mean = Math.Round(s.Mean, 1), min = Math.Round(s.Min, 1), max = Math.Round(s.Max, 1) };
                }));

        var configPayload = configs.ToDictionary(c => c.Code, c => new { name = c.Name, quota = c.Quota });
        var displayNames = new Dictionary<string, string>
        {
            ["FR"] = "France",
            ["BE"] = "Belgique",
            ["LU"] = "Luxembourg"
        };
        var quotaSummary = string.Join(" · ", configs.Select(c => $"{displayNames.GetValueOrDefault(c.Code, c.Name)} {c.Quota}"));
        var json = JsonSerializer.Serialize(payload);
        var metricsJson = JsonSerializer.Serialize(metrics);
        var configsJson = JsonSerializer.Serialize(configPayload);

        var html = $$"""
<!doctype html>
<html lang="fr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
  <title>World Conquest — City Selection Lab</title>
  <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
  <style>
    :root { color-scheme: dark; --bg:#0b1020; --panel:#151c31; --text:#eef3ff; --muted:#9aa8c7; --border:#2a3555; --accent:#72a7ff; }
    * { box-sizing:border-box; }
    html,body { margin:0; min-height:100%; background:var(--bg); color:var(--text); font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
    .app { display:grid; grid-template-rows:auto 1fr; min-height:100vh; }
    header { padding:14px 14px 10px; background:rgba(11,16,32,.96); border-bottom:1px solid var(--border); position:relative; z-index:1000; }
    h1 { margin:0 0 4px; font-size:20px; }
    .sub { color:var(--muted); font-size:13px; }
    .controls { display:flex; gap:8px; flex-wrap:wrap; margin-top:12px; }
    button { border:1px solid var(--border); background:var(--panel); color:var(--text); padding:8px 11px; border-radius:10px; font-weight:650; cursor:pointer; }
    button.active { background:var(--accent); color:#071126; border-color:var(--accent); }
    main { position:relative; min-height:0; }
    #map { width:100%; height:calc(100vh - 126px); min-height:520px; background:#dfe7ef; }
    .panel { position:absolute; z-index:900; left:12px; bottom:18px; width:min(360px,calc(100% - 24px)); background:rgba(11,16,32,.94); backdrop-filter:blur(8px); border:1px solid var(--border); border-radius:14px; padding:12px; box-shadow:0 10px 28px rgba(0,0,0,.35); }
    .panel h2 { font-size:15px; margin:0 0 8px; }
    .metric { display:grid; grid-template-columns:1fr auto; gap:6px 14px; font-size:13px; margin-top:5px; }
    .metric div:nth-child(odd) { color:var(--muted); }
    .legend { display:flex; gap:12px; flex-wrap:wrap; margin-top:9px; color:var(--muted); font-size:12px; }
    .dot { width:9px; height:9px; border-radius:50%; display:inline-block; margin-right:5px; }
    .city-popup strong { font-size:14px; }
    @media (max-width:700px) {
      header { padding-top:max(12px,env(safe-area-inset-top)); }
      #map { height:calc(100vh - 145px); min-height:460px; }
      .panel { bottom:max(12px,env(safe-area-inset-bottom)); }
    }
  </style>
</head>
<body>
<div class="app">
  <header>
    <h1>🌍 World Conquest — sélection des villes</h1>
    <div class="sub">GeoNames ≥ 500 habitants · {{quotaSummary}}</div>
    <div class="controls" id="weights"></div>
  </header>
  <main>
    <div id="map"></div>
    <section class="panel">
      <h2 id="panel-title">Réglage</h2>
      <div id="stats"></div>
      <div class="legend">
        <span><i class="dot" style="background:#3178ff"></i>France</span>
        <span><i class="dot" style="background:#ff9d19"></i>Belgique</span>
        <span><i class="dot" style="background:#ef3d5c"></i>Luxembourg</span>
      </div>
    </section>
  </main>
</div>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<script>
const data = {{json}};
const metrics = {{metricsJson}};
const configs = {{configsJson}};
const colors = { FR:'#3178ff', BE:'#ff9d19', LU:'#ef3d5c' };
const weights = Object.keys(data).map(Number).sort((a,b)=>a-b);
const map = L.map('map', { zoomControl:true }).setView([48.7, 2.8], 5);
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 13,
  attribution:'&copy; OpenStreetMap contributors'
}).addTo(map);
const layer = L.layerGroup().addTo(map);
const controls = document.getElementById('weights');
const stats = document.getElementById('stats');
const title = document.getElementById('panel-title');

for (const weight of weights) {
  const b = document.createElement('button');
  b.textContent = `${weight}% population`;
  b.dataset.weight = String(weight);
  b.onclick = () => render(weight);
  controls.appendChild(b);
}

function radius(pop) {
  return Math.max(4, Math.min(11, 3.2 + Math.log10(Math.max(500,pop)) - 2.6));
}

function render(weight) {
  layer.clearLayers();
  document.querySelectorAll('#weights button').forEach(b => b.classList.toggle('active', Number(b.dataset.weight) === weight));
  const snapshot = data[String(weight)];
  const bounds = [];
  for (const country of ['FR','BE','LU']) {
    for (const city of snapshot[country]) {
      bounds.push([city.lat,city.lon]);
      L.circleMarker([city.lat,city.lon], {
        radius: radius(city.population),
        color:'#ffffff', weight:1,
        fillColor:colors[country], fillOpacity:.84
      }).bindPopup(`<div class="city-popup"><strong>${city.name}</strong><br>${configs[country].name}<br>${city.population.toLocaleString('fr-FR')} habitants<br><small>${city.lat.toFixed(4)}, ${city.lon.toFixed(4)}</small></div>`).addTo(layer);
    }
  }
  title.textContent = `${weight}% population / ${100-weight}% couverture`;
  stats.innerHTML = ['FR','BE','LU'].map(country => {
    const m = metrics[String(weight)][country];
    return `<div class="metric"><div><b>${configs[country].name}</b> · ${configs[country].quota} villes</div><div></div><div>Voisin moyen</div><div>${m.mean.toFixed(1)} km</div><div>Plage min–max</div><div>${m.min.toFixed(1)}–${m.max.toFixed(1)} km</div></div>`;
  }).join('');
  if (!map._initialFitDone) {
    map.fitBounds(bounds, { padding:[24,24] });
    map._initialFitDone = true;
  }
}

render(weights.includes(60) ? 60 : weights[Math.floor(weights.length/2)]);
</script>
</body>
</html>
""";

        File.WriteAllText(path, html);
    }
}
