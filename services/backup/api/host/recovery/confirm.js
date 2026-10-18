import { route } from "../../../lib/http.mjs";
import { confirmRecoveryRoute } from "../../../lib/routes.mjs";

// A provisional host taking the lost host's place, once the kit's password has opened the backup.
// See lib/routes.mjs.
export const POST = route(confirmRecoveryRoute);
