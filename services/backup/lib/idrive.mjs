// IDrive e2 as the service uses it: the reseller API for accounts, storage and keys, and S3 for
// buckets. Everything the service does at iDrive goes through what connectIdrive returns, so the
// tests can stand a pretend iDrive in its place.

import { createReseller, listIn, ResellerError } from "./reseller.mjs";
import { endpointFor, s3Client } from "./s3.mjs";

/** Where households' storage is, unless IDRIVE_REGION says otherwise: Oregon-2, where the spike ran. */
export const DEFAULT_REGION = "OR2";

/**
 * The service's way into iDrive, or null where there's no reseller token: a preview deployment, say.
 * `label` starts each account's address, so accounts made by something other than the service
 * itself, like a trial run, can be told apart from households'.
 */
export function connectIdrive(env = process.env, { label = "household" } = {}) {
  if (!env.IDRIVE_RESELLER_TOKEN) return null;
  return {
    reseller: createReseller({ token: env.IDRIVE_RESELLER_TOKEN }),
    region: env.IDRIVE_REGION || DEFAULT_REGION,
    s3: (storageDn, key) => s3Client({ endpoint: endpointFor(storageDn), ...key }),
    accountEmail: (householdId) => `${label}-${householdId}@uncloud.life`,
    // How long a new key is given to start working, and how often it's tried meanwhile.
    patience: { timeout: 15_000, every: 1_000 },
  };
}

/** The e2 account with this address, or undefined. */
export async function findUser(reseller, email) {
  try {
    const users = listIn(await reseller.users(email), "users");
    return users.find((user) => user.email?.toLowerCase() === email.toLowerCase());
  } catch (error) {
    if (error instanceof ResellerError && error.code === "account_non_existant") return undefined;
    throw error;
  }
}

/**
 * Where an account has storage in `region` (its key or its name), from list_user_regions, or
 * undefined. That answer names regions by name ("Oregon-2") and calls the address `user_dn`.
 */
export async function findStorage(reseller, email, region) {
  const names = [region.region_key, region.region_name].filter(Boolean);
  const entries = listIn(await reseller.listUserRegions(email), "user_regions");
  const entry = entries.find((candidate) => names.includes(candidate.region) || names.includes(candidate.region_key));
  return entry?.user_dn ?? entry?.storage_dn ?? undefined;
}

/**
 * A new key, named for what it's for. A key's secret is shown once, when it's made, so any key
 * already going by that name is one whose secret nobody can count on holding: it's removed first.
 */
export async function freshKey(reseller, account, name, options) {
  await removeKeysNamed(reseller, account, name);
  const answer = await reseller.createAccessKey({ email: account.email, storage_dn: account.storageDn, name, ...options });
  const accessKeyId = answer?.data?.access_key;
  const secretAccessKey = answer?.data?.secret_key;
  if (!accessKeyId || !secretAccessKey) throw new Error("create_access_key answered without a key.");
  return { accessKeyId, secretAccessKey };
}

export async function removeKeysNamed(reseller, { email, storageDn }, name) {
  const keys = listIn(await reseller.listAccessKeys(email, storageDn), "access_keys");
  for (const key of keys.filter((candidate) => candidate.name === name)) {
    await reseller.removeAccessKey(email, storageDn, key.key_id);
  }
}
