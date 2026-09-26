const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const load = () => import('./acquisition-pages.mjs');
const dollars = (cents) => '$' + (cents / 100).toLocaleString('en-US', { minimumFractionDigits: 2 });

test('each generated page matches acquisition-pages.mjs (rerun it after editing the copy)', async () => {
  const { SLUGS, render, file } = await load();
  for (const slug of SLUGS) assert.equal(fs.readFileSync(file(slug), 'utf8'), render(slug), `${slug}.html is stale`);
});

test('titles and descriptions fit in search results', async () => {
  const { PAGES } = await load();
  for (const [slug, p] of Object.entries(PAGES)) {
    assert.ok(p.title.length <= 60, `${slug} title is ${p.title.length} characters`);
    assert.ok(p.description.length <= 155, `${slug} description is ${p.description.length} characters`);
  }
});

test('each page is canonical at its own clean URL, with valid FAQ structured data', async () => {
  const { SLUGS, PAGES, render } = await load();
  for (const slug of SLUGS) {
    const html = render(slug);
    assert.match(html, new RegExp(`<link rel="canonical" href="https://www.uncloud.life/${slug}" />`));
    assert.match(html, new RegExp(`<meta property="og:url" content="https://www.uncloud.life/${slug}" />`));
    const ld = JSON.parse(html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)[1]);
    const faq = ld['@graph'].find((node) => node['@type'] === 'FAQPage');
    assert.deepEqual(faq.mainEntity.map((q) => q.name), PAGES[slug].faq.map((f) => f.q));
    assert.equal((html.match(/class="launchlist-widget"/g) || []).length, 2, `${slug} ends on the signup form`);
  }
});

test('every page and the homepage link to every page, and the sitemap lists them', async () => {
  const { SLUGS, render } = await load();
  const home = fs.readFileSync(__dirname + '/index.html', 'utf8');
  const sitemap = fs.readFileSync(__dirname + '/public/sitemap.xml', 'utf8');
  for (const slug of SLUGS) {
    assert.ok(sitemap.includes(`<loc>https://www.uncloud.life/${slug}</loc>`), `sitemap lacks ${slug}`);
    assert.ok(home.includes(`href="/${slug}"`), `homepage lacks ${slug}`);
    for (const other of SLUGS) assert.ok(render(other).includes(`href="/${slug}"`), `${other} lacks ${slug}`);
  }
});

test('price comparisons add up from the monthly price', async () => {
  const { PAGES } = await load();
  for (const [slug, p] of Object.entries(PAGES)) {
    if (!p.monthly) continue;
    const year = p.monthly * 12;
    assert.deepEqual(p.cmp.rows.map((r) => r.a), [year, year * 3, year * 5].map(dollars), slug);
    assert.equal(p.cmp.boxBig, dollars(year * 5 - 7900), slug);
    assert.equal(p.hero.totalBig, dollars(year * 3), slug);
    for (const row of p.hero.rows) assert.equal(row.value, dollars(year), slug);
  }
});
