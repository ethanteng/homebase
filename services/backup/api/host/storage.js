import { route } from "../../lib/http.mjs";
import { hostStorageRoute } from "../../lib/routes.mjs";

// Where a host backs up, and its key, setting the household's storage up first if need be. See
// lib/routes.mjs.
export const POST = route(hostStorageRoute);
