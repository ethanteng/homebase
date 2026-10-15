import { fromVercelCron } from "../../lib/cron.mjs";

// Vercel Cron's nightly call (vercel.json). What it's for comes with the service's first real work:
// checking every household's bucket is still protected and putting right what isn't, reading usage,
// and carrying out removals once their delay is up (docs/backup.md). For now it only makes sure it's
// Vercel calling.
export async function GET(request) {
  if (!fromVercelCron(request, process.env.CRON_SECRET)) {
    return new Response("Only Vercel Cron calls this.", { status: 401 });
  }
  return Response.json({ ran: [] }, { headers: { "cache-control": "no-store" } });
}
