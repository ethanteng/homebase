import { createHash } from "node:crypto";
import { bearer } from "./http.mjs";

// A host's credential is 256 random bits, so a plain SHA-256 of it is as good as anything slower:
// there's no guessing it from the hash. Only the hash is kept.
export function hashCredential(credential) {
  return createHash("sha256").update(credential).digest("hex");
}

/**
 * The host a request comes from, by the credential it carries, or null. A revoked host is no host,
 * and nor is a provisional one whose time to confirm has run out. Noting when it was last seen is
 * the same statement as finding it.
 */
export async function authenticateHost(db, request, now = new Date()) {
  const credential = bearer(request);
  if (!credential) return null;
  const { rows: [host] } = await db.query(
    `update hosts set last_seen_at = $2
     where credential_hash = $1 and revoked_at is null and (provisional_until is null or provisional_until > $2)
     returning id, household_id, name, provisional_until`,
    [hashCredential(credential), now]);
  return host
    ? { id: host.id, household: host.household_id, name: host.name, provisional: host.provisional_until !== null }
    : null;
}
