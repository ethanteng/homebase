// The S3 client, against a local server standing in for e2: every request the service makes gets
// sent, with Content-MD5 where S3 wants a checksum, and the protection reaches the wire as meant.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import {
  CreateBucketCommand,
  GetBucketLifecycleConfigurationCommand,
  GetBucketVersioningCommand,
  GetObjectLockConfigurationCommand,
  ListBucketsCommand,
  ListObjectsV2Command,
} from "@aws-sdk/client-s3";
import { protectBucket } from "../lib/protection.mjs";
import { endpointFor, s3Client } from "../lib/s3.mjs";

async function withStore(run) {
  const seen = [];
  const server = createServer((request, response) => {
    let body = "";
    request.on("data", (chunk) => (body += chunk));
    request.on("end", () => {
      seen.push({ method: request.method, url: request.url, headers: request.headers, body });
      response.end();
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  try {
    const s3 = s3Client({ endpoint: `http://127.0.0.1:${server.address().port}`, accessKeyId: "AKIAEXAMPLE", secretAccessKey: "secret" });
    await run(s3);
  } finally {
    server.close();
  }
  return seen;
}

test("every request the service makes gets sent, with Content-MD5 where a checksum is due", async () => {
  const seen = await withStore(async (s3) => {
    await s3.send(new ListBucketsCommand({}));
    await s3.send(new CreateBucketCommand({ Bucket: "b", ObjectLockEnabledForBucket: true }));
    await protectBucket(s3, "b");
    await s3.send(new GetBucketVersioningCommand({ Bucket: "b" }));
    await s3.send(new GetObjectLockConfigurationCommand({ Bucket: "b" }));
    await s3.send(new GetBucketLifecycleConfigurationCommand({ Bucket: "b" }));
    await s3.send(new ListObjectsV2Command({ Bucket: "b", MaxKeys: 1 }));
  });

  const due = ["?versioning", "?object-lock", "?lifecycle"];
  assert.equal(seen.length, 9);
  for (const { method, url, headers } of seen) {
    const label = `${method} ${url}`;
    assert.deepEqual(Object.keys(headers).filter((name) => name.startsWith("x-amz-checksum")), [], label);
    const signed = /SignedHeaders=([^,]+)/.exec(headers.authorization)?.[1].split(";") ?? [];
    if (method === "PUT" && due.some((query) => url.includes(query))) {
      assert.match(headers["content-md5"] ?? "", /^[A-Za-z0-9+/]{22}==$/, label);
      assert.ok(signed.includes("content-md5"), `${label} signs Content-MD5`);
    } else {
      assert.equal(headers["content-md5"], undefined, label);
    }
  }
  // Path-style, since a household's bucket is reached at e2's address for its region.
  assert.ok(seen[1].url.startsWith("/b"));
  assert.equal(seen[1].headers["x-amz-bucket-object-lock-enabled"], "true");
});

test("compliance mode and the expiry rule reach the wire as meant", async () => {
  const seen = await withStore((s3) => protectBucket(s3, "b"));

  const [versioning, lock, lifecycle] = seen.map((request) => request.body);
  assert.match(versioning, /<Status>Enabled<\/Status>/);
  assert.match(lock, /<Mode>COMPLIANCE<\/Mode><Days>30<\/Days>/);
  assert.match(lifecycle, /<NoncurrentDays>30<\/NoncurrentDays>/);
  assert.match(lifecycle, /<ExpiredObjectDeleteMarker>true<\/ExpiredObjectDeleteMarker>/);
  assert.doesNotMatch(lifecycle, /AbortIncompleteMultipartUpload/);
});

test("e2's bare addresses become URLs", () => {
  assert.equal(endpointFor("s3.us-west-4.idrivee2.com"), "https://s3.us-west-4.idrivee2.com");
  assert.equal(endpointFor("http://127.0.0.1:9000"), "http://127.0.0.1:9000");
});
