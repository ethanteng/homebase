"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const handler = require("./partner-interest.js");

const CONFIGURED = { MAILTRAP_TOKEN: "token", SIGNUP_NOTIFY_TO: "ethan@example.com" };
const VALID = {
  name: "Ada Lovelace",
  company: "OWC",
  email: "ada@owc.example",
  tests: ["Bundle", "Customer education"],
  note: "Interested in a holiday bundle."
};

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
    return { ok: result.ok, status: result.status, text: async () => result.text ?? "" };
  };
  fetchImpl.calls = calls;
  return fetchImpl;
}

const quiet = (fn) => async (...args) => {
  const original = console.error;
  console.error = () => {};
  try { return await fn(...args); } finally { console.error = original; }
};

test("only POST is answered", async () => {
  const res = mockResponse();
  await handler({ method: "GET" }, res, CONFIGURED, recorder());
  assert.equal(res.code, 405);
  assert.equal(res.headers.Allow, "POST");
});

test("a name and a usable address are required", async () => {
  for (const [payload, error] of [
    [{ ...VALID, name: "  " }, "Please add your name."],
    [{ ...VALID, email: "nope" }, "Please add a valid work email."],
    [{ ...VALID, email: "ada@owc.example,victim@example.com" }, "Please add a valid work email."],
    [{ ...VALID, email: "ada@owc.example\r\nBcc: victim@example.com" }, "Please add a valid work email."]
  ]) {
    const fetchImpl = recorder();
    const res = mockResponse();
    await handler({ method: "POST", body: payload }, res, CONFIGURED, fetchImpl);
    assert.equal(res.code, 400);
    assert.equal(res.body.error, error);
    assert.equal(fetchImpl.calls.length, 0);
  }
});

test("a submission reaches Mailtrap addressed to the notify inbox, replying to the sender", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: JSON.stringify(VALID) }, res, CONFIGURED, fetchImpl);
  assert.equal(res.code, 200);
  assert.deepEqual(res.body, { ok: true, sandbox: false });
  const [{ url, options }] = fetchImpl.calls;
  assert.equal(url, "https://send.api.mailtrap.io/api/send");
  assert.equal(options.headers["Api-Token"], "token");
  const sent = JSON.parse(options.body);
  assert.deepEqual(sent.to, [{ email: "ethan@example.com" }]);
  assert.deepEqual(sent.headers, { "Reply-To": "ada@owc.example" });
  assert.equal(sent.subject, "Partner interest: Ada Lovelace, OWC");
  assert.match(sent.text, /Wants to test: Bundle, Customer education/);
  assert.match(sent.text, /Interested in a holiday bundle\./);
});

test("PARTNER_NOTIFY_TO takes precedence, and an inbox id routes to the sandbox", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  const env = { ...CONFIGURED, PARTNER_NOTIFY_TO: "partners@example.com", MAILTRAP_INBOX_ID: "42" };
  await handler({ method: "POST", body: VALID }, res, env, fetchImpl);
  const [{ url, options }] = fetchImpl.calls;
  assert.equal(url, "https://sandbox.api.mailtrap.io/api/send/42");
  assert.deepEqual(JSON.parse(options.body).to, [{ email: "partners@example.com" }]);
  assert.equal(res.body.sandbox, true);
});

test("line breaks can't reach the subject, and unknown test choices are dropped", () => {
  const s = handler.readSubmission({ ...VALID, name: "Ada\r\nBcc: x@example.com", tests: ["Bundle", "<script>"] });
  assert.equal(s.name, "Ada Bcc: x@example.com");
  assert.deepEqual(s.tests, ["Bundle"]);
});

test("the honeypot is answered without sending anything", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { ...VALID, website: "spam.example" } }, res, CONFIGURED, fetchImpl);
  assert.equal(res.code, 200);
  assert.equal(fetchImpl.calls.length, 0);
});

test("missing settings and Mailtrap failures don't leak detail to the visitor", quiet(async () => {
  let res = mockResponse();
  await handler({ method: "POST", body: VALID }, res, {}, recorder());
  assert.equal(res.code, 503);
  assert.doesNotMatch(res.body.error, /MAILTRAP|NOTIFY/);

  res = mockResponse();
  await handler({ method: "POST", body: VALID }, res, CONFIGURED, recorder({ ok: false, status: 401, text: "Unauthorized" }));
  assert.equal(res.code, 502);
  assert.doesNotMatch(res.body.error, /401|Unauthorized/);
}));

test("an unreadable body is refused", async () => {
  const res = mockResponse();
  await handler({ method: "POST", body: "{not json" }, res, CONFIGURED, recorder());
  assert.equal(res.code, 400);
});
