const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const read = (name) => fs.readFileSync(`${__dirname}/${name}`, 'utf8');
const html = read('faq.html');
const footer = (page) => page.slice(page.indexOf('<footer class="site-footer">'), page.indexOf('</footer>'));
const header = (page) => page.slice(page.indexOf('<header class="site-header">'), page.indexOf('</header>'));
const load = () => import('./faq-questions.mjs');

test('faq.html matches faq-questions.mjs (rerun it after editing the questions)', async () => {
  const { render } = await load();
  assert.equal(html, render(html), 'faq.html is stale');
});

test('the FAQ structured data lists the questions shown on the page', async () => {
  const { QUESTIONS } = await load();
  const ld = JSON.parse(html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)[1]);
  const faq = ld['@graph'].find((node) => node['@type'] === 'FAQPage');
  assert.deepEqual(faq.mainEntity.map((q) => [q.name, q.acceptedAnswer.text]), QUESTIONS.map((f) => [f.q, f.a.join(' ')]));
  assert.equal((html.match(/<details>/g) || []).length, QUESTIONS.length);
});

test('the FAQ is canonical at /faq, fits in search results, is built, and is in the sitemap', () => {
  assert.match(html, /<link rel="canonical" href="https:\/\/www.uncloud.life\/faq" \/>/);
  assert.match(html, /<meta property="og:url" content="https:\/\/www.uncloud.life\/faq" \/>/);
  assert.ok(html.match(/<title>(.*?)<\/title>/)[1].length <= 60, 'title is too long');
  assert.ok(html.match(/<meta name="description" content="(.*?)" \/>/)[1].length <= 155, 'description is too long');
  assert.ok(read('public/sitemap.xml').includes('<loc>https://www.uncloud.life/faq</loc>'), 'sitemap lacks /faq');
  assert.ok(fs.readFileSync(`${__dirname}/../src/homebase-web/vite.landing.config.ts`, 'utf8').includes('"faq"'), 'vite.landing.config.ts doesn\'t build faq');
});

test('every page with the full header and footer links to the FAQ in both', async () => {
  const { SLUGS, render } = await import('./acquisition-pages.mjs');
  const pages = { index: read('index.html'), hardware: read('hardware.html'), privacy: read('privacy.html'), terms: read('terms.html') };
  for (const slug of SLUGS) pages[slug] = render(slug);
  for (const [name, page] of Object.entries(pages)) {
    assert.ok(header(page).includes('<a class="faq-link" href="/faq">FAQ</a>'), `${name} header lacks /faq`);
    assert.ok(footer(page).includes('<a class="partners-link" href="/faq">FAQ</a>'), `${name} footer lacks /faq`);
  }
  assert.ok(header(html).includes('<a class="faq-link" href="/faq" aria-current="page">FAQ</a>'), 'the FAQ header doesn\'t mark it current');
  assert.ok(footer(html).includes('<a class="partners-link" href="/faq" aria-current="page">FAQ</a>'), 'the FAQ footer doesn\'t mark it current');
});
