const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const read = (name) => fs.readFileSync(`${__dirname}/${name}`, 'utf8');

test('the page Stripe returns buyers to is built, kept out of search, and points them at their email', () => {
  const html = read('thanks.html');
  const vite = fs.readFileSync(`${__dirname}/../src/homebase-web/vite.landing.config.ts`, 'utf8');
  const { sessionParameters } = require('../api/checkout.js');
  assert.equal(sessionParameters('price_1', 'https://www.uncloud.life').get('success_url'), 'https://www.uncloud.life/thanks');
  assert.ok(vite.includes('"thanks"'), "vite.landing.config.ts doesn't build thanks");
  assert.match(html, /<meta name="robots" content="noindex" \/>/);
  assert.ok(!read('public/sitemap.xml').includes('/thanks'), 'the sitemap lists /thanks');
  assert.match(html, /Check your email/);
  assert.match(html, /mailto:hello@uncloud\.life/);
  assert.match(html, /href="\/help#set-up"/);
});

test('robots.txt keeps crawlers from starting checkouts', () => {
  assert.match(read('public/robots.txt'), /^Disallow: \/buy$/m);
});
