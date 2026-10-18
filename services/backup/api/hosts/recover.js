import { route } from "../../lib/http.mjs";
import { recoverHostRoute } from "../../lib/routes.mjs";

// A new host taking a lost one's place with the household's recovery token. See lib/routes.mjs.
export const POST = route(recoverHostRoute);
