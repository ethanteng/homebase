"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const handler = require("./video.js");
const { videoId, DEFAULT_VIDEO } = handler;

const CONNECTION = "https://edge-config.vercel.com/ecfg_test?token=read-token";
const OTHER = "dQw4w9WgXcQ";

function mockResponse() {
  return {
    headers: {},
    statusCode: 200,
    ended: false,
    setHeader(name, value) { this.headers[name] = value; },
    end() { this.ended = true; }
  };
}

/**
 * Runs the function with EDGE_CONFIG set to `connection` and Edge Config answering with
 * `answer` — a function from the request to a Response, or a value to send as JSON.
 * Returns the response and every request Edge Config received.
 */
async function request(url, { connection, answer, method = "GET" } = {}) {
  const asked = [];
  const originalFetch = globalThis.fetch;
  const originalConnection = process.env.EDGE_CONFIG;
  globalThis.fetch = async (to, init) => {
    asked.push({ to: String(to), init });
    if (typeof answer === "function") return answer(to, init);
    return new Response(JSON.stringify(answer), { headers: { "Content-Type": "application/json" } });
  };
  if (connection === undefined) delete process.env.EDGE_CONFIG;
  else process.env.EDGE_CONFIG = connection;
  try {
    const response = mockResponse();
    await handler({ method, url }, response);
    return { response, asked };
  } finally {
    globalThis.fetch = originalFetch;
    if (originalConnection === undefined) delete process.env.EDGE_CONFIG;
    else process.env.EDGE_CONFIG = originalConnection;
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

test("Edge Config is asked for the video item with the connection's token", async () => {
  const { asked } = await request("/api/video", { connection: CONNECTION, answer: OTHER });
  assert.equal(asked.length, 1);
  assert.equal(asked[0].to, "https://edge-config.vercel.com/ecfg_test/item/video");
  assert.equal(asked[0].init.headers.Authorization, "Bearer read-token");
});

test("without an Edge Config the default video plays and nothing is fetched", async () => {
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
    "Edge Config unreachable": () => { throw new TypeError("fetch failed"); },
    "Edge Config answering nonsense": () => new Response("<html>", { status: 200 })
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
