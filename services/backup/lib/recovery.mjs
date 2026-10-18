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
 * A new recovery token for a host's household, in place of any before it, which stops working,
 * including a recovery already under way with it: that host is retired here. The token is in what
 * this returns and nowhere else. The household is held for the whole change, so a confirm can't
 * take the household with the token this is replacing.
 */
export async function issueRecovery(db, host, now = new Date()) {
  const token = newRecoveryToken();
  const hash = hashToken(normaliseRecovery(token));
  await db.transaction(async (tx) => {
    await tx.query("select id from households where id = $1 for update", [host.household]);
    const { rows: [live] } = await tx.query(
      "select id from hosts where id = $1 and revoked_at is null and provisional_until is null for update",
      [host.id]);
    if (!live) throw new ServiceError(401, "not_a_host", "That isn't the credential of a host Uncloud Backup knows.");
    await tx.query("update households set recovery_hash = $2, recovery_made_at = $3 where id = $1",
      [host.household, hash, now]);
    await tx.query(
      "update hosts set revoked_at = $2 where household_id = $1 and provisional_until is not null and revoked_at is null",
      [host.household, now]);
  });
  return { recovery: token, madeAt: now.toISOString() };
}

/** How long a host that used the recovery token has to show it has the kit's password. */
export const PROVISIONAL_SECONDS = 3600;

/**
 * Recovery is two steps, so a mistyped password never costs the household the host it has. First,
 * a new host uses the household's recovery token and is let in provisionally: it gets a credential
 * of its own, shown this once, and with it the household's storage, to try the kit's password on.
 * Nothing else changes yet. Then it confirms (confirmRecovery), which retires the household's
 * other hosts, or withdraws (abandonRecovery), which retires only itself. One that does neither
 * within PROVISIONAL_SECONDS stops working, and the night retires it. The token goes on working
 * until a new kit replaces it, which retires a recovery already under way, so a recovery can always
 * be tried again with the kit that's current.
 */
export async function recoverHost(db, { token, name, now = new Date() }) {
  const bare = normaliseRecovery(token);
  if (!bare) throw new ServiceError(403, "recovery_invalid", INVALID);
  const hostName = typeof name === "string" && name.trim() ? name.trim() : "Uncloud host";
  if (hostName.length > 100) throw new ServiceError(400, "bad_request", "A host's name is up to 100 characters.");

  // Held with the hash check, so a new kit can't replace the token in the moment between them.
  return db.transaction(async (tx) => {
    const { rows: [household] } = await tx.query(
      "select * from households where recovery_hash = $1 for update", [hashToken(bare)]);
    if (!household) throw new ServiceError(403, "recovery_invalid", INVALID);
    // One recovery under way at a time: a new one retires any earlier that hasn't finished, so a
    // token can't open more than one provisional host, nor have more than one given a key.
    await tx.query(
      "update hosts set revoked_at = $2 where household_id = $1 and provisional_until is not null and revoked_at is null",
      [household.id, now]);
    const credential = randomBytes(32).toString("base64url");
    const { rows: [added] } = await tx.query(
      `insert into hosts (household_id, name, credential_hash, created_at, provisional_until)
       values ($1, $2, $3, $4, $5) returning id`,
      [household.id, hostName, hashCredential(credential), now, new Date(now.getTime() + PROVISIONAL_SECONDS * 1000)]);
    return {
      household: { id: household.id },
      host: { id: added.id, name: hostName },
      credential,
      // Still the kit in use: the token typed in goes on working until a new kit replaces it.
      recoveryMadeAt: household.recovery_made_at ? new Date(household.recovery_made_at).toISOString() : null,
    };
  });
}

/**
 * A provisional host that has shown it has the kit's password taking the lost host's place: the
 * household's other hosts are revoked and their e2 keys removed, and it's a host like any other.
 */
export async function confirmRecovery(db, idrive, host, now = new Date()) {
  if (!host.provisional) throw new ServiceError(409, "not_recovering", "This host isn't taking another's place.");
  const revoked = await db.transaction(async (tx) => {
    // Same lock a new kit takes, so this can't revoke the household with a token just replaced.
    await tx.query("select id from households where id = $1 for update", [host.household]);
    const { rows: [held] } = await tx.query(
      "select id from hosts where id = $1 and revoked_at is null and provisional_until > $2 for update", [host.id, now]);
    if (!held) {
      const { rows: [current] } = await tx.query("select revoked_at, provisional_until from hosts where id = $1", [host.id]);
      // A repeat of a confirm that already landed: this host is the household's host.
      if (current && current.revoked_at === null && current.provisional_until === null) {
        throw new ServiceError(409, "not_recovering", "This host isn't taking another's place.");
      }
      throw new ServiceError(401, "not_a_host", "That isn't the credential of a host Uncloud Backup knows.");
    }
    const { rows } = await tx.query(
      "update hosts set revoked_at = $3 where household_id = $1 and id <> $2 and revoked_at is null returning id",
      [host.household, host.id, now]);
    await tx.query("update hosts set provisional_until = null where id = $1", [host.id]);
    return rows.length;
  });
  await removeRevokedKeys(db, idrive, { household: host.household, now });
  return { revoked };
}

/**
 * A provisional host withdrawing, its password not having opened the backup: only it is retired,
 * and its key removed. The household's hosts are as they were.
 */
export async function abandonRecovery(db, idrive, host, now = new Date()) {
  if (!host.provisional) throw new ServiceError(409, "not_recovering", "This host isn't taking another's place.");
  // Provisional only. A confirm that already landed cleared that, and withdrawing must not retire
  // the host that just took the household's place: the check above can be stale by then.
  const { rows } = await db.query(
    `update hosts set revoked_at = $2
     where id = $1 and revoked_at is null and provisional_until is not null
     returning id`,
    [host.id, now]);
  if (rows.length === 0) {
    const { rows: [current] } = await db.query("select revoked_at, provisional_until from hosts where id = $1", [host.id]);
    if (current && current.revoked_at === null && current.provisional_until === null) {
      throw new ServiceError(409, "not_recovering", "This host isn't taking another's place.");
    }
    throw new ServiceError(401, "not_a_host", "That isn't the credential of a host Uncloud Backup knows.");
  }
  await removeRevokedKeys(db, idrive, { household: host.household, now });
  return { withdrawn: true };
}

/** Retires every provisional host whose time to confirm has run out, and removes its key. */
export async function retireLapsedRecoveries(db, now = new Date()) {
  const { rows } = await db.query(
    "update hosts set revoked_at = $1 where provisional_until <= $1 and revoked_at is null returning id", [now]);
  return rows.length;
}

/**
 * Removes the e2 key of every revoked host whose key may still be there, of one household or of all,
 * marking each once it's gone. One iDrive won't answer for now is left for next time: this runs
 * after a recovery is confirmed or withdrawn, whenever the household's host asks for its storage,
 * and every night.
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
