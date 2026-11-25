"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { POST, handle, DOWNLOADS, verifiedEvent, downloadEmail } = require("./stripe-webhook.js");

const SECRET = "whsec_test_secret";
const CONFIGURED = { STRIPE_WEBHOOK_SECRET: SECRET, MAILTRAP_TOKEN: "token" };
const NOW = 1_790_000_000_000;
const now = () => NOW;

function session(overrides = {}) {
  return {
    id: "cs_test_1",
    object: "checkout.session",
    payment_status: "paid",
    metadata: { product: "uncloud-home" },
    customer_details: { email: "buyer@example.com" },
    ...overrides
  };
}

const event = (object, type = "checkout.session.completed") =>
  JSON.stringify({ id: "evt_1", object: "event", type, livemode: false, data: { object } });

function sign(payload, { secret = SECRET, at = NOW / 1000 } = {}) {
  const signature = crypto.createHmac("sha256", secret).update(`${at}.${payload}`).digest("hex");
  return `t=${at},v1=${signature}`;
}

function delivery(payload, signature = sign(payload)) {
  return new Request("https://www.uncloud.life/api/stripe-webhook", {
    method: "POST",
    headers: { "Content-Type": "application/json", "Stripe-Signature": signature },
    body: payload
  });
}

function mailtrap(result = { ok: true, status: 200 }) {
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, options });
    return { ok: result.ok, status: result.status, text: async () => "" };
  };
  fetchImpl.calls = calls;
  return fetchImpl;
}

const quiet = (fn) => async (...args) => {
  const { error, log } = console;
  console.error = () => {};
  console.log = () => {};
  try { return await fn(...args); } finally { Object.assign(console, { error, log }); }
};

test("a paid Uncloud checkout emails the buyer the download links", quiet(async () => {
  const fetchImpl = mailtrap();
  const res = await handle(delivery(event(session())), CONFIGURED, fetchImpl, now);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { received: true, sent: true });

  const [{ url, options }] = fetchImpl.calls;
  assert.equal(url, "https://send.api.mailtrap.io/api/send");
  assert.equal(options.headers["Api-Token"], "token");
  const sent = JSON.parse(options.body);
  assert.deepEqual(sent.to, [{ email: "buyer@example.com" }]);
  assert.deepEqual(sent.from, { email: "hello@uncloud.life", name: "Uncloud" });
  assert.equal(sent.subject, "Your Uncloud download");
  for (const link of [DOWNLOADS.arm64, DOWNLOADS.x64]) {
    assert.ok(sent.text.includes(link), `text lacks ${link}`);
    assert.ok(sent.html.includes(`href="${link}"`), `html lacks ${link}`);
  }
  assert.match(sent.text, /help#set-up/);
}));

test("a payment that settles later is emailed when it succeeds, not when checkout ends", quiet(async () => {
  const pending = mailtrap();
  await handle(delivery(event(session({ payment_status: "unpaid" }))), CONFIGURED, pending, now);
  assert.equal(pending.calls.length, 0);

  const settled = mailtrap();
  const res = await handle(delivery(event(session(), "checkout.session.async_payment_succeeded")), CONFIGURED, settled, now);
  assert.equal(res.status, 200);
  assert.equal(settled.calls.length, 1);
}));

test("a checkout a 100% promotion code paid for is fulfilled too", quiet(async () => {
  const fetchImpl = mailtrap();
  await handle(delivery(event(session({ payment_status: "no_payment_required" }))), CONFIGURED, fetchImpl, now);
  assert.equal(fetchImpl.calls.length, 1);
}));

test("other events and other products are acknowledged without an email", quiet(async () => {
  for (const payload of [
    event(session({ metadata: {} })),
    event(session({ metadata: { product: "something-else" } })),
    event(session(), "checkout.session.expired"),
    event({ id: "pi_1", object: "payment_intent" }, "payment_intent.succeeded")
  ]) {
    const fetchImpl = mailtrap();
    const res = await handle(delivery(payload), CONFIGURED, fetchImpl, now);
    assert.equal(res.status, 200, payload);
    assert.equal(fetchImpl.calls.length, 0, payload);
  }
}));

test("a delivery Stripe didn't sign, or signed too long ago, is refused", quiet(async () => {
  const payload = event(session());
  for (const signature of [
    "",
    "t=1790000000",
    sign(payload, { secret: "whsec_someone_else" }),
    sign(payload, { at: NOW / 1000 - 301 }),
    sign(event(session({ customer_details: { email: "attacker@example.com" } })))
  ]) {
    const fetchImpl = mailtrap();
    const res = await handle(delivery(payload, signature), CONFIGURED, fetchImpl, now);
    assert.equal(res.status, 400, signature);
    assert.equal(fetchImpl.calls.length, 0);
  }
}));

test("any one of several v1 signatures may match, as while a secret is being rolled", () => {
  const payload = event(session());
  const good = sign(payload).split(",")[1];
  const header = `t=${NOW / 1000},v1=${"0".repeat(64)},${good},v0=ignored`;
  assert.equal(verifiedEvent(Buffer.from(payload), header, SECRET, NOW).type, "checkout.session.completed");
});

test("when the email can't be sent, Stripe is told to try again", quiet(async () => {
  const res = await handle(delivery(event(session())), CONFIGURED, mailtrap({ ok: false, status: 503 }), now);
  assert.equal(res.status, 500);
}));

test("unconfigured, every delivery fails so Stripe keeps it for later", quiet(async () => {
  for (const env of [{}, { STRIPE_WEBHOOK_SECRET: SECRET }, { MAILTRAP_TOKEN: "token" }]) {
    const fetchImpl = mailtrap();
    const res = await handle(delivery(event(session())), env, fetchImpl, now);
    assert.equal(res.status, 500);
    assert.equal(fetchImpl.calls.length, 0);
  }
}));

test("a paid session without a usable address is acknowledged, not retried", quiet(async () => {
  const fetchImpl = mailtrap();
  const res = await handle(delivery(event(session({ customer_details: { email: null } }))), CONFIGURED, fetchImpl, now);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { received: true, sent: false });
  assert.equal(fetchImpl.calls.length, 0);
}));

test("download, sender and sandbox settings are honoured, and a bad download address isn't used", quiet(async () => {
  const fetchImpl = mailtrap();
  const env = {
    ...CONFIGURED,
    ORDER_EMAIL_FROM: "orders@uncloud.life",
    MAILTRAP_INBOX_ID: "42",
    MAC_DOWNLOAD_ARM64_URL: "https://downloads.example.com/Uncloud-arm64.dmg",
    MAC_DOWNLOAD_X64_URL: "javascript:alert(1)"
  };
  await handle(delivery(event(session())), env, fetchImpl, now);
  const [{ url, options }] = fetchImpl.calls;
  assert.equal(url, "https://sandbox.api.mailtrap.io/api/send/42");
  const sent = JSON.parse(options.body);
  assert.equal(sent.from.email, "orders@uncloud.life");
  assert.ok(sent.text.includes("https://downloads.example.com/Uncloud-arm64.dmg"));
  assert.ok(sent.text.includes(DOWNLOADS.x64));
  assert.ok(!sent.html.includes("javascript:"));
}));

test("the email names both Macs and how to tell them apart", () => {
  const { text, html } = downloadEmail(DOWNLOADS);
  for (const body of [text, html]) {
    assert.match(body, /Apple silicon/);
    assert.match(body, /Intel/);
    assert.match(body, /About This Mac/);
  }
});

test("on Vercel, POST reads process.env, not the event the runtime passes as its second argument", quiet(async () => {
  const saved = { fetch: globalThis.fetch, secret: process.env.STRIPE_WEBHOOK_SECRET, token: process.env.MAILTRAP_TOKEN };
  const fetchImpl = mailtrap();
  globalThis.fetch = fetchImpl;
  Object.assign(process.env, CONFIGURED);
  try {
    const payload = event(session());
    // A real clock this time, so the signature is made now.
    const res = await POST(delivery(payload, sign(payload, { at: Math.floor(Date.now() / 1000) })), { waitUntil() {} });
    assert.equal(res.status, 200);
    assert.equal(fetchImpl.calls.length, 1);
  } finally {
    globalThis.fetch = saved.fetch;
    for (const [name, value] of [["STRIPE_WEBHOOK_SECRET", saved.secret], ["MAILTRAP_TOKEN", saved.token]]) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
}));
