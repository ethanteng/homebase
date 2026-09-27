const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const html = fs.readFileSync(__dirname + '/hardware.html', 'utf8');
const attr = (tag, name) => (tag.match(new RegExp(`${name}="([^"]*)"`)) || [])[1];
const unescape = (s) => s.replaceAll('&amp;', '&');

test('every drive size links to a Newegg page over https', () => {
  const urls = [...html.matchAll(/data-url="([^"]*)"/g)].map((m) => new URL(unescape(m[1])));
  assert.ok(urls.length >= 9, 'three sizes per drive');
  for (const url of urls) assert.equal(url.origin, 'https://www.newegg.com', url.href);
});

test('without JavaScript, each card links to its checked size, tagged for the 8 TB default', () => {
  const cards = html.split('<article').slice(1);
  assert.equal(cards.length, 3);
  for (const card of cards) {
    const drive = attr(card, 'data-drive');
    const checked = card.match(/<button[^>]*aria-checked="true"[^>]*>/g);
    assert.equal(checked.length, 1, `${drive} has one checked size`);
    const tb = attr(checked[0], 'data-tb');
    const cta = new URL(unescape(attr(card.match(/<a [^>]*data-cta[^>]*>/)[0], 'href')));
    const url = new URL(unescape(attr(checked[0], 'data-url')));
    for (const key of cta.searchParams.keys()) if (key.startsWith('utm_')) url.searchParams.set(key, cta.searchParams.get(key));
    assert.equal(cta.href, url.href, `${drive} CTA is its checked size's URL`);
    assert.equal(cta.searchParams.get('utm_content'), `${drive}-${tb}tb-card`);
    assert.equal(cta.searchParams.get('utm_term'), 'need-8tb');
    assert.ok(card.includes(`View ${tb} TB at Newegg`), `${drive} CTA names ${tb} TB`);
  }
});

test('the page is canonical at /hardware, in the sitemap, and linked from every footer', async () => {
  assert.match(html, /<link rel="canonical" href="https:\/\/www.uncloud.life\/hardware" \/>/);
  const title = html.match(/<title>([^<]*)<\/title>/)[1];
  const description = attr(html.match(/<meta name="description"[^>]*>/)[0], 'content');
  assert.ok(title.length <= 60, `title is ${title.length} characters`);
  assert.ok(description.length <= 155, `description is ${description.length} characters`);
  assert.ok(fs.readFileSync(__dirname + '/public/sitemap.xml', 'utf8').includes('<loc>https://www.uncloud.life/hardware</loc>'));

  const { SLUGS, render } = await import('./acquisition-pages.mjs');
  const footer = (page) => page.slice(page.indexOf('<footer class="site-footer">'));
  const pages = { index: fs.readFileSync(__dirname + '/index.html', 'utf8'), ...Object.fromEntries(SLUGS.map((s) => [s, render(s)])) };
  for (const [name, page] of Object.entries(pages)) assert.ok(footer(page).includes('href="/hardware">Buy more storage</a>'), `${name} footer lacks /hardware`);
});
