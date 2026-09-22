const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

function page(response, email = 'qa@example.com', visit = {}) {
  let onSubmit;
  const sent = [];
  const field = { value: email, focus() {} };
  const button = {};
  const status = { dataset: {} };
  const form = {
    hidden: false,
    querySelector: (selector) => selector === '#access-email' ? field : selector === 'button[type=submit]' ? button : { value: '' },
    addEventListener: (_, fn) => { onSubmit = fn; },
  };
  const window = { dataLayer: [], matchMedia: () => ({}), location: { search: visit.search || '' } };
  vm.runInNewContext(fs.readFileSync(__dirname + '/main.js', 'utf8'), {
    document: {
      querySelectorAll: () => [],
      querySelector: selector => selector === '#access-form' ? form : status,
      referrer: visit.referrer || '',
    },
    window,
    URLSearchParams,
    fetch: async (_, options) => { sent.push(JSON.parse(options.body)); return { ok: response.ok !== false, json: async () => response }; },
  });
  return { submit: () => onSubmit({ preventDefault() {} }), events: window.dataLayer, field, form, status, sent, requests: () => sent.length };
}

test('empty input records one intent, never a lead or request', async () => {
  const p = page({}, '');
  await p.submit(); await p.submit();
  assert.deepEqual(p.events.map(e => e.event), ['cta_click']);
  assert.equal(p.requests(), 0);
});

test('a kept signup records exactly one lead and never the email', async () => {
  const p = page({ ok: true, accepted: true });
  await Promise.all([p.submit(), p.submit()]);
  await p.submit();
  assert.deepEqual(p.events.map(e => e.event), ['cta_click', 'generate_lead']);
  assert.equal(p.requests(), 1);
  assert.equal(p.form.hidden, true);
  assert.doesNotMatch(JSON.stringify(p.events), /qa@|email|example/);
});

test('the campaign and referrer ride along with the signup, and never reach the data layer', async () => {
  const p = page({ ok: true, accepted: true }, 'qa@example.com',
    { search: '?utm_source=newsletter&utm_medium=email', referrer: 'https://news.ycombinator.com/' });
  await p.submit();

  assert.equal(p.sent[0].source, 'newsletter');
  assert.equal(p.sent[0].referrer, 'https://news.ycombinator.com/');
  assert.doesNotMatch(JSON.stringify(p.events), /newsletter|ycombinator/);
});

test('a visit with no campaign and no referrer still signs up', async () => {
  const p = page({ ok: true, accepted: true });
  await p.submit();

  assert.deepEqual(p.sent[0], { email: 'qa@example.com', website: '', source: '', referrer: '' });
  assert.equal(p.form.hidden, true);
});

for (const [name, response] of Object.entries({
  failure: { ok: false, error: 'Try again' },
  duplicate: { ok: true, accepted: false },
  honeypot: { ok: true, accepted: false },
  legacyResponse: { ok: true },
})) {
  test(name + ' never records a conversion', async () => {
    const p = page(response);
    await p.submit();
    assert.deepEqual(p.events.map(e => e.event), ['cta_click']);
    if (response.ok === false) assert.equal(p.form.hidden, false);
  });
}
