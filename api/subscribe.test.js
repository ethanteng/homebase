"use strict";

const test = require("node:test");
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
  assert.deepEqual(res.body, { ok: true, sandbox: false });
  assert.equal(fetchImpl.calls.length, 1);
  const [call] = fetchImpl.calls;
  assert.equal(call.url, "https://send.api.mailtrap.io/api/send");
  assert.equal(call.options.headers["Api-Token"], "token");
  const sent = JSON.parse(call.options.body);
  // Normalised, and delivered to the configured inbox rather than the visitor.
  assert.match(sent.text, /someone@uncloud\.life/);
  assert.deepEqual(sent.to, [{ email: "ethan@example.com" }]);
});

test("the honeypot is answered warmly and silently", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "bot@uncloud.life", website: "http://spam" } },
    res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 200);
  // Nothing was sent: a bot that gets an error learns how to avoid the trap.
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
  assert.deepEqual(res.body, { ok: true, sandbox: true });
});

test("a body that isn't JSON is refused rather than guessed at", quiet(async () => {
  const res = mockResponse();
  await handler({ method: "POST", body: "this is not json" }, res, CONFIGURED, recorder());
  assert.equal(res.code, 400);
}));
