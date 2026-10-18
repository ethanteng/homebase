import { route } from "../../../lib/http.mjs";
import { abandonRecoveryRoute } from "../../../lib/routes.mjs";

// A provisional host withdrawing, the kit's password not having opened the backup. See lib/routes.mjs.
export const POST = route(abandonRecoveryRoute);
