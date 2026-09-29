"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const handler = require("./video.js");
const { videoId, DEFAULT_VIDEO } = handler;

const CONNECTION = "https://global-config.vercel.com/ecfg_test?token=read-token";
const OTHER = "dQw4w9WgXcQ";

function mockResponse() {
  return {
    headers: {},
    statusCode: 200,
    ended: false,
    body: undefined,
    setHeader(name, value) { this.headers[name] = value; },
    end(body) { this.ended = true; this.body = body; }
  };
}

const VARIABLES = ["GLOBAL_CONFIG", "EDGE_CONFIG"];

/**
 * Runs the function with `variable` (GLOBAL_CONFIG unless given) set to `connection` and Global
 * Config answering with `answer` — a function from the request to a Response, or a value to send
 * as JSON. Returns the response and every request Global Config received.
 */
async function request(url, { connection, answer, method = "GET", variable = "GLOBAL_CONFIG" } = {}) {
  const asked = [];
  const originalFetch = globalThis.fetch;
  const original = Object.fromEntries(VARIABLES.map((name) => [name, process.env[name]]));
  globalThis.fetch = async (to, init) => {
    asked.push({ to: String(to), init });
    if (typeof answer === "function") return answer(to, init);
    return new Response(JSON.stringify(answer), { headers: { "Content-Type": "application/json" } });
  };
  for (const name of VARIABLES) delete process.env[name];
  if (connection !== undefined) process.env[variable] = connection;
  try {
    const response = mockResponse();
    await handler({ method, url }, response);
    return { response, asked };
  } finally {
    globalThis.fetch = originalFetch;
    for (const name of VARIABLES) {
      if (original[name] === undefined) delete process.env[name];
      else process.env[name] = original[name];
    }
  }
}

const embedOf = (id) => `https://www.youtube-nocookie.com/embed/${id}?rel=0&playsinline=1`;
const watchOf = (id) => `https://www.youtube.com/watch?v=${id}`;

test("the player is sent to the embed and a link to the watch page", async () => {
  const embed = await request("/api/video?embed=1", { connection: CONNECTION, answer: OTHER });
  assert.equal(embed.response.statusCode, 302);
  assert.equal(embed.response.headers.Location, embedOf(OTHER));

  const watch = await request("/api/video", { connection: CONNECTION, answer: OTHER });
  assert.equal(watch.response.statusCode, 302);
  assert.equal(watch.response.headers.Location, watchOf(OTHER));
});

test("the embed is recognised whether the rewrite shows the query or the original path", async () => {
  const { response } = await request("/video/embed", { connection: CONNECTION, answer: OTHER });
  assert.equal(response.headers.Location, embedOf(OTHER));
});

test("Global Config is asked for the video item with the connection's token", async () => {
  const { asked } = await request("/api/video", { connection: CONNECTION, answer: OTHER });
  assert.equal(asked.length, 1);
  assert.equal(asked[0].to, "https://global-config.vercel.com/ecfg_test/item/video");
  assert.equal(asked[0].init.headers.Authorization, "Bearer read-token");
});

test("a store connected as EDGE_CONFIG, before the rename, still works", async () => {
  const connection = "https://edge-config.vercel.com/ecfg_old?token=old-token";
  const { response, asked } = await request("/api/video", { connection, answer: OTHER, variable: "EDGE_CONFIG" });
  assert.equal(response.headers.Location, watchOf(OTHER));
  assert.equal(asked[0].to, "https://edge-config.vercel.com/ecfg_old/item/video");
  assert.equal(asked[0].init.headers.Authorization, "Bearer old-token");
});

test("without a Global Config the default video plays and nothing is fetched", async () => {
  const { response, asked } = await request("/api/video?embed=1");
  assert.equal(response.headers.Location, embedOf(DEFAULT_VIDEO));
  assert.equal(asked.length, 0);
});

test("anything short of a readable video falls back to the default", async () => {
  const failures = {
    "no video item": () => new Response("", { status: 404 }),
    "not a YouTube video": "https://vimeo.com/123456",
    "a typo": "oELh5dwlmH",
    "not a string": { id: OTHER },
    "Global Config unreachable": () => { throw new TypeError("fetch failed"); },
    "Global Config answering nonsense": () => new Response("<html>", { status: 200 })
  };
  for (const [name, answer] of Object.entries(failures)) {
    const { response } = await request("/api/video", { connection: CONNECTION, answer });
    assert.equal(response.headers.Location, watchOf(DEFAULT_VIDEO), name);
  }
  const { response } = await request("/api/video", { connection: "not a url", answer: OTHER });
  assert.equal(response.headers.Location, watchOf(DEFAULT_VIDEO), "a malformed connection string");
});

test("a pasted link is as good as an id", () => {
  for (const pasted of [
    OTHER,
    `  ${OTHER}\n`,
    `https://www.youtube.com/watch?v=${OTHER}&t=42s`,
    `https://m.youtube.com/watch?v=${OTHER}`,
    `https://youtu.be/${OTHER}?si=share`,
    `https://www.youtube.com/embed/${OTHER}`,
    `https://www.youtube-nocookie.com/embed/${OTHER}?rel=0`,
    `https://www.youtube.com/shorts/${OTHER}`,
    `https://www.youtube.com/live/${OTHER}`
  ]) assert.equal(videoId(pasted), OTHER, pasted);
});

test("nothing but a YouTube video id ever reaches the redirect", () => {
  for (const value of [
    "",
    "https://www.youtube.com/@Uncloud-life",
    `https://evil.example/watch?v=${OTHER}`,
    `https://youtube.com.evil.example/watch?v=${OTHER}`,
    `https://www.youtube.com/watch?v=${OTHER}/../x`,
    `${OTHER}"><script>`,
    null,
    42
  ]) assert.equal(videoId(value), null, String(value));
});

test("the CDN may hold the answer briefly and browsers not at all", async () => {
  const { response } = await request("/api/video", { connection: CONNECTION, answer: OTHER });
  assert.match(response.headers["Cache-Control"], /max-age=0/);
  assert.match(response.headers["Cache-Control"], /s-maxage=60/);
  assert.equal(response.headers["Referrer-Policy"], undefined);
});

test("only GET and HEAD are answered", async () => {
  const head = await request("/api/video", { method: "HEAD" });
  assert.equal(head.response.statusCode, 302);
  const post = await request("/api/video", { method: "POST" });
  assert.equal(post.response.statusCode, 405);
  assert.equal(post.response.headers.Allow, "GET, HEAD");
  assert.equal(post.response.headers.Location, undefined);
});

const MP4 = "https://store.public.blob.vercel-storage.com/teaser-2026-09.mp4";
const POSTER = "https://store.public.blob.vercel-storage.com/teaser-2026-09.jpg";
const CAPTIONS = "https://store.public.blob.vercel-storage.com/teaser-2026-09.vtt";
const VTT = "WEBVTT\n\n00:00.000 --> 00:02.000\nYour files. Your home.\n";

/** Global Config answers with `item`; any other address answers with `files[address]`, or 404. */
function serving(item, files = {}) {
  return (to) => {
    const address = String(to);
    if (address.includes("global-config.vercel.com")) return Response.json(item);
    return address in files ? new Response(files[address]) : new Response("", { status: 404 });
  };
}

test("an mp4 link gets our own player, and the link opens the file", async () => {
  const embed = await request("/video/embed", { connection: CONNECTION, answer: serving(MP4) });
  assert.equal(embed.response.statusCode, 200);
  assert.match(embed.response.headers["Content-Type"], /^text\/html/);
  assert.match(embed.response.body, new RegExp(`<source src="${MP4}" type="video/mp4">`));
  assert.match(embed.response.body, /<video controls playsinline preload="metadata">/);
  assert.doesNotMatch(embed.response.body, /<track|poster=/);

  const watch = await request("/video", { connection: CONNECTION, answer: serving(MP4) });
  assert.equal(watch.response.statusCode, 302);
  assert.equal(watch.response.headers.Location, MP4);
});

test("a poster holds the download until Play, and captions come from this site", async () => {
  const item = { mp4: MP4, poster: POSTER, captions: CAPTIONS };
  const { response } = await request("/video/embed", { connection: CONNECTION, answer: serving(item) });
  assert.match(response.body, new RegExp(`preload="none" poster="${POSTER}"`));
  assert.match(response.body, /<track kind="captions" src="\/video\/captions" srclang="en" label="English">/);
  assert.doesNotMatch(response.body, /crossorigin/);
});

test("the player page loads nothing but its media and can only be framed here", async () => {
  const { response } = await request("/video/embed", { connection: CONNECTION, answer: serving(MP4) });
  const policy = response.headers["Content-Security-Policy"];
  assert.match(policy, /default-src 'none'/);
  assert.match(policy, /frame-ancestors 'self'/);
  assert.doesNotMatch(response.body, /<script/);
});

test("captions are relayed from the file host as WebVTT", async () => {
  const item = { mp4: MP4, captions: CAPTIONS };
  const { response, asked } = await request("/video/captions", {
    connection: CONNECTION, answer: serving(item, { [CAPTIONS]: VTT })
  });
  assert.equal(response.statusCode, 200);
  assert.equal(response.headers["Content-Type"], "text/vtt; charset=utf-8");
  assert.equal(response.body, VTT);
  assert.equal(asked[1].to, CAPTIONS);
  // Also reached through the rewrite's query.
  const viaQuery = await request("/api/video?captions=1", {
    connection: CONNECTION, answer: serving(item, { [CAPTIONS]: `\uFEFF${VTT}` })
  });
  assert.equal(viaQuery.response.statusCode, 200);
});

test("captions that aren't there, or aren't WebVTT, are a 404", async () => {
  const cases = {
    "a YouTube video": serving(OTHER),
    "a file with no captions": serving(MP4),
    "captions missing from the host": serving({ mp4: MP4, captions: CAPTIONS }),
    "an error page instead": serving({ mp4: MP4, captions: CAPTIONS }, { [CAPTIONS]: "<html>Not found</html>" })
  };
  for (const [name, answer] of Object.entries(cases)) {
    const { response } = await request("/video/captions", { connection: CONNECTION, answer });
    assert.equal(response.statusCode, 404, name);
  }
});

test("a video file that isn't a plain https address falls back to the default", async () => {
  for (const value of [
    "http://store.example/teaser.mp4",
    "javascript:alert(1)//.mp4",
    "https://user:pass@store.example/teaser.mp4",
    "https://store.example/teaser.mov",
    { mp4: "http://store.example/teaser.mp4" },
    { poster: POSTER },
    [MP4]
  ]) {
    const { response } = await request("/video/embed", { connection: CONNECTION, answer: serving(value) });
    assert.equal(response.statusCode, 302, JSON.stringify(value));
    assert.equal(response.headers.Location, embedOf(DEFAULT_VIDEO), JSON.stringify(value));
  }
});

test("an extra that isn't https is left out, and the video still plays", async () => {
  const item = { mp4: MP4, poster: "http://store.example/poster.jpg", captions: "not a link" };
  const { response } = await request("/video/embed", { connection: CONNECTION, answer: serving(item) });
  assert.equal(response.statusCode, 200);
  assert.doesNotMatch(response.body, /poster=|<track/);
});

test("nothing in a configured address can break out of its attribute", async () => {
  const item = { mp4: `${MP4}?a=1&b="><script>alert(1)</script>`, poster: `${POSTER}?"onerror="x` };
  const { response } = await request("/video/embed", { connection: CONNECTION, answer: serving(item) });
  assert.doesNotMatch(response.body, /<script|"onerror/);
  assert.match(response.body, /\?a=1&amp;b=%22%3E%3Cscript%3E/);
});

test("autoplay starts the YouTube player muted, and only the player", async () => {
  const embed = await request("/video/embed?autoplay=1", { connection: CONNECTION, answer: OTHER });
  assert.equal(embed.response.headers.Location, `${embedOf(OTHER)}&autoplay=1&mute=1`);
  const viaQuery = await request("/api/video?embed=1&autoplay=1", { connection: CONNECTION, answer: OTHER });
  assert.equal(viaQuery.response.headers.Location, `${embedOf(OTHER)}&autoplay=1&mute=1`);
  const watch = await request("/video?autoplay=1", { connection: CONNECTION, answer: OTHER });
  assert.equal(watch.response.headers.Location, watchOf(OTHER));
  const otherValue = await request("/video/embed?autoplay=yes", { connection: CONNECTION, answer: OTHER });
  assert.equal(otherValue.response.headers.Location, embedOf(OTHER));
});

test("autoplay starts our own player muted, poster and all", async () => {
  const item = { mp4: MP4, poster: POSTER };
  const { response } = await request("/video/embed?autoplay=1", { connection: CONNECTION, answer: serving(item) });
  assert.match(response.body, new RegExp(`<video controls playsinline autoplay muted poster="${POSTER}">`));
  assert.doesNotMatch(response.body, /preload=/);
});
