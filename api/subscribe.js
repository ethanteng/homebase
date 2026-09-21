"use strict";

// Early-access signups from the landing page.
//
// This exists because the page is static: a Mailtrap token in browser JavaScript
// is a published token. The form posts here instead, and only this function
// talks to Mailtrap.
//
// Mailtrap sends mail; it is not a subscriber list. A signup becomes an email in
// the inbox named by SIGNUP_NOTIFY_TO, and that inbox is the list for now.

const MAX_BODY_BYTES = 4096;
const MAX_EMAIL_LENGTH = 254; // RFC 5321
const SEND_TIMEOUT_MS = 8000;

// Deliberately strict rather than clever: one @, no delimiters that would let an
// address smuggle a second recipient, and a plausible domain.
const EMAIL_PATTERN = /^[^\s@,;:<>"'\\]{1,64}@[^\s@,;:<>"'\\]{1,185}\.[A-Za-z]{2,24}$/;

function text(value) {
  return typeof value === "string" ? value.trim() : "";
}

/** Returns a message to show the visitor, or null when the address is usable. */
function validateEmail(email) {
  if (!email) return "Enter your email address.";
  if (email.length > MAX_EMAIL_LENGTH) return "That email address is too long.";
  // A carriage return in an address is where header injection begins.
  if (/[\r\n\t\0]/.test(email)) return "That doesn’t look like an email address.";
  if (!EMAIL_PATTERN.test(email)) return "That doesn’t look like an email address.";
  return null;
}

/** Reads the settings this function needs, and says plainly which are missing. */
function readConfig(env) {
  const token = text(env.MAILTRAP_TOKEN);
  const notify = text(env.SIGNUP_NOTIFY_TO);
  const missing = [];
  if (!token) missing.push("MAILTRAP_TOKEN");
  if (!notify) missing.push("SIGNUP_NOTIFY_TO");
  const inbox = text(env.MAILTRAP_INBOX_ID);
  return {
    missing,
    token,
    notify,
    from: text(env.MAILTRAP_FROM) || "early-access@uncloud.life",
    // An inbox id routes to Mailtrap's sandbox, which captures mail instead of
    // delivering it — useful before a sending domain is verified.
    url: inbox
      ? `https://sandbox.api.mailtrap.io/api/send/${encodeURIComponent(inbox)}`
      : "https://send.api.mailtrap.io/api/send",
    sandbox: Boolean(inbox)
  };
}

async function sendToMailtrap(email, config, fetchImpl) {
  const response = await fetchImpl(config.url, {
    method: "POST",
    headers: {
      "Api-Token": config.token,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      from: { email: config.from, name: "Uncloud" },
      to: [{ email: config.notify }],
      subject: "Uncloud early access request",
      text: `${email}\n\nRequested early access from uncloud.life.\n${new Date().toISOString()}\n`,
      category: "early-access"
    }),
    signal: AbortSignal.timeout(SEND_TIMEOUT_MS)
  });
  if (!response.ok) {
    const detail = await response.text().catch(() => "");
    const error = new Error(`Mailtrap responded ${response.status}: ${detail.slice(0, 300)}`);
    error.status = response.status;
    throw error;
  }
  return true;
}

function body(request) {
  const raw = request.body;
  if (raw && typeof raw === "object") return raw;
  const asText = typeof raw === "string" ? raw : "";
  if (!asText) return {};
  if (Buffer.byteLength(asText, "utf8") > MAX_BODY_BYTES) throw new Error("too large");
  try {
    return JSON.parse(asText);
  } catch {
    throw new Error("not json");
  }
}

async function handler(request, response, env = process.env, fetchImpl = fetch) {
  response.setHeader("Cache-Control", "no-store");
  response.setHeader("Content-Type", "application/json; charset=utf-8");

  if (request.method !== "POST") {
    response.setHeader("Allow", "POST");
    return response.status(405).json({ error: "Send this as a POST." });
  }

  let payload;
  try {
    payload = body(request);
  } catch {
    return response.status(400).json({ error: "That request wasn’t readable." });
  }

  // Bots fill in every field they find. A real visitor never sees this one, so
  // anything in it is answered with a cheerful nothing.
  if (text(payload.website)) return response.status(200).json({ ok: true });

  const email = text(payload.email).toLowerCase();
  const problem = validateEmail(email);
  if (problem) return response.status(400).json({ error: problem });

  const config = readConfig(env);
  if (config.missing.length > 0) {
    console.error(`Signup not configured; missing ${config.missing.join(", ")}`);
    return response.status(503).json({
      error: "Early access isn’t open just yet. Try again shortly."
    });
  }

  try {
    await sendToMailtrap(email, config, fetchImpl);
  } catch (failure) {
    // The visitor gets nothing actionable; the detail belongs in the log.
    console.error("Signup send failed:", failure.message);
    return response.status(502).json({
      error: "That didn’t go through. Try again in a moment."
    });
  }

  return response.status(200).json({ ok: true, sandbox: config.sandbox });
}

module.exports = handler;
module.exports.validateEmail = validateEmail;
module.exports.readConfig = readConfig;
