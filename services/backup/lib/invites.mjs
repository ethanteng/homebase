import { createHash, randomBytes } from "node:crypto";
import { ServiceError } from "./http.mjs";
import { hashCredential } from "./hosts.mjs";

// Invite codes are typed by people, so they leave out what's easily misread (0 and O, 1 and I) and
// are grouped in fours: K7QM-3XPD-9TWA-HF2R. Sixteen characters from 32 is 80 bits, too many to guess.
const ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
const LENGTH = 16;

const INVALID = "That invite code isn't one Uncloud Backup knows, or it has been used, or it has expired.";

export function newInviteCode() {
  // 256 is a multiple of 32, so taking each byte modulo 32 favours no character.
  const characters = [...randomBytes(LENGTH)].map((byte) => ALPHABET[byte % ALPHABET.length]).join("");
  return characters.match(/.{4}/g).join("-");
}

/** A code as somebody might type it, in any case and with or without its dashes, or null if it can't be one. */
export function normalise(code) {
  if (typeof code !== "string") return null;
  const bare = code.toUpperCase().replace(/[\s-]/g, "");
  return bare.length === LENGTH && [...bare].every((character) => ALPHABET.includes(character)) ? bare : null;
}

function hashCode(bare) {
  return createHash("sha256").update(bare).digest("hex");
}

/** Makes an invite. The code is in what this returns and nowhere else, so whoever makes one passes it on. */
export async function createInvite(db, { note = null, days = 30, now = new Date() } = {}) {
  if (!Number.isInteger(days) || days < 1 || days > 365) {
    throw new ServiceError(400, "bad_request", "An invite lasts between 1 and 365 days.");
  }
  if (note !== null && (typeof note !== "string" || note.length > 200)) {
    throw new ServiceError(400, "bad_request", "A note is up to 200 characters.");
  }
  const code = newInviteCode();
  const expires = new Date(now.getTime() + days * 86_400_000);
  const { rows: [invite] } = await db.query(
    "insert into invites (code_hash, note, created_at, expires_at) values ($1, $2, $3, $4) returning id",
    [hashCode(normalise(code)), note, now, expires]);
  return { id: invite.id, code, note, expiresAt: expires.toISOString() };
}

/** Every invite and what became of it. Never the codes, which aren't kept. */
export async function listInvites(db, now = new Date()) {
  const { rows } = await db.query(
    "select id, note, created_at, expires_at, redeemed_at, household_id from invites order by created_at desc");
  return rows.map((invite) => ({
    id: invite.id,
    note: invite.note,
    createdAt: new Date(invite.created_at).toISOString(),
    expiresAt: new Date(invite.expires_at).toISOString(),
    state: invite.redeemed_at ? "used" : new Date(invite.expires_at) <= now ? "expired" : "open",
    redeemedAt: invite.redeemed_at ? new Date(invite.redeemed_at).toISOString() : null,
    household: invite.household_id,
  }));
}

/**
 * Uses an invite: a household for it, and a host for that household with a credential of its own.
 * The credential is in what this returns and nowhere else; the host keeps it. All of it happens or
 * none of it does, and an invite is used once even when two hosts try it at the same moment.
 */
export async function redeemInvite(db, { code, name, now = new Date() }) {
  const bare = normalise(code);
  if (!bare) throw new ServiceError(403, "invite_invalid", INVALID);
  const hostName = typeof name === "string" && name.trim() ? name.trim() : "Uncloud host";
  if (hostName.length > 100) throw new ServiceError(400, "bad_request", "A host's name is up to 100 characters.");

  return db.transaction(async (tx) => {
    // Locked, so a second try at the same code waits for this one, then finds it used.
    const { rows: [invite] } = await tx.query(
      "select id from invites where code_hash = $1 and redeemed_at is null and expires_at > $2 for update",
      [hashCode(bare), now]);
    // Unknown, used and expired all get the same answer: telling them apart tells somebody guessing
    // which codes exist.
    if (!invite) throw new ServiceError(403, "invite_invalid", INVALID);
    const { rows: [household] } = await tx.query(
      "insert into households (created_at) values ($1) returning id", [now]);
    const credential = randomBytes(32).toString("base64url");
    const { rows: [host] } = await tx.query(
      "insert into hosts (household_id, name, credential_hash, created_at) values ($1, $2, $3, $4) returning id",
      [household.id, hostName, hashCredential(credential), now]);
    await tx.query("update invites set redeemed_at = $1, household_id = $2 where id = $3", [now, household.id, invite.id]);
    return { household: { id: household.id }, host: { id: host.id, name: hostName }, credential };
  });
}
