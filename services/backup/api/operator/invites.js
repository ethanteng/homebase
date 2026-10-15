import { route } from "../../lib/http.mjs";
import { createInviteRoute, listInvitesRoute } from "../../lib/routes.mjs";

// The operator's invites. See lib/routes.mjs.
export const GET = route(listInvitesRoute);
export const POST = route(createInviteRoute);
