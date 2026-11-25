"use strict";

// The site's "Buy now" buttons. /buy, rewritten here by vercel.json, starts a Stripe Checkout for
// Uncloud Home and sends the browser to it.
//
// What Uncloud costs is STRIPE_PRICE_ID, a Price in the Stripe dashboard; nothing here names an
// amount, so keep that Price and the price the pages show the same. Stripe asks for the buyer's
// email. When the payment goes through, Stripe tells api/stripe-webhook.js, which emails the
// download links. Every session is marked `metadata.product = PRODUCT`, and that function sends
// links only for sessions with the mark, so nothing else sold on the same Stripe account gets them.
//
// It answers a GET so the button stays a plain link: it works without JavaScript, and
// uncloud.life/buy can be pasted into an email or a post. A session nobody pays for expires after
// 24 hours and charges nobody, so a crawler following the link costs nothing; robots.txt asks
// them not to.

const PRODUCT = "uncloud-home";
const STRIPE_SESSIONS = "https://api.stripe.com/v1/checkout/sessions";
const TIMEOUT_MS = 8000;
const SUPPORT = "hello@uncloud.life";

function text(value) {
  return typeof value === "string" ? value.trim() : "";
}

/** Reads the settings this function needs, and says plainly which are missing. */
function readConfig(env) {
  const key = text(env.STRIPE_SECRET_KEY);
  const price = text(env.STRIPE_PRICE_ID);
  const missing = [];
  if (!key) missing.push("STRIPE_SECRET_KEY");
  if (!price) missing.push("STRIPE_PRICE_ID");
  return { missing, key, price };
}

/**
 * Where Stripe sends the buyer back to: this deployment, so a preview with test keys comes back
 * to the preview. Only the origin is taken from the request.
 */
function siteOrigin(requestUrl) {
  try {
    const url = new URL(requestUrl);
    if (url.protocol === "https:" || url.protocol === "http:") return url.origin;
  } catch {
    // Fall through to the public site.
  }
  return "https://www.uncloud.life";
}

/** The Checkout Session, as the form fields Stripe's API takes. */
function sessionParameters(price, origin) {
  const form = new URLSearchParams();
  form.set("mode", "payment");
  form.set("line_items[0][price]", price);
  form.set("line_items[0][quantity]", "1");
  form.set("success_url", `${origin}/thanks`);
  form.set("cancel_url", `${origin}/#pricing`);
  // Reservation credits and launch offers are promotion codes made in the dashboard.
  form.set("allow_promotion_codes", "true");
  form.set("metadata[product]", PRODUCT);
  form.set("payment_intent_data[description]", "Uncloud Home");
  form.set("payment_intent_data[metadata][product]", PRODUCT);
  form.set("custom_text[submit][message]", "We’ll email the download link for the Mac app to this address.");
  return form;
}

async function createSession(config, origin, fetchImpl) {
  const response = await fetchImpl(STRIPE_SESSIONS, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${config.key}`,
      "Content-Type": "application/x-www-form-urlencoded"
    },
    body: sessionParameters(config.price, origin).toString(),
    signal: AbortSignal.timeout(TIMEOUT_MS)
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    // Stripe's message names the problem (a deleted price, a key for the wrong mode) and no secret.
    throw new Error(`Stripe responded ${response.status}: ${text(body?.error?.message).slice(0, 300)}`);
  }
  let url;
  try {
    url = new URL(body.url);
  } catch {
    throw new Error("Stripe's session had no checkout address");
  }
  if (url.protocol !== "https:") throw new Error("Stripe's checkout address wasn't https");
  return url.href;
}

/** A plain page for when checkout can't start, so the buyer isn't left on a blank error. */
function unavailable(status) {
  const page = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>Checkout isn’t available | Uncloud</title>
<style>
  body { margin: 0; padding: 64px 20px; background: #f7fafd; color: #172033; font: 17px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; }
  main { max-width: 520px; margin: 0 auto; }
  h1 { font-size: 1.6rem; line-height: 1.2; margin: 0 0 12px; }
  a { color: #1a66d8; }
</style>
</head>
<body>
<main>
<h1>Checkout isn’t available right now</h1>
<p>Nothing was charged. Please try again in a few minutes, or email <a href="mailto:${SUPPORT}">${SUPPORT}</a> and we’ll sort it out.</p>
<p><a href="/">Back to Uncloud</a></p>
</main>
</body>
</html>
`;
  return new Response(page, {
    status,
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "Cache-Control": "no-store",
      "X-Robots-Tag": "noindex"
    }
  });
}

/** The work of GET, with its settings and Stripe passed in so tests can supply their own. */
async function handle(request, env, fetchImpl) {
  const config = readConfig(env);
  if (config.missing.length > 0) {
    console.error(`Checkout not configured; missing ${config.missing.join(", ")}`);
    return unavailable(503);
  }

  let location;
  try {
    location = await createSession(config, siteOrigin(request.url), fetchImpl);
  } catch (failure) {
    console.error("Checkout session failed:", failure.message);
    return unavailable(502);
  }

  return new Response(null, {
    status: 303,
    headers: { Location: location, "Cache-Control": "no-store", "X-Robots-Tag": "noindex" }
  });
}

// Vercel calls a Web handler as handler(request, event), so the second argument is the runtime's
// event, never settings: the handler reads process.env itself and passes it on.
const GET = (request) => handle(request, process.env, fetch);

module.exports = { GET, handle, PRODUCT, readConfig, siteOrigin, sessionParameters };
