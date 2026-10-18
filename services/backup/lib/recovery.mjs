import { createHash, randomBytes } from "node:crypto";
import { ServiceError } from "./http.mjs";
import { hashCredential } from "./hosts.mjs";
import { removeKeysNamed } from "./idrive.mjs";

// A household's recovery token: what a recovery kit holds besides the backup's password, and what
// the service takes in place of a lost host's credential. Written down and typed back in, so it's
// in the invite codes' alphabet, without 0, O, 1 or I, in groups of five: 30 characters from 32 is
// 150 bits. Only its hash is kept.
const ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
const LENGTH = 30;

const INVALID = "That recovery token isn't one Uncloud Backup knows, or a newer kit has replaced it.";

export function newRecoveryToken() {
  // 256 is a multiple of 32, so taking each byte modulo 32 favours no character.
  const characters = [...randomBytes(LENGTH)].map((byte) => ALPHABET[byte % ALPHABET.length]).join("");
  return characters.match(/.{5}/g).join("-");
}

/** A token as somebody might type it, in any case and with or without its dashes, or null if it can't be one. */
export function normaliseRecovery(token) {
  if (typeof token !== "string") return null;
  const bare = token.toUpperCase().replace(/[\s-]/g, "");
  return bare.length === LENGTH && [...bare].every((character) => ALPHABET.includes(character)) ? bare : null;
}

const hashToken = (bare) => createHash("sha256").update(bare).digest("hex");

/**
 * A new recovery token for a host's household, in place of any before it, which stops working.
 * The token is in what this returns and nowhere else.
 */
export async function issueRecovery(db, host, now = new Date()) {
  const token = newRecoveryToken();
  await db.query("update households set recovery_hash = $2, recovery_made_at = $3 where id = $1",
    [host.household, hashToken(normaliseRecovery(token)), now]);
  return { recovery: token, madeAt: now.toISOString() };
}

/**
 * A new host taking the place of the household's others, with the household's recovery token: the
 * others are revoked, and the new host gets a credential of its own, in what this returns and
 * nowhere else. The token goes on working until a new kit replaces it, so a recovery that goes
 * wrong partway can be tried again with the same kit.
 *
 * The revoked hosts' e2 keys are removed too, once the rest is done: a stolen computer's key would
 * otherwise go on reaching the bucket. Each is marked once it's gone, and any that iDrive couldn't
 * be reached for is tried again until it is (removeRevokedKeys), so the new host can carry on.
 */
export async function recoverHost(db, idrive, { token, name, now = new Date() }) {
  const bare = normaliseRecovery(token);
  if (!bare) throw new ServiceError(403, "recovery_invalid", INVALID);
  const hostName = typeof name === "string" && name.trim() ? name.trim() : "Uncloud host";
  if (hostName.length > 100) throw new ServiceError(400, "bad_request", "A host's name is up to 100 characters.");

  const { household, host, credential } = await db.transaction(async (tx) => {
    const { rows: [found] } = await tx.query(
      "select * from households where recovery_hash = $1 for update", [hashToken(bare)]);
    if (!found) throw new ServiceError(403, "recovery_invalid", INVALID);
    await tx.query("update hosts set revoked_at = $2 where household_id = $1 and revoked_at is null", [found.id, now]);
    const made = randomBytes(32).toString("base64url");
    const { rows: [added] } = await tx.query(
      "insert into hosts (household_id, name, credential_hash, created_at) values ($1, $2, $3, $4) returning id",
      [found.id, hostName, hashCredential(made), now]);
    return { household: found, host: { id: added.id, name: hostName }, credential: made };
  });

  await removeRevokedKeys(db, idrive, { household: household.id, now });
  return {
    household: { id: household.id },
    host,
    credential,
    // Still the kit in use: the token typed in goes on working until a new kit replaces it.
    recoveryMadeAt: household.recovery_made_at ? new Date(household.recovery_made_at).toISOString() : null,
  };
}

/**
 * Removes the e2 key of every revoked host whose key may still be there, of one household or of all,
 * marking each once it's gone. One iDrive won't answer for now is left for next time: this runs
 * after each recovery, whenever the household's host asks for its storage, and every night.
 */
export async function removeRevokedKeys(db, idrive, { household = null, now = new Date() } = {}) {
  const { rows } = await db.query(
    `select hosts.id, households.account_email, households.storage_dn from hosts
     join households on households.id = hosts.household_id
     where hosts.revoked_at is not null and hosts.key_removed_at is null
       and households.storage_dn is not null and ($1::uuid is null or households.id = $1)`,
    [household]);
  let removed = 0;
  let failed = 0;
  for (const row of rows) {
    try {
      await removeKeysNamed(idrive.reseller, { email: row.account_email, storageDn: row.storage_dn }, `host-${row.id}`);
      await db.query("update hosts set key_removed_at = $2 where id = $1", [row.id, now]);
      removed += 1;
    } catch (error) {
      failed += 1;
      console.error(`Removing revoked host ${row.id}'s key, to be tried again:`, error);
    }
  }
  return { removed, failed };
}
