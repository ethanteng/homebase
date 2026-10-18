// How every household's bucket is protected (docs/backup.md, "Protecting history"): every version
// kept, each locked in compliance mode from when it's written, and a rule that clears old versions
// once their lock allows. Setting a bucket up applies it; the nightly check will read it back.

import {
  GetBucketLifecycleConfigurationCommand,
  GetBucketVersioningCommand,
  GetObjectLockConfigurationCommand,
  PutBucketLifecycleConfigurationCommand,
  PutBucketVersioningCommand,
  PutObjectLockConfigurationCommand,
} from "@aws-sdk/client-s3";

/**
 * How long each version is locked, and how long a replaced or deleted one is kept (docs/backup.md,
 * "The lock period"). iDrive bills old versions until the expiry rule removes them, which it does.
 * Changing these changes what new versions get, never the lock on one already written.
 */
export const LOCK_DAYS = 30;
export const EXPIRE_DAYS = 30;
export const EXPIRY_RULE = "uncloud-expire-old-versions";

/**
 * The expiry rule, as iDrive keeps it. It would take a part that clears unfinished uploads too,
 * then drop it, so that part isn't sent: what's read back is then exactly what was set.
 */
export function expiryRule() {
  return {
    ID: EXPIRY_RULE,
    Status: "Enabled",
    Filter: { Prefix: "" },
    NoncurrentVersionExpiration: { NoncurrentDays: EXPIRE_DAYS },
    Expiration: { ExpiredObjectDeleteMarker: true },
  };
}

/**
 * Applies the protection to a bucket made with object lock. Each part is a setting, so applying it
 * to a bucket that already has it changes nothing.
 */
export async function protectBucket(s3, bucket) {
  await s3.send(new PutBucketVersioningCommand({ Bucket: bucket, VersioningConfiguration: { Status: "Enabled" } }));
  await s3.send(new PutObjectLockConfigurationCommand({
    Bucket: bucket,
    ObjectLockConfiguration: {
      ObjectLockEnabled: "Enabled",
      Rule: { DefaultRetention: { Mode: "COMPLIANCE", Days: LOCK_DAYS } },
    },
  }));
  await s3.send(new PutBucketLifecycleConfigurationCommand({
    Bucket: bucket,
    LifecycleConfiguration: { Rules: [expiryRule()] },
  }));
}

/**
 * What's wrong with a bucket's protection, as the store reads it back: nothing is an empty list.
 * Read back rather than trusted, since a store can accept a setting and keep something else.
 */
export async function inspectBucket(s3, bucket) {
  const problems = [];

  const { Status } = await s3.send(new GetBucketVersioningCommand({ Bucket: bucket }));
  if (Status !== "Enabled") problems.push(`versioning is ${Status ? Status.toLowerCase() : "off"}`);

  const lock = await readLock(s3, bucket);
  const retention = lock?.Rule?.DefaultRetention;
  if (lock?.ObjectLockEnabled !== "Enabled") problems.push("object lock is off");
  else if (!retention) problems.push("nothing is locked by default");
  else {
    if (retention.Mode !== "COMPLIANCE") problems.push(`the default lock is in ${String(retention.Mode).toLowerCase()} mode, not compliance`);
    if (retention.Days !== LOCK_DAYS || retention.Years) {
      problems.push(`the default lock is ${retention.Years ? `${retention.Years} years` : `${retention.Days} days`}, not ${LOCK_DAYS} days`);
    }
  }

  const rules = (await readRules(s3, bucket)).filter((rule) => rule.Status === "Enabled");
  const ours = rules.find((rule) => wholeBucket(rule)
    && rule.NoncurrentVersionExpiration?.NoncurrentDays === EXPIRE_DAYS
    && rule.Expiration?.ExpiredObjectDeleteMarker === true);
  if (!ours) problems.push(`no rule clears old versions after ${EXPIRE_DAYS} days`);
  // A rule that expires current objects would delete the backup itself, as soon as each lock let it.
  if (rules.some((rule) => rule.Expiration?.Days || rule.Expiration?.Date)) problems.push("a rule expires current objects");

  return problems;
}

async function readLock(s3, bucket) {
  try {
    return (await s3.send(new GetObjectLockConfigurationCommand({ Bucket: bucket }))).ObjectLockConfiguration;
  } catch (error) {
    if (error?.name === "ObjectLockConfigurationNotFoundError" || error?.$metadata?.httpStatusCode === 404) return undefined;
    throw error;
  }
}

async function readRules(s3, bucket) {
  try {
    return (await s3.send(new GetBucketLifecycleConfigurationCommand({ Bucket: bucket }))).Rules ?? [];
  } catch (error) {
    if (error?.name === "NoSuchLifecycleConfiguration" || error?.$metadata?.httpStatusCode === 404) return [];
    throw error;
  }
}

function wholeBucket(rule) {
  const filter = rule.Filter ?? {};
  return !rule.Prefix && !filter.Prefix && !filter.Tag && !filter.And
    && filter.ObjectSizeGreaterThan === undefined && filter.ObjectSizeLessThan === undefined;
}
