import { route } from "../../lib/http.mjs";
import { makeRecoveryRoute } from "../../lib/routes.mjs";

// A new recovery token for the host's household, for its recovery kit. See lib/routes.mjs.
export const POST = route(makeRecoveryRoute);
