// The data side: talking to a household's bucket over S3 with a key the reseller API made.

import {
  DeleteObjectCommand,
  ListObjectVersionsCommand,
  S3Client,
} from "@aws-sdk/client-s3";

/** `storage_dn` is a bare host name; S3 wants a URL. */
export function endpointFor(storageDn) {
  return /^https?:\/\//.test(storageDn) ? storageDn : `https://${storageDn}`;
}

export function s3Client({ endpoint, region, accessKey, secretKey }) {
  return new S3Client({
    endpoint,
    region,
    forcePathStyle: true,
    credentials: { accessKeyId: accessKey, secretAccessKey: secretKey },
    // Recent AWS SDKs put a checksum on every request by default, which not every S3-compatible
    // store accepts. Send one only where S3 itself requires it.
    requestChecksumCalculation: "WHEN_REQUIRED",
    responseChecksumValidation: "WHEN_REQUIRED",
    maxAttempts: 2,
  });
}

/** What S3 said, short enough for a table cell: `AccessDenied (403)`. */
export function errorLabel(error) {
  const status = error?.$metadata?.httpStatusCode;
  const name = error?.Code ?? error?.name ?? "Error";
  return status ? `${name} (${status})` : `${name}: ${error?.message ?? error}`;
}

/** Whether `action` was allowed, and if not, what the store said. */
export async function attempt(action) {
  try {
    await action();
    return { allowed: true };
  } catch (error) {
    return { allowed: false, error: errorLabel(error) };
  }
}

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const secondsSince = (start) => Math.round((Date.now() - start) / 100) / 10;

/**
 * Seconds until `probe` first succeeds. A new key can take a while to reach the storage servers,
 * and how long is one of the things worth knowing.
 */
export async function secondsUntilWorks(probe, { timeout = 180, onError } = {}) {
  const start = Date.now();
  for (;;) {
    try {
      await probe();
      return secondsSince(start);
    } catch (error) {
      onError?.(error);
      if (secondsSince(start) > timeout) throw new Error(`Still failing after ${timeout}s: ${errorLabel(error)}`);
      await sleep(2000);
    }
  }
}

/** Seconds until `probe` first fails, or null if it still worked at `timeout`. */
export async function secondsUntilRefused(probe, { timeout = 300 } = {}) {
  const start = Date.now();
  for (;;) {
    try {
      await probe();
    } catch (error) {
      return { seconds: secondsSince(start), error: errorLabel(error) };
    }
    if (secondsSince(start) > timeout) return null;
    await sleep(2000);
  }
}

/** Every version and delete marker in a bucket. */
export async function allVersions(s3, bucket) {
  const versions = [];
  const markers = [];
  for (let KeyMarker, VersionIdMarker; ;) {
    const page = await s3.send(new ListObjectVersionsCommand({ Bucket: bucket, KeyMarker, VersionIdMarker }));
    versions.push(...(page.Versions ?? []));
    markers.push(...(page.DeleteMarkers ?? []));
    if (!page.IsTruncated) return { versions, markers };
    KeyMarker = page.NextKeyMarker;
    VersionIdMarker = page.NextVersionIdMarker;
  }
}

/** How much a bucket holds that anyone can see, and how much only versioning is keeping. */
export function tally({ versions, markers }) {
  const current = versions.filter((v) => v.IsLatest);
  const older = versions.filter((v) => !v.IsLatest);
  const bytes = (list) => list.reduce((sum, v) => sum + (v.Size ?? 0), 0);
  return {
    current: current.length,
    currentBytes: bytes(current),
    older: older.length,
    olderBytes: bytes(older),
    markers: markers.length,
    locks: current.filter((v) => v.Key.includes("/locks/")).length,
  };
}

/** Deletes every version and marker, so the bucket itself can go. */
export async function emptyBucket(s3, bucket) {
  for (let pass = 0; pass < 5; pass++) {
    const { versions, markers } = await allVersions(s3, bucket);
    const everything = [...versions, ...markers];
    if (everything.length === 0) return;
    for (const item of everything) {
      await s3.send(new DeleteObjectCommand({ Bucket: bucket, Key: item.Key, VersionId: item.VersionId }));
    }
  }
  throw new Error(`${bucket} still isn't empty after five passes.`);
}
