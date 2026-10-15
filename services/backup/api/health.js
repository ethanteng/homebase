import { databaseUrl, query } from "../lib/db.mjs";
import { checkHealth } from "../lib/health.mjs";

// Whether the service is set up. See lib/health.mjs.
export async function GET() {
  const health = await checkHealth(process.env, databaseUrl() ? query : null);
  return Response.json(health, { headers: { "cache-control": "no-store" } });
}
