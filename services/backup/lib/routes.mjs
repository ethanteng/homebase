// What each endpoint does, given { db, env, now }. The files under api/ hand these the real ones;
// the tests hand them PGlite and a clock.

import { authenticateHost } from "./hosts.mjs";
import { body, carries, json, needDatabase, ServiceError } from "./http.mjs";
import { createInvite, listInvites, redeemInvite } from "./invites.mjs";

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

/** GET /api/host: which host this is, by its credential, so it can tell its credential still works. */
export async function currentHostRoute(request, { db, now }) {
  const host = await authenticateHost(needDatabase(db), request, now);
  if (!host) throw new ServiceError(401, "not_a_host", "That isn't the credential of a host Uncloud Backup knows.");
  return json(200, { host: { id: host.id, name: host.name }, household: { id: host.household } });
}
