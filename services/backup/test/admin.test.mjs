// The operator's invites page (public/admin): it's static, so what keeps the operator token safe is
// how it's served and what it may load. These read the page and vercel.json as they ship.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const read = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");
const config = JSON.parse(await read("vercel.json"));

/** The headers Vercel gives a path, by the rules in vercel.json. */
function headersFor(path) {
  const headers = {};
  for (const rule of config.headers ?? [])
    if (new RegExp(`^${rule.source}$`).test(path))
      for (const { key, value } of rule.headers) headers[key.toLowerCase()] = value;
  return headers;
}

test("the invites page and its files can't be framed, cached or made to load anything from elsewhere", () => {
  for (const path of ["/admin", "/admin/", "/admin/index.html", "/admin/admin.js", "/admin/admin.css"]) {
    const headers = headersFor(path);
    const policy = headers["content-security-policy"] ?? "";
    for (const directive of ["default-src 'none'", "script-src 'self'", "connect-src 'self'", "frame-ancestors 'none'", "form-action 'none'", "base-uri 'none'"])
      assert.ok(policy.includes(directive), `${path} has ${directive}`);
    assert.equal(headers["x-frame-options"], "DENY", path);
    assert.equal(headers["referrer-policy"], "no-referrer", path);
    assert.equal(headers["cache-control"], "no-store", path);
  }
});

test("the page loads only its own files, and keeps the token out of the address and out of storage", async () => {
  const page = await read("public/admin/index.html");
  const sources = [...page.matchAll(/\b(?:src|href)="([^"]+)"/g)].map((match) => match[1]);
  assert.deepEqual(sources.sort(), ["/admin/admin.css", "/admin/admin.js"]);
  // Were its script ever not to run, a form sent the ordinary way would put what's in it in the
  // address with GET; with POST, and form-action 'none', it isn't sent at all.
  for (const form of page.match(/<form\b[^>]*>/g)) assert.match(form, /method="post"/);
  // A password manager passes over a field that isn't drawn, so the name it saves the token under is shown.
  const name = page.match(/<input\b[^>]*autocomplete="username"[^>]*>/)[0];
  assert.doesNotMatch(name, /\bhidden\b/);

  const script = await read("public/admin/admin.js");
  assert.doesNotMatch(script, /localStorage|sessionStorage|document\.cookie|indexedDB/);
  assert.doesNotMatch(script, /innerHTML|insertAdjacentHTML|outerHTML/, "what it shows is set as text");
  assert.deepEqual([...new Set([...script.matchAll(/call\("([^"]+)"/g)].map((match) => match[1]))], ["/api/operator/invites"]);
});
