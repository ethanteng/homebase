// Setting up a household's storage in IDrive e2 (docs/backup.md, "One account per household"): an
// account, storage in a region, and a bucket with object lock, each a step that's remembered once
// done. Then each host gets a key to that bucket alone.
//
// The reseller API's calls aren't transactional, and a step can succeed at iDrive while the function
// running it dies or loses the answer. So no step trusts the record alone: each first looks at what's
// already at iDrive, and only makes what's missing.

import { randomBytes } from "node:crypto";
import { CreateBucketCommand, ListBucketsCommand, ListObjectsV2Command } from "@aws-sdk/client-s3";
import { findStorage, findUser, freshKey, removeKeysNamed } from "./idrive.mjs";
import { inspectBucket, protectBucket } from "./protection.mjs";
import { listIn } from "./reseller.mjs";
import { endpointFor, errorLabel, SIGNING_REGION, untilWorks } from "./s3.mjs";

/**
 * Each household's quota, in GB, enforced: past it, the account turns read-only, so backups stop and
 * restores still work. A beta figure, until there's pricing.
 */
export const QUOTA_GB = 1000;

/**
 * How much of a function's time setting up may use. Vercel stops a function at 60 seconds
 * (vercel.json), and a step that hasn't time left to finish is left for the next request.
 */
export const BUDGET = 50_000;

/** Longer than any function runs, so a lease outlives whoever took it only if they died holding it. */
const LEASE_SECONDS = 75;

/** A host key: made, then waited on until e2's storage servers know it. */
const KEY_NEEDS = 20_000;

const STEPS = [
  { name: "account", needs: 5_000, done: (h) => h.account_email !== null, run: makeAccount },
  // enable_user_region took 6.7 to 44.4 seconds in the spike.
  { name: "storage", needs: 45_000, done: (h) => h.storage_dn !== null, run: giveStorage },
  // A key for the service, waited on, then the bucket made, protected and read back.
  { name: "bucket", needs: 30_000, done: (h) => h.bucket !== null, run: makeBucket },
];

export const bucketName = (householdId) => `uncloud-${householdId}`;

/**
 * Takes a household's setup as far as time allows, from wherever it stopped. Whoever is already
 * setting it up keeps it: this then only says how it stands. Returns the household's row and its
 * state as `describe` puts it.
 */
export async function advanceSetup(db, idrive, householdId, { now = new Date(), until = Date.now() + BUDGET } = {}) {
  const { rows: [claimed] } = await db.query(
    `update households set setup_lease_until = $2, setup_tried_at = $3
     where id = $1 and ready_at is null and (setup_lease_until is null or setup_lease_until < $3)
     returning *`,
    [householdId, new Date(now.getTime() + LEASE_SECONDS * 1000), now]);
  if (!claimed) {
    const household = await loadHousehold(db, householdId);
    return { household, state: household && describe(household, now) };
  }

  let household = claimed;
  try {
    for (const step of STEPS) {
      if (step.done(household)) continue;
      if (Date.now() + step.needs > until) break;
      household = await remember(db, household.id, await step.run(household, idrive));
    }
    if (STEPS.every((step) => step.done(household))) {
      household = await remember(db, household.id, { ready_at: now });
    }
  } catch (error) {
    console.error(`Setting up household ${householdId}:`, error);
    const { rows: [failed] } = await db.query(
      "update households set setup_failures = setup_failures + 1, setup_problem = $2 where id = $1 returning *",
      [householdId, problemIn(error)]);
    household = failed;
  } finally {
    await db.query("update households set setup_lease_until = null where id = $1", [householdId]);
  }
  household.setup_lease_until = null;
  return { household, state: describe(household, now) };
}

/** A household's setup in words: ready, or the step it's at and, if the last go failed, why. */
export function describe(household, now = new Date()) {
  if (household.ready_at) return { state: "ready", readyAt: iso(household.ready_at) };
  const step = STEPS.find((candidate) => !candidate.done(household))?.name ?? "finishing";
  const busy = household.setup_lease_until && new Date(household.setup_lease_until) > now;
  return {
    state: household.setup_problem && !busy ? "failing" : "setting_up",
    step,
    problem: household.setup_problem,
    failures: household.setup_failures,
    triedAt: household.setup_tried_at ? iso(household.setup_tried_at) : null,
  };
}

/**
 * Gives a host a key of its own to its household's bucket, and answers with everything it needs to
 * back up there. The secret is in that answer and nowhere else: any key the host had before is
 * removed, so asking again replaces it.
 *
 * Replacing a key is removing the old and making the new, so two requests at once could each
 * remove the other's. The host is held while it's done, and a request that finds it held gets
 * null, to ask again.
 */
export async function issueHostKey(db, idrive, household, host, { now = new Date() } = {}) {
  const { rows: [held] } = await db.query(
    "update hosts set key_lease_until = $2 where id = $1 and (key_lease_until is null or key_lease_until < $3) returning id",
    [host.id, new Date(now.getTime() + LEASE_SECONDS * 1000), now]);
  if (!held) return null;

  const account = { email: household.account_email, storageDn: household.storage_dn };
  let key;
  try {
    // Read and write, to this one bucket only, without deleting the bucket. No bypassing a lock,
    // which compliance mode refuses anyway. disable_delete_version doesn't stop a key destroying
    // versions, but it's what keeps it off the bucket's lifecycle rules: every spike key with it
    // was refused changing the expiry rule, and a key without it deleted the rule.
    key = await freshKey(idrive.reseller, account, `host-${host.id}`, {
      permissions: 2,
      buckets: [household.bucket],
      disable_delete_bucket: true,
      disable_delete_version: true,
      bypass_governance_retention: false,
    });
    const s3 = idrive.s3(household.storage_dn, key);
    await untilWorks(() => s3.send(new ListObjectsV2Command({ Bucket: household.bucket, MaxKeys: 1 })), idrive.patience);
    await db.query("update hosts set key_id = $2, key_issued_at = $3, key_lease_until = null where id = $1",
      [host.id, key.accessKeyId, now]);
  } catch (error) {
    await db.query("update hosts set key_lease_until = null where id = $1", [host.id]).catch(() => {});
    throw error;
  }
  return {
    endpoint: endpointFor(household.storage_dn),
    region: SIGNING_REGION,
    bucket: household.bucket,
    accessKeyId: key.accessKeyId,
    secretAccessKey: key.secretAccessKey,
  };
}

/** Whether there's time left in this request to give a host its key. */
export const timeForKey = (until) => Date.now() + KEY_NEEDS <= until;

export async function loadHousehold(db, householdId) {
  const { rows: [household] } = await db.query("select * from households where id = $1", [householdId]);
  return household;
}

// The steps. Each takes the household as it stands and returns what it made, to be remembered.

/** The account: its address comes from the household, so it can always be looked for first. */
async function makeAccount(household, { reseller, accountEmail }) {
  const email = accountEmail(household.id);
  if (!(await findUser(reseller, email))) {
    try {
      await reseller.createUser({
        email,
        password: unknowable(),
        first_name: "Uncloud",
        last_name: "Household",
        quota: QUOTA_GB,
        enforce_quota: true,
        email_notification: false,
      });
    } catch (error) {
      // Made after all, by a go whose answer was lost, or this one's: either way it's there.
      if (!(await findUser(reseller, email))) throw error;
    }
  }
  return { account_email: email };
}

/**
 * An account's password: nobody signs in to the account, so nobody learns it, and it's kept nowhere.
 * Upper and lower case, digits and a symbol, in case iDrive has rules.
 */
function unknowable() {
  return ["Ub-", randomBytes(24).toString("base64url"), "9"].join("");
}

/** Storage in the region: list_user_regions first, since enabling it twice is an error. */
async function giveStorage(household, { reseller, region: key }) {
  const email = household.account_email;
  const region = listIn(await reseller.regions(), "regions").find((candidate) => candidate.region_key === key);
  if (!region) throw new Error(`iDrive has no region ${key}.`);
  if (region.active === false) throw new Error(`iDrive's region ${key} isn't active.`);

  let storageDn = await findStorage(reseller, email, region);
  if (!storageDn) {
    try {
      storageDn = (await reseller.enableUserRegion(email, key))?.storage_dn;
    } catch (error) {
      if (!(storageDn = await findStorage(reseller, email, region))) throw error;
    }
    storageDn ||= await findStorage(reseller, email, region);
  }
  if (!storageDn) throw new Error(`iDrive gave the account storage in ${key} but didn't say where.`);
  return { region: key, storage_dn: storageDn };
}

/**
 * The bucket, with object lock, protected and read back, made with a key the service holds only for
 * as long as this takes. A key left by a go that died is removed by the next, by its name.
 */
async function makeBucket(household, idrive) {
  const account = { email: household.account_email, storageDn: household.storage_dn };
  const name = `service-${household.id}`;
  const bucket = bucketName(household.id);
  const key = await freshKey(idrive.reseller, account, name, { permissions: 2 });
  try {
    const s3 = idrive.s3(household.storage_dn, key);
    await untilWorks(() => s3.send(new ListBucketsCommand({})), idrive.patience);
    try {
      await s3.send(new CreateBucketCommand({ Bucket: bucket, ObjectLockEnabledForBucket: true }));
    } catch (error) {
      // Made by a go that didn't get to remember it.
      if (error?.name !== "BucketAlreadyOwnedByYou") throw error;
    }
    await protectBucket(s3, bucket);
    const problems = await inspectBucket(s3, bucket);
    if (problems.length) throw new Error(`${bucket} doesn't read back protected: ${problems.join("; ")}.`);
  } catch (error) {
    await removeKeysNamed(idrive.reseller, account, name)
      .catch((also) => console.error(`Removing ${name}, which the next go will:`, also));
    throw error;
  }
  // Not done until the key is gone: a full-access key nobody needs mustn't outlast a setup that
  // has finished, when there's no next go to remove it.
  await removeKeysNamed(idrive.reseller, account, name);
  return { bucket };
}

/**
 * Writes what a step made, and clears the last failure, since the setup has moved on. Only these
 * columns are ever written, whatever a step returns.
 */
async function remember(db, householdId, made) {
  const { rows: [household] } = await db.query(
    `update households set
       account_email = coalesce($2, account_email),
       region = coalesce($3, region),
       storage_dn = coalesce($4, storage_dn),
       bucket = coalesce($5, bucket),
       ready_at = coalesce($6, ready_at),
       setup_problem = null
     where id = $1 returning *`,
    [householdId, made.account_email ?? null, made.region ?? null, made.storage_dn ?? null, made.bucket ?? null,
      made.ready_at ?? null]);
  return household;
}

/** What went wrong, for the operator: what iDrive or the store said, never more than a line or two. */
function problemIn(error) {
  const said = error?.$metadata ? errorLabel(error) : error?.message ?? String(error);
  return said.slice(0, 500);
}

const iso = (value) => new Date(value).toISOString();
