import { timingSafeEqual } from "node:crypto";

/**
 * Whether a request is Vercel Cron's. Vercel sends the project's CRON_SECRET as a bearer token when
 * there is one. Without one, nothing is let in: a nightly job anybody could start is one anybody
 * could start a thousand times.
 */
export function fromVercelCron(request, secret) {
  if (!secret) return false;
  const given = Buffer.from(request.headers.get("authorization") ?? "");
  const expected = Buffer.from(`Bearer ${secret}`);
  return given.length === expected.length && timingSafeEqual(given, expected);
}
