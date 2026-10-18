import { fromVercelCron } from "../../lib/cron.mjs";
import { database, ready } from "../../lib/db.mjs";
import { connectIdrive } from "../../lib/idrive.mjs";
import { nightly } from "../../lib/nightly.mjs";

// Vercel Cron's nightly call (vercel.json). See lib/nightly.mjs for what it does. Only Vercel gets
// in, by the cron secret.
export async function GET(request) {
  if (!fromVercelCron(request, process.env.CRON_SECRET)) {
    return new Response("Only Vercel Cron calls this.", { status: 401 });
  }
  const db = database();
  const done = await nightly({ db: db ? await ready(db) : null, idrive: connectIdrive(process.env), now: new Date() });
  return Response.json(done, { headers: { "cache-control": "no-store" } });
}
