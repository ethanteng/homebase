"use strict";

// The one address Dropbox returns a sign-in to, for everybody who connects Dropbox
// through Uncloud's own Dropbox app.
//
// Dropbox will only send a browser back to an address registered with the app, and
// an app registered by whoever is running Uncloud has to be registered by them —
// which is the whole reason connecting anything used to mean a developer console.
// So this is registered once, here, and it forwards the sign-in the last hop to the
// Uncloud the person is actually sitting at.
//
// What makes a relay in the middle of somebody's sign-in acceptable is PKCE. The
// verifier is made on their computer and never leaves it, so the code that comes
// through here cannot be exchanged for a token by this function, by Vercel, by
// anyone reading these logs, or by anyone who intercepts the hop. Only the Uncloud
// that started the sign-in holds the other half.
//
// The one thing that would undo that: if this forwarded to an address someone else
// chose. Then an attacker could start a sign-in with a verifier they hold, send the
// victim through Uncloud's own app — consent screen and all — and collect the code
// at their own server. So the destination is not somebody's to choose. Scheme, host
// and path are fixed below, and the only thing taken from the request is a port
// number on the loopback address, which reaches nothing but the person's own
// computer.

// Where a sign-in is going, in full, minus the port. Not built from any input.
const CALLBACK_PATH = "/api/providers/dropbox/callback";
const LOOPBACK = "127.0.0.1";

// A port and nothing else: no sign, no separators, no leading zeros to normalise away.
const PORT_PATTERN = /^[1-9][0-9]{0,4}$/;
const MAX_PORT = 65535;
// The suffix Uncloud puts on its state, as `.h5210` or `.s5210` — the scheme it
// answers on and the port it listens on. The rest of the state is its own business:
// it is passed back untouched and checked there, and this function neither needs to
// understand it nor could learn anything by trying.
const RETURN_PATTERN = /\.([hs])([0-9]{1,5})$/;
const MAX_STATE_LENGTH = 512;

/** The `scheme://host:port` this sign-in belongs to, or null when the state doesn't say. */
function destination(state) {
  if (typeof state !== "string" || state.length === 0 || state.length > MAX_STATE_LENGTH) return null;
  const found = RETURN_PATTERN.exec(state);
  if (!found) return null;
  const [, scheme, port] = found;
  if (!PORT_PATTERN.test(port) || Number(port) > MAX_PORT) return null;
  return `${scheme === "s" ? "https" : "http"}://${LOOPBACK}:${port}`;
}

/**
 * The query, read from the raw URL rather than from a parsed copy a platform may or may not
 * attach. `request.url` is the Node primitive and is always there, which keeps this function
 * working the same whoever ends up running it.
 */
function parameters(request) {
  // The base is only to satisfy the parser; `request.url` on a serverless request is a path.
  try {
    return new URL(request.url || "/", "http://relay.invalid").searchParams;
  } catch {
    return new URLSearchParams();
  }
}

/**
 * The one value given for a parameter, or nothing when it was repeated. A second copy must never
 * become the one that counts: `?state=mine&state=theirs` would otherwise be a way to choose where
 * somebody else's code is sent.
 */
function single(query, name) {
  const all = query.getAll(name);
  return all.length === 1 ? all[0] : undefined;
}

/**
 * The page somebody sees when this cannot work out where their sign-in belongs —
 * usually because they opened the address by hand rather than arriving from Dropbox.
 *
 * Self-contained on purpose. Nothing is loaded from anywhere, so there is no request
 * this page could carry the code out on, and the code is never written into it.
 */
const STRANDED = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>Nothing to finish</title>
<style>
  :root { color-scheme: light dark; }
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px;
         font: 16px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; }
  main { max-width: 32rem; }
  h1 { font-size: 1.4rem; margin: 0 0 0.75rem; }
  p { margin: 0 0 0.75rem; }
</style>
</head>
<body>
<main>
  <h1>Nothing to finish here</h1>
  <p>This page only has a job in the middle of connecting Dropbox to Uncloud, and it
     didn’t arrive with a sign-in to pass on.</p>
  <p>If you were connecting Dropbox, go back to Uncloud on your own computer and press
     Connect again. Nothing was changed and nothing was shared.</p>
</main>
</body>
</html>
`;

module.exports = async function handler(request, response) {
  // Nothing here reads a body or changes anything, and a sign-in always arrives as a
  // plain navigation from dropbox.com.
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.setHeader("Allow", "GET, HEAD");
    return response.status(405).send("");
  }

  // A sign-in code is single-use and useless without the verifier, but it has no
  // business in a cache or in the next page's Referer either way.
  response.setHeader("Cache-Control", "no-store");
  response.setHeader("Referrer-Policy", "no-referrer");

  const query = parameters(request);
  const state = single(query, "state");
  const where = destination(state);
  if (!where) return response.status(400).setHeader("Content-Type", "text/html; charset=utf-8").send(STRANDED);

  // Passed on as they came: the state so the waiting Uncloud can check it is the one
  // it sent, and the refusal so someone who pressed Cancel is told that, rather than
  // being shown a failure.
  const onward = new URLSearchParams({ state });
  const code = single(query, "code");
  const error = single(query, "error");
  if (typeof code === "string" && code.length > 0) onward.set("code", code);
  if (typeof error === "string" && error.length > 0) onward.set("error", error);
  if (!onward.has("code") && !onward.has("error")) onward.set("error", "invalid_request");

  response.setHeader("Location", `${where}${CALLBACK_PATH}?${onward}`);
  return response.status(302).send("");
};

// Exported for the tests, which are the only reason these are visible at all.
module.exports.destination = destination;
