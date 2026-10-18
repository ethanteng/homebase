// What each endpoint does, given { db, idrive, env, now }. The files under api/ hand these the real
// ones; the tests hand them PGlite, a pretend iDrive and a clock.

import { authenticateHost } from "./hosts.mjs";
import { body, carries, json, needDatabase, needIdrive, ServiceError } from "./http.mjs";
import { createInvite, listInvites, redeemInvite } from "./invites.mjs";
import { abandonRecovery, confirmRecovery, issueRecovery, recoverHost, removeRevokedKeys } from "./recovery.mjs";
import { advanceSetup, BUDGET, describe, issueHostKey, timeForKey } from "./setup.mjs";

function operatorOnly(request, env) {
  if (!carries(request, env.OPERATOR_TOKEN)) {
    throw new ServiceError(401, "operator_only", "Only the operator of Uncloud Backup can do that.");
  }
}

/** GET /api/operator/invites: every invite and what became of it. */
export async function listInvitesRoute(request, { db, env, now }) {
  operatorOnly(request, env);
  return json(200, { invites: await listInvites(needDatabase(db), now) });
}

/** POST /api/operator/invites { note?, days? }: a new invite. Its code is in this answer and nowhere else. */
export async function createInviteRoute(request, { db, env, now }) {
  operatorOnly(request, env);
  const { note = null, days = 30 } = await body(request);
  return json(201, await createInvite(needDatabase(db), { note, days, now }));
}

/**
 * POST /api/hosts { invite, name }: a host joining with an invite code. It gets its household and
 * a credential it keeps and sends with everything after, shown this once.
 */
export async function registerHostRoute(request, { db, now }) {
  const { invite, name } = await body(request);
  return json(201, await redeemInvite(needDatabase(db), { code: invite, name, now }));
}

async function theHost(request, db, now) {
  // Provisional hosts are hosts here too: they get storage, to try a kit's password on, and confirm or withdraw.
  const host = await authenticateHost(needDatabase(db), request, now);
  if (!host) throw new ServiceError(401, "not_a_host", "That isn't the credential of a host Uncloud Backup knows.");
  return host;
}

/** GET /api/host: which host this is, by its credential, so it can tell its credential still works. */
export async function currentHostRoute(request, { db, now }) {
  const host = await theHost(request, db, now);
  return json(200, { host: { id: host.id, name: host.name }, household: { id: host.household } });
}

/**
 * POST /api/host/storage: where this host backs up, with a key of its own, shown this once. Any key
 * it had before stops working, so a host asks when it has none or its key has stopped working.
 *
 * The household's storage is set up first if it isn't yet, as far as this request has time for.
 * Until it's done the answer is 202, with how far it has got, and the host asks again.
 */
export async function hostStorageRoute(request, { db, idrive, now, until = Date.now() + BUDGET }) {
  const host = await theHost(request, db, now);
  // Any key a revoked host of this household still has goes first, if it hasn't already.
  await removeRevokedKeys(db, needIdrive(idrive), { household: host.household, now });
  const { household, state } = await advanceSetup(db, needIdrive(idrive), host.household, { now, until });
  // The host isn't told what went wrong, only that it did: that's for the operator, who sees it.
  const askAgain = (setup) => json(202, { setup }, { "retry-after": setup.state === "failing" ? "60" : "5" });
  if (state.state !== "ready") return askAgain({ state: state.state, step: state.step });
  // Ready, but this request has no time left for a key, or another is making this host's key now.
  const storage = timeForKey(until) ? await issueHostKey(db, idrive, household, host, { now }) : null;
  return storage ? json(201, { storage }) : askAgain({ state: "setting_up", step: "host key" });
}

/**
 * POST /api/host/recovery: a new recovery token for this host's household, for its recovery kit,
 * shown this once. Any token before it stops working.
 */
export async function makeRecoveryRoute(request, { db, now }) {
  const host = await theHost(request, db, now);
  // A new kit would retire the token this host is still part-way through using.
  if (host.provisional) {
    throw new ServiceError(409, "recovering", "Finish taking the lost host's place before making a new recovery kit.");
  }
  return json(201, await issueRecovery(db, host, now));
}

/**
 * POST /api/hosts/recover { recovery, name }: a new host, with the household's recovery token, let
 * in provisionally to take a lost one's place (lib/recovery.mjs). It gets a credential of its own,
 * shown this once, and nothing about the household's hosts changes until it confirms.
 */
export async function recoverHostRoute(request, { db, idrive, now }) {
  const { recovery, name } = await body(request);
  // Not without iDrive: confirming will have to remove the lost host's key, and only iDrive can.
  needIdrive(idrive);
  return json(201, await recoverHost(needDatabase(db), { token: recovery, name, now }));
}

/**
 * POST /api/host/recovery/confirm: a provisional host, the kit's password having opened the
 * backup, taking the lost host's place. The household's other hosts are revoked and their keys
 * removed.
 */
export async function confirmRecoveryRoute(request, { db, idrive, now }) {
  const host = await theHost(request, db, now);
  return json(200, await confirmRecovery(db, needIdrive(idrive), host, now));
}

/** POST /api/host/recovery/abandon: a provisional host withdrawing. Only it is retired. */
export async function abandonRecoveryRoute(request, { db, idrive, now }) {
  const host = await theHost(request, db, now);
  return json(200, await abandonRecovery(db, needIdrive(idrive), host, now));
}

/** GET /api/operator/households: every household, how far its setup has got, and why it's stuck if it is. */
export async function listHouseholdsRoute(request, { db, env, now }) {
  operatorOnly(request, env);
  const { rows } = await needDatabase(db).query(`
    select households.*,
      (select count(*) from hosts where household_id = households.id and revoked_at is null)::int as hosts
    from households order by created_at desc`);
  return json(200, {
    households: rows.map((household) => ({
      id: household.id,
      createdAt: new Date(household.created_at).toISOString(),
      hosts: household.hosts,
      account: household.account_email,
      region: household.region,
      bucket: household.bucket,
      setup: describe(household, now),
    })),
  });
}
