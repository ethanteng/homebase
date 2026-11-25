"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { GET, handle, PRODUCT, siteOrigin } = require("./checkout.js");

const CONFIGURED = { STRIPE_SECRET_KEY: "sk_test_123", STRIPE_PRICE_ID: "price_123" };

function stripe(result = { ok: true, status: 200, body: { id: "cs_test_1", url: "https://checkout.stripe.com/c/pay/cs_test_1" } }) {
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, options });
    return { ok: result.ok, status: result.status, json: async () => result.body };
  };
  fetchImpl.calls = calls;
  return fetchImpl;
}

const quiet = (fn) => async (...args) => {
  const original = console.error;
  console.error = () => {};
  try { return await fn(...args); } finally { console.error = original; }
};

test("a visit starts a Checkout Session for the configured price and goes to it", async () => {
  const fetchImpl = stripe();
  const res = await handle(new Request("https://www.uncloud.life/buy"), CONFIGURED, fetchImpl);
  assert.equal(res.status, 303);
  assert.equal(res.headers.get("Location"), "https://checkout.stripe.com/c/pay/cs_test_1");
  assert.equal(res.headers.get("Cache-Control"), "no-store");

  const [{ url, options }] = fetchImpl.calls;
  assert.equal(url, "https://api.stripe.com/v1/checkout/sessions");
  assert.equal(options.method, "POST");
  assert.equal(options.headers.Authorization, "Bearer sk_test_123");
  const form = new URLSearchParams(options.body);
  assert.equal(form.get("mode"), "payment");
  assert.equal(form.get("line_items[0][price]"), "price_123");
  assert.equal(form.get("line_items[0][quantity]"), "1");
  assert.equal(form.get("success_url"), "https://www.uncloud.life/thanks");
  assert.equal(form.get("cancel_url"), "https://www.uncloud.life/#pricing");
  assert.equal(form.get("allow_promotion_codes"), "true");
  // The webhook sends download links only for sessions carrying this.
  assert.equal(form.get("metadata[product]"), PRODUCT);
});

test("a preview deployment gets its buyers back to itself", async () => {
  const fetchImpl = stripe();
  await handle(new Request("https://homebase-git-x.vercel.app/api/checkout"), CONFIGURED, fetchImpl);
  const form = new URLSearchParams(fetchImpl.calls[0].options.body);
  assert.equal(form.get("success_url"), "https://homebase-git-x.vercel.app/thanks");
  assert.equal(siteOrigin("not a url"), "https://www.uncloud.life");
});

test("without Stripe settings, nothing is called and the visitor gets a page saying so", quiet(async () => {
  for (const env of [{}, { STRIPE_SECRET_KEY: "sk_test_123" }, { STRIPE_PRICE_ID: "price_123" }]) {
    const fetchImpl = stripe();
    const res = await handle(new Request("https://www.uncloud.life/buy"), env, fetchImpl);
    assert.equal(res.status, 503);
    assert.equal(fetchImpl.calls.length, 0);
    assert.match(res.headers.get("Content-Type"), /text\/html/);
    assert.match(await res.text(), /Nothing was charged[\s\S]*hello@uncloud\.life/);
  }
}));

test("a Stripe error or an odd answer is a page, not a redirect", quiet(async () => {
  for (const result of [
    { ok: false, status: 400, body: { error: { message: "No such price: 'price_123'" } } },
    { ok: true, status: 200, body: { id: "cs_test_1" } },
    { ok: true, status: 200, body: { id: "cs_test_1", url: "http://checkout.stripe.com/c/pay/cs_test_1" } }
  ]) {
    const res = await handle(new Request("https://www.uncloud.life/buy"), CONFIGURED, stripe(result));
    assert.equal(res.status, 502);
    assert.equal(res.headers.get("Location"), null);
  }
}));

test("on Vercel, GET reads process.env, not the event the runtime passes as its second argument", async () => {
  const saved = { fetch: globalThis.fetch, key: process.env.STRIPE_SECRET_KEY, price: process.env.STRIPE_PRICE_ID };
  const fetchImpl = stripe();
  globalThis.fetch = fetchImpl;
  Object.assign(process.env, CONFIGURED);
  try {
    // What @edge-runtime/node-utils does: webHandler(request, toFetchEvent(request)).
    const res = await GET(new Request("https://www.uncloud.life/buy"), { waitUntil() {} });
    assert.equal(res.status, 303);
    assert.equal(new URLSearchParams(fetchImpl.calls[0].options.body).get("line_items[0][price]"), "price_123");
  } finally {
    globalThis.fetch = saved.fetch;
    for (const [name, value] of [["STRIPE_SECRET_KEY", saved.key], ["STRIPE_PRICE_ID", saved.price]]) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
});
