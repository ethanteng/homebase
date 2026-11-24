const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const read = (name) => fs.readFileSync(`${__dirname}/${name}`, 'utf8');
const footer = (html) => html.slice(html.indexOf('<footer class="site-footer">'), html.indexOf('</footer>'));

test('the help page is canonical at /help, built, and in the sitemap', () => {
  const html = read('help.html');
  const vite = fs.readFileSync(`${__dirname}/../src/homebase-web/vite.landing.config.ts`, 'utf8');
  assert.match(html, /<link rel="canonical" href="https:\/\/www.uncloud.life\/help" \/>/);
  assert.match(html, /<meta property="og:url" content="https:\/\/www.uncloud.life\/help" \/>/);
  assert.ok(html.match(/<title>(.*?)<\/title>/)[1].length <= 60, 'help title is too long');
  assert.ok(html.match(/<meta name="description" content="(.*?)" \/>/)[1].length <= 155, 'help description is too long');
  assert.match(footer(html), /<a class="partners-link" href="\/help" aria-current="page">Help<\/a>/);
  assert.ok(read('public/sitemap.xml').includes('<loc>https://www.uncloud.life/help</loc>'), 'sitemap lacks /help');
  assert.ok(vite.includes('"help"'), "vite.landing.config.ts doesn't build help");
});

test('every page with the full footer links to the help page', async () => {
  const { SLUGS, render } = await import('./acquisition-pages.mjs');
  const pages = {};
  for (const name of ['index', 'faq', 'hardware', 'privacy', 'terms']) pages[name] = read(`${name}.html`);
  for (const slug of SLUGS) pages[slug] = render(slug);
  for (const [name, html] of Object.entries(pages))
    assert.match(footer(html), /<a class="partners-link" href="\/help">Help<\/a>/, `${name} footer lacks /help`);
});

test('every screenshot the help page shows exists, and every section in its contents is there', () => {
  const html = read('help.html');
  const images = [...html.matchAll(/<img src="\.\/(help\/[^"]+)"/g)].map((match) => match[1]);
  assert.ok(images.length > 0, 'help page shows no screenshots');
  for (const image of images) assert.ok(fs.existsSync(`${__dirname}/${image}`), `${image} is missing`);
  for (const [, id] of html.matchAll(/<a href="#([^"]+)">/g))
    assert.ok(html.includes(`id="${id}"`), `#${id} is linked but nothing has that id`);
});
