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
 * otherwise go on reaching the bucket. If iDrive can't be reached for that, it's logged, and the
 * new host carries on.
 */
export async function recoverHost(db, idrive, { token, name, now = new Date() }) {
  const bare = normaliseRecovery(token);
  if (!bare) throw new ServiceError(403, "recovery_invalid", INVALID);
  const hostName = typeof name === "string" && name.trim() ? name.trim() : "Uncloud host";
  if (hostName.length > 100) throw new ServiceError(400, "bad_request", "A host's name is up to 100 characters.");

  const { household, host, credential, revoked } = await db.transaction(async (tx) => {
    const { rows: [found] } = await tx.query(
      "select * from households where recovery_hash = $1 for update", [hashToken(bare)]);
    if (!found) throw new ServiceError(403, "recovery_invalid", INVALID);
    const { rows: gone } = await tx.query(
      "update hosts set revoked_at = $2 where household_id = $1 and revoked_at is null returning id", [found.id, now]);
    const made = randomBytes(32).toString("base64url");
    const { rows: [added] } = await tx.query(
      "insert into hosts (household_id, name, credential_hash, created_at) values ($1, $2, $3, $4) returning id",
      [found.id, hostName, hashCredential(made), now]);
    return { household: found, host: { id: added.id, name: hostName }, credential: made, revoked: gone.map((row) => row.id) };
  });

  if (idrive && household.account_email && household.storage_dn) {
    const account = { email: household.account_email, storageDn: household.storage_dn };
    for (const id of revoked) {
      await removeKeysNamed(idrive.reseller, account, `host-${id}`)
        .catch((error) => console.error(`Removing revoked host ${id}'s key:`, error));
    }
  }
  return { household: { id: household.id }, host, credential };
}
