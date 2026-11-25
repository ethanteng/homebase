const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

// Uncloud runs on a Mac. Windows is coming soon; Linux isn't promised. The site must say so and
// claim nothing more.
const read = (name) => fs.readFileSync(`${__dirname}/${name}`, 'utf8');

async function pages() {
  const { SLUGS, render } = await import('./acquisition-pages.mjs');
  const all = {};
  for (const name of ['index', 'faq', 'hardware', 'partners', 'partners-one-pager', 'privacy', 'terms', 'thanks']) all[name] = read(`${name}.html`);
  for (const slug of SLUGS) all[slug] = render(slug);
  return { all, SLUGS };
}

test('no page claims Windows or Linux support', async () => {
  const { all } = await pages();
  for (const [name, html] of Object.entries(all)) {
    assert.ok(!/linux/i.test(html), `${name} mentions Linux`);
    assert.ok(!/Mac, PC/.test(html), `${name} says "Mac, PC"`);
    for (const [, os] of html.matchAll(/"?operatingSystem"?: (\[[^\]]*\])/g))
      assert.deepEqual(JSON.parse(os), ['macOS'], `${name} structured data lists ${os}`);
  }
  assert.ok(!/linux|PC/i.test(read('social-card.svg').match(/<text[^>]*y="550"[^>]*>(.*?)<\/text>/)[1]), 'the social card lists PC or Linux');
});

test('the homepage and comparison pages show Windows as coming soon, and every buy button says Mac', async () => {
  const { all, SLUGS } = await pages();
  for (const name of ['index', ...SLUGS]) assert.ok(all[name].includes('Windows <small>coming soon</small>'), `${name} lacks Windows coming soon`);
  for (const [name, html] of Object.entries(all)) {
    for (const [note] of html.matchAll(/<a [^>]*data-buy[^>]*>[\s\S]*?<p class="access-note">[^<]*<\/p>/g))
      assert.match(note, /Mac app\. Windows is coming soon\./, `${name} has a buy button without the platform note`);
  }
});

test('the FAQ and every comparison page answer the Windows question', async () => {
  const { all, SLUGS } = await pages();
  for (const name of ['faq', ...SLUGS]) assert.ok(all[name].includes('Does Uncloud work on Windows?'), `${name} lacks the Windows question`);
});
