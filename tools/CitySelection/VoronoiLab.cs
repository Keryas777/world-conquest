using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

static class VoronoiLab
{
    sealed record Pt(double X, double Y);

    static readonly GeometryFactory GeometryFactory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

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

        // Canonical initial territories: each country's cities partition only that
        // country's current Natural Earth 10m territory. This guarantees that at T0
        // the union of a country's cells reconstructs its present-day border/coastline.
        var countryTerritories = await LoadCountryTerritoriesAsync(http, quotas.Keys);
        var clippedCells = Enumerable.Repeat<Geometry>(GeometryFactory.CreatePolygon(), cities.Count).ToList();

        var globalIndexById = cities
            .Select((city, index) => (city.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        foreach (var (countryCode, countryCities) in selected)
        {
            if (!countryTerritories.TryGetValue(countryCode, out var countryGeometry))
                throw new InvalidOperationException($"Missing Natural Earth territory for {countryCode}.");

            var countryPts = countryCities
                .Select(city => new Pt(city.Lon * cosLat, city.Lat))
                .ToList();

            var env = countryGeometry.EnvelopeInternal;
            var minX = env.MinX * cosLat - .5;
            var maxX = env.MaxX * cosLat + .5;
            var minY = env.MinY - .5;
            var maxY = env.MaxY + .5;

            for (var localIndex = 0; localIndex < countryCities.Count; localIndex++)
            {
                var poly = new List<Pt>
                {
                    new(minX, minY), new(maxX, minY),
                    new(maxX, maxY), new(minX, maxY)
                };

                for (var j = 0; j < countryPts.Count && poly.Count > 0; j++)
                {
                    if (localIndex == j) continue;
                    poly = ClipCloserTo(poly, countryPts[localIndex], countryPts[j]);
                }

                var rawCell = ToPolygon(poly, cosLat);
                Geometry clipped;
                try
                {
                    clipped = rawCell.Intersection(countryGeometry);
                }
                catch
                {
                    clipped = rawCell.Buffer(0).Intersection(countryGeometry.Buffer(0));
                }

                if (!clipped.IsValid) clipped = clipped.Buffer(0);
                clippedCells[globalIndexById[countryCities[localIndex].Id]] = clipped;
            }
        }

        // Rebuild the graph from the final constrained cells themselves.
        // Same-country contacts are internal edges; different-country contacts are
        // initial political borders. Corner-only contacts do not create adjacency.
        var adjacency = new HashSet<(int A, int B)>();
        for (var i = 0; i < clippedCells.Count; i++)
        {
            var a = clippedCells[i];
            if (a.IsEmpty) continue;

            for (var j = i + 1; j < clippedCells.Count; j++)
            {
                var b = clippedCells[j];
                if (b.IsEmpty || !a.EnvelopeInternal.Intersects(b.EnvelopeInternal)) continue;

                Geometry shared;
                try
                {
                    shared = a.Boundary.Intersection(b.Boundary);
                }
                catch
                {
                    shared = a.Buffer(0).Boundary.Intersection(b.Buffer(0).Boundary);
                }

                if (!shared.IsEmpty && shared.Length > 1e-6)
                    adjacency.Add((i, j));
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
            geometry = GeometryToGeoJson(clippedCells[i])
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
<div class="top"><b>🕸️ Voronoï — maillage implicite des villes</b><div class="muted">{{quotaText}} · 60/40 · Voronoï contraint par pays · Natural Earth 10m</div></div>
<div class="legend">Clique une ville : ses voisins terrestres apparaissent.<br><span style="color:#ffcf5a">— voisin étranger</span> · <span style="color:#9ca3af">— voisin interne</span></div>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<script>
const cells={{jsonCells}}, edges={{jsonEdges}};
const colors={FR:'#3178ff',BE:'#ff9d19',LU:'#ef3d5c',DE:'#8b5cf6',CH:'#f43f5e',IT:'#22c55e',ES:'#facc15'};
const byId=new Map(cells.map(c=>[c.id,c]));
const neighbors=new Map(cells.map(c=>[c.id,[]]));
for(const e of edges){neighbors.get(e.a).push({...e,other:e.b});neighbors.get(e.b).push({...e,other:e.a});}
const map=L.map('map').setView([48,5],5);
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:13,attribution:'&copy; OpenStreetMap contributors'}).addTo(map);

const cellLayers=[];
for(const c of cells){
  if(!c.geometry) continue;
  const layer=L.geoJSON(c.geometry,{style:{
    color:'#94a3b8',weight:.8,opacity:.72,
    fillColor:colors[c.country]||'#aaa',fillOpacity:.12
  },interactive:false}).addTo(map);
  cellLayers.push(layer);
}
const highlight=L.layerGroup().addTo(map);
const cityLayers=[];
for(const c of cells){
  const list=neighbors.get(c.id)||[];
  const foreign=list.filter(e=>e.foreign);
  const marker=L.circleMarker([c.lat,c.lon],{radius:5,color:'#fff',weight:1,fillColor:colors[c.country]||'#aaa',fillOpacity:.95})
   .bindPopup(()=>{
      const rows=list.slice().sort((a,b)=>a.km-b.km).map(e=>{const o=byId.get(e.other);return '<span style="color:'+(e.foreign?'#ffcf5a':'#cbd5e1')+'">'+o.name+' ('+o.country+') — '+e.km+' km</span>';}).join('<br>');
      return '<b>'+c.name+'</b> ('+c.country+')<br>'+Number(c.population).toLocaleString('fr-FR')+' hab.<br><b>'+list.length+'</b> voisin(s) terrestre(s), <b>'+foreign.length+'</b> étranger(s)<br>'+rows;
   })
   .on('click',()=>{
      highlight.clearLayers();
      for(const e of list){
        const o=byId.get(e.other);
        L.polyline([[c.lat,c.lon],[o.lat,o.lon]],{color:e.foreign?'#ffcf5a':'#9ca3af',weight:e.foreign?3:2,opacity:.95,interactive:false}).addTo(highlight);
      }
   }).addTo(map);
  cityLayers.push(marker);
}
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
                new
                {
                    populationWeight,
                    quotas,
                    referenceLat = referenceLat * 180.0 / Math.PI,
                    generation = "country-constrained Voronoi",
                    borderSource = "Natural Earth 1:10m admin-0 countries",
                    terrestrialAdjacencyCount = adjacency.Count,
                    cells = cellPayload,
                    edges = edgePayload
                },
                new JsonSerializerOptions { WriteIndented = true }));

        var foreignEdges = edgePayload.Count(e => e.foreign);
        Console.WriteLine(
            $"Voronoi lab: {cities.Count} country-constrained cells, " +
            $"{edgePayload.Count} terrestrial adjacencies, {foreignEdges} cross-border adjacencies");
    }

    static Polygon ToPolygon(List<Pt> poly, double cosLat)
    {
        if (poly.Count < 3) return GeometryFactory.CreatePolygon();

        var coords = poly
            .Select(p => new Coordinate(p.X / cosLat, p.Y))
            .ToList();

        if (!coords[0].Equals2D(coords[^1]))
            coords.Add(new Coordinate(coords[0]));

        return GeometryFactory.CreatePolygon(coords.ToArray());
    }

    static object? GeometryToGeoJson(Geometry geometry)
    {
        if (geometry.IsEmpty) return null;

        object Coordinates(Coordinate[] coords) =>
            coords.Select(c => new[] { c.X, c.Y }).ToArray();

        object PolygonCoordinates(Polygon polygon)
        {
            var rings = new List<object> { Coordinates(polygon.ExteriorRing.Coordinates) };
            for (var i = 0; i < polygon.NumInteriorRings; i++)
                rings.Add(Coordinates(polygon.GetInteriorRingN(i).Coordinates));
            return rings;
        }

        if (geometry is Polygon polygon)
            return new { type = "Polygon", coordinates = PolygonCoordinates(polygon) };

        if (geometry is MultiPolygon multi)
        {
            var polygons = Enumerable.Range(0, multi.NumGeometries)
                .Select(i => PolygonCoordinates((Polygon)multi.GetGeometryN(i)))
                .ToArray();
            return new { type = "MultiPolygon", coordinates = polygons };
        }

        // Intersection can exceptionally return a geometry collection.
        var polygonParts = Enumerable.Range(0, geometry.NumGeometries)
            .Select(i => geometry.GetGeometryN(i))
            .OfType<Polygon>()
            .ToArray();

        if (polygonParts.Length == 1)
            return new { type = "Polygon", coordinates = PolygonCoordinates(polygonParts[0]) };

        if (polygonParts.Length > 1)
            return new
            {
                type = "MultiPolygon",
                coordinates = polygonParts.Select(PolygonCoordinates).ToArray()
            };

        return null;
    }

    static async Task<Dictionary<string, Geometry>> LoadCountryTerritoriesAsync(
        HttpClient http,
        IEnumerable<string> countryCodes)
    {
        var wanted = countryCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cacheDir = Path.Combine("data", "raw", "natural-earth");
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, "ne_10m_admin_0_countries.geojson");

        if (!File.Exists(path))
        {
            const string url =
                "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_10m_admin_0_countries.geojson";
            var json = await http.GetStringAsync(url);
            await File.WriteAllTextAsync(path, json);
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var partsByCountry = wanted.ToDictionary(
            code => code,
            _ => new List<Geometry>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            var properties = feature.GetProperty("properties");
            var iso = ReadIso2(properties);
            if (iso is null || !wanted.Contains(iso)) continue;

            var geometry = ParseGeoJsonGeometry(feature.GetProperty("geometry"));
            if (geometry is not null && !geometry.IsEmpty)
                partsByCountry[iso].Add(geometry);
        }

        var result = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in wanted)
        {
            var parts = partsByCountry[code];
            if (parts.Count == 0)
                throw new InvalidOperationException($"Natural Earth territory not found for {code}.");

            var geometry = UnaryUnionOp.Union(parts);
            if (!geometry.IsValid) geometry = geometry.Buffer(0);
            result[code] = geometry;
        }

        return result;
    }

    static string? ReadIso2(JsonElement properties)
    {
        foreach (var key in new[] { "ISO_A2", "ISO_A2_EH", "WB_A2" })
        {
            if (!properties.TryGetProperty(key, out var value)) continue;
            var code = value.GetString();
            if (!string.IsNullOrWhiteSpace(code) && code != "-99")
                return code;
        }

        return null;
    }

    static Geometry? ParseGeoJsonGeometry(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("type", out var typeElement)) return null;
        if (!geometry.TryGetProperty("coordinates", out var coordinates)) return null;

        return typeElement.GetString() switch
        {
            "Polygon" => ParsePolygon(coordinates),
            "MultiPolygon" => GeometryFactory.CreateMultiPolygon(
                coordinates.EnumerateArray().Select(ParsePolygon).ToArray()),
            _ => null
        };
    }

    static Polygon ParsePolygon(JsonElement polygonCoordinates)
    {
        var rings = polygonCoordinates.EnumerateArray()
            .Select(ParseRing)
            .ToArray();

        if (rings.Length == 0)
            return GeometryFactory.CreatePolygon();

        return GeometryFactory.CreatePolygon(
            rings[0],
            rings.Skip(1).ToArray());
    }

    static LinearRing ParseRing(JsonElement ringCoordinates)
    {
        var coords = ringCoordinates.EnumerateArray()
            .Select(point =>
            {
                var values = point.EnumerateArray().ToArray();
                return new Coordinate(values[0].GetDouble(), values[1].GetDouble());
            })
            .ToList();

        if (coords.Count > 0 && !coords[0].Equals2D(coords[^1]))
            coords.Add(new Coordinate(coords[0]));

        return GeometryFactory.CreateLinearRing(coords.ToArray());
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
