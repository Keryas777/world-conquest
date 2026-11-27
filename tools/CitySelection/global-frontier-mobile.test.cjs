const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { webkit, devices } = require('playwright');

const root = path.resolve('data/generated/global-frontier-preview');
const mime = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.geojson', 'application/geo+json; charset=utf-8']
]);

async function main() {
  for (const file of [
    'index.html',
    'voronoi-graph.json',
    'global-frontier-results.json',
    'global-frontier-reconstruction-summary.json',
    'global-frontier-reconstruction-adaptive.geojson',
    'global-frontier-reconstruction-ref40.geojson',
    'global-frontier-reconstruction-ref85.geojson',
    'global-frontier-reconstruction-protected.geojson'
  ]) assert.ok(fs.existsSync(path.join(root, file)), `Missing preview file ${file}`);

  const server = http.createServer((request, response) => {
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const file = path.resolve(root, `.${pathname}`);
    if (!file.startsWith(`${root}${path.sep}`) && file !== path.join(root, 'index.html')) {
      response.writeHead(403).end();
      return;
    }
    fs.readFile(file, (error, content) => {
      if (error) { response.writeHead(404).end(); return; }
      response.writeHead(200, { 'Content-Type': mime.get(path.extname(file)) || 'application/octet-stream' });
      response.end(request.method === 'HEAD' ? undefined : content);
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));

  const browser = await webkit.launch({ headless: true });
  try {
    const context = await browser.newContext({ ...devices['iPhone 13'], locale: 'fr-FR' });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const base = `http://127.0.0.1:${server.address().port}`;
    await page.goto(`${base}/index.html`);
    await page.waitForFunction(() => document.querySelector('#status')?.textContent.includes('cellules source'));
    await page.waitForFunction(() => document.querySelectorAll('#anomaly option').length > 1);

    for (const selector of ['#world', '#pair', '#anomaly', '#reconstruct', '#plus', '#minus', '#fit'])
      assert.ok(await page.locator(selector).isVisible(), `Mobile control ${selector} must be visible`);
    for (const method of ['#ref40', '#ref85', '#protected', '#adaptive']) {
      await page.locator(method).scrollIntoViewIfNeeded();
      await page.locator(method).click();
      assert.ok(await page.locator(method).evaluate(element => element.classList.contains('active')));
    }

    await page.locator('#reconstruct').scrollIntoViewIfNeeded();
    await page.locator('#reconstruct').click();
    await page.waitForFunction(() => document.querySelector('#status')?.textContent.includes('Carte reconstruite adaptive'));
    assert.match(await page.locator('#details').innerText(), /9.?895 cellules/);
    assert.equal(await page.locator('#reconstruct').getAttribute('aria-pressed'), 'true');
    for (const method of ['adaptive', 'ref40', 'ref85', 'protected']) {
      if (method !== 'adaptive') await page.locator(`#${method}`).click();
      await page.waitForFunction(expected => document.querySelector('#status')?.textContent.includes(`Carte reconstruite ${expected}`), method);
      assert.match(await page.locator('#status').innerText(), /9.?895 cellules/);
      assert.match(await page.locator('#details').innerText(), /Monde reconstruit/);
    }
    await page.locator('#adaptive').click();
    await page.waitForFunction(() => document.querySelector('#status')?.textContent.includes('Carte reconstruite adaptive'));

    await page.locator('#plus').click();
    await page.locator('#minus').click();
    await page.locator('#fit').click();
    await page.locator('#world').click();

    const canvas = page.locator('canvas');
    await canvas.evaluate(element => {
      const point = element.getBoundingClientRect();
      const send = (type, pointerId, x, y) => element.dispatchEvent(new PointerEvent(type, {
        pointerId, pointerType: 'touch', isPrimary: pointerId === 1,
        clientX: point.left + x, clientY: point.top + y, bubbles: true
      }));
      send('pointerdown', 1, 120, 200);
      send('pointermove', 1, 130, 210);
      send('pointerup', 1, 130, 210);
      send('pointerdown', 2, 150, 200);
      send('pointerdown', 3, 230, 200);
      send('pointermove', 3, 250, 200);
      send('pointerup', 2, 150, 200);
      send('pointerup', 3, 250, 200);
    });
    assert.equal(await canvas.getAttribute('data-last-gesture'), 'pinch');
    assert.ok(Number(await canvas.getAttribute('data-pan-count')) > 0, 'Mobile pan handler must run');
    assert.deepEqual(errors, [], `Browser errors: ${errors.join('; ')}`);
    await context.close();
    process.stdout.write('Playwright WebKit mobile passed: original map, four modes, adaptive reconstructed cells, anomaly list, zoom controls, pan/pinch pointer handlers, and no page errors.\n');
  } finally {
    await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
