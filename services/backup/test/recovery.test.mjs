// A household's recovery token, against a real Postgres (PGlite) and a pretend iDrive: kept only as
// a hash, replaced by each new kit, and taken in place of a lost host's credential, which revokes
// the household's other hosts and their keys.

import { test } from "node:test";
import assert from "node:assert/strict";
import { freshDatabase } from "./database.mjs";
import { fakeIdrive } from "./fake-idrive.mjs";
import { ready } from "../lib/db.mjs";
import { ServiceError } from "../lib/http.mjs";
import { authenticateHost } from "../lib/hosts.mjs";
import { createInvite, redeemInvite } from "../lib/invites.mjs";
import { newRecoveryToken, normaliseRecovery } from "../lib/recovery.mjs";
import { hostStorageRoute, makeRecoveryRoute, recoverHostRoute } from "../lib/routes.mjs";
import { advanceSetup, issueHostKey } from "../lib/setup.mjs";
import { nightly } from "../lib/nightly.mjs";

const NOW = new Date("2026-10-18T12:00:00Z");
const later = (seconds) => new Date(NOW.getTime() + seconds * 1000);

const asking = (path, { method = "GET", token, body } = {}) => new Request(`https://backup.example${path}`, {
  method,
  headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), ...(body ? { "content-type": "application/json" } : {}) },
  body: body === undefined ? undefined : JSON.stringify(body),
});
const refused = (code) => (error) => error instanceof ServiceError && error.code === code;

/** A household set up, with its host given a key, as one that's been backing up would be. */
async function withHousehold(run) {
  const db = await freshDatabase();
  try {
    await ready(db);
    const idrive = fakeIdrive();
    const { code } = await createInvite(db, { now: NOW });
    const joined = await redeemInvite(db, { code, name: "Mac mini", now: NOW });
    await advanceSetup(db, idrive, joined.household.id, { now: NOW });
    assert.equal((await hostStorageRoute(asking("/api/host/storage", { method: "POST", token: joined.credential }), { db, idrive, now: NOW })).status, 201);
    await run({ db, idrive, joined, deps: { db, idrive, env: {}, now: later(60) } });
  } finally {
    await db.close();
  }
}

const hostKeys = (idrive, joined) => idrive.users.get(`household-${joined.household.id}@uncloud.life`).keys
  .filter((key) => key.name.startsWith("host-")).map((key) => key.name);

test("a recovery token is easy to write down and type back in", () => {
  const token = newRecoveryToken();
  assert.match(token, /^[2-9A-HJ-NP-Z]{5}(-[2-9A-HJ-NP-Z]{5}){5}$/);
  assert.notEqual(newRecoveryToken(), token);
  assert.equal(normaliseRecovery(token.toLowerCase().replaceAll("-", " ")), token.replaceAll("-", ""));
  assert.equal(normaliseRecovery("K7QMX-3XPD9"), null);
  assert.equal(normaliseRecovery(42), null);
});

test("a host makes its household's token, kept only as a hash, and a new one replaces the old", async () => {
  await withHousehold(async ({ db, joined, deps }) => {
    const made = await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps);
    assert.equal(made.status, 201);
    assert.equal(made.headers.get("cache-control"), "no-store");
    const { recovery } = await made.json();
    const { rows } = await db.query("select * from households");
    assert.ok(!JSON.stringify(rows).includes(recovery.replaceAll("-", "")));
    assert.ok(!JSON.stringify(rows).includes(recovery));
    assert.equal(new Date(rows[0].recovery_made_at).toISOString(), later(60).toISOString());

    const { recovery: newer } = await (await makeRecoveryRoute(
      asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json();
    await assert.rejects(recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), deps),
      refused("recovery_invalid"));
    assert.equal((await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery: newer } }), deps)).status, 201);
  });
});

test("a new host takes a lost one's place with the token, and the lost one's credential and key stop working", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const { recovery } = await (await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json();
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`]);

    const answer = await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery: recovery.toLowerCase(), name: "New Mac" } }), deps);

    assert.equal(answer.status, 201);
    const recovered = await answer.json();
    assert.equal(recovered.recoveryMadeAt, later(60).toISOString(), "the kit used is still the one in use");
    assert.equal(recovered.household.id, joined.household.id);
    assert.equal(recovered.host.name, "New Mac");
    assert.notEqual(recovered.host.id, joined.host.id);
    assert.equal(await authenticateHost(db, asking("/api/host", { token: joined.credential }), later(61)), null);
    assert.deepEqual((await authenticateHost(db, asking("/api/host", { token: recovered.credential }), later(61)))?.household, joined.household.id);
    assert.deepEqual(hostKeys(idrive, joined), [], "the lost host's key no longer reaches the bucket");

    // And the new host gets a key of its own to the same bucket.
    const storage = await (await hostStorageRoute(asking("/api/host/storage", { method: "POST", token: recovered.credential }), { ...deps, now: later(120) })).json();
    assert.equal(storage.storage.bucket, `uncloud-${joined.household.id}`);
  });
});

test("the same kit works again if a recovery goes wrong partway, and each use replaces the host before", async () => {
  await withHousehold(async ({ db, joined, deps }) => {
    const { recovery } = await (await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json();
    const first = await (await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), deps)).json();

    const second = await (await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), deps)).json();

    assert.equal(await authenticateHost(db, asking("/api/host", { token: first.credential }), later(61)), null);
    assert.ok(await authenticateHost(db, asking("/api/host", { token: second.credential }), later(61)));
    const { rows } = await db.query("select count(*)::int as live from hosts where revoked_at is null");
    assert.equal(rows[0].live, 1);
  });
});

test("a lost host's key iDrive couldn't remove at first is removed by the next storage request, or overnight", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const { recovery } = await (await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json();
    idrive.failOnce("listAccessKeys");
    const original = console.error;
    console.error = () => {};
    let recovered;
    try {
      recovered = await (await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), deps)).json();
    } finally {
      console.error = original;
    }
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`], "still there, for now");
    const { rows: [pending] } = await db.query("select key_removed_at from hosts where id = $1", [joined.host.id]);
    assert.equal(pending.key_removed_at, null);

    assert.equal((await hostStorageRoute(asking("/api/host/storage", { method: "POST", token: recovered.credential }), { ...deps, now: later(120) })).status, 201);

    assert.deepEqual(hostKeys(idrive, joined), [`host-${recovered.host.id}`], "the lost host's key is gone, the new host's given");
    const { rows: [done] } = await db.query("select key_removed_at from hosts where id = $1", [joined.host.id]);
    assert.ok(done.key_removed_at);
  });

  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const { recovery } = await (await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json();
    idrive.failOnce("listAccessKeys");
    const original = console.error;
    console.error = () => {};
    try {
      await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), deps);
    } finally {
      console.error = original;
    }

    assert.deepEqual(await nightly({ db, idrive, now: later(3600) }), { ran: [{ job: "revoked keys", removed: 1, failed: 0 }] });
    assert.deepEqual(hostKeys(idrive, joined), []);
    assert.deepEqual(await nightly({ db, idrive, now: later(7200) }), { ran: [{ job: "revoked keys", removed: 0, failed: 0 }] });
  });
});

test("a key made after its host was revoked does not stay in the bucket", async () => {
  await withHousehold(async ({ db, idrive, joined }) => {
    const { rows: [household] } = await db.query("select * from households where id = $1", [joined.household.id]);
    const host = { id: joined.host.id };
    const original = idrive.reseller.createAccessKey.bind(idrive.reseller);
    // Recovery has revoked this host and recorded the key it had as gone, while this request,
    // already past its lease, is still making a replacement.
    idrive.reseller.createAccessKey = async (options) => {
      await db.query("update hosts set revoked_at = $2, key_removed_at = $2 where id = $1", [host.id, later(90)]);
      return original(options);
    };

    assert.equal(await issueHostKey(db, idrive, household, host, { now: later(90) }), null);

    assert.deepEqual(hostKeys(idrive, joined), [], "the key made for the revoked host is removed again");
    const { rows: [row] } = await db.query("select key_removed_at, key_lease_until, key_id from hosts where id = $1", [host.id]);
    assert.ok(row.key_removed_at);
    assert.equal(row.key_lease_until, null);
    assert.equal(row.key_id, null);
  });
});

test("a recovery is refused where the service can't reach iDrive to remove the lost host's key", async () => {
  await withHousehold(async ({ db, joined, deps }) => {
    const { recovery } = await (await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json();
    await assert.rejects(recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), { ...deps, idrive: null }),
      refused("no_idrive"));
    assert.ok(await authenticateHost(db, asking("/api/host", { token: joined.credential }), later(61)), "nothing was revoked");
  });
});

test("an unknown or mistyped token gets one answer, and only a host makes a token", async () => {
  await withHousehold(async ({ deps }) => {
    for (const recovery of [newRecoveryToken(), "not a token", undefined]) {
      await assert.rejects(recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), deps),
        (error) => refused("recovery_invalid")(error) && /isn't one Uncloud Backup knows/.test(error.message));
    }
    await assert.rejects(makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: "guess" }), deps),
      refused("not_a_host"));
  });
});
