// Setting up a household's storage, against a real Postgres (PGlite) and a pretend iDrive
// (fake-idrive.mjs): the steps, picking up after any of them is cut short, never two goes at once,
// a bucket that has to read back protected, and each host's key to it.

import { test } from "node:test";
import assert from "node:assert/strict";
import { freshDatabase } from "./database.mjs";
import { fakeIdrive } from "./fake-idrive.mjs";
import { ready } from "../lib/db.mjs";
import { ServiceError } from "../lib/http.mjs";
import { findStorage } from "../lib/idrive.mjs";
import { createInvite, redeemInvite } from "../lib/invites.mjs";
import { EXPIRE_DAYS, EXPIRY_RULE, inspectBucket, LOCK_DAYS } from "../lib/protection.mjs";
import { hostStorageRoute, listHouseholdsRoute } from "../lib/routes.mjs";
import { advanceSetup, bucketName, QUOTA_GB } from "../lib/setup.mjs";

const NOW = new Date("2026-10-15T12:00:00Z");
const OPERATOR = "operator-token-for-tests";
const later = (seconds) => new Date(NOW.getTime() + seconds * 1000);

async function withHousehold(run, options) {
  const db = await freshDatabase();
  try {
    await ready(db);
    const { code } = await createInvite(db, { now: NOW });
    const joined = await redeemInvite(db, { code, name: "Mac mini", now: NOW });
    const idrive = fakeIdrive(options);
    await run({ db, idrive, id: joined.household.id, joined });
  } finally {
    await db.close();
  }
}

const quietly = async (run) => {
  const original = console.error;
  console.error = () => {};
  try { return await run(); } finally { console.error = original; }
};

const asking = (path, { method = "GET", token } = {}) => new Request(`https://backup.example${path}`, {
  method,
  headers: token ? { authorization: `Bearer ${token}` } : {},
});

/** Everything iDrive holds for a household that's set up, checked against what it should be. */
function assertSetUp(idrive, id) {
  const email = `household-${id}@uncloud.life`;
  const account = idrive.users.get(email);
  assert.ok(account, "the account is made");
  assert.equal(idrive.users.size, 1, "and only the one");
  assert.equal(account.quota, QUOTA_GB);
  assert.equal(account.enforce_quota, true);
  assert.equal(account.email_notification, false);
  assert.deepEqual([...account.storage], [["OR2", "s3.or2.example"]]);
  const bucket = idrive.buckets.get(bucketName(id));
  assert.ok(bucket?.objectLock, "the bucket is made with object lock");
  assert.equal(idrive.buckets.size, 1);
  assert.equal(bucket.versioning, "Enabled");
  assert.deepEqual(bucket.lock, { ObjectLockEnabled: "Enabled", Rule: { DefaultRetention: { Mode: "COMPLIANCE", Days: LOCK_DAYS } } });
  assert.deepEqual(bucket.rules.map((rule) => [rule.ID, rule.NoncurrentVersionExpiration.NoncurrentDays]), [[EXPIRY_RULE, EXPIRE_DAYS]]);
  assert.deepEqual(account.keys.filter((key) => key.name.startsWith("service-")), [], "no key of the service's is left");
}

test("a household is set up in one go: an account, storage, and a bucket locked in compliance mode", async () => {
  await withHousehold(async ({ db, idrive, id }) => {
    const { state, household } = await advanceSetup(db, idrive, id, { now: NOW });

    assert.deepEqual(state, { state: "ready", readyAt: NOW.toISOString() });
    assert.equal(household.account_email, `household-${id}@uncloud.life`);
    assert.equal(household.region, "OR2");
    assert.equal(household.storage_dn, "s3.or2.example");
    assert.equal(household.bucket, `uncloud-${id}`);
    assert.equal(household.setup_lease_until, null);
    assertSetUp(idrive, id);
    // The account's password is iDrive's alone: it's in no column.
    const password = idrive.users.get(household.account_email).password;
    const { rows } = await db.query("select * from households");
    assert.ok(!JSON.stringify(rows).includes(password));
  });
});

test("set up already, a household is left alone", async () => {
  await withHousehold(async ({ db, idrive, id }) => {
    await advanceSetup(db, idrive, id, { now: NOW });
    const asked = idrive.calls.length;

    const { state } = await advanceSetup(db, idrive, id, { now: later(60) });

    assert.equal(state.state, "ready");
    assert.equal(idrive.calls.length, asked);
  });
});

// Each call that makes something at iDrive, or needs to work for the next to, failing before it does
// anything or after it has done it and the answer was lost. Some the same go sees were done after all
// and carries on past; the rest the next go picks up. Either way nothing is made twice.
const interruptions = [
  // [call, after it was done, carried on past in the same go]
  ["createUser", false, false], ["createUser", true, true],
  ["enableUserRegion", false, false], ["enableUserRegion", true, true],
  ["createAccessKey", true, false], ["ListBuckets", false, true],
  ["CreateBucket", true, false], ["PutObjectLockConfiguration", false, false],
  ["GetBucketLifecycleConfiguration", false, false], ["removeAccessKey", false, false],
];

for (const [call, after, carriesOn] of interruptions) {
  const what = `cut short ${after ? "after" : "before"} ${call}`;
  test(`${what}, a setup ${carriesOn ? "sees what was done and carries on" : "picks up where it was next time"}, making nothing twice`, async () => {
    await withHousehold(async ({ db, idrive, id }) => {
      idrive.failOnce(call, { after });

      const first = await quietly(() => advanceSetup(db, idrive, id, { now: NOW }));
      if (!carriesOn) {
        assert.equal(first.state.state, "failing");
        assert.match(first.state.problem, new RegExp(call));
        assert.equal(first.state.failures, 1);
      }

      const second = await advanceSetup(db, idrive, id, { now: later(60) });
      assert.equal(second.state.state, "ready");
      assert.equal(second.household.setup_problem, null);
      assert.equal(second.household.setup_failures, carriesOn ? 0 : 1);
      assertSetUp(idrive, id);
      // Asked for twice, these would be refused: they're looked for instead. (A bucket is asked for
      // again, and iDrive answers that it's already ours.)
      if (after && ["createUser", "enableUserRegion"].includes(call)) assert.equal(idrive.count(call), 1, `${call} isn't asked for again`);
    });
  });
}

test("a step there isn't time left for waits for the next request", async () => {
  await withHousehold(async ({ db, idrive, id }) => {
    // Time for the account (5 s) but not for storage, which can take 45.
    const first = await advanceSetup(db, idrive, id, { now: NOW, until: Date.now() + 20_000 });

    assert.deepEqual({ state: first.state.state, step: first.state.step, problem: first.state.problem },
      { state: "setting_up", step: "storage", problem: null });
    assert.equal(idrive.count("enableUserRegion"), 0);

    assert.equal((await advanceSetup(db, idrive, id, { now: later(10) })).state.state, "ready");
    assert.equal(idrive.count("createUser"), 1);
  });
});

test("two goes at once don't both set a household up, and a go that died doesn't hold it forever", async () => {
  await withHousehold(async ({ db, idrive, id }) => {
    const [one, two] = await Promise.all([
      advanceSetup(db, idrive, id, { now: NOW }),
      advanceSetup(db, idrive, id, { now: NOW }),
    ]);
    assert.deepEqual([one.state.state, two.state.state].sort(), ["ready", "setting_up"]);
    assert.equal(idrive.count("createUser"), 1);
  });

  await withHousehold(async ({ db, idrive, id }) => {
    // Somebody took it at NOW and was never heard from again.
    await db.query("update households set setup_lease_until = $2, setup_tried_at = $3 where id = $1", [id, later(75), NOW]);

    const meanwhile = await advanceSetup(db, idrive, id, { now: later(30) });
    assert.equal(meanwhile.state.state, "setting_up");
    assert.equal(idrive.calls.length, 0);

    assert.equal((await advanceSetup(db, idrive, id, { now: later(90) })).state.state, "ready");
  });
});

test("a bucket that doesn't read back protected isn't called ready, and the service's key doesn't stay", async () => {
  const governance = (config) => ({ ...config, Rule: { DefaultRetention: { Mode: "GOVERNANCE", Days: LOCK_DAYS } } });
  await withHousehold(async ({ db, idrive, id }) => {
    const { state, household } = await quietly(() => advanceSetup(db, idrive, id, { now: NOW }));

    assert.equal(state.state, "failing");
    assert.equal(state.step, "bucket");
    assert.match(state.problem, /governance mode, not compliance/);
    assert.equal(household.bucket, null);
    assert.deepEqual(idrive.users.get(household.account_email).keys, []);
  }, { storesLock: governance });
});

test("a new key iDrive's storage servers don't know yet is waited for", async () => {
  await withHousehold(async ({ db, idrive, id }) => {
    assert.equal((await advanceSetup(db, idrive, id, { now: NOW })).state.state, "ready");
    assert.equal(idrive.count("ListBuckets"), 4);
  }, { keyDelay: 3 });
});

test("storage is found by its region's name or key, and only in that region", async () => {
  const reseller = { listUserRegions: async () => ({ user_regions: [{ region: "Oregon-2", user_dn: "s3.us-west-4.idrivee2.com" }] }) };
  assert.equal(await findStorage(reseller, "a@example", { region_key: "OR2", region_name: "Oregon-2" }), "s3.us-west-4.idrivee2.com");
  assert.equal(await findStorage(reseller, "a@example", { region_key: "VA", region_name: "Virginia" }), undefined);
  const byKey = { listUserRegions: async () => ({ user_regions: [{ region: "OR2", storage_dn: "s3.example" }] }) };
  assert.equal(await findStorage(byKey, "a@example", { region_key: "OR2", region_name: "Oregon-2" }), "s3.example");
});

test("a bucket's protection is read back and anything short of it is named", async () => {
  const good = {
    GetBucketVersioning: { Status: "Enabled" },
    GetObjectLockConfiguration: { ObjectLockConfiguration: { ObjectLockEnabled: "Enabled", Rule: { DefaultRetention: { Mode: "COMPLIANCE", Days: LOCK_DAYS } } } },
    GetBucketLifecycleConfiguration: { Rules: [{ ID: EXPIRY_RULE, Status: "Enabled", Filter: { Prefix: "" }, NoncurrentVersionExpiration: { NoncurrentDays: EXPIRE_DAYS }, Expiration: { ExpiredObjectDeleteMarker: true } }] },
  };
  const missing = (name) => Object.assign(new Error(name), { name, $metadata: { httpStatusCode: 404 } });
  const store = (changes) => ({
    send: async (command) => {
      const answer = { ...good, ...changes }[command.constructor.name.replace(/Command$/, "")];
      if (answer instanceof Error) throw answer;
      return structuredClone(answer);
    },
  });
  const rules = (...extra) => ({ Rules: [...good.GetBucketLifecycleConfiguration.Rules, ...extra] });

  assert.deepEqual(await inspectBucket(store({}), "b"), []);
  assert.deepEqual(await inspectBucket(store({ GetBucketVersioning: { Status: "Suspended" } }), "b"), ["versioning is suspended"]);
  assert.deepEqual(await inspectBucket(store({ GetObjectLockConfiguration: missing("ObjectLockConfigurationNotFoundError") }), "b"), ["object lock is off"]);
  assert.deepEqual(await inspectBucket(store({ GetObjectLockConfiguration: { ObjectLockConfiguration: { ObjectLockEnabled: "Enabled" } } }), "b"), ["nothing is locked by default"]);
  assert.deepEqual(await inspectBucket(store({ GetObjectLockConfiguration: { ObjectLockConfiguration: { ObjectLockEnabled: "Enabled", Rule: { DefaultRetention: { Mode: "COMPLIANCE", Years: 10 } } } } }), "b"),
    [`the default lock is 10 years, not ${LOCK_DAYS} days`]);
  assert.deepEqual(await inspectBucket(store({ GetBucketLifecycleConfiguration: missing("NoSuchLifecycleConfiguration") }), "b"),
    [`no rule clears old versions after ${EXPIRE_DAYS} days`]);
  assert.deepEqual(await inspectBucket(store({ GetBucketLifecycleConfiguration: { Rules: [{ ...good.GetBucketLifecycleConfiguration.Rules[0], Filter: { Prefix: "data/" } }] } }), "b"),
    [`no rule clears old versions after ${EXPIRE_DAYS} days`]);
  assert.deepEqual(await inspectBucket(store({ GetBucketLifecycleConfiguration: rules({ ID: "x", Status: "Enabled", Filter: { Prefix: "" }, Expiration: { Days: 1 } }) }), "b"),
    ["a rule expires current objects"]);
  assert.deepEqual(await inspectBucket(store({ GetBucketLifecycleConfiguration: rules({ ID: "x", Status: "Disabled", Filter: { Prefix: "" }, Expiration: { Days: 1 } }) }), "b"), []);
});

test("a host gets a key of its own to its one bucket, and asking again replaces it", async () => {
  await withHousehold(async ({ db, idrive, id, joined }) => {
    const deps = { db, idrive, env: {}, now: NOW };
    const request = () => asking("/api/host/storage", { method: "POST", token: joined.credential });

    const answer = await hostStorageRoute(request(), deps);

    assert.equal(answer.status, 201);
    assert.equal(answer.headers.get("cache-control"), "no-store");
    const { storage } = await answer.json();
    assert.deepEqual({ ...storage, accessKeyId: undefined, secretAccessKey: undefined }, {
      endpoint: "https://s3.or2.example", region: "us-east-1", bucket: `uncloud-${id}`, accessKeyId: undefined, secretAccessKey: undefined,
    });
    const keys = () => idrive.users.get(`household-${id}@uncloud.life`).keys;
    const [key] = keys();
    assert.equal(key.key_id, storage.accessKeyId);
    assert.equal(key.secret, storage.secretAccessKey);
    assert.equal(key.name, `host-${joined.host.id}`);
    assert.deepEqual(key.options, { permissions: 2, buckets: [`uncloud-${id}`], disable_delete_bucket: true, bypass_governance_retention: false });
    const { rows: [host] } = await db.query("select key_id, key_issued_at from hosts");
    assert.equal(host.key_id, storage.accessKeyId);
    const { rows } = await db.query("select * from hosts, households");
    assert.ok(!JSON.stringify(rows).includes(storage.secretAccessKey), "the secret is the host's alone");

    const again = await (await hostStorageRoute(request(), { ...deps, now: later(60) })).json();
    assert.notEqual(again.storage.accessKeyId, storage.accessKeyId);
    assert.deepEqual(keys().map((k) => k.key_id), [again.storage.accessKeyId], "the old key is gone");
  });
});

test("a host whose household isn't set up yet is told to ask again, and not why it's stuck", async () => {
  await withHousehold(async ({ db, idrive, joined }) => {
    const deps = { db, idrive, env: {}, now: NOW };
    const request = () => asking("/api/host/storage", { method: "POST", token: joined.credential });

    const waiting = await hostStorageRoute(request(), { ...deps, until: Date.now() + 20_000 });
    assert.equal(waiting.status, 202);
    assert.equal(waiting.headers.get("retry-after"), "5");
    assert.deepEqual(await waiting.json(), { setup: { state: "setting_up", step: "storage" } });

    idrive.failOnce("enableUserRegion");
    const failing = await quietly(() => hostStorageRoute(request(), { ...deps, now: later(10) }));
    assert.equal(failing.status, 202);
    assert.equal(failing.headers.get("retry-after"), "60");
    assert.deepEqual(await failing.json(), { setup: { state: "failing", step: "storage" } });

    assert.equal((await hostStorageRoute(request(), { ...deps, now: later(80) })).status, 201);
  });
});

test("the storage endpoint turns away a stranger, and says so where there's no iDrive", async () => {
  await withHousehold(async ({ db, idrive, joined }) => {
    const refused = (code) => (error) => error instanceof ServiceError && error.code === code;
    await assert.rejects(hostStorageRoute(asking("/api/host/storage", { method: "POST", token: "guess" }), { db, idrive, now: NOW }),
      refused("not_a_host"));
    await assert.rejects(hostStorageRoute(asking("/api/host/storage", { method: "POST", token: joined.credential }), { db, idrive: null, now: NOW }),
      refused("no_idrive"));
  });
});

test("the operator sees every household and why its setup is stuck, and nobody else does", async () => {
  await withHousehold(async ({ db, idrive, id }) => {
    const deps = { db, env: { OPERATOR_TOKEN: OPERATOR }, now: later(120) };
    idrive.failOnce("enableUserRegion");
    await quietly(() => advanceSetup(db, idrive, id, { now: NOW }));

    await assert.rejects(listHouseholdsRoute(asking("/api/operator/households", { token: "guess" }), deps),
      (error) => error instanceof ServiceError && error.code === "operator_only");
    const { households } = await (await listHouseholdsRoute(asking("/api/operator/households", { token: OPERATOR }), deps)).json();

    assert.deepEqual(households, [{
      id,
      createdAt: NOW.toISOString(),
      hosts: 1,
      account: `household-${id}@uncloud.life`,
      region: null,
      bucket: null,
      setup: {
        state: "failing",
        step: "storage",
        problem: "enableUserRegion: the connection dropped",
        failures: 1,
        triedAt: NOW.toISOString(),
      },
    }]);
  });
});
