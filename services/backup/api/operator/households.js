import { route } from "../../lib/http.mjs";
import { listHouseholdsRoute } from "../../lib/routes.mjs";

// The operator's view of every household and its setup. See lib/routes.mjs.
export const GET = route(listHouseholdsRoute);
