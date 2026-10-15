// Sets one pretend household up at IDrive e2 for real, through the service's own code, then checks
// what its host was given and removes the account. The tests use a pretend iDrive; this is how to
// know the real one answers the same.
//
//   node --env-file=../../tools/idrive-spike/.env scripts/try-setup.mjs [--keep]
//
// It needs IDRIVE_RESELLER_TOKEN, and IDRIVE_REGION if not Oregon-2. The account it makes is
// try-<household>@uncloud.life, and it's removed at the end, locked object and all, unless --keep.
// Its database is PGlite, in memory: nothing touches the service's own.

import { parseArgs } from "node:util";
import {
  CreateBucketCommand,
  DeleteBucketLifecycleCommand,
  PutBucketLifecycleConfigurationCommand,
  DeleteObjectCommand,
  GetObjectRetentionCommand,
  ListObjectsV2Command,
  PutObjectCommand,
} from "@aws-sdk/client-s3";
import { freshDatabase } from "../test/database.mjs";
import { ready } from "../lib/db.mjs";
import { connectIdrive } from "../lib/idrive.mjs";
import { createInvite, redeemInvite } from "../lib/invites.mjs";
import { LOCK_DAYS } from "../lib/protection.mjs";
import { hostStorageRoute } from "../lib/routes.mjs";
import { errorLabel, s3Client } from "../lib/s3.mjs";

const { values: { keep } } = parseArgs({ options: { keep: { type: "boolean", default: false } } });
const idrive = connectIdrive(process.env, { label: "try" });
if (!idrive) throw new Error("Set IDRIVE_RESELLER_TOKEN, for example with --env-file=../../tools/idrive-spike/.env.");

const results = [];
const note = (what, outcome, detail = "") => {
  results.push({ what, outcome, detail });
  console.log(`${outcome === "pass" ? "✓" : outcome === "fail" ? "✗" : "·"} ${what}${detail ? `: ${detail}` : ""}`);
};
const seconds = (since) => `${((Date.now() - since) / 1000).toFixed(1)} s`;

const db = await ready(await freshDatabase());
const { code } = await createInvite(db, { note: "try-setup" });
const joined = await redeemInvite(db, { code, name: "try-setup" });
const email = idrive.accountEmail(joined.household.id);
console.log(`Household ${joined.household.id}, account ${email}, region ${idrive.region}\n`);

const askForStorage = () => hostStorageRoute(
  new Request("https://backup.example/api/host/storage", { method: "POST", headers: { authorization: `Bearer ${joined.credential}` } }),
  { db, idrive, env: {}, now: new Date() });

try {
  // The host's side of setting up: ask, and ask again while it's told to.
  let storage;
  const started = Date.now();
  for (let asked = 1; asked <= 6 && !storage; asked++) {
    const since = Date.now();
    const answer = await askForStorage();
    const said = await answer.json();
    if (answer.status === 201) storage = said.storage;
    else note(`Asked for storage (${asked})`, "note", `${answer.status} after ${seconds(since)}: ${JSON.stringify(said.setup ?? said.error)}`);
  }
  if (!storage) {
    const { rows: [household] } = await db.query("select setup_problem from households");
    throw new Error(`Still not set up after six asks. Last problem: ${household.setup_problem ?? "none"}`);
  }
  note("Set up, and the host given its key", "pass", `${seconds(started)} in all; ${storage.endpoint}, bucket ${storage.bucket}`);

  const host = s3Client(storage);
  const { bucket } = storage;
  const put = await host.send(new PutObjectCommand({ Bucket: bucket, Key: "try/probe.txt", Body: "probe", ChecksumAlgorithm: "CRC32" }));
  note("The host's key writes to its bucket", "pass");

  const { Retention } = await host.send(new GetObjectRetentionCommand({ Bucket: bucket, Key: "try/probe.txt", VersionId: put.VersionId }));
  const days = (new Date(Retention?.RetainUntilDate) - Date.now()) / 86_400_000;
  note("What it wrote is locked in compliance mode for the lock period",
    Retention?.Mode === "COMPLIANCE" && Math.abs(days - LOCK_DAYS) < 1 ? "pass" : "fail",
    `${Retention?.Mode ?? "no lock"}, ${days.toFixed(2)} days`);

  // Refused means the store said no. Trouble reaching it says nothing either way. (iDrive refuses
  // destroying a locked version with a 400 where S3 would say 403.)
  const refused = async (what, action) => {
    try {
      await action();
      note(what, "fail", "it was allowed");
    } catch (error) {
      const said = [400, 401, 403].includes(error?.$metadata?.httpStatusCode);
      note(what, said ? "pass" : "fail", said ? errorLabel(error) : `neither allowed nor refused: ${errorLabel(error)}`);
    }
  };
  await refused("The host's key can't destroy a version", () =>
    host.send(new DeleteObjectCommand({ Bucket: bucket, Key: "try/probe.txt", VersionId: put.VersionId })));
  // A rule of the host's own would be worse than none: one expiring every object would hide the
  // whole backup behind delete markers, and clear each version as its lock ran out.
  await refused("The host's key can't replace the expiry rule", () => host.send(new PutBucketLifecycleConfigurationCommand({
    Bucket: bucket,
    LifecycleConfiguration: { Rules: [{ ID: "host-was-here", Status: "Enabled", Filter: { Prefix: "" }, Expiration: { Days: 1 } }] },
  })));
  await refused("The host's key can't remove the expiry rule", () => host.send(new DeleteBucketLifecycleCommand({ Bucket: bucket })));
  await refused("The host's key can't make a bucket", () => host.send(new CreateBucketCommand({ Bucket: `${bucket}-x` })));

  const since = Date.now();
  const again = await askForStorage();
  const replaced = (await again.json()).storage;
  note("Asking again gives a new key", again.status === 201 && replaced.accessKeyId !== storage.accessKeyId ? "pass" : "fail", seconds(since));
  await new Promise((resolve) => setTimeout(resolve, 3_000));
  await refused("…and the old one stops working", () => host.send(new ListObjectsV2Command({ Bucket: bucket, MaxKeys: 1 })));
} catch (error) {
  note("The trial stopped", "fail", error.message);
} finally {
  if (keep) {
    console.log(`\nKept ${email}. Remove it from the iDrive reseller console when you're done.`);
  } else {
    try {
      await idrive.reseller.disableUser(email).catch(() => {});
      await idrive.reseller.removeUser(email);
      note("Removed the account", "pass", email);
    } catch (error) {
      note("Removing the account", error.code === "account_non_existant" ? "pass" : "fail", error.message);
    }
  }
  await db.close();
}

const failed = results.filter((result) => result.outcome === "fail").length;
console.log(failed ? `\n${failed} check${failed === 1 ? "" : "s"} failed.` : "\nEverything checked out.");
process.exitCode = failed ? 1 : 0;
