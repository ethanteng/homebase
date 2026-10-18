import { removeRevokedKeys, retireLapsedRecoveries } from "./recovery.mjs";

// The service's nightly work, run by Vercel Cron. So far: retiring hosts that began a recovery and
// never finished it, and removing any revoked host's key that's still there. Checking every
// bucket's protection, reading usage and carrying out removals come next (docs/backup.md).

export async function nightly({ db, idrive, now = new Date() }) {
  const ran = [];
  if (db && idrive) {
    ran.push({ job: "lapsed recoveries", retired: await retireLapsedRecoveries(db, now) });
    ran.push({ job: "revoked keys", ...(await removeRevokedKeys(db, idrive, { now })) });
  }
  return { ran };
}
