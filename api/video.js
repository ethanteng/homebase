"use strict";

// The teaser video on the homepage and the acquisition pages, swappable without a deploy.
//
// The pages don't name a video. The player loads /video/embed and "Watch on YouTube" opens
// /video, both rewritten here by vercel.json, and this sends each on to YouTube. Which video is
// the `video` item in the Vercel Global Config (formerly Edge Config) connected to this project:
// an item can be changed in the dashboard and is read on the next request, where an environment
// variable only reaches deployments made after it changed.
//
// Anything that stops a video being read — no Global Config connected, no `video` item, a value
// that isn't a YouTube video, Global Config slow or down — sends visitors to DEFAULT_VIDEO, so a
// typo in the dashboard can't leave the player empty.

const DEFAULT_VIDEO = "oELh5dwlmHs";
const ITEM = "video";
const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;
const READ_TIMEOUT_MS = 1500;
// Browsers ask every time; the CDN answers from its copy for a minute, so a change in the
// dashboard reaches visitors within a minute or two.
const CACHE_CONTROL = "public, max-age=0, s-maxage=60, stale-while-revalidate=600";

/**
 * The YouTube video id in what somebody put in the dashboard: an id, or any link to the video
 * (watch, youtu.be, embed, shorts or live). Null when there isn't one.
 */
function videoId(value) {
  if (typeof value !== "string") return null;
  const text = value.trim();
  if (VIDEO_ID.test(text)) return text;
  let url;
  try {
    url = new URL(text);
  } catch {
    return null;
  }
  const host = url.hostname.replace(/^(www|m)\./, "");
  let found = null;
  if (host === "youtu.be") found = url.pathname.slice(1);
  else if (host === "youtube.com" || host === "youtube-nocookie.com") {
    found = url.searchParams.get("v") ?? /^\/(?:embed|shorts|live)\/([^/]+)/.exec(url.pathname)?.[1];
  }
  return found && VIDEO_ID.test(found) ? found : null;
}

/**
 * The `video` item, read over Global Config's REST API with the connection string Vercel puts in
 * GLOBAL_CONFIG (`https://global-config.vercel.com/<id>?token=<token>`), or in EDGE_CONFIG for a
 * store connected before Edge Config was renamed. Read by hand rather than through
 * @vercel/global-config because nothing is installed for these functions.
 */
async function configured(env) {
  let connection;
  try {
    connection = new URL(env.GLOBAL_CONFIG || env.EDGE_CONFIG);
  } catch {
    return undefined;
  }
  const token = connection.searchParams.get("token");
  if (!token) return undefined;
  try {
    const response = await fetch(`${connection.origin}${connection.pathname}/item/${ITEM}`, {
      headers: { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(READ_TIMEOUT_MS)
    });
    return response.ok ? await response.json() : undefined;
  } catch {
    return undefined;
  }
}

/** Whether this is the player's request rather than somebody following a link. */
function wantsEmbed(rawUrl) {
  // The base is only to satisfy the parser; `request.url` is a path.
  const url = new URL(rawUrl || "/", "http://video.invalid");
  return url.searchParams.has("embed") || url.pathname.endsWith("/embed");
}

// Plain Node response calls rather than Vercel's helpers, so the landing page's Vite server can
// answer /video with this same function when previewing locally.
module.exports = async function handler(request, response) {
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.statusCode = 405;
    response.setHeader("Allow", "GET, HEAD");
    return response.end();
  }
  const id = videoId(await configured(process.env)) ?? DEFAULT_VIDEO;
  const location = wantsEmbed(request.url)
    ? `https://www.youtube-nocookie.com/embed/${id}?rel=0&playsinline=1`
    : `https://www.youtube.com/watch?v=${id}`;
  // No Referrer-Policy here: a redirect's policy replaces the iframe's, and YouTube refuses to
  // play an embed that arrives without a referrer.
  response.statusCode = 302;
  response.setHeader("Location", location);
  response.setHeader("Cache-Control", CACHE_CONTROL);
  return response.end();
};

// Exported for the tests.
module.exports.videoId = videoId;
module.exports.DEFAULT_VIDEO = DEFAULT_VIDEO;
