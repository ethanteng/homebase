const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

function page(response, email = 'qa@example.com') {
  let onSubmit;
  let requests = 0;
  const field = { value: email, focus() {} };
  const button = {};
  const status = { dataset: {} };
  const form = {
    hidden: false,
    querySelector: (selector) => selector === '#access-email' ? field : selector === 'button[type=submit]' ? button : { value: '' },
    addEventListener: (_, fn) => { onSubmit = fn; },
  };
  const window = { dataLayer: [], matchMedia: () => ({}) };
  vm.runInNewContext(fs.readFileSync(__dirname + '/main.js', 'utf8'), {
    document: { querySelectorAll: () => [], querySelector: selector => selector === '#access-form' ? form : status },
    window,
    fetch: async () => { requests++; return { ok: response.ok !== false, json: async () => response }; },
  });
  return { submit: () => onSubmit({ preventDefault() {} }), events: window.dataLayer, field, form, status, requests: () => requests };
}

test('empty input records one intent, never a lead or request', async () => {
  const p = page({}, '');
  await p.submit(); await p.submit();
  assert.deepEqual(p.events.map(e => e.event), ['cta_click']);
  assert.equal(p.requests(), 0);
});

test('confirmed delivery records exactly one lead and never the email', async () => {
  const p = page({ ok: true, accepted: true, sandbox: false });
  await Promise.all([p.submit(), p.submit()]);
  await p.submit();
  assert.deepEqual(p.events.map(e => e.event), ['cta_click', 'generate_lead']);
  assert.equal(p.requests(), 1);
  assert.equal(p.form.hidden, true);
  assert.doesNotMatch(JSON.stringify(p.events), /qa@|email|example/);
});

for (const [name, response] of Object.entries({
  failure: { ok: false, error: 'Try again' },
  duplicate: { ok: true, accepted: false },
  honeypot: { ok: true, accepted: false },
  sandbox: { ok: true, accepted: true, sandbox: true },
  legacyResponse: { ok: true },
})) {
  test(name + ' never records a conversion', async () => {
    const p = page(response);
    await p.submit();
    assert.deepEqual(p.events.map(e => e.event), ['cta_click']);
    if (response.ok === false) assert.equal(p.form.hidden, false);
  });
}
