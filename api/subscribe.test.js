"use strict";

const test = require("node:test");
const { beforeEach } = require("node:test");
const assert = require("node:assert/strict");
const handler = require("./subscribe.js");

const CONFIGURED = { MAILTRAP_TOKEN: "token", SIGNUP_NOTIFY_TO: "ethan@example.com" };

function mockResponse() {
  return {
    headers: {},
    code: 0,
    body: undefined,
    setHeader(name, value) { this.headers[name] = value; },
    status(code) { this.code = code; return this; },
    json(payload) { this.body = payload; return this; }
  };
}

function recorder(result = { ok: true, status: 200 }) {
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, options });
    return {
      ok: result.ok,
      status: result.status,
      text: async () => result.text ?? ""
    };
  };
  fetchImpl.calls = calls;
  return fetchImpl;
}

// The rate limiter lives in module scope, so every test starts from an empty one.
beforeEach(() => handler.resetLimits());

const quiet = (fn) => async (...args) => {
  const original = console.error;
  console.error = () => {};
  try { return await fn(...args); } finally { console.error = original; }
};

test("an address has to look like one", () => {
  assert.equal(handler.validateEmail("someone@uncloud.life"), null);
  assert.equal(handler.validateEmail("a.b+tag@sub.example.co.uk"), null);
  for (const bad of ["", "someone", "someone@", "@uncloud.life", "someone@uncloud",
                     "two people@uncloud.life", `${"a".repeat(250)}@uncloud.life`]) {
    assert.notEqual(handler.validateEmail(bad), null, `should reject ${JSON.stringify(bad)}`);
  }
});

test("a newline in an address is refused, not passed along", () => {
  // Where header injection starts, even though the API takes JSON.
  assert.notEqual(handler.validateEmail("someone@uncloud.life\nBcc: victim@example.com"), null);
  assert.notEqual(handler.validateEmail("someone@uncloud.life\r\nSubject: spam"), null);
  // A comma would be a second recipient.
  assert.notEqual(handler.validateEmail("someone@uncloud.life,victim@example.com"), null);
});

test("only POST is answered", quiet(async () => {
  const res = mockResponse();
  await handler({ method: "GET" }, res, CONFIGURED, recorder());
  assert.equal(res.code, 405);
  assert.equal(res.headers.Allow, "POST");
}));

test("a signup reaches Mailtrap with the address and nothing else", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "  Someone@Uncloud.Life " } }, res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 200);
  assert.deepEqual(res.body, { ok: true, accepted: true, sandbox: false });
  assert.equal(fetchImpl.calls.length, 1);
  const [call] = fetchImpl.calls;
  assert.equal(call.url, "https://send.api.mailtrap.io/api/send");
  assert.equal(call.options.headers["Api-Token"], "token");
  const sent = JSON.parse(call.options.body);
  // Trimmed, domain folded, and delivered to the configured inbox rather than
  // the visitor.
  assert.match(sent.text, /Someone@uncloud\.life/);
  assert.deepEqual(sent.to, [{ email: "ethan@example.com" }]);
});

test("the honeypot is answered warmly and silently", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "bot@uncloud.life", website: "http://spam" } },
    res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 200);
  // Nothing was sent: a bot that gets an error learns how to avoid the trap.
  assert.equal(res.body.accepted, false);
  assert.equal(fetchImpl.calls.length, 0);
});

test("a bad address never reaches Mailtrap", quiet(async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "not-an-address" } }, res, CONFIGURED, fetchImpl);
  assert.equal(res.code, 400);
  assert.equal(fetchImpl.calls.length, 0);
}));

test("missing settings are a server problem, not the visitor's fault", quiet(async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res, {}, fetchImpl);

  assert.equal(res.code, 503);
  assert.equal(fetchImpl.calls.length, 0);
  // The visitor is told to come back, not which variable is unset.
  assert.doesNotMatch(JSON.stringify(res.body), /MAILTRAP|SIGNUP_NOTIFY/);
}));

test("a Mailtrap failure says nothing about Mailtrap", quiet(async () => {
  const fetchImpl = recorder({ ok: false, status: 401, text: "unauthorized: bad api token" });
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 502);
  assert.doesNotMatch(JSON.stringify(res.body), /token|unauthorized|401/i);
}));

test("an inbox id routes to the sandbox instead of delivering", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res,
    { ...CONFIGURED, MAILTRAP_INBOX_ID: "12345" }, fetchImpl);

  assert.equal(fetchImpl.calls[0].url, "https://sandbox.api.mailtrap.io/api/send/12345");
  assert.deepEqual(res.body, { ok: true, accepted: true, sandbox: true });
});

test("a body that isn't JSON is refused rather than guessed at", quiet(async () => {
  const res = mockResponse();
  await handler({ method: "POST", body: "this is not json" }, res, CONFIGURED, recorder());
  assert.equal(res.code, 400);
}));

test("a domain has to be made of real labels", () => {
  // A character class that allows dots and hyphens anywhere lets these through,
  // and none of them can receive mail.
  for (const bad of ["someone@uncloud..life", "someone@-uncloud.life",
                     "someone@uncloud-.life", "someone@.uncloud.life",
                     "someone@uncloud.life.", "someone@unc_loud.life"]) {
    assert.notEqual(handler.validateEmail(bad), null, `should reject ${JSON.stringify(bad)}`);
  }
  assert.equal(handler.validateEmail("someone@a-b.uncloud.life"), null);
});

test("the local part keeps its case and the domain loses its own", () => {
  // SMTP lets the receiving system treat the local part as case sensitive, so
  // folding it can turn a working address into one that bounces.
  assert.equal(handler.normalizeEmail("Ethan.Teng@Example.COM"), "Ethan.Teng@example.com");
  assert.equal(handler.normalizeEmail("UPPER@EXAMPLE.COM"), "UPPER@example.com");
});

test("one caller cannot spend the whole sending quota", quiet(async () => {
  const fetchImpl = recorder();
  const from = { "x-forwarded-for": "203.0.113.7, 10.0.0.1" };
  let last;
  for (let i = 0; i < 5; i += 1) {
    last = mockResponse();
    await handler({ method: "POST", headers: from, body: { email: `person${i}@uncloud.life` } },
      last, CONFIGURED, fetchImpl);
  }

  // Three go through; the rest are refused before Mailtrap is asked for anything.
  assert.equal(fetchImpl.calls.length, 3);
  assert.equal(last.code, 429);
  assert.equal(last.headers["Retry-After"], "60");

  // A different caller is unaffected by the first one's spending.
  const other = mockResponse();
  await handler({ method: "POST", headers: { "x-forwarded-for": "198.51.100.4" },
    body: { email: "someone@uncloud.life" } }, other, CONFIGURED, fetchImpl);
  assert.equal(other.code, 200);
  assert.equal(fetchImpl.calls.length, 4);
}));

test("the same address twice is answered, not sent twice", async () => {
  const fetchImpl = recorder();
  const request = { method: "POST", headers: { "x-forwarded-for": "203.0.113.9" },
    body: { email: "someone@uncloud.life" } };

  const first = mockResponse();
  await handler(request, first, CONFIGURED, fetchImpl);
  const second = mockResponse();
  await handler(request, second, CONFIGURED, fetchImpl);

  // The visitor is already on the list; a second identical email only spends quota.
  assert.equal(second.code, 200);
  assert.equal(first.body.accepted, true);
  assert.equal(second.body.accepted, false);
  assert.equal(fetchImpl.calls.length, 1);
});

test("a failed send leaves the address free to try again", quiet(async () => {
  const failing = recorder({ ok: false, status: 500, text: "upstream is down" });
  const request = { method: "POST", headers: { "x-forwarded-for": "203.0.113.11" },
    body: { email: "someone@uncloud.life" } };

  const first = mockResponse();
  await handler(request, first, CONFIGURED, failing);
  assert.equal(first.code, 502);

  // Suppressing the retry would tell them they are on a list they never reached.
  const working = recorder();
  const second = mockResponse();
  await handler(request, second, CONFIGURED, working);
  assert.equal(second.code, 200);
  assert.equal(working.calls.length, 1);
}));
