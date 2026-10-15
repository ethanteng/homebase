import { ResellerError } from "../lib/reseller.mjs";

/**
 * A pretend IDrive e2, in the shape connectIdrive gives the service: the reseller API's calls and S3,
 * over accounts, keys and buckets kept in memory. It answers as the spike saw iDrive answer
 * (tools/idrive-spike/runs): list_user_regions names regions by name and calls the address
 * `user_dn`, and a lifecycle rule loses any part that clears unfinished uploads.
 *
 * `failOnce(call, { after })` makes the next `call` fail: before doing anything, or after doing it,
 * as when iDrive did the work and the answer was lost. `keyDelay` is how many requests a new key is
 * refused before it works. `storesLock` is what a bucket keeps of the lock it's given.
 */
export function fakeIdrive({ keyDelay = 0, storesLock = (config) => config } = {}) {
  const regions = [
    { region_key: "OR2", region_name: "Oregon-2", active: true, region_code: "us-west-4" },
    { region_key: "VA", region_name: "Virginia", active: true, region_code: "us-east-1" },
  ];
  const users = new Map();
  const buckets = new Map();
  const calls = [];
  const faults = new Map();
  let made = 0;

  async function doing(name, work) {
    calls.push(name);
    const fault = faults.get(name);
    faults.delete(name);
    if (fault && !fault.after) throw Object.assign(new Error(`${name}: the connection dropped`), { code: "ECONNRESET" });
    const answer = await work();
    if (fault) throw Object.assign(new Error(`${name}: the connection dropped before the answer came`), { code: "ECONNRESET" });
    return structuredClone(answer);
  }

  const refused = (path, code, status = 403) => new ResellerError("POST", path, status, { error: { type: "error", code, message: code } });

  function user(path, email) {
    const found = users.get(email);
    if (!found) throw refused(path, "account_non_existant");
    return found;
  }

  function storageOf(path, email, storageDn) {
    const found = user(path, email);
    if (![...found.storage.values()].includes(storageDn)) throw refused(path, "invalid_storage_dn", 400);
    return found;
  }

  const reseller = {
    regions: () => doing("regions", () => ({ regions })),
    users: (email) => doing("users", () => ({
      users: [...users.values()].filter((u) => !email || u.email === email).map((u) => ({
        email: u.email, storage_quota: u.quota, enforce_quota: u.enforce_quota, read_only_mode: false,
      })),
    })),
    createUser: (details) => doing("createUser", () => {
      if (users.has(details.email)) throw refused("/create_user", "email_already_in_use", 409);
      users.set(details.email, { ...details, storage: new Map(), keys: [] });
      return { user_created: true };
    }),
    enableUserRegion: (email, key) => doing("enableUserRegion", () => {
      const found = user("/enable_user_region", email);
      if (found.storage.has(key)) throw refused("/enable_user_region", "region_already_enabled", 409);
      const storageDn = `s3.${key.toLowerCase()}.example`;
      found.storage.set(key, storageDn);
      return { storage_added: true, storage_dn: storageDn, public_ip: "192.0.2.1" };
    }),
    listUserRegions: (email) => doing("listUserRegions", () => ({
      user_regions: [...user("/list_user_regions", email).storage].map(([key, storageDn]) => ({
        region: regions.find((region) => region.region_key === key).region_name,
        user_dn: storageDn,
        server_active: true,
        read_only: false,
      })),
    })),
    createAccessKey: ({ email, storage_dn: storageDn, name, ...options }) => doing("createAccessKey", () => {
      const found = storageOf("/create_access_key", email, storageDn);
      made += 1;
      const key = { key_id: `AKFAKE${made}`, secret: `secret-${made}-${Math.random()}`, name, options, refusals: keyDelay };
      found.keys.push(key);
      return { data: { access_key: key.key_id, secret_key: key.secret } };
    }),
    listAccessKeys: (email, storageDn) => doing("listAccessKeys", () => ({
      storage_dn: storageDn,
      access_keys: storageOf("/list_access_keys", email, storageDn).keys
        .map((key) => ({ key_id: key.key_id, name: key.name, creation_date: "2026-10-15T00:00:00.000Z", description: "" })),
    })),
    removeAccessKey: (email, storageDn, keyId) => doing("removeAccessKey", () => {
      const found = storageOf("/remove_access_key", email, storageDn);
      const index = found.keys.findIndex((key) => key.key_id === keyId);
      if (index < 0) throw refused("/remove_access_key", "access_key_not_found", 404);
      found.keys.splice(index, 1);
      return { removed: true };
    }),
  };

  function s3(storageDn, { accessKeyId, secretAccessKey }) {
    return {
      send: (command) => {
        const kind = command.constructor.name.replace(/Command$/, "");
        return doing(kind, () => answer(kind, command.input));
      },
    };

    function answer(kind, input) {
      const owner = [...users.values()].find((u) => u.keys.some((key) => key.key_id === accessKeyId));
      const key = owner?.keys.find((candidate) => candidate.key_id === accessKeyId);
      if (!key || key.secret !== secretAccessKey || ![...owner.storage.values()].includes(storageDn)) {
        throw s3Error("InvalidAccessKeyId", 403);
      }
      if (key.refusals > 0) {
        key.refusals -= 1;
        throw s3Error("InvalidAccessKeyId", 403);
      }
      const limited = Array.isArray(key.options.buckets);
      if (kind === "ListBuckets") {
        if (limited) throw s3Error("AccessDenied", 403);
        return { Buckets: [...buckets].filter(([, b]) => b.owner === owner.email).map(([Name]) => ({ Name })) };
      }
      const bucket = buckets.get(input.Bucket);
      if (kind === "CreateBucket") {
        if (limited) throw s3Error("AccessDenied", 403);
        if (bucket) throw s3Error(bucket.owner === owner.email ? "BucketAlreadyOwnedByYou" : "BucketAlreadyExists", 409);
        const locked = input.ObjectLockEnabledForBucket === true;
        buckets.set(input.Bucket, { owner: owner.email, objectLock: locked, versioning: locked ? "Enabled" : undefined, rules: [] });
        return {};
      }
      if (!bucket) throw s3Error("NoSuchBucket", 404);
      if (bucket.owner !== owner.email || (limited && !key.options.buckets.includes(input.Bucket))) {
        throw s3Error("AccessDenied", 403);
      }
      switch (kind) {
        case "PutBucketVersioning":
          if (bucket.objectLock && input.VersioningConfiguration.Status !== "Enabled") throw s3Error("InvalidBucketState", 409);
          bucket.versioning = input.VersioningConfiguration.Status;
          return {};
        case "GetBucketVersioning":
          return { Status: bucket.versioning };
        case "PutObjectLockConfiguration":
          if (!bucket.objectLock) throw s3Error("InvalidBucketState", 409);
          bucket.lock = storesLock(structuredClone(input.ObjectLockConfiguration));
          return {};
        case "GetObjectLockConfiguration":
          if (!bucket.objectLock) throw s3Error("ObjectLockConfigurationNotFoundError", 404);
          return { ObjectLockConfiguration: bucket.lock ?? { ObjectLockEnabled: "Enabled" } };
        case "PutBucketLifecycleConfiguration":
          bucket.rules = input.LifecycleConfiguration.Rules.map(({ AbortIncompleteMultipartUpload, ...kept }) => structuredClone(kept));
          return {};
        case "GetBucketLifecycleConfiguration":
          if (!bucket.rules.length) throw s3Error("NoSuchLifecycleConfiguration", 404);
          return { Rules: bucket.rules };
        case "ListObjectsV2":
          return { Contents: [], KeyCount: 0 };
        default:
          throw new Error(`The pretend iDrive doesn't do ${kind}.`);
      }
    }
  }

  return {
    reseller,
    region: "OR2",
    s3,
    accountEmail: (householdId) => `household-${householdId}@uncloud.life`,
    patience: { timeout: 1_000, every: 1 },
    users,
    buckets,
    calls,
    failOnce: (call, { after = false } = {}) => faults.set(call, { after }),
    count: (call) => calls.filter((name) => name === call).length,
  };
}

function s3Error(name, status) {
  return Object.assign(new Error(name), { name, Code: name, $metadata: { httpStatusCode: status } });
}
