import { route } from "../lib/http.mjs";
import { currentHostRoute } from "../lib/routes.mjs";

// Which host is asking, by its credential. See lib/routes.mjs.
export const GET = route(currentHostRoute);
