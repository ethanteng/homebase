const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const read = (name) => fs.readFileSync(`${__dirname}/${name}`, 'utf8');
const footer = (html) => html.slice(html.indexOf('<footer class="site-footer">'), html.indexOf('</footer>'));
const LEGAL = ['privacy', 'terms'];

test('every public page links to the privacy policy and terms in its footer', async () => {
  const { SLUGS, render } = await import('./acquisition-pages.mjs');
  const pages = { index: read('index.html'), hardware: read('hardware.html'), partners: read('partners.html') };
  for (const slug of [...SLUGS, ...LEGAL]) pages[slug] = SLUGS.includes(slug) ? render(slug) : read(`${slug}.html`);
  for (const [name, html] of Object.entries(pages)) {
    for (const slug of LEGAL) assert.match(footer(html), new RegExp(`<a href="/${slug}"`), `${name} footer lacks /${slug}`);
    assert.ok(footer(html).includes('© 2026 Ethan Teng Consulting LLC'), `${name} footer lacks the copyright line`);
  }
});

test('the legal pages are canonical at their clean URLs, built, and in the sitemap', () => {
  const sitemap = read('public/sitemap.xml');
  const vite = fs.readFileSync(`${__dirname}/../src/homebase-web/vite.landing.config.ts`, 'utf8');
  for (const slug of LEGAL) {
    const html = read(`${slug}.html`);
    assert.match(html, new RegExp(`<link rel="canonical" href="https://www.uncloud.life/${slug}" />`));
    assert.match(html, new RegExp(`<meta property="og:url" content="https://www.uncloud.life/${slug}" />`));
    assert.ok(html.match(/<title>(.*?)<\/title>/)[1].length <= 60, `${slug} title is too long`);
    assert.ok(html.match(/<meta name="description" content="(.*?)" \/>/)[1].length <= 155, `${slug} description is too long`);
    assert.match(footer(html), new RegExp(`<a href="/${slug}" aria-current="page">`), `${slug} doesn't mark itself current`);
    assert.ok(html.includes('Ethan Teng Consulting LLC') && html.includes('mailto:hello@uncloud.life'), `${slug} lacks the contact`);
    assert.ok(sitemap.includes(`<loc>https://www.uncloud.life/${slug}</loc>`), `sitemap lacks ${slug}`);
    assert.ok(vite.includes(`"${slug}"`), `vite.landing.config.ts doesn't build ${slug}`);
  }
});
