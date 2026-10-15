// Talking to a household's bucket over S3, with a key the reseller API made. The same client as the
// spike's (tools/idrive-spike/s3.mjs), which proved it against IDrive e2: a change to one is a change
// to the other.

import { createHash } from "node:crypto";
import { setTimeout as wait } from "node:timers/promises";
import { S3Client } from "@aws-sdk/client-s3";

/**
 * What requests to e2 are signed for. e2 takes us-east-1 whatever the region (the spike signed
 * every request to Oregon-2 with it), and a host is told the same.
 */
export const SIGNING_REGION = "us-east-1";

/** `storage_dn` is a bare host name; S3 wants a URL. */
export function endpointFor(storageDn) {
  return /^https?:\/\//.test(storageDn) ? storageDn : `https://${storageDn}`;
}

export function s3Client({ endpoint, accessKeyId, secretAccessKey, region = SIGNING_REGION }) {
  const client = new S3Client({
    endpoint,
    region,
    forcePathStyle: true,
    credentials: { accessKeyId, secretAccessKey },
    // Recent AWS SDKs put a checksum on every request by default, which not every S3-compatible
    // store accepts. Send one only where S3 itself requires it.
    requestChecksumCalculation: "WHEN_REQUIRED",
    responseChecksumValidation: "WHEN_REQUIRED",
    maxAttempts: 2,
  });
  // Where S3 does require one (object lock settings, versioning, lifecycle rules), recent SDKs send a
  // CRC32 header, while stores built to older S3 want Content-MD5. Send that instead. This runs last
  // in the build step, after any checksum is added and before the request is signed.
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

/** What S3 said, short: `AccessDenied (403)`, or the message when there was no answer at all. */
export function errorLabel(error) {
  const status = error?.$metadata?.httpStatusCode;
  const name = error?.Code ?? error?.name ?? "Error";
  return status ? `${name} (${status})` : `${name}: ${error?.message ?? error}`;
}

/**
 * Waits for a new key to work: `probe` is asked until it succeeds or `timeout` ms pass. A key takes
 * up to a few seconds to reach e2's storage servers (0.1 to 5.4 in the spike), and until it does,
 * they refuse it as one they don't know.
 */
export async function untilWorks(probe, { timeout = 15_000, every = 1_000 } = {}) {
  const started = Date.now();
  for (;;) {
    try {
      return await probe();
    } catch (error) {
      // Only an answer from the store, or trouble reaching it, is worth waiting out.
      if (!error?.$metadata && !error?.code) throw error;
      if (Date.now() - started + every > timeout) {
        throw new Error(`A new key still didn't work after ${Math.round(timeout / 1000)} s: ${errorLabel(error)}`);
      }
      await wait(every);
    }
  }
}
