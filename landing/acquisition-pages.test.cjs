const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const load = () => import('./acquisition-pages.mjs');
// The id of each buy button, in page order; every one must start the checkout at /buy.
const buyIds = (html) => [...html.matchAll(/<a [^>]*data-buy[^>]*>/g)].map(([tag]) => {
  assert.ok(tag.includes('href="/buy"'), tag);
  return tag.match(/id="([^"]+)"/)[1];
});
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
    assert.deepEqual(buyIds(html), ['buy-hero', 'buy-pricing', 'buy-close'], `${slug} has the buy button in the hero, the pricing card and at the close`);
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
    assert.equal(p.cmp.boxBig, dollars(year * 5 - 9900), slug);
  }
});

test('the homepage and every acquisition page link to /partners in the footer', async () => {
  const { SLUGS, render } = await load();
  const footer = (html) => html.slice(html.indexOf('<footer class="site-footer">'));
  const home = fs.readFileSync(__dirname + '/index.html', 'utf8');
  assert.ok(footer(home).includes('href="/partners">For Partners</a>'), 'homepage footer lacks /partners');
  for (const slug of SLUGS) assert.ok(footer(render(slug)).includes('href="/partners">For Partners</a>'), `${slug} footer lacks /partners`);
});

test('the homepage, FAQ and storage page buy straight from their calls to action', () => {
  const read = (name) => fs.readFileSync(`${__dirname}/${name}.html`, 'utf8');
  assert.deepEqual(buyIds(read('index')), ['buy-hero', 'buy-pricing', 'buy-close']);
  assert.deepEqual(buyIds(read('faq')), ['buy-close']);
  assert.deepEqual(buyIds(read('hardware')), ['buy-close']);
  for (const name of ['index', 'faq', 'hardware']) assert.ok(!read(name).includes('buymeacoffee.com'), `${name} still links the old reservation`);
});

test('every page charges the full $99: no $79 offer, and every buy button says $99', async () => {
  const { SLUGS, render } = await load();
  const pages = {};
  for (const name of ['index', 'faq', 'hardware', 'help', 'privacy', 'terms', 'thanks', 'partners']) pages[name] = fs.readFileSync(`${__dirname}/${name}.html`, 'utf8');
  for (const slug of SLUGS) pages[slug] = render(slug);
  for (const [name, html] of Object.entries(pages)) {
    assert.ok(!html.includes('$79'), `${name} still mentions $79`);
    for (const [label] of html.matchAll(/data-buy>[^<]*</g)) assert.equal(label, 'data-buy>Buy now –&nbsp;$99<', name);
  }
});
