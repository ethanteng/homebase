import { route } from "../lib/http.mjs";
import { registerHostRoute } from "../lib/routes.mjs";

// A host joining with an invite code. See lib/routes.mjs.
export const POST = route(registerHostRoute);
