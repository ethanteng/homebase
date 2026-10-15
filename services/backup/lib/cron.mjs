import { carries } from "./http.mjs";

/**
 * Whether a request is Vercel Cron's. Vercel sends the project's CRON_SECRET as a bearer token when
 * there is one. Without one, nothing is let in: a nightly job anybody could start is one anybody
 * could start a thousand times.
 */
export function fromVercelCron(request, secret) {
  return carries(request, secret);
}
