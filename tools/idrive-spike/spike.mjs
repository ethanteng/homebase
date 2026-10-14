// Phase 0 of Uncloud Backup: everything a household's backup would go through, done once against
// IDrive e2 for real before anything is built on it.
//
// It makes one throwaway e2 account through the reseller API and gives it a bucket. It backs a
// made-up library up into that bucket with restic, using the kind of key a host would hold. Then
// it tries what the design depends on: that the key can't reach past its bucket, weaken the
// bucket's protection or destroy history, that history can be rolled back, that old versions are
// set to expire, how usage is reported, how soon a revoked key stops working, and how an account
// is removed. What it finds goes in runs/<id>/report.md.
//
// README.md says how to run it.

import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import {
  CreateBucketCommand,
  DeleteBucketCommand,
  DeleteBucketLifecycleCommand,
  DeleteObjectCommand,
  GetBucketLifecycleConfigurationCommand,
  GetBucketVersioningCommand,
  GetObjectLockConfigurationCommand,
  HeadObjectCommand,
  ListBucketsCommand,
  ListObjectsV2Command,
  PutBucketLifecycleConfigurationCommand,
  PutBucketVersioningCommand,
  PutObjectCommand,
  PutObjectLockConfigurationCommand,
} from "@aws-sdk/client-s3";
import { createReseller, findStorageDn, listIn } from "./reseller.mjs";
import {
  allVersions,
  attempt,
  emptyBucket,
  endpointFor,
  errorLabel,
  s3Client,
  secondsUntilRefused,
  secondsUntilWorks,
  tally,
} from "./s3.mjs";
import { changeLibrary, compare, describe, fingerprint, makeFiller, makeLibrary, MB } from "./library.mjs";
import { createRestic, resticVersion, tail } from "./restic.mjs";
import { Recorder, shapeOf } from "./record.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
// How long object lock keeps a version, with --object-lock. Short, since every version stays billed
// that long, and governance mode, so the cleanup can still get past it.
const RETENTION_DAYS = 1;
// How long after a version is replaced or deleted the expiry rule removes it. Under object lock,
// not before its retention ends either.
const EXPIRE_DAYS = 1;
const EXPIRY_RULE = "uncloud-expire-old-versions";
const RUNS = join(HERE, "runs");
// What the reseller API calls an email it has no account for, spelling and all.
const GONE = "account_non_existant";

try {
  process.loadEnvFile(join(HERE, ".env"));
} catch {
  // No .env beside the script: the environment it was started with is all there is.
}

const HELP = `Usage: node spike.mjs <command>

  regions               List e2's regions. Read-only: a safe first check of the token.
  run                   Make a test user, put it through everything, then remove it.
    --size-mb <n>         How big a library to back up (default 200).
    --keep                Leave the test user in place afterwards, to look again later.
    --quota-test          Also push the user over its quota (adds ~1.1 GB; implies --keep).
    --object-lock         Protect versions with object lock (governance mode, one day) as well.
  stats <run id>        Look at a kept run's usage again, and whether it still takes writes.
  cleanup <run id>      Remove a run's test user and everything it stored.

Settings come from the environment or from .env beside this script: see example.env.`;

await main().catch((error) => {
  if (!error?.recorded) console.error(`\n${error?.message ?? error}`);
  process.exitCode = 1;
});

async function main() {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: {
      keep: { type: "boolean", default: false },
      "size-mb": { type: "string", default: "200" },
      "quota-test": { type: "boolean", default: false },
      "object-lock": { type: "boolean", default: false },
      help: { type: "boolean", short: "h", default: false },
    },
  });
  const [command, id] = positionals;
  if (values.help || !command) return console.log(HELP);
  switch (command) {
    case "regions":
      return listRegions();
    case "run":
      return run({
        sizeMb: Number(values["size-mb"]),
        quotaTest: values["quota-test"],
        objectLock: values["object-lock"],
        keep: values.keep || values["quota-test"],
      });
    case "stats":
      return stats(need(id));
    case "cleanup":
      return cleanupRun(need(id));
    default:
      throw new Error(`There's no command called ${command}. Try --help.`);
  }
}

function need(id) {
  if (!id) throw new Error("Which run? Give its id, the folder name under runs/.");
  return id;
}

function settings() {
  const token = process.env.IDRIVE_RESELLER_TOKEN;
  if (!token) throw new Error("Set IDRIVE_RESELLER_TOKEN, in the environment or in .env beside this script.");
  return {
    token,
    email: process.env.SPIKE_EMAIL,
    region: process.env.IDRIVE_REGION || undefined,
    s3Region: process.env.IDRIVE_S3_REGION || "us-east-1",
    restic: process.env.RESTIC_BINARY || "restic",
  };
}

async function listRegions() {
  const reseller = createReseller({ token: settings().token });
  const regions = listIn(await reseller.regions(), "regions");
  console.table(regions.map((r) => ({ key: r.region_key, name: r.region_name, country: r.country, active: r.active })));
}

async function run({ sizeMb, quotaTest, objectLock, keep }) {
  const config = settings();
  if (!config.email?.includes("{id}")) {
    throw new Error('Set SPIKE_EMAIL to an address with {id} in it, like "spike-{id}@example.com", so every run makes a user nobody has used.');
  }
  if (!(sizeMb >= 5)) throw new Error("--size-mb has to be 5 or more.");
  // Before anything is made, so a missing restic costs nothing.
  const version = await resticVersion(config.restic);

  const id = runId();
  const dir = join(RUNS, id);
  mkdirSync(dir, { recursive: true });
  const state = {
    id,
    email: config.email.replaceAll("{id}", id),
    buckets: { main: `uncloud-spike-${id}`, other: `uncloud-spike-${id}-other` },
    s3Region: config.s3Region,
    objectLock,
    started: new Date().toISOString(),
  };
  const save = () => writeFileSync(join(dir, "state.json"), JSON.stringify(state, null, 2));
  save();

  const record = new Recorder(dir, `iDrive e2 spike, run ${id}`);
  record.fact("Started", state.started);
  record.fact("Test user", state.email);
  record.fact("restic", version.split("\n")[0]);
  record.fact("Protecting versions", objectLock
    ? `object lock, governance mode, ${RETENTION_DAYS} day, and a host key without bypass_governance_retention or version deletion`
    : "a host key without version deletion (disable_delete_version)");
  const reseller = createReseller({ token: config.token, onCall: (call) => record.call(call) });
  const data = join(dir, "data");
  const library = join(data, "library");
  const { main, other } = state.buckets;

  const leftBehind = () => console.log(`\nThe test user is still there. To remove it:\n  node spike.mjs cleanup ${id}`);
  // Ctrl-C is a run stopping early like any other: restic and any waiting stop, no further step
  // starts, and the test user is removed unless --keep. A second Ctrl-C leaves straight away.
  const stopping = new AbortController();
  record.signal = stopping.signal;
  const interrupt = () => {
    if (stopping.signal.aborted) {
      record.write();
      if (state.userCreated && !state.removed) leftBehind();
      process.exit(130);
    }
    console.log(keep || !state.userCreated
      ? "\nStopping."
      : "\nStopping, and removing the test user first. Press Ctrl-C again to leave it.");
    stopping.abort();
  };
  process.on("SIGINT", interrupt);
  const signal = stopping.signal;
  console.log(`Run ${id}, test user ${state.email}`);

  let admin;
  let host;
  let restic;
  let snapshot;
  let expected;
  let wiped;
  try {
    await record.step("Pick a region", async () => {
      const regions = listIn(await reseller.regions(), "regions");
      const active = regions.filter((r) => r.active);
      const region = config.region ? regions.find((r) => r.region_key === config.region) : active[0];
      if (!region) throw new Error(config.region ? `There's no region ${config.region}.` : "No region is active.");
      if (!region.active) throw new Error(`${region.region_key} isn't active.`);
      state.region = region.region_key;
      save();
      record.fact("Region", `${region.region_name} (${region.region_key})`);
      return `${region.region_name} (${region.region_key}), one of ${active.length} active regions`;
    });

    await record.step("Create the test user", async () => {
      await reseller.createUser({
        email: state.email,
        password: password(),
        first_name: "Uncloud",
        last_name: "Spike",
        quota: 5,
        email_notification: false,
        enforce_quota: false,
      });
      state.userCreated = true;
      save();
      const user = await findUser(reseller, state.email);
      if (!user) return { outcome: "fail", detail: "Created, but /users doesn't list it." };
      return `Created. /users says is_signed_up ${user.is_signed_up}, is_managed ${user.is_managed}, quota ${user.storage_quota} GB.`;
    });

    await record.step("Give the user storage in the region", async () => {
      const answer = await reseller.enableUserRegion(state.email, state.region);
      const listed = await reseller.listUserRegions(state.email);
      state.storageDn = answer?.storage_dn ?? findStorageDn(listed, state.region);
      if (!state.storageDn) throw new Error(`No storage_dn came back. list_user_regions answered ${shapeOf(listed)}`);
      state.endpoint = endpointFor(state.storageDn);
      save();
      record.fact("Endpoint", state.endpoint);
      return `Endpoint ${state.endpoint}. list_user_regions answers ${shapeOf(listed)}.`;
    });

    await record.step("Make a full-access key, as the service would hold", async () => {
      admin = await newKey(reseller, state, "spike-admin", {
        permissions: 2,
        ...(objectLock ? { bypass_governance_retention: true } : {}),
      });
      const seconds = await secondsUntilWorks(() => admin.s3.send(new ListBucketsCommand({})), {
        signal,
        onError: (error) => adoptRegion(error, state, admin, record),
      });
      save();
      return `Works after ${seconds}s.`;
    });

    await record.step("Create the bucket with versioning on, and a second one", async () => {
      const locked = await makeMainBucket(admin, state);
      await admin.s3.send(new CreateBucketCommand({ Bucket: other }));
      state.bucketsCreated = true;
      save();
      const { Status } = await admin.s3.send(new GetBucketVersioningCommand({ Bucket: main }));
      if (Status !== "Enabled") return { outcome: "fail", detail: `Versioning reads back as ${Status ?? "nothing"}.` };
      return `${main}, versioning on${locked ? `, ${locked}` : ""}. ${other} is there for the host's key not to reach.`;
    });

    await record.step("Set a rule that expires old versions, with the service's key", async () => {
      // If iDrive won't take the whole rule, one for old versions alone is the part that matters.
      let set = "the whole rule";
      try {
        await setExpiry(admin, main);
      } catch (error) {
        set = `a rule for old versions alone, after the whole rule was turned down (${errorLabel(error)})`;
        await admin.s3.send(new PutBucketLifecycleConfigurationCommand({
          Bucket: main,
          LifecycleConfiguration: {
            Rules: [{ ID: EXPIRY_RULE, Status: "Enabled", Filter: { Prefix: "" }, NoncurrentVersionExpiration: { NoncurrentDays: EXPIRE_DAYS } }],
          },
        }));
      }
      const rules = await readExpiry(admin, main);
      state.expiry = rules.length > 0;
      save();
      if (!rules.length) return { outcome: "fail", detail: `Accepted ${set}, but it reads back as no rule at all.` };
      return {
        outcome: rules.some((rule) => rule.NoncurrentVersionExpiration?.NoncurrentDays) ? "pass" : "fail",
        detail: `Set ${set}. It reads back as: ${rules.map(describeRule).join("; ")}. Whether iDrive acts on it shows in "stats" over the next few days.`,
      };
    }, { carryOn: true });

    await record.step("Make the host's key: one bucket, no deleting versions or the bucket", async () => {
      host = await newKey(reseller, state, "spike-host", {
        permissions: 2,
        buckets: [main],
        disable_delete_bucket: true,
        disable_delete_version: true,
        bypass_governance_retention: false,
      });
      const seconds = await secondsUntilWorks(
        () => host.s3.send(new ListObjectsV2Command({ Bucket: main, MaxKeys: 1 })), { signal });
      const keys = await reseller.listAccessKeys(state.email, state.storageDn);
      return `Works after ${seconds}s. list_access_keys answers ${shapeOf(keys)}.`;
    });

    await record.step("The host's key can't reach past its bucket", async () => {
      const probes = {
        "list the other bucket": () => host.s3.send(new ListObjectsV2Command({ Bucket: other, MaxKeys: 1 })),
        "write to the other bucket": () => host.s3.send(new PutObjectCommand({ Bucket: other, Key: "probe.txt", Body: "probe" })),
        "create a bucket": () => host.s3.send(new CreateBucketCommand({ Bucket: `${main}-x` })),
        "delete its own bucket": () => host.s3.send(new DeleteBucketCommand({ Bucket: main })),
      };
      const said = [];
      const allowed = [];
      // Only a deliberate no from the store counts. A probe that failed some other way didn't show
      // the key was stopped, so it fails the step rather than passing it.
      const unclear = [];
      for (const [what, action] of Object.entries(probes)) {
        const result = await attempt(action);
        if (result.allowed) {
          allowed.push(what);
          said.push(`${what}: ALLOWED`);
        } else if (result.refused) {
          said.push(`${what}: refused, ${result.error}`);
        } else {
          unclear.push(what);
          said.push(`${what}: neither allowed nor refused, ${result.error}`);
        }
      }
      // Undo whatever a probe managed, so the rest of the run starts where it should.
      if (allowed.includes("create a bucket")) await admin.s3.send(new DeleteBucketCommand({ Bucket: `${main}-x` }));
      if (allowed.includes("delete its own bucket")) {
        await makeMainBucket(admin, state);
      }
      try {
        const { Buckets = [] } = await host.s3.send(new ListBucketsCommand({}));
        said.push(`listing buckets shows ${Buckets.length}`);
      } catch (error) {
        said.push(`listing buckets: refused, ${errorLabel(error)}`);
      }
      return { outcome: allowed.length || unclear.length ? "fail" : "pass", detail: `${said.join("; ")}.` };
    });

    await record.step("The host's key can't weaken the bucket's protection", async () => {
      const said = [];
      const weakened = [];
      const unclear = [];
      // Each try is judged by reading the setting back with the service's key, since a store can
      // answer either way and do the other, and is put right if the host managed it.
      async function tryTo(what, action, intact, putBack) {
        const result = await attempt(action);
        if (result.passing) {
          unclear.push(what);
          said.push(`${what}: couldn't reach the store, ${result.error}`);
        } else if (await intact()) {
          said.push(`${what}: ${result.allowed ? "accepted, yet nothing changed" : `turned down, ${result.error}`}`);
        } else {
          weakened.push(what);
          said.push(`${what}: DONE`);
          await putBack();
        }
      }
      await tryTo("suspend versioning",
        () => host.s3.send(new PutBucketVersioningCommand({ Bucket: main, VersioningConfiguration: { Status: "Suspended" } })),
        async () => (await admin.s3.send(new GetBucketVersioningCommand({ Bucket: main }))).Status === "Enabled",
        () => turnOnVersioning(admin, main));
      if (state.expiry) {
        await tryTo("replace the expiry rule",
          () => setExpiry(host, main, "host-was-here"),
          async () => !(await readExpiry(admin, main)).some((rule) => rule.ID === "host-was-here"),
          () => setExpiry(admin, main));
        await tryTo("delete the expiry rule",
          () => host.s3.send(new DeleteBucketLifecycleCommand({ Bucket: main })),
          async () => (await readExpiry(admin, main)).length > 0,
          () => setExpiry(admin, main));
      } else {
        said.push("the expiry rule: not set, so not tried");
      }
      if (objectLock) {
        await tryTo("change object lock's retention",
          () => setLock(host, main, RETENTION_DAYS + 1),
          async () => (await readLock(admin, main))?.Days === RETENTION_DAYS,
          () => setLock(admin, main, RETENTION_DAYS));
      }
      return { outcome: weakened.length || unclear.length ? "fail" : "pass", detail: `${said.join("; ")}.` };
    }, { carryOn: true });

    restic = createRestic({
      binary: config.restic,
      repository: `s3:${state.endpoint}/${main}/restic`,
      password: randomBytes(24).toString("base64url"),
      ...host.credentials,
      region: state.s3Region,
      signal,
    });

    await record.step("Back up a made-up library with the host's key", async () => {
      const made = makeLibrary(library, sizeMb);
      record.fact("Library", `${made.files} files, ${mb(made.bytes)}`);
      await restic.run(["init"]);
      const first = await restic.backup(library);
      return `${made.files} files, ${mb(made.bytes)}: ${mb(first.uploaded)} uploaded in ${first.seconds.toFixed(0)}s, ${rate(first.uploaded, first.seconds)}.`;
    });

    await record.step("Change the library and back up again", async () => {
      const changed = changeLibrary(library);
      const second = await restic.backup(library);
      snapshot = second.snapshot_id;
      expected = await fingerprint(library);
      return `${changed.removed} files deleted, ${changed.edited} edited, ${changed.added} added: ${mb(second.uploaded)} uploaded in ${second.seconds.toFixed(0)}s.`;
    });

    await record.step("Prune the first backup with the host's key", async () => {
      await restic.run(["forget", "--keep-last", "1", "--prune"]);
      const held = tally(await allVersions(admin.s3, main));
      return `Pruned. The bucket shows ${held.current} objects (${mb(held.currentBytes)}); versioning keeps ${held.older} older versions (${mb(held.olderBytes)}) and ${held.markers} delete markers. Lock files left behind: ${held.locks}.`;
    }, { carryOn: true });

    await record.step("Check every byte, restore, and compare", async () => {
      await restic.run(["check", "--read-data"]);
      const target = join(data, "restore 1");
      await restic.run(["restore", snapshot, "--target", target]);
      // restic restores a backed-up path in full beneath the target.
      const difference = compare(expected, await fingerprint(join(target, library)));
      return { outcome: difference.same ? "pass" : "fail", detail: describe(difference) };
    }, { carryOn: true });

    await record.step("The host's key tries to wipe the bucket", async () => {
      const before = await allVersions(admin.s3, main);
      const current = before.versions.filter((v) => v.IsLatest);
      let hidden = 0;
      let accepted = 0;
      // How the store turned deletes down. Listing the bucket afterwards is what says whether
      // anything was destroyed, so any answer at all is enough here...
      const declined = [];
      // ...but not trouble reaching the store, which leaves a version standing without showing
      // anything about the key.
      const unclear = [];
      for (const v of current) {
        const result = await attempt(() => host.s3.send(new DeleteObjectCommand({ Bucket: main, Key: v.Key })));
        if (result.allowed) hidden++;
        else if (result.passing) unclear.push(result.error);
      }
      for (const v of before.versions) {
        const result = await attempt(() => host.s3.send(new DeleteObjectCommand({ Bucket: main, Key: v.Key, VersionId: v.VersionId })));
        if (result.allowed) accepted++;
        else if (result.passing) unclear.push(result.error);
        else declined.push(result.error);
      }
      // A store can answer yes and keep the version anyway, so what counts is what's left.
      const left = new Set((await allVersions(admin.s3, main)).versions.map((v) => `${v.Key}\n${v.VersionId}`));
      const destroyed = before.versions.filter((v) => !left.has(`${v.Key}\n${v.VersionId}`)).length;
      wiped = new Set(current.map((v) => v.Key));
      // What the host's key sees now, asked of the store directly rather than through restic.
      const { KeyCount = 0 } = await host.s3.send(new ListObjectsV2Command({ Bucket: main }));
      const config = current.find((v) => v.Key.endsWith("/config"));
      const read = config
        ? await attempt(() => host.s3.send(new HeadObjectCommand({ Bucket: main, Key: config.Key })), { tries: 1 })
        : undefined;
      const seen = `The host's key now sees ${KeyCount} objects${read
        ? `, and reading restic's hidden config gets ${read.allowed ? "it back" : read.error}`
        : ""}.`;
      // restic retries an error it doesn't recognise for many minutes before giving up; a minute
      // shows which kind this is.
      const after = await restic.run(["snapshots"], { allowFailure: true, timeout: 60_000 });
      const opened = after.timedOut
        ? `restic was still retrying a minute later (${tail(after.stderr, 2)})`
        : after.code === 0
          ? "restic still opens the repository"
          : `restic can't open the repository (exit ${after.code}: ${tail(after.stderr, 1)})`;
      const doubt = unclear.length
        ? ` ${unclear.length} attempts couldn't reach the store (${[...new Set(unclear)].join(", ")}), so this proves less than it should.`
        : "";
      const versions = before.versions.length;
      const how = declined.length ? ` (${[...new Set(declined)].join("; ")})` : "";
      const verdict = destroyed
        ? `DESTROYED ${destroyed} of ${versions} versions (${accepted} deletes accepted, ${declined.length} turned down${how}).`
        : accepted
          ? `Accepted ${accepted} of ${versions} deletes of a version, yet every version is still there.`
          : `Every version is still there: the store turned down all ${declined.length} deletes${how}.`;
      return {
        outcome: destroyed === 0 && unclear.length === 0 ? "pass" : "fail",
        detail: `Hid ${hidden} of ${current.length} objects behind delete markers. ${verdict} ${seen} Afterwards ${opened}.${doubt}`,
      };
    }, { carryOn: true });

    await record.step("Roll the bucket back with the service's key, then restore", async () => {
      if (!wiped) return { outcome: "fail", detail: "Nothing to roll back: the wipe didn't run." };
      // Taking the marker off each object the wipe hid puts the bucket back as it was just before.
      const { markers } = await allVersions(admin.s3, main);
      const lifting = markers.filter((m) => m.IsLatest && wiped.has(m.Key));
      for (const m of lifting) {
        await admin.s3.send(new DeleteObjectCommand({ Bucket: main, Key: m.Key, VersionId: m.VersionId }));
      }
      // Anything whose versions the wipe destroyed can't come back, and restic would retry for
      // many minutes over each one, so check first.
      const back = new Set((await allVersions(admin.s3, main)).versions.filter((v) => v.IsLatest).map((v) => v.Key));
      const lost = [...wiped].filter((key) => !back.has(key));
      if (lost.length) {
        return {
          outcome: "fail",
          detail: `Took ${lifting.length} delete markers off, but ${lost.length} of ${wiped.size} objects can't come back: their versions are gone. Restoring skipped.`,
        };
      }
      await restic.run(["check", "--read-data"]);
      const target = join(data, "restore 2");
      await restic.run(["restore", snapshot, "--target", target]);
      const difference = compare(expected, await fingerprint(join(target, library)));
      return {
        outcome: difference.same ? "pass" : "fail",
        detail: `Took ${lifting.length} delete markers off. ${describe(difference)}`,
      };
    }, { carryOn: true });

    await record.step("Usage, as iDrive reports it and as the bucket holds it", () =>
      usage(reseller, state, admin.s3), { carryOn: true });

    if (quotaTest) {
      await record.step("Go over quota: does the account turn read-only, and can it still restore?", async () => {
        const filler = join(data, "filler");
        makeFiller(filler, 1100);
        await restic.backup(filler);
        await reseller.editUser({ email: state.email, quota: 1, enforce_quota: true });
        state.quotaTest = true;
        save();
        const user = await findUser(reseller, state.email);
        const write = await attempt(() =>
          host.s3.send(new PutObjectCommand({ Bucket: main, Key: "probe/over-quota.txt", Body: "probe", ChecksumAlgorithm: "CRC32" })));
        // Restoring with --no-lock, because taking a lock is itself a write.
        const one = `${library}/documents/new/added 0.txt`;
        const read = await restic.run(
          ["--no-lock", "restore", snapshot, "--target", join(data, "restore 3"), "--include", one],
          { allowFailure: true });
        return {
          outcome: "note",
          detail: `Quota set to 1 GB with about 1.3 GB stored. /users says read_only_mode ${user?.read_only_mode}. A new write was ${answered(write)}. Restoring a file without a lock ${read.code === 0 ? "worked" : `failed (exit ${read.code})`}. iDrive says usage can take up to 24 hours to count, so run "node spike.mjs stats ${id}" later.`,
        };
      }, { carryOn: true });
    }

    await record.step("Revoke the host's key: how soon does it stop working?", async () => {
      await reseller.removeAccessKey(state.email, state.storageDn, host.accessKey);
      const refused = await secondsUntilRefused(
        () => host.s3.send(new ListObjectsV2Command({ Bucket: main, MaxKeys: 1 })), { signal });
      const trouble = refused.trouble ? ` Other errors on the way, not counted: ${refused.trouble}.` : "";
      return refused.seconds === null
        ? { outcome: "fail", detail: `Not refused five minutes after it was revoked.${trouble}` }
        : `Refused after ${refused.seconds}s, ${refused.error}.${trouble}`;
    }, { carryOn: true });

    if (keep) {
      record.fact("Kept", `The test user is still there. "node spike.mjs stats ${id}" looks again; "node spike.mjs cleanup ${id}" removes it.`);
    } else {
      await removeUser(record, reseller, state, save, admin);
    }
  } catch (error) {
    if (signal.aborted) record.fact("Stopped", "With Ctrl-C, before the run finished.");
    else if (!error?.recorded) console.error(error);
    if (!keep && state.userCreated && !state.removed) {
      console.log("\nStopped early. Removing the test user anyway.");
      // Removing it has to go ahead even though the run was told to stop.
      record.signal = null;
      await removeUser(record, reseller, state, save, admin).catch(() => {});
    }
  } finally {
    process.removeListener("SIGINT", interrupt);
    record.write();
    await record.close();
    if (state.userCreated && !state.removed && !keep) leftBehind();
    if (signal.aborted) process.exitCode = 130;
    if (record.troubled) {
      console.log(`\nThe test files are left in ${data}, for a look.`);
      process.exitCode = 1;
    } else {
      rmSync(data, { recursive: true, force: true });
    }
    console.log(`\nReport: ${join(dir, "report.md")}`);
  }
}

/** Looks at a kept run again: usage hours later, and whether a quota has made it read-only yet. */
async function stats(id) {
  const { dir, state } = load(id);
  const config = settings();
  const record = new Recorder(dir, `iDrive e2 spike, run ${id}, looked at again ${new Date().toISOString()}`, `stats ${stamp()}.md`);
  const reseller = createReseller({ token: config.token, onCall: (call) => record.call(call) });
  let key;
  try {
    await record.step("Make a short-lived key to look with", async () => {
      key = await newKey(reseller, state, "spike-stats", { permissions: 2, buckets: [state.buckets.main] });
      return `Works after ${await secondsUntilWorks(() =>
        key.s3.send(new ListObjectsV2Command({ Bucket: state.buckets.main, MaxKeys: 1 })))}s.`;
    });
    await record.step("Usage, as iDrive reports it and as the bucket holds it", () =>
      usage(reseller, state, key.s3), { carryOn: true });
    await record.step("Does the account still take writes?", async () => {
      const write = await attempt(() =>
        key.s3.send(new PutObjectCommand({ Bucket: state.buckets.main, Key: `probe/${stamp()}.txt`, Body: "probe", ChecksumAlgorithm: "CRC32" })));
      return { outcome: "note", detail: `A new object was ${answered(write)}.` };
    }, { carryOn: true });
    await record.step("Remove the short-lived key", async () => {
      await reseller.removeAccessKey(state.email, state.storageDn, key.accessKey);
      return "Removed.";
    }, { carryOn: true });
  } finally {
    record.write();
    await record.close();
    console.log(`\nReport: ${join(dir, record.file)}`);
  }
}

async function cleanupRun(id) {
  const { dir, state, save } = load(id);
  if (!state.userCreated) return console.log("This run never made a user, so there's nothing to remove.");
  if (state.removed) return console.log(`${state.email} was already removed.`);
  const config = settings();
  const record = new Recorder(dir, `iDrive e2 spike, run ${id}, removed`, "cleanup.md");
  const reseller = createReseller({ token: config.token, onCall: (call) => record.call(call) });
  try {
    await removeUser(record, reseller, state, save);
  } finally {
    record.write();
    await record.close();
    console.log(`\nReport: ${join(dir, record.file)}`);
  }
}

/**
 * Removes the test user. First the way cancelling a subscription would want to, with the data
 * still there; if iDrive won't, by emptying the buckets first, and the report says which it took.
 */
async function removeUser(record, reseller, state, save, admin) {
  const direct = await record.step("Remove the user with its data still stored", async () => {
    try {
      await disable(reseller, state.email);
      await reseller.removeUser(state.email);
      return "Removed: disable_user, then remove_user, with the data in place.";
    } catch (error) {
      if (error.code === GONE) return "Already gone: iDrive has no account with this email.";
      return { outcome: "note", detail: `Refused: ${error.message}. Emptying it first instead.` };
    }
  });
  if (direct === "note") {
    await record.step("Empty the buckets, then remove the user", async () => {
      await enable(reseller, state.email);
      if (state.storageDn) {
        let key = admin;
        if (!key) {
          key = await newKey(reseller, state, "spike-cleanup", {
            permissions: 2,
            ...(state.objectLock ? { bypass_governance_retention: true } : {}),
          });
          await secondsUntilWorks(() => key.s3.send(new ListBucketsCommand({})));
        }
        for (const bucket of Object.values(state.buckets)) {
          // A bucket the run never got as far as making.
          if (!(await attempt(() => key.s3.send(new ListObjectsV2Command({ Bucket: bucket, MaxKeys: 1 })))).allowed) continue;
          await emptyBucket(key.s3, bucket, { bypass: Boolean(state.objectLock) && bucket === state.buckets.main });
          await key.s3.send(new DeleteBucketCommand({ Bucket: bucket }));
        }
      }
      await disable(reseller, state.email);
      await reseller.removeUser(state.email);
      return "Removed, after deleting every version and both buckets.";
    });
  }
  await record.step("The user is gone", async () => {
    const user = await findUser(reseller, state.email);
    state.removed = !user;
    save();
    return user ? { outcome: "fail", detail: "/users still lists it." } : "/users doesn't list it any more.";
  });
}

/** Usage three ways: what the reseller API says, and what the bucket really holds. */
async function usage(reseller, state, s3) {
  const said = [];
  const user = await findUser(reseller, state.email);
  said.push(user
    ? `/users: storage_used ${mb(user.storage_used)}, quota ${user.storage_quota} GB, enforce_quota ${user.enforce_quota}, read_only_mode ${user.read_only_mode}`
    : "/users: not listed");
  try {
    const days = listIn(await reseller.usageStats(state.email, day(-1), day(0)), "usage");
    const last = days.at(-1);
    said.push(last
      ? `usage_stats for ${last.date}: disk_used ${mb(last.disk_used)}, uploads ${mb(last.uploads)}, downloads ${mb(last.downloads)}`
      : "usage_stats: no days yet");
  } catch (error) {
    said.push(`usage_stats: ${error.message}`);
  }
  try {
    const bucket = await reseller.bucketStats(state.email, state.storageDn, state.buckets.main);
    said.push(`bucket_stats: ${bucket?.file_count} files, ${mb(bucket?.bucket_size)}`);
  } catch (error) {
    said.push(`bucket_stats: ${error.message}`);
  }
  const held = tally(await allVersions(s3, state.buckets.main));
  const oldest = held.oldestOlder ? `, the oldest written ${hoursSince(held.oldestOlder)} hours ago` : "";
  said.push(`The bucket really holds ${held.current} current objects (${mb(held.currentBytes)}) and ${held.older} older versions (${mb(held.olderBytes)})${oldest}`);
  try {
    const rules = await readExpiry({ s3 }, state.buckets.main);
    said.push(rules.length ? `Expiry rule: ${rules.map(describeRule).join("; ")}` : "No expiry rule");
  } catch (error) {
    said.push(`Expiry rule: can't read it, ${errorLabel(error)}`);
  }
  return { outcome: "note", detail: `${said.join(". ")}.` };
}

async function newKey(reseller, state, name, options) {
  const answer = await reseller.createAccessKey({ email: state.email, storage_dn: state.storageDn, name, ...options });
  const accessKey = answer?.data?.access_key;
  const secretKey = answer?.data?.secret_key;
  if (!accessKey || !secretKey) throw new Error(`create_access_key answered without a key: ${shapeOf(answer)}`);
  const credentials = { accessKey, secretKey };
  return { accessKey, credentials, s3: s3Client({ endpoint: state.endpoint, region: state.s3Region, ...credentials }) };
}

/** If the endpoint says requests should be signed for another region, believe it, and note it. */
function adoptRegion(error, state, key, record) {
  const asked = error?.Region ?? /expecting '([^']+)'/.exec(error?.message ?? "")?.[1];
  if (error?.name !== "AuthorizationHeaderMalformed" || !asked || asked === state.s3Region) return;
  state.s3Region = asked;
  key.s3 = s3Client({ endpoint: state.endpoint, region: asked, ...key.credentials });
  record.fact("S3 signing region", `${asked}, which the endpoint asked for`);
}

/** How the store answered an attempt, in words. */
function answered(result) {
  if (result.allowed) return "accepted";
  return result.refused ? `refused, ${result.error}` : `neither accepted nor refused (${result.error})`;
}

/**
 * The bucket a household backs up into: versioned, and with --object-lock, locked by default for
 * RETENTION_DAYS in governance mode, which a key can get past only if it was given
 * bypass_governance_retention. Says what lock it reads back, if any.
 */
async function makeMainBucket(admin, state) {
  const bucket = state.buckets.main;
  await admin.s3.send(new CreateBucketCommand({ Bucket: bucket, ...(state.objectLock ? { ObjectLockEnabledForBucket: true } : {}) }));
  await turnOnVersioning(admin, bucket);
  if (!state.objectLock) return undefined;
  await setLock(admin, bucket, RETENTION_DAYS);
  const retention = await readLock(admin, bucket);
  if (!retention) throw new Error("Object lock reads back as off, or with no retention.");
  const [length, unit] = retention.Days ? [retention.Days, "day"] : [retention.Years, "year"];
  return `object lock on, ${retention.Mode} for ${length} ${unit}${length === 1 ? "" : "s"}`;
}

/** Locks every new version for `days`, in governance mode. */
function setLock(key, bucket, days) {
  return key.s3.send(new PutObjectLockConfigurationCommand({
    Bucket: bucket,
    ObjectLockConfiguration: { ObjectLockEnabled: "Enabled", Rule: { DefaultRetention: { Mode: "GOVERNANCE", Days: days } } },
  }));
}

/** The default retention object lock reads back with, or undefined if it's off. */
async function readLock(key, bucket) {
  const { ObjectLockConfiguration: lock } = await key.s3.send(new GetObjectLockConfigurationCommand({ Bucket: bucket }));
  return lock?.ObjectLockEnabled === "Enabled" ? lock.Rule?.DefaultRetention : undefined;
}

/**
 * The expiry rule: a version goes EXPIRE_DAYS after it's replaced or deleted, as does a delete
 * marker left with nothing behind it, and an upload nobody finished.
 */
function expiryRule(id = EXPIRY_RULE) {
  return {
    Rules: [{
      ID: id,
      Status: "Enabled",
      Filter: { Prefix: "" },
      NoncurrentVersionExpiration: { NoncurrentDays: EXPIRE_DAYS },
      Expiration: { ExpiredObjectDeleteMarker: true },
      AbortIncompleteMultipartUpload: { DaysAfterInitiation: 1 },
    }],
  };
}

function setExpiry(key, bucket, id) {
  return key.s3.send(new PutBucketLifecycleConfigurationCommand({ Bucket: bucket, LifecycleConfiguration: expiryRule(id) }));
}

/** The bucket's expiry rules as the store reads them back: none is an empty list. */
async function readExpiry(key, bucket) {
  try {
    const { Rules = [] } = await key.s3.send(new GetBucketLifecycleConfigurationCommand({ Bucket: bucket }));
    return Rules;
  } catch (error) {
    if (error?.name === "NoSuchLifecycleConfiguration" || error?.$metadata?.httpStatusCode === 404) return [];
    throw error;
  }
}

/** A rule as it read back, in words, so anything the store dropped from it shows. */
function describeRule(rule) {
  const days = rule.NoncurrentVersionExpiration?.NoncurrentDays;
  return [
    `${rule.ID ?? "no id"} (${rule.Status})`,
    days ? `old versions go after ${days} day${days === 1 ? "" : "s"}` : "NOTHING expires old versions",
    rule.Expiration?.ExpiredObjectDeleteMarker ? "bare delete markers go" : "bare delete markers stay",
    rule.AbortIncompleteMultipartUpload?.DaysAfterInitiation ? "unfinished uploads go" : "unfinished uploads stay",
  ].join(", ");
}

function turnOnVersioning(key, bucket) {
  return key.s3.send(new PutBucketVersioningCommand({ Bucket: bucket, VersioningConfiguration: { Status: "Enabled" } }));
}

async function findUser(reseller, email) {
  try {
    const users = listIn(await reseller.users(email), "users");
    return users.find((user) => user.email?.toLowerCase() === email.toLowerCase());
  } catch (error) {
    if (error.code === GONE) return undefined;
    throw error;
  }
}

async function disable(reseller, email) {
  try {
    await reseller.disableUser(email);
  } catch (error) {
    if (error.code !== "user_account_already_disabled") throw error;
  }
}

async function enable(reseller, email) {
  try {
    await reseller.enableUser(email);
  } catch (error) {
    if (error.code !== "user_account_already_enabled") throw error;
  }
}

function load(id) {
  const file = join(RUNS, id, "state.json");
  if (!existsSync(file)) throw new Error(`There's no run ${id} in ${RUNS}.`);
  const state = JSON.parse(readFileSync(file, "utf8"));
  return { dir: join(RUNS, id), state, save: () => writeFileSync(file, JSON.stringify(state, null, 2)) };
}

/** Sorts by time, and is short and lowercase enough to go in a bucket name. */
function runId() {
  return `${stamp()}-${randomBytes(2).toString("hex")}`;
}

function stamp() {
  const now = new Date();
  const two = (n) => String(n).padStart(2, "0");
  return `${now.getFullYear()}${two(now.getMonth() + 1)}${two(now.getDate())}-${two(now.getHours())}${two(now.getMinutes())}${two(now.getSeconds())}`;
}

/**
 * Nobody signs in with it. Upper and lower case, a digit and a symbol, in case iDrive has rules.
 */
function password() {
  return `Spk-${randomBytes(18).toString("base64url")}7`;
}

function hoursSince(date) {
  return ((Date.now() - new Date(date).getTime()) / 3_600_000).toFixed(1);
}

function day(offset) {
  return new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);
}

function mb(bytes) {
  const n = Number(bytes);
  return bytes == null || Number.isNaN(n) ? "?" : `${(n / MB).toFixed(1)} MB`;
}

function rate(bytes, seconds) {
  return seconds > 0 ? `${((bytes * 8) / 1e6 / seconds).toFixed(0)} Mbit/s` : "too quick to time";
}
