// What each endpoint does, given { db, idrive, env, now }. The files under api/ hand these the real
// ones; the tests hand them PGlite, a pretend iDrive and a clock.

import { authenticateHost } from "./hosts.mjs";
import { body, carries, json, needDatabase, needIdrive, ServiceError } from "./http.mjs";
import { createInvite, listInvites, redeemInvite } from "./invites.mjs";
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
  const { household, state } = await advanceSetup(db, needIdrive(idrive), host.household, { now, until });
  if (state.state !== "ready" || !timeForKey(until)) {
    // The host isn't told what went wrong, only that it did: that's for the operator, who sees it.
    const setup = state.state === "ready" ? { state: "setting_up", step: "host key" } : { state: state.state, step: state.step };
    return json(202, { setup }, { "retry-after": setup.state === "failing" ? "60" : "5" });
  }
  return json(201, { storage: await issueHostKey(db, idrive, household, host, { now }) });
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
