"use strict";

// Where Stripe says a purchase went through, and where the buyer's download links are emailed from.
//
// api/checkout.js starts each Checkout Session marked `metadata.product = PRODUCT`. When one is
// paid, Stripe posts `checkout.session.completed` here (or, for a payment method that settles
// later, `checkout.session.async_payment_succeeded` once it has). This checks Stripe signed it,
// then emails the address the buyer gave Stripe the links to the Mac app, through Mailtrap, as
// api/partner-interest.js does.
//
// Anything else is acknowledged and left alone: other events, sessions without the mark (anything
// else sold on the same Stripe account), and sessions not paid yet. When the email can't be sent,
// this answers 500 and Stripe tries again, for up to three days, so a Mailtrap outage delays the
// email rather than losing it. Stripe can deliver an event more than once, so on rare occasions a
// buyer may get the email twice; the links are the same, so that's harmless.
//
// The signature is over the exact bytes Stripe sent, so this is a Web handler (`POST(request)`):
// that's how a Vercel function reads the body untouched.

const crypto = require("node:crypto");
const { PRODUCT } = require("./checkout.js");

// How old a signed delivery may be. Stripe's own libraries allow five minutes.
const TOLERANCE_SECONDS = 300;
const SEND_TIMEOUT_MS = 8000;
const SUPPORT = "hello@uncloud.life";
const HELP_URL = "https://www.uncloud.life/help#set-up";

// The Mac app, as built from main by CI's mac-latest release; the help page links the same files.
const DOWNLOADS = {
  arm64: "https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-arm64.dmg",
  x64: "https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-x64.dmg"
};

const FULFILLING_EVENTS = new Set(["checkout.session.completed", "checkout.session.async_payment_succeeded"]);
// `no_payment_required` is a session a 100% promotion code paid for.
const PAID = new Set(["paid", "no_payment_required"]);
const EMAIL_PATTERN = /^[^\s@,;:<>"'\\]{1,64}@[^\s@,;:<>"'\\]{1,185}\.[A-Za-z]{2,24}$/;

function text(value) {
  return typeof value === "string" ? value.trim() : "";
}

/** An https address, or null. A typo in a download setting falls back to the default link. */
function https(value) {
  try {
    const url = new URL(text(value));
    return url.protocol === "https:" && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

/** Reads the settings this function needs, and says plainly which are missing. */
function readConfig(env) {
  const secret = text(env.STRIPE_WEBHOOK_SECRET);
  const token = text(env.MAILTRAP_TOKEN);
  const missing = [];
  if (!secret) missing.push("STRIPE_WEBHOOK_SECRET");
  if (!token) missing.push("MAILTRAP_TOKEN");
  const inbox = text(env.MAILTRAP_INBOX_ID);
  return {
    missing,
    secret,
    token,
    from: text(env.ORDER_EMAIL_FROM) || SUPPORT,
    downloads: {
      arm64: https(env.MAC_DOWNLOAD_ARM64_URL) || DOWNLOADS.arm64,
      x64: https(env.MAC_DOWNLOAD_X64_URL) || DOWNLOADS.x64
    },
    // An inbox id routes to Mailtrap's sandbox, which captures mail instead of delivering it:
    // a preview deployment with Stripe test keys can be tried without emailing anyone.
    url: inbox
      ? `https://sandbox.api.mailtrap.io/api/send/${encodeURIComponent(inbox)}`
      : "https://send.api.mailtrap.io/api/send"
  };
}

/**
 * The event in `payload` if `header` (Stripe-Signature) shows Stripe signed it with `secret` within
 * the tolerance, or null. `payload` is the body as received, as a Buffer.
 */
function verifiedEvent(payload, header, secret, nowMs = Date.now()) {
  if (typeof header !== "string" || !secret) return null;
  let timestamp = null;
  const signatures = [];
  for (const part of header.split(",")) {
    const at = part.indexOf("=");
    if (at < 0) continue;
    const key = part.slice(0, at).trim();
    const value = part.slice(at + 1).trim();
    if (key === "t" && /^\d{1,12}$/.test(value)) timestamp = Number(value);
    else if (key === "v1" && /^[0-9a-f]{64}$/.test(value)) signatures.push(Buffer.from(value, "hex"));
  }
  if (timestamp === null || signatures.length === 0) return null;
  if (Math.abs(nowMs / 1000 - timestamp) > TOLERANCE_SECONDS) return null;

  const expected = crypto.createHmac("sha256", secret).update(`${timestamp}.`).update(payload).digest();
  if (!signatures.some((signature) => crypto.timingSafeEqual(signature, expected))) return null;

  try {
    const event = JSON.parse(payload.toString("utf8"));
    return event && typeof event === "object" ? event : null;
  } catch {
    return null;
  }
}

/** The purchase this event completes, as `{ session, email }`, or null when it isn't one. */
function purchaseIn(event) {
  if (!FULFILLING_EVENTS.has(event.type)) return null;
  const session = event.data && event.data.object;
  if (!session || session.object !== "checkout.session") return null;
  if (!session.metadata || session.metadata.product !== PRODUCT) return null;
  if (!PAID.has(session.payment_status)) return null;
  const email = text(session.customer_details && session.customer_details.email) || text(session.customer_email);
  return { session: text(session.id), email };
}

const escapeHtml = (s) =>
  String(s).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

/** The email a buyer gets: subject, plain text and HTML. */
function downloadEmail(downloads) {
  const subject = "Your Uncloud download";
  const textBody = [
    "Thanks for buying Uncloud.",
    "",
    "Download the Mac app for your kind of Mac:",
    "",
    `Apple silicon (M1 and later): ${downloads.arm64}`,
    `Intel Macs: ${downloads.x64}`,
    "",
    "Not sure which you have? Open the Apple menu and choose About This Mac. It says “Chip: Apple M…” or “Processor: Intel”.",
    "",
    "Open the download and drag Uncloud onto the Applications folder. The setup guide walks you through the rest; it takes about five minutes:",
    HELP_URL,
    "",
    "Keep this email. The links always download the latest version, so you can use them again to reinstall.",
    "",
    "Questions? Just reply to this email.",
    "",
    "Uncloud",
    ""
  ].join("\n");

  const button = (href, label) =>
    `<a href="${escapeHtml(href)}" style="display:block;margin:0 0 12px;padding:14px 18px;border-radius:10px;background:#1f6fe5;color:#ffffff;font-weight:600;text-align:center;text-decoration:none">${label}</a>`;
  const html = `<!doctype html>
<html lang="en">
<body style="margin:0;padding:24px 12px;background:#f7fafd;color:#172033;font:16px/1.6 -apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif">
<div style="max-width:520px;margin:0 auto;padding:32px 28px;border-radius:16px;background:#ffffff">
<h1 style="margin:0 0 12px;font-size:22px;line-height:1.25">Thanks for buying Uncloud</h1>
<p style="margin:0 0 20px">Download the Mac app for your kind of Mac:</p>
${button(downloads.arm64, "Download for Apple silicon (M1 and later)")}
${button(downloads.x64, "Download for Intel Macs")}
<p style="margin:8px 0 20px;color:#667189;font-size:14px">Not sure which you have? Open the Apple menu and choose <strong>About This Mac</strong>. It says <strong>Chip: Apple M…</strong> or <strong>Processor: Intel</strong>.</p>
<p style="margin:0 0 16px">Open the download and drag <strong>Uncloud</strong> onto the <strong>Applications</strong> folder. The <a href="${escapeHtml(HELP_URL)}" style="color:#1a66d8">setup guide</a> walks you through the rest; it takes about five minutes.</p>
<p style="margin:0 0 16px">Keep this email. The links always download the latest version, so you can use them again to reinstall.</p>
<p style="margin:0">Questions? Just reply to this email.</p>
</div>
</body>
</html>
`;
  return { subject, text: textBody, html };
}

async function sendDownloadEmail(email, config, fetchImpl) {
  const message = downloadEmail(config.downloads);
  const response = await fetchImpl(config.url, {
    method: "POST",
    headers: { "Api-Token": config.token, "Content-Type": "application/json" },
    body: JSON.stringify({
      from: { email: config.from, name: "Uncloud" },
      to: [{ email }],
      headers: { "Reply-To": SUPPORT },
      subject: message.subject,
      text: message.text,
      html: message.html,
      category: "purchase"
    }),
    signal: AbortSignal.timeout(SEND_TIMEOUT_MS)
  });
  if (!response.ok) {
    const detail = await response.text().catch(() => "");
    throw new Error(`Mailtrap responded ${response.status}: ${detail.slice(0, 300)}`);
  }
}

const reply = (status, body) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" }
  });

async function POST(request, env = process.env, fetchImpl = fetch, now = Date.now) {
  const config = readConfig(env);
  if (config.missing.length > 0) {
    // 500, so Stripe keeps the event and tries again once this is configured.
    console.error(`Stripe webhook not configured; missing ${config.missing.join(", ")}`);
    return reply(500, { error: "Not configured." });
  }

  const payload = Buffer.from(await request.arrayBuffer());
  const event = verifiedEvent(payload, request.headers.get("stripe-signature"), config.secret, now());
  if (!event) return reply(400, { error: "Signature didn’t verify." });

  const purchase = purchaseIn(event);
  if (!purchase) return reply(200, { received: true });

  if (!EMAIL_PATTERN.test(purchase.email)) {
    // Retrying won't produce an address. The buyer will write in; Stripe has the payment.
    console.error(`Paid session ${purchase.session} has no usable email; the download wasn't sent.`);
    return reply(200, { received: true, sent: false });
  }

  try {
    await sendDownloadEmail(purchase.email, config, fetchImpl);
  } catch (failure) {
    console.error(`Download email for session ${purchase.session} failed:`, failure.message);
    return reply(500, { error: "Couldn’t send the email." });
  }
  // The session id finds the buyer in the Stripe dashboard when someone says it never arrived.
  console.log(`Download email sent for session ${purchase.session}`);
  return reply(200, { received: true, sent: true });
}

module.exports = { POST, DOWNLOADS, readConfig, verifiedEvent, purchaseIn, downloadEmail };
