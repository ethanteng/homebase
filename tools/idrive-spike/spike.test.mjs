// The parts of the spike that don't need iDrive: the reseller client's requests and error
// handling, what gets recorded, and the library comparison every restore is judged by.

import { test } from "node:test";
import assert from "node:assert/strict";
import { cpSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createReseller, findStorageDn, listIn, ResellerError } from "./reseller.mjs";
import { Recorder, redact, shapeOf } from "./record.mjs";
import { changeLibrary, compare, fingerprint, makeLibrary } from "./library.mjs";
import { attempt, isRefusal, s3Client, secondsUntilRefused, secondsUntilWorks } from "./s3.mjs";
import { createServer } from "node:http";
import {
  CreateBucketCommand,
  DeleteBucketCommand,
  DeleteObjectCommand,
  GetBucketVersioningCommand,
  GetObjectLockConfigurationCommand,
  HeadObjectCommand,
  ListBucketsCommand,
  ListObjectsV2Command,
  ListObjectVersionsCommand,
  PutBucketVersioningCommand,
  PutObjectCommand,
  PutObjectLockConfigurationCommand,
} from "@aws-sdk/client-s3";

const answer = (body, status = 200) => async () => new Response(JSON.stringify(body), { status });

test("the token goes in a header and the password goes base64-encoded", async () => {
  const sent = [];
  const reseller = createReseller({
    token: "t0ken",
    fetchImpl: async (url, init) => {
      sent.push({ url, init });
      return new Response(JSON.stringify({ user_created: true }));
    },
  });
  await reseller.createUser({ email: "a@example.com", password: "pässword", first_name: "A", quota: 1 });
  assert.equal(sent[0].url, "https://api.idrivee2.com/api/reseller/v1/create_user");
  assert.equal(sent[0].init.method, "PUT");
  assert.equal(sent[0].init.headers.token, "t0ken");
  const body = JSON.parse(sent[0].init.body);
  assert.equal(body.password, Buffer.from("pässword").toString("base64"));
  assert.equal(body.email, "a@example.com");
});

test("an error answer becomes a ResellerError with its code, even one sent as a 200", async () => {
  for (const status of [403, 200]) {
    const reseller = createReseller({
      token: "t",
      fetchImpl: answer({ error: { type: "invalid_request_error", code: "user_account_already_disabled", message: "Already disabled" } }, status),
    });
    await assert.rejects(reseller.disableUser("a@example.com"),
      (error) => error instanceof ResellerError && error.code === "user_account_already_disabled" && error.status === status);
  }
});

test("every call is reported, a failed one included", async () => {
  const calls = [];
  const reseller = createReseller({
    token: "t",
    fetchImpl: answer({ error: { code: "account_non_existant" } }, 403),
    onCall: (call) => calls.push(call),
  });
  await assert.rejects(reseller.removeUser("a@example.com"));
  assert.equal(calls.length, 1);
  assert.equal(calls[0].path, "/remove_user");
  assert.equal(calls[0].status, 403);
  assert.deepEqual(calls[0].request, { email: "a@example.com" });
});

test("what's recorded has no secrets in it", () => {
  const recorded = redact({
    request: { email: "a@example.com", password: "c2VjcmV0" },
    response: { data: { access_key: "AKIAEXAMPLE1234", secret_key: "shh" }, users: [{ invite_url: "https://x" }] },
  });
  assert.equal(recorded.request.password, "[removed]");
  assert.equal(recorded.request.email, "a@example.com");
  assert.equal(recorded.response.data.secret_key, "[removed]");
  assert.equal(recorded.response.data.access_key, "AKIA…");
  assert.equal(recorded.response.users[0].invite_url, "[removed]");

  // list_access_keys names the same id differently.
  const listed = redact({ access_keys: [{ key_id: "AKIAEXAMPLE5678", name: "spike-host" }] });
  assert.deepEqual(listed.access_keys[0], { key_id: "AKIA…", name: "spike-host" });
});

const s3Error = (name, status) =>
  Object.assign(new Error(name), { name, $metadata: status ? { httpStatusCode: status } : {} });

/** An action that fails with each error in turn (null for success), repeating the last. */
function scripted(...outcomes) {
  let calls = 0;
  const action = async () => {
    const next = outcomes[Math.min(calls++, outcomes.length - 1)];
    if (next) throw next;
  };
  return { action, calls: () => calls };
}

test("only a deliberate no counts as a refusal", () => {
  assert.equal(isRefusal(s3Error("AccessDenied", 403)), true);
  assert.equal(isRefusal(s3Error("InvalidAccessKeyId", 403)), true);
  assert.equal(isRefusal(s3Error("ServiceUnavailable", 503)), false);
  assert.equal(isRefusal(s3Error("NoSuchBucket", 404)), false);
  assert.equal(isRefusal(Object.assign(new Error("socket hang up"), { code: "ECONNRESET" })), false);
});

test("passing trouble is asked again, and never passes for a refusal", async () => {
  const flaky = scripted(s3Error("ServiceUnavailable", 503), s3Error("AccessDenied", 403));
  assert.deepEqual(await attempt(flaky.action, { every: 1 }), { allowed: false, refused: true, error: "AccessDenied (403)" });
  assert.equal(flaky.calls(), 2);

  const down = scripted(s3Error("InternalError", 500));
  assert.deepEqual(await attempt(down.action, { every: 1 }),
    { allowed: false, refused: false, passing: true, error: "InternalError (500)" });
  assert.equal(down.calls(), 3);

  // A plain answer isn't asked again, and isn't a refusal either.
  const missing = scripted(s3Error("NoSuchBucket", 404));
  assert.deepEqual(await attempt(missing.action, { every: 1 }),
    { allowed: false, refused: false, passing: false, error: "NoSuchBucket (404)" });
  assert.equal(missing.calls(), 1);

  // An unusual no keeps the store's own words.
  const locked = scripted(Object.assign(s3Error("InvalidRequest", 400), { message: "Object is WORM protected" }));
  assert.equal((await attempt(locked.action, { every: 1 })).error, "InvalidRequest (400): Object is WORM protected");
});

test("a revoked key is timed to its refusal, not to the first error on the way", async () => {
  const probe = scripted(null, s3Error("ServiceUnavailable", 503), s3Error("InvalidAccessKeyId", 403));
  const refused = await secondsUntilRefused(probe.action, { every: 1 });
  assert.equal(refused.error, "InvalidAccessKeyId (403)");
  assert.equal(refused.trouble, "ServiceUnavailable (503)");
  assert.equal(probe.calls(), 3);

  const never = await secondsUntilRefused(scripted(s3Error("InternalError", 500)).action, { every: 1, timeout: 0.05 });
  assert.equal(never.seconds, null);
  assert.equal(never.trouble, "InternalError (500)");
});

test("every kind of request the spike makes gets sent, with Content-MD5 where a checksum is due", async () => {
  const seen = [];
  const server = createServer((request, response) => {
    seen.push(request.headers);
    request.resume().on("end", () => response.end());
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const s3 = s3Client({
    endpoint: `http://127.0.0.1:${server.address().port}`,
    region: "us-east-1",
    accessKey: "AKIAEXAMPLE",
    secretKey: "secret",
  });
  // Bucket "b", key "k": what each command is sent with, and whether a checksum is due.
  const requests = [
    [ListBucketsCommand, {}, false],
    [CreateBucketCommand, { ObjectLockEnabledForBucket: true }, false],
    [PutBucketVersioningCommand, { VersioningConfiguration: { Status: "Enabled" } }, true],
    [GetBucketVersioningCommand, {}, false],
    [PutObjectLockConfigurationCommand,
      { ObjectLockConfiguration: { ObjectLockEnabled: "Enabled", Rule: { DefaultRetention: { Mode: "GOVERNANCE", Days: 1 } } } },
      true],
    [GetObjectLockConfigurationCommand, {}, false],
    [ListObjectsV2Command, { MaxKeys: 1 }, false],
    [ListObjectVersionsCommand, {}, false],
    [PutObjectCommand, { Key: "k", Body: "probe" }, false],
    [PutObjectCommand, { Key: "k", Body: "probe", ChecksumAlgorithm: "CRC32" }, true],
    [HeadObjectCommand, { Key: "k" }, false],
    [DeleteObjectCommand, { Key: "k", VersionId: "v", BypassGovernanceRetention: true }, false],
    [DeleteBucketCommand, {}, false],
  ];
  try {
    for (const [Command, input] of requests) {
      await s3.send(new Command({ ...(Command === ListBucketsCommand ? {} : { Bucket: "b" }), ...input }));
    }
  } finally {
    server.close();
  }
  assert.equal(seen.length, requests.length);
  requests.forEach(([Command, , due], i) => {
    const headers = seen[i];
    const label = Command.name;
    assert.deepEqual(Object.keys(headers).filter((name) => name.startsWith("x-amz-checksum")), [], label);
    // Signed after the swap, or the signature wouldn't match what's sent.
    const signed = /SignedHeaders=([^,]+)/.exec(headers.authorization)?.[1].split(";") ?? [];
    if (due) {
      assert.match(headers["content-md5"] ?? "", /^[A-Za-z0-9+/]{22}==$/, label);
      assert.ok(signed.includes("content-md5"), `${label} signs Content-MD5`);
    } else {
      assert.equal(headers["content-md5"], undefined, label);
    }
  });
});

test("waiting for a key gives up at once on trouble that never left this computer", async () => {
  let calls = 0;
  const broken = async () => {
    calls++;
    throw new Error("a middleware that isn't there");
  };
  await assert.rejects(secondsUntilWorks(broken, { every: 1 }), /a middleware that isn't there/);
  assert.equal(calls, 1);
  // Whereas a key the store doesn't know yet is waited for.
  const fresh = scripted(s3Error("InvalidAccessKeyId", 403), null);
  assert.equal(typeof (await secondsUntilWorks(fresh.action, { every: 1 })), "number");
  assert.equal(fresh.calls(), 2);
});

test("no step starts once a run is told to stop, except what removing the user needs", async () => {
  const dir = mkdtempSync(join(tmpdir(), "idrive-spike-"));
  const record = new Recorder(dir, "test");
  try {
    const stopping = new AbortController();
    record.signal = stopping.signal;
    assert.equal(await record.step("before", async () => "ran"), "pass");
    stopping.abort();
    let ran = false;
    await assert.rejects(record.step("after", async () => { ran = true; }), { name: "AbortError" });
    assert.equal(ran, false);
    // What a stopped run does before removing the test user.
    record.signal = null;
    assert.equal(await record.step("remove the user", async () => "ran"), "pass");
  } finally {
    await record.close();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("storage_dn is found wherever list_user_regions puts it", () => {
  const listed = { user_regions: [{ region_key: "OR", storage_dn: "a.or.example" }, { region_key: "VA", storage_dn: "b.va.example" }] };
  assert.equal(findStorageDn(listed, "VA"), "b.va.example");
  assert.equal(findStorageDn(listed), "a.or.example");
  assert.equal(findStorageDn({ user_regions: { VA: "c.va.example" } }, "VA"), "c.va.example");
  assert.equal(findStorageDn({}, "VA"), undefined);
});

test("a list is found bare or wrapped", () => {
  assert.deepEqual(listIn([1], "users"), [1]);
  assert.deepEqual(listIn({ users: [2] }, "users"), [2]);
  assert.deepEqual(listIn({ data: [3] }, "users"), [3]);
  assert.deepEqual(listIn({ id: "x", whatever: [4] }, "users"), [4]);
  assert.deepEqual(listIn(null, "users"), []);
});

test("an answer's shape is described without its values", () => {
  assert.equal(shapeOf({ user_regions: [{ storage_dn: "secret.example", active: true }], n: 1 }),
    "{ user_regions: [{ storage_dn: string, active: boolean }], n: number }");
});

test("a faithful copy of the library compares identical, and a changed one doesn't", async () => {
  const root = mkdtempSync(join(tmpdir(), "idrive-spike-"));
  try {
    const library = join(root, "library");
    makeLibrary(library, 2);
    const copy = join(root, "copy");
    cpSync(library, copy, { recursive: true, preserveTimestamps: true });
    const original = await fingerprint(library);
    const same = compare(original, await fingerprint(copy));
    assert.equal(same.same, true);
    assert.equal(same.modified, 0);
    assert.ok(original.has("names/café.txt") && original.has("empty folder") && original.has("names/.hidden"));

    changeLibrary(copy);
    const changed = compare(original, await fingerprint(copy));
    assert.equal(changed.same, false);
    assert.ok(changed.missing.length > 0, "deleted files show as missing");
    assert.ok(changed.different.length > 0, "edited files show as different");
    assert.ok(changed.extra.length > 0, "added files show as extra");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
