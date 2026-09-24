"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const handler = require("./dropbox-callback.js");

function mockResponse() {
  return {
    headers: {},
    code: 0,
    body: undefined,
    setHeader(name, value) { this.headers[name] = value; return this; },
    status(code) { this.code = code; return this; },
    send(payload) { this.body = payload; return this; }
  };
}

/**
 * A request as the runtime actually delivers one: a method and a raw URL. Built by hand rather
 * than from an object of parameters, so that what these tests exercise is the parsing too.
 */
async function get(query, method = "GET") {
  const search = new URLSearchParams();
  for (const [name, value] of Object.entries(query)) {
    if (value === undefined) continue;
    for (const one of Array.isArray(value) ? value : [value]) search.append(name, one);
  }
  const url = `/api/dropbox-callback${search.toString() ? `?${search}` : ""}`;
  const response = mockResponse();
  await handler({ method, url }, response);
  return response;
}

/** Where a response is sending the browser, as a parsed URL, or null when it isn't. */
function sentTo(response) {
  const location = response.headers.Location;
  return typeof location === "string" ? new URL(location) : null;
}

const STATE = `${"a".repeat(86)}.h5210`;

test("a sign-in goes back to the Uncloud that started it", async () => {
  const response = await get({ code: "the-code", state: STATE });
  assert.equal(response.code, 302);
  const sent = sentTo(response);
  assert.equal(sent.origin, "http://127.0.0.1:5210");
  assert.equal(sent.pathname, "/api/providers/dropbox/callback");
  assert.equal(sent.searchParams.get("code"), "the-code");
  // Passed on whole, suffix included: the state is checked by the Uncloud that made it.
  assert.equal(sent.searchParams.get("state"), STATE);
});

test("a host that answers on https is sent back on https", async () => {
  const sent = sentTo(await get({ code: "c", state: `${"a".repeat(86)}.s8443` }));
  assert.equal(sent.origin, "https://127.0.0.1:8443");
});

test("pressing cancel comes back as a refusal rather than a failure", async () => {
  const sent = sentTo(await get({ error: "access_denied", state: STATE }));
  assert.equal(sent.searchParams.get("error"), "access_denied");
  assert.equal(sent.searchParams.get("code"), null);
});

test("arriving with neither a code nor a refusal is still a refusal", async () => {
  // Never a bare redirect with nothing on it: the waiting Uncloud would have no way to
  // tell a finished sign-in from an abandoned one, and would sit there saying nothing.
  const sent = sentTo(await get({ state: STATE }));
  assert.equal(sent.searchParams.get("error"), "invalid_request");
});

// The whole of the security argument for relaying somebody's sign-in: the code may pass
// through here, but the only place it can be sent is the person's own computer. A
// destination an attacker could choose would hand them a code they hold the verifier
// for — Uncloud's own consent screen included — so each of these must refuse.
test("a sign-in is never sent anywhere but the person's own computer", async () => {
  const elsewhere = [
    // A whole address where a port belongs.
    `${"a".repeat(86)}.hevil.example`,
    `${"a".repeat(86)}.h5210@evil.example`,
    `${"a".repeat(86)}.h//evil.example`,
    // A second suffix, in case only the first were read.
    `${"a".repeat(86)}.h5210.hevil.example`,
    // A scheme that isn't one of the two.
    `${"a".repeat(86)}.jjavascript:alert(1)`,
    `${"a".repeat(86)}.f5210`,
    // Ports that aren't.
    `${"a".repeat(86)}.h0`,
    `${"a".repeat(86)}.h-1`,
    `${"a".repeat(86)}.h99999`,
    `${"a".repeat(86)}.h 5210`,
    // No suffix at all, and nothing to guess from.
    "a".repeat(86),
    "",
    undefined
  ];
  for (const state of elsewhere) {
    const response = await get({ code: "the-code", state });
    assert.equal(response.code, 400, `state ${JSON.stringify(state)} was not refused`);
    assert.equal(response.headers.Location, undefined,
      `state ${JSON.stringify(state)} was forwarded somewhere`);
    // Whatever it says, it must not hand the code to the page it says it in.
    assert.ok(!String(response.body).includes("the-code"));
  }
});

test("a repeated state cannot smuggle a second destination past the first", async () => {
  // `?state=<mine>&state=<theirs>`. Taking either end of that would let whoever added the second
  // copy decide where the code goes, which is the one thing this function must never allow.
  const response = await get({ code: "c", state: [STATE, `${"a".repeat(86)}.h9999`] });
  assert.equal(response.code, 400);
  assert.equal(response.headers.Location, undefined);
});

test("a repeated code cannot smuggle a second code past the first", async () => {
  const sent = sentTo(await get({ code: ["first", "second"], state: STATE }));
  // Neither of them: an ambiguous code is no code, and the sign-in fails cleanly.
  assert.equal(sent.searchParams.get("code"), null);
  assert.equal(sent.searchParams.get("error"), "invalid_request");
});

test("an over-long state is refused rather than matched against", async () => {
  const response = await get({ code: "c", state: `${"a".repeat(4096)}.h5210` });
  assert.equal(response.code, 400);
});

test("the code is kept out of caches and out of the next page's Referer", async () => {
  const response = await get({ code: "the-code", state: STATE });
  assert.equal(response.headers["Cache-Control"], "no-store");
  assert.equal(response.headers["Referrer-Policy"], "no-referrer");
});

test("only a navigation is answered", async () => {
  for (const method of ["POST", "PUT", "DELETE", "OPTIONS"]) {
    const response = await get({ code: "c", state: STATE }, method);
    assert.equal(response.code, 405);
    assert.equal(response.headers.Location, undefined);
  }
});

test("a request with no query at all is answered rather than thrown on", async () => {
  const response = mockResponse();
  await handler({ method: "GET", url: "/api/dropbox-callback" }, response);
  assert.equal(response.code, 400);
  // And the same when the runtime gives no url to speak of.
  const bare = mockResponse();
  await handler({ method: "GET" }, bare);
  assert.equal(bare.code, 400);
});

test("someone who opens the address by hand is told what it is for", async () => {
  const response = await get({});
  assert.equal(response.code, 400);
  assert.match(String(response.body), /connecting Dropbox/);
  // Nothing loaded from anywhere: no request this page could carry a code out on.
  assert.ok(!/<(script|img|link|iframe)\b/i.test(String(response.body)));
});
