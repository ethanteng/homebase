const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const html = fs.readFileSync(__dirname + '/hardware.html', 'utf8');
const attr = (tag, name) => (tag.match(new RegExp(`${name}="([^"]*)"`)) || [])[1];
const unescape = (s) => s.replaceAll('&amp;', '&');

test('every drive in the table links to Newegg, tagged with its drive and size', () => {
  const rows = html.slice(html.indexOf('<tbody>'), html.indexOf('</tbody>')).split('<tr ').slice(1);
  assert.ok(rows.length >= 5, 'a row per size');
  let links = 0;
  for (const row of rows) {
    const tb = row.match(/<th scope="row">(\d+) TB/)[1];
    for (const tag of row.match(/<a [^>]*>/g) || []) {
      const url = new URL(unescape(attr(tag, 'href')));
      assert.equal(url.origin, 'https://www.newegg.com', url.href);
      assert.match(url.searchParams.get('utm_content'), new RegExp(`^[a-z0-9-]+-${tb}tb$`), `${tb} TB row links to a ${tb} TB drive`);
      links++;
    }
  }
  assert.ok(links >= rows.length, 'every size has at least one drive');
});

test('each row names its size and the types it has, for the filters', () => {
  const rows = html.slice(html.indexOf('<tbody>'), html.indexOf('</tbody>')).split('<tr ').slice(1);
  for (const row of rows) {
    const tb = row.match(/<th scope="row">(\d+) TB/)[1];
    assert.equal(attr(row, 'data-tb'), tb);
    const linked = ['ssd', 'desktop'].filter((type) => new RegExp(`<td class="col-${type}"><a `).test(row));
    assert.deepEqual(attr(row, 'data-types').split(' '), linked, `${tb} TB row's data-types`);
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
