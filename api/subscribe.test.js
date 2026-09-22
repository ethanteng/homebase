"use strict";

const test = require("node:test");
const { beforeEach } = require("node:test");
const assert = require("node:assert/strict");
const handler = require("./subscribe.js");

const STORE_ONLY = { AIRTABLE_TOKEN: "pat-token", AIRTABLE_BASE_ID: "appBase" };
const CONFIGURED = { ...STORE_ONLY, MAILTRAP_TOKEN: "token", SIGNUP_NOTIFY_TO: "ethan@example.com" };

const AIRTABLE_URL = "https://api.airtable.com/v0/appBase/Signups";

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

/**
 * A stand-in for fetch that records every call. `outcomes` can answer the two
 * services differently, which is the only way to tell "the record failed" from
 * "the notification failed" — and those two now have opposite consequences.
 */
function recorder(outcomes = {}) {
  const ok = { ok: true, status: 200 };
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, options });
    const which = url.includes("airtable.com") ? "airtable" : "mailtrap";
    const result = outcomes[which] || outcomes.both || ok;
    return { ok: result.ok, status: result.status, text: async () => result.text ?? "" };
  };
  fetchImpl.calls = calls;
  fetchImpl.to = (host) => calls.filter((call) => call.url.includes(host));
  fetchImpl.stored = () => {
    const call = fetchImpl.to("airtable.com")[0];
    return call ? JSON.parse(call.options.body).records[0].fields : null;
  };
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

test("a signup becomes a row before it becomes an email", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "  Someone@Uncloud.Life " } }, res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 200);
  assert.deepEqual(res.body, { ok: true, accepted: true });

  // The record first: everything after it is a courtesy.
  const [record, mail] = fetchImpl.calls;
  assert.equal(record.url, AIRTABLE_URL);
  assert.equal(record.options.method, "PATCH");
  assert.equal(record.options.headers.Authorization, "Bearer pat-token");
  // Trimmed and domain folded, the same address the notification carries.
  assert.equal(fetchImpl.stored().Email, "Someone@uncloud.life");
  assert.match(fetchImpl.stored()["Signed Up"], /^\d{4}-\d\d-\d\dT/);

  assert.equal(mail.url, "https://send.api.mailtrap.io/api/send");
  const sent = JSON.parse(mail.options.body);
  assert.match(sent.text, /Someone@uncloud\.life/);
  assert.deepEqual(sent.to, [{ email: "ethan@example.com" }]);
});

test("the row is upserted on the address, so a returning visitor has one row", async () => {
  const fetchImpl = recorder();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } },
    mockResponse(), CONFIGURED, fetchImpl);

  const body = JSON.parse(fetchImpl.to("airtable.com")[0].options.body);
  assert.deepEqual(body.performUpsert, { fieldsToMergeOn: ["Email"] });
});

test("where a visitor came from is recorded with them", async () => {
  const fetchImpl = recorder();
  await handler({ method: "POST", body: {
    email: "someone@uncloud.life",
    source: "newsletter",
    referrer: "https://news.ycombinator.com/"
  } }, mockResponse(), CONFIGURED, fetchImpl);

  assert.equal(fetchImpl.stored().Source, "newsletter");
  assert.equal(fetchImpl.stored().Referrer, "https://news.ycombinator.com/");
});

test("an empty origin is left out rather than written over the first one", async () => {
  const fetchImpl = recorder();
  await handler({ method: "POST", body: { email: "someone@uncloud.life", source: "", referrer: "  " } },
    mockResponse(), CONFIGURED, fetchImpl);

  // The upsert writes every field it is given, so sending blanks on a second
  // visit would erase what the first one learned.
  assert.deepEqual(Object.keys(fetchImpl.stored()).sort(), ["Email", "Signed Up"]);
});

test("origin strings are trimmed of control characters and capped", () => {
  assert.equal(handler.contextValue("news\r\nletter"), "newsletter");
  assert.equal(handler.contextValue("  spaced  "), "spaced");
  assert.equal(handler.contextValue("x".repeat(500)).length, 200);
  assert.equal(handler.contextValue(undefined), "");
});

test("the honeypot is answered warmly and silently", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "bot@uncloud.life", website: "http://spam" } },
    res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 200);
  // Nothing was stored or sent: a bot that gets an error learns to avoid the trap.
  assert.equal(res.body.accepted, false);
  assert.equal(fetchImpl.calls.length, 0);
});

test("a bad address never reaches either service", quiet(async () => {
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
  assert.doesNotMatch(JSON.stringify(res.body), /AIRTABLE|MAILTRAP|SIGNUP_NOTIFY/);
}));

test("only the store is required; mail settings are optional", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res, STORE_ONLY, fetchImpl);

  // The signup is kept even where nobody is being told about it.
  assert.equal(res.code, 200);
  assert.equal(res.body.accepted, true);
  assert.equal(fetchImpl.to("airtable.com").length, 1);
  assert.equal(fetchImpl.to("mailtrap.io").length, 0);
});

test("a table name can be set per environment so previews stay out of the list", () => {
  const config = handler.readConfig({ ...STORE_ONLY, AIRTABLE_TABLE: "Preview Signups" });
  assert.equal(config.url, undefined);
  assert.equal(config.store.url, "https://api.airtable.com/v0/appBase/Preview%20Signups");
  assert.deepEqual(config.missing, []);
});

test("a store failure is the visitor's problem, and says nothing about the store", quiet(async () => {
  const fetchImpl = recorder({ airtable: { ok: false, status: 401, text: "invalid personal access token" } });
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res, CONFIGURED, fetchImpl);

  assert.equal(res.code, 502);
  assert.doesNotMatch(JSON.stringify(res.body), /token|airtable|401/i);
  // Telling them they are on a list nobody kept would be the worse failure, so
  // no notification goes out either.
  assert.equal(fetchImpl.to("mailtrap.io").length, 0);
}));

test("a notification failure does not lose a signup that was already kept", quiet(async () => {
  const fetchImpl = recorder({ mailtrap: { ok: false, status: 500, text: "mail is down" } });
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res, CONFIGURED, fetchImpl);

  // The row exists, so the visitor is on the list and is told so; the failure
  // belongs in the log, where it can be noticed without costing them anything.
  assert.equal(res.code, 200);
  assert.deepEqual(res.body, { ok: true, accepted: true });
  assert.equal(fetchImpl.to("airtable.com").length, 1);
}));

test("an inbox id routes the notification to the sandbox, and the row is real either way", async () => {
  const fetchImpl = recorder();
  const res = mockResponse();
  await handler({ method: "POST", body: { email: "someone@uncloud.life" } }, res,
    { ...CONFIGURED, MAILTRAP_INBOX_ID: "12345" }, fetchImpl);

  assert.equal(fetchImpl.to("mailtrap.io")[0].url, "https://sandbox.api.mailtrap.io/api/send/12345");
  assert.equal(fetchImpl.to("airtable.com").length, 1);
  assert.deepEqual(res.body, { ok: true, accepted: true });
});

test("a body that isn't JSON is refused rather than guessed at", quiet(async () => {
  const res = mockResponse();
  await handler({ method: "POST", body: "this is not json" }, res, CONFIGURED, recorder());
  assert.equal(res.code, 400);
}));

test("valid JSON that isn't an object fails validation, not the function", quiet(async () => {
  for (const body of ["null", "7", "[]", '"someone@uncloud.life"']) {
    const fetchImpl = recorder();
    const res = mockResponse();
    await handler({ method: "POST", body }, res, CONFIGURED, fetchImpl);
    assert.equal(res.code, 400, `should reject ${body}`);
    assert.equal(fetchImpl.calls.length, 0);
  }
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

test("one caller cannot spend the whole month's quota", quiet(async () => {
  const fetchImpl = recorder();
  const from = { "x-forwarded-for": "203.0.113.7, 10.0.0.1" };
  let last;
  for (let i = 0; i < 5; i += 1) {
    last = mockResponse();
    await handler({ method: "POST", headers: from, body: { email: `person${i}@uncloud.life` } },
      last, CONFIGURED, fetchImpl);
  }

  // Three go through; the rest are refused before anything is written.
  assert.equal(fetchImpl.to("airtable.com").length, 3);
  assert.equal(last.code, 429);
  assert.equal(last.headers["Retry-After"], "60");

  // A different caller is unaffected by the first one's spending.
  const other = mockResponse();
  await handler({ method: "POST", headers: { "x-forwarded-for": "198.51.100.4" },
    body: { email: "someone@uncloud.life" } }, other, CONFIGURED, fetchImpl);
  assert.equal(other.code, 200);
  assert.equal(fetchImpl.to("airtable.com").length, 4);
}));

test("the same address twice is answered, not written twice", async () => {
  const fetchImpl = recorder();
  const request = { method: "POST", headers: { "x-forwarded-for": "203.0.113.9" },
    body: { email: "someone@uncloud.life" } };

  const first = mockResponse();
  await handler(request, first, CONFIGURED, fetchImpl);
  const second = mockResponse();
  await handler(request, second, CONFIGURED, fetchImpl);

  // The upsert would collapse them anyway; refusing here spends no quota at all.
  assert.equal(second.code, 200);
  assert.equal(first.body.accepted, true);
  assert.equal(second.body.accepted, false);
  assert.equal(fetchImpl.to("airtable.com").length, 1);
});

test("a failed store leaves the address free to try again", quiet(async () => {
  const failing = recorder({ airtable: { ok: false, status: 500, text: "upstream is down" } });
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
  assert.equal(working.to("airtable.com").length, 1);
}));
