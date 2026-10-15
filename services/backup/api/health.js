import { database } from "../lib/db.mjs";
import { checkHealth } from "../lib/health.mjs";
import { json } from "../lib/http.mjs";

// Whether the service is set up. See lib/health.mjs.
export async function GET() {
  return json(200, await checkHealth(process.env, database()));
}
