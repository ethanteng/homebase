// The data side: talking to a household's bucket over S3 with a key the reseller API made.

import { createHash } from "node:crypto";
import { setTimeout as wait } from "node:timers/promises";
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
  const client = new S3Client({
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
  // Where S3 does require one (object lock settings, deleting many objects at once), recent SDKs
  // send a CRC32 header, while stores built to older S3 want Content-MD5. Send that instead. This
  // runs last in the build step, after any checksum is added and before the request is signed. It
  // can't be placed relative to the checksum middleware itself, which most requests don't have.
  client.middlewareStack.add((next) => async (args) => {
    const { request } = args;
    const headers = request?.headers ?? {};
    const checksums = Object.keys(headers).filter((name) => /^x-amz-(checksum-|sdk-checksum-algorithm)/i.test(name));
    if (checksums.length && (typeof request.body === "string" || request.body instanceof Uint8Array)) {
      for (const name of checksums) delete headers[name];
      headers["content-md5"] = createHash("md5").update(request.body).digest("base64");
    }
    return next(args);
  }, { step: "build", priority: "low", name: "contentMd5Instead" });
  return client;
}

/**
 * What S3 said, short enough for a table cell: `AccessDenied (403)`. An unusual answer, like a 400
 * where S3 would say 403, comes with the store's own message, since the name alone says little.
 */
export function errorLabel(error) {
  const status = error?.$metadata?.httpStatusCode;
  const name = error?.Code ?? error?.name ?? "Error";
  if (!status) return `${name}: ${error?.message ?? error}`;
  const message = error?.message;
  const telling = status >= 400 && status < 500 && ![401, 403, 404].includes(status)
    && message && message !== name && message !== "UnknownError";
  return telling ? `${name} (${status}): ${message}` : `${name} (${status})`;
}

const REFUSALS = new Set(["AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch", "AllAccessDisabled", "AccountProblem"]);

/**
 * Whether the store said no on purpose: the key isn't allowed to, or isn't a key any more. A
 * dropped connection or a 503 says nothing about what a key may do, and mustn't be read as if it
 * did, or a probe that merely failed would pass for a key that was stopped.
 */
export function isRefusal(error) {
  const status = error?.$metadata?.httpStatusCode;
  return status === 401 || status === 403 || REFUSALS.has(error?.Code ?? error?.name);
}

/** Worth asking again: no answer at all, a timeout, throttling, or the server's own trouble. */
function isPassing(error) {
  const status = error?.$metadata?.httpStatusCode;
  return status === undefined || status === 408 || status === 429 || status >= 500;
}

/**
 * Tries `action`, asking again while the failure is passing trouble. `refused` is true only when
 * the store said no on purpose. Any other failure says what was asked wasn't done, but not that the
 * key is barred from doing it; `passing` says whether that was trouble reaching the store at all.
 */
export async function attempt(action, { tries = 3, every = 2000 } = {}) {
  for (let tried = 1; ; tried++) {
    try {
      await action();
      return { allowed: true, refused: false };
    } catch (error) {
      if (isRefusal(error)) return { allowed: false, refused: true, error: errorLabel(error) };
      const passing = isPassing(error);
      if (!passing || tried >= tries) return { allowed: false, refused: false, passing, error: errorLabel(error) };
      await wait(every);
    }
  }
}

const secondsSince = (start) => Math.round((Date.now() - start) / 100) / 10;

/**
 * Seconds until `probe` first succeeds. A new key can take a while to reach the storage servers,
 * and how long is one of the things worth knowing.
 */
export async function secondsUntilWorks(probe, { timeout = 180, every = 2000, signal, onError } = {}) {
  const start = Date.now();
  for (;;) {
    try {
      await probe();
      return secondsSince(start);
    } catch (error) {
      // Only an answer from the store, or trouble reaching it, is worth waiting out. Anything else
      // went wrong before a request left, and will go wrong the same way every time.
      if (!error?.$metadata && !error?.code) throw error;
      onError?.(error);
      if (secondsSince(start) > timeout) throw new Error(`Still failing after ${timeout}s: ${errorLabel(error)}`);
      await wait(every, undefined, { signal });
    }
  }
}

/**
 * Seconds until the store refuses `probe`: `{ seconds, error }`, or `{ seconds: null }` if it never
 * did by `timeout`. Any other failure on the way is passing trouble, not the refusal being timed,
 * so it keeps asking, and the last such failure comes back as `trouble`.
 */
export async function secondsUntilRefused(probe, { timeout = 300, every = 2000, signal } = {}) {
  const start = Date.now();
  let trouble;
  for (;;) {
    try {
      await probe();
    } catch (error) {
      if (isRefusal(error)) return { seconds: secondsSince(start), error: errorLabel(error), trouble };
      trouble = errorLabel(error);
    }
    if (secondsSince(start) > timeout) return { seconds: null, trouble };
    await wait(every, undefined, { signal });
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
    // When the oldest version kept only by versioning was written: what an expiry rule should be
    // seen clearing, day by day.
    oldestOlder: older.map((v) => v.LastModified).filter(Boolean).sort((a, b) => a - b)[0],
  };
}

/**
 * Deletes every version and marker, so the bucket itself can go. A bucket under object lock needs
 * `bypass`, and a key allowed to use it.
 */
export async function emptyBucket(s3, bucket, { bypass = false } = {}) {
  for (let pass = 0; pass < 5; pass++) {
    const { versions, markers } = await allVersions(s3, bucket);
    const everything = [...versions, ...markers];
    if (everything.length === 0) return;
    for (const item of everything) {
      await s3.send(new DeleteObjectCommand({
        Bucket: bucket,
        Key: item.Key,
        VersionId: item.VersionId,
        ...(bypass ? { BypassGovernanceRetention: true } : {}),
      }));
    }
  }
  throw new Error(`${bucket} still isn't empty after five passes.`);
}
