"use strict";

// The teaser video on the homepage and the acquisition pages, swappable without a deploy.
//
// The pages don't name a video. The player loads /video/embed and "Watch in a new tab" opens
// /video, both rewritten here by vercel.json. Which video is the `video` item in the Vercel
// Global Config (formerly Edge Config) connected to this project: an item can be changed in the
// dashboard and is read on the next request, where an environment variable only reaches
// deployments made after it changed. The item is one of:
//
//   "oELh5dwlmHs" or any YouTube link          YouTube: both routes redirect there
//   "https://…/teaser.mp4"                     a video file, played by our own player page
//   { "mp4": "https://…", "poster": "https://…", "captions": "https://…" }
//                                              the same, with a poster frame and WebVTT captions
//
// Anything that stops a video being read — no Global Config connected, no `video` item, a value
// that is neither of those, Global Config slow or down — sends visitors to DEFAULT_VIDEO, so a
// typo in the dashboard can't leave the player empty.

const DEFAULT_VIDEO = "oELh5dwlmHs";
const ITEM = "video";
const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;
const READ_TIMEOUT_MS = 1500;
const CAPTIONS_TIMEOUT_MS = 5000;
const MAX_CAPTIONS_CHARS = 1_000_000;
// Browsers ask every time; the CDN answers from its copy for a minute, so a change in the
// dashboard reaches visitors within a minute or two.
const CACHE_CONTROL = "public, max-age=0, s-maxage=60, stale-while-revalidate=600";
// The player page loads the video and poster from https and the captions from here, and nothing
// else at all; it has no script. Only this site may frame it.
const PLAYER_POLICY =
  "default-src 'none'; media-src https: 'self'; img-src https:; style-src 'unsafe-inline'; frame-ancestors 'self'";

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

/** An https address, normalised by the URL parser (which percent-encodes quotes and brackets). */
function https(value) {
  if (typeof value !== "string") return null;
  let url;
  try {
    url = new URL(value.trim());
  } catch {
    return null;
  }
  return url.protocol === "https:" && !url.username && !url.password ? url.href : null;
}

/**
 * What to play: `{ youtube }` or `{ mp4, poster, captions }`, or null when the value is neither.
 * A poster or captions address that isn't https is left out rather than failing the video.
 */
function choose(value) {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    const mp4 = https(value.mp4);
    return mp4 ? { mp4, poster: https(value.poster), captions: https(value.captions) } : null;
  }
  const id = videoId(value);
  if (id) return { youtube: id };
  const mp4 = https(value);
  return mp4 && /\.mp4$/i.test(new URL(mp4).pathname) ? { mp4, poster: null, captions: null } : null;
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

/** Which of the three addresses this is: "embed" (the player), "captions", or "watch" (the link). */
function routeOf(rawUrl) {
  // The base is only to satisfy the parser; `request.url` is a path. A rewrite may show either
  // the query it added or the address the browser asked for, so both are recognised.
  const url = new URL(rawUrl || "/", "http://video.invalid");
  if (url.searchParams.has("embed") || url.pathname.endsWith("/embed")) return "embed";
  if (url.searchParams.has("captions") || url.pathname.endsWith("/captions")) return "captions";
  return "watch";
}

const attribute = (value) => value.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;");

/**
 * The page the iframe shows for a video file. Nothing but a <video>: native controls, fullscreen
 * through the iframe's `allowfullscreen`, and the card's own background around it. With a poster
 * nothing is downloaded until somebody presses Play; without one, just enough for a first frame.
 */
function playerPage({ mp4, poster, captions }) {
  const posterAttribute = poster ? ` poster="${attribute(poster)}"` : "";
  // Same-origin, relayed by this function, so the captions never depend on the file host's CORS.
  const track = captions ? '\n  <track kind="captions" src="/video/captions" srclang="en" label="English">' : "";
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>Uncloud Home teaser video</title>
<style>
  html, body { margin: 0; height: 100%; background: #0e1a2d; }
  video { display: block; width: 100%; height: 100%; background: #0e1a2d; }
</style>
</head>
<body>
<video controls playsinline preload="${poster ? "none" : "metadata"}"${posterAttribute}>
  <source src="${attribute(mp4)}" type="video/mp4">${track}
</video>
</body>
</html>
`;
}

/** The captions file's text when it is WebVTT of a sensible size, or null. */
async function fetchCaptions(url) {
  try {
    const answer = await fetch(url, { signal: AbortSignal.timeout(CAPTIONS_TIMEOUT_MS) });
    if (!answer.ok) return null;
    const text = await answer.text();
    // An error page or the wrong file served in its place is not passed off as captions.
    if (text.length > MAX_CAPTIONS_CHARS || !text.replace(/^﻿/, "").startsWith("WEBVTT")) return null;
    return text;
  } catch {
    return null;
  }
}

function redirect(response, location) {
  // No Referrer-Policy here: a redirect's policy replaces the iframe's, and YouTube refuses to
  // play an embed that arrives without a referrer.
  response.statusCode = 302;
  response.setHeader("Location", location);
  response.setHeader("Cache-Control", CACHE_CONTROL);
  return response.end();
}

function page(response, status, type, body) {
  response.statusCode = status;
  response.setHeader("Content-Type", type);
  response.setHeader("Cache-Control", CACHE_CONTROL);
  response.setHeader("X-Content-Type-Options", "nosniff");
  return response.end(body);
}

// Plain Node response calls rather than Vercel's helpers, so the landing page's Vite server can
// answer /video with this same function when previewing locally.
module.exports = async function handler(request, response) {
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.statusCode = 405;
    response.setHeader("Allow", "GET, HEAD");
    return response.end();
  }
  const video = choose(await configured(process.env)) ?? { youtube: DEFAULT_VIDEO };
  const route = routeOf(request.url);

  if (route === "captions") {
    const text = video.captions ? await fetchCaptions(video.captions) : null;
    if (text === null) return page(response, 404, "text/plain; charset=utf-8", "No captions.\n");
    return page(response, 200, "text/vtt; charset=utf-8", text);
  }
  if (video.youtube) {
    return redirect(response, route === "embed"
      ? `https://www.youtube-nocookie.com/embed/${video.youtube}?rel=0&playsinline=1`
      : `https://www.youtube.com/watch?v=${video.youtube}`);
  }
  if (route === "watch") return redirect(response, video.mp4);
  response.setHeader("Content-Security-Policy", PLAYER_POLICY);
  return page(response, 200, "text/html; charset=utf-8", playerPage(video));
};

// Exported for the tests.
module.exports.videoId = videoId;
module.exports.DEFAULT_VIDEO = DEFAULT_VIDEO;
