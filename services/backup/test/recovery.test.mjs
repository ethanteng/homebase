// A household's recovery token, against a real Postgres (PGlite) and a pretend iDrive: kept only as
// a hash, replaced by each new kit, and taken in place of a lost host's credential in two steps. A
// new host is let in provisionally, to try the kit's password; only once it confirms are the
// household's other hosts and their keys retired, and if it withdraws, nothing else changes.

import { test } from "node:test";
import assert from "node:assert/strict";
import { freshDatabase } from "./database.mjs";
import { fakeIdrive } from "./fake-idrive.mjs";
import { ready } from "../lib/db.mjs";
import { ServiceError } from "../lib/http.mjs";
import { authenticateHost } from "../lib/hosts.mjs";
import { createInvite, redeemInvite } from "../lib/invites.mjs";
import { abandonRecovery, newRecoveryToken, normaliseRecovery, PROVISIONAL_SECONDS } from "../lib/recovery.mjs";
import {
  abandonRecoveryRoute, confirmRecoveryRoute, hostStorageRoute, makeRecoveryRoute, recoverHostRoute,
} from "../lib/routes.mjs";
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

const quietly = async (run) => {
  const original = console.error;
  console.error = () => {};
  try { return await run(); } finally { console.error = original; }
};

/** The household's recovery token, as its host makes one. */
async function tokenOf(joined, deps) {
  return (await (await makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: joined.credential }), deps)).json()).recovery;
}

async function recover(recovery, deps, name) {
  return (await recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery, name } }), deps)).json();
}

const confirm = (credential, deps) => confirmRecoveryRoute(asking("/api/host/recovery/confirm", { method: "POST", token: credential }), deps);
const abandon = (credential, deps) => abandonRecoveryRoute(asking("/api/host/recovery/abandon", { method: "POST", token: credential }), deps);
const storageFor = (credential, deps) => hostStorageRoute(asking("/api/host/storage", { method: "POST", token: credential }), deps);
const isHost = async (db, credential, at) => (await authenticateHost(db, asking("/api/host", { token: credential }), at)) !== null;

test("using the token lets a new host in provisionally, and changes nothing about the household's hosts", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovery = await tokenOf(joined, deps);

    const recovered = await recover(recovery.toLowerCase(), deps, "New Mac");

    assert.equal(recovered.recoveryMadeAt, later(60).toISOString(), "the kit used is still the one in use");
    assert.equal(recovered.household.id, joined.household.id);
    assert.equal(recovered.host.name, "New Mac");
    assert.ok(await isHost(db, joined.credential, later(61)), "the household's host goes on working");
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`]);
    // The provisional host may get storage, to try the kit's password on.
    const storage = await (await storageFor(recovered.credential, { ...deps, now: later(120) })).json();
    assert.equal(storage.storage.bucket, `uncloud-${joined.household.id}`);
    // But not make a new kit, which would retire the token it's using.
    await assert.rejects(makeRecoveryRoute(asking("/api/host/recovery", { method: "POST", token: recovered.credential }), deps),
      refused("recovering"));
  });
});

test("confirming takes the lost host's place: its credential and key stop working, the new host's go on", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovered = await recover(await tokenOf(joined, deps), deps);
    await storageFor(recovered.credential, { ...deps, now: later(120) });

    const confirmed = await confirm(recovered.credential, { ...deps, now: later(180) });

    assert.equal(confirmed.status, 200);
    assert.deepEqual(await confirmed.json(), { revoked: 1 });
    assert.equal(await isHost(db, joined.credential, later(181)), false);
    assert.ok(await isHost(db, recovered.credential, later(PROVISIONAL_SECONDS * 2)), "a host like any other now, past the provisional time");
    assert.deepEqual(hostKeys(idrive, joined), [`host-${recovered.host.id}`], "the lost host's key no longer reaches the bucket");
    // Confirmed, it's no longer taking anybody's place.
    await assert.rejects(confirm(recovered.credential, { ...deps, now: later(240) }), refused("not_recovering"));
    await assert.rejects(confirm(joined.credential, deps), refused("not_a_host"));
  });
});

test("withdrawing retires only the new host and its key, and the kit works again after", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovery = await tokenOf(joined, deps);
    const mistaken = await recover(recovery, deps);
    await storageFor(mistaken.credential, { ...deps, now: later(120) });

    assert.deepEqual(await (await abandon(mistaken.credential, { ...deps, now: later(180) })).json(), { withdrawn: true });

    assert.equal(await isHost(db, mistaken.credential, later(181)), false);
    assert.ok(await isHost(db, joined.credential, later(181)), "the household's host is as it was");
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`]);
    await assert.rejects(abandon(joined.credential, deps), refused("not_recovering"));

    const again = await recover(recovery, { ...deps, now: later(240) });
    assert.equal((await confirm(again.credential, { ...deps, now: later(300) })).status, 200);
    assert.equal(await isHost(db, joined.credential, later(301)), false);
  });
});

test("a new kit retires a recovery already under way, and that recovery can no longer take the host's place", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovery = await tokenOf(joined, deps);
    const intruder = await recover(recovery, deps);
    await storageFor(intruder.credential, { ...deps, now: later(120) });

    const { recovery: newer } = await (await makeRecoveryRoute(
      asking("/api/host/recovery", { method: "POST", token: joined.credential }), { ...deps, now: later(180) })).json();

    assert.equal(await isHost(db, intruder.credential, later(181)), false, "the old token's host is retired");
    assert.ok(await isHost(db, joined.credential, later(181)), "the household's host goes on working");
    await assert.rejects(confirm(intruder.credential, { ...deps, now: later(182) }), refused("not_a_host"));
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`], "the retired recovery's key is gone");
    await assert.rejects(recover(recovery, { ...deps, now: later(183) }), refused("recovery_invalid"));

    const again = await recover(newer, { ...deps, now: later(240) });
    assert.equal((await confirm(again.credential, { ...deps, now: later(300) })).status, 200);
    assert.equal(await isHost(db, joined.credential, later(301)), false);
  });
});

test("withdrawing after a confirm has landed does not retire the host that took the lost one's place", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovered = await recover(await tokenOf(joined, deps), deps);
    await storageFor(recovered.credential, { ...deps, now: later(120) });
    assert.equal((await confirm(recovered.credential, { ...deps, now: later(180) })).status, 200);

    // The withdrawal was decided while this host still looked provisional. Confirm has since landed.
    await assert.rejects(
      abandonRecovery(db, idrive, { id: recovered.host.id, household: joined.household.id, provisional: true }, later(181)),
      refused("not_recovering"));

    assert.ok(await isHost(db, recovered.credential, later(182)), "the host that confirmed is still the household's");
    assert.equal(await isHost(db, joined.credential, later(182)), false);
    assert.deepEqual(hostKeys(idrive, joined), [`host-${recovered.host.id}`]);
  });
});

test("one recovery is under way at a time: a new one retires an earlier one and its key", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovery = await tokenOf(joined, deps);
    const first = await recover(recovery, deps);
    await storageFor(first.credential, { ...deps, now: later(90) });
    assert.deepEqual(hostKeys(idrive, joined).sort(), [`host-${first.host.id}`, `host-${joined.host.id}`].sort());

    const second = await recover(recovery, { ...deps, now: later(100) });

    assert.equal(await isHost(db, first.credential, later(101)), false, "the earlier recovery is retired");
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`], "and its key with it");
    assert.ok(await isHost(db, joined.credential, later(101)), "the household's host is untouched");
    assert.deepEqual(await (await confirm(second.credential, { ...deps, now: later(120) })).json(), { revoked: 1 });

    assert.equal(await isHost(db, joined.credential, later(121)), false);
    const { rows } = await db.query("select count(*)::int as live from hosts where revoked_at is null");
    assert.equal(rows[0].live, 1);
  });
});

test("a host that neither confirms nor withdraws stops working, and the night retires it and its key", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const lapsing = await recover(await tokenOf(joined, deps), deps);
    await storageFor(lapsing.credential, { ...deps, now: later(120) });
    const past = later(60 + PROVISIONAL_SECONDS + 1);

    assert.equal(await isHost(db, lapsing.credential, past), false);
    await assert.rejects(confirm(lapsing.credential, { ...deps, now: past }), refused("not_a_host"));
    assert.deepEqual(await nightly({ db, idrive, now: past }), {
      ran: [{ job: "lapsed recoveries", retired: 1 }, { job: "revoked keys", removed: 1, failed: 0 }],
    });
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`]);
    assert.ok(await isHost(db, joined.credential, past), "the household's host was never touched");
  });
});

test("a lost host's key iDrive couldn't remove at once is removed by the next storage request, or overnight", async () => {
  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovered = await recover(await tokenOf(joined, deps), deps);
    idrive.failOnce("listAccessKeys");
    await quietly(() => confirm(recovered.credential, deps));
    assert.deepEqual(hostKeys(idrive, joined), [`host-${joined.host.id}`], "still there, for now");
    const { rows: [pending] } = await db.query("select key_removed_at from hosts where id = $1", [joined.host.id]);
    assert.equal(pending.key_removed_at, null);

    assert.equal((await storageFor(recovered.credential, { ...deps, now: later(120) })).status, 201);

    assert.deepEqual(hostKeys(idrive, joined), [`host-${recovered.host.id}`], "the lost host's key is gone, the new host's given");
    const { rows: [done] } = await db.query("select key_removed_at from hosts where id = $1", [joined.host.id]);
    assert.ok(done.key_removed_at);
  });

  await withHousehold(async ({ db, idrive, joined, deps }) => {
    const recovered = await recover(await tokenOf(joined, deps), deps);
    idrive.failOnce("listAccessKeys");
    await quietly(() => confirm(recovered.credential, deps));

    assert.deepEqual(await nightly({ db, idrive, now: later(3600) }), {
      ran: [{ job: "lapsed recoveries", retired: 0 }, { job: "revoked keys", removed: 1, failed: 0 }],
    });
    assert.deepEqual(hostKeys(idrive, joined), []);
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
    const recovery = await tokenOf(joined, deps);
    await assert.rejects(recoverHostRoute(asking("/api/hosts/recover", { method: "POST", body: { recovery } }), { ...deps, idrive: null }),
      refused("no_idrive"));
    const recovered = await recover(recovery, deps);
    await assert.rejects(confirm(recovered.credential, { ...deps, idrive: null }), refused("no_idrive"));
    assert.ok(await isHost(db, joined.credential, later(61)), "nothing was revoked");
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
