"use strict";

// Early-access signups from the landing page.
//
// This exists because the page is static: an API token in browser JavaScript is
// a published token. The form posts here instead, and only this function talks
// to anything that holds one.
//
// A signup is a row in an Airtable table, and that table is the list. The
// notification email is how a signup gets noticed, not where it is kept, so it
// is sent after the row exists and a failed one is logged rather than surfaced.

const MAX_BODY_BYTES = 4096;
const MAX_EMAIL_LENGTH = 254; // RFC 5321
const MAX_DOMAIN_LENGTH = 253;
const MAX_CONTEXT_LENGTH = 200;
const SEND_TIMEOUT_MS = 8000;
const STORE_TIMEOUT_MS = 8000;

// Requests are rate limited because this endpoint spends someone else’s quota:
// every accepted one writes an Airtable record and sends an email, and the free
// Airtable plan counts calls by the month. The window is per warm instance, so
// it blunts a naive flood rather than stopping a distributed one; Vercel’s
// firewall rate limiting is the durable control and this is the floor.
const RATE_WINDOW_MS = 60 * 1000;
const MAX_PER_ADDRESS_MS = 10 * 60 * 1000;
const MAX_PER_IP = 3;
const MAX_PER_INSTANCE = 30;
const MAX_TRACKED_KEYS = 5000;

// Deliberately strict rather than clever: no delimiters that would let an
// address smuggle a second recipient, and a domain of real labels.
const LOCAL_PATTERN = /^[^\s@,;:<>"'\\]{1,64}$/;
const LABEL_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/;
const TLD_PATTERN = /^[A-Za-z]{2,24}$/;

function text(value) {
  return typeof value === "string" ? value.trim() : "";
}

/** Returns a message to show the visitor, or null when the address is usable. */
function validateEmail(email) {
  const rejection = "That doesn’t look like an email address.";
  if (!email) return "Enter your email address.";
  if (email.length > MAX_EMAIL_LENGTH) return "That email address is too long.";
  // A carriage return in an address is where header injection begins.
  if (/[\r\n\t\0]/.test(email)) return rejection;

  const at = email.lastIndexOf("@");
  if (at < 1 || at === email.length - 1) return rejection;
  const local = email.slice(0, at);
  const domain = email.slice(at + 1);

  if (!LOCAL_PATTERN.test(local)) return rejection;
  if (domain.length > MAX_DOMAIN_LENGTH) return rejection;

  // Checked label by label rather than with one clever pattern: a character
  // class that allows dots and hyphens anywhere accepts example..com and
  // -example.com, and neither of those can receive mail.
  const labels = domain.split(".");
  if (labels.length < 2) return rejection;
  if (!labels.every((label) => LABEL_PATTERN.test(label))) return rejection;
  if (!TLD_PATTERN.test(labels[labels.length - 1])) return rejection;
  return null;
}

/**
 * Lowercases the domain and leaves the local part alone. SMTP lets the
 * receiving system treat the local part as case sensitive, so folding it can
 * turn a working address into one that bounces.
 */
function normalizeEmail(email) {
  const at = email.lastIndexOf("@");
  if (at < 0) return email;
  return `${email.slice(0, at)}@${email.slice(at + 1).toLowerCase()}`;
}

/**
 * Cleans a referrer or campaign string on its way into a record. These come
 * from the page, so they are trimmed of the control characters that would let
 * a value carry a line break, and capped so a long one cannot pad the request.
 */
function contextValue(value) {
  return text(value).replace(/[\u0000-\u001F\u007F]/g, "").slice(0, MAX_CONTEXT_LENGTH);
}

// key -> timestamps, kept only for the window each key is checked against.
const hits = new Map();

function within(key, windowMs, now) {
  const stamps = (hits.get(key) || []).filter((at) => now - at < windowMs);
  if (stamps.length === 0) hits.delete(key);
  else hits.set(key, stamps);
  return stamps.length;
}

function note(key, now) {
  hits.set(key, [...(hits.get(key) || []), now]);
  // Bounded rather than exact: a cleared table forgets a window of history,
  // which is the safe direction for a counter that only delays strangers.
  if (hits.size > MAX_TRACKED_KEYS) hits.clear();
}

function callerAddress(request) {
  const headers = request.headers || {};
  const forwarded = text(headers["x-forwarded-for"]).split(",")[0].trim();
  return forwarded || text(headers["x-real-ip"]) || "unknown";
}

/**
 * Returns "limit" when this caller has had its turn, "repeat" when this exact
 * address just signed up, or null to go ahead.
 */
function throttle(email, request, now) {
  if (within(`addr:${email}`, MAX_PER_ADDRESS_MS, now) > 0) return "repeat";
  if (within("all", RATE_WINDOW_MS, now) >= MAX_PER_INSTANCE) return "limit";
  if (within(`ip:${callerAddress(request)}`, RATE_WINDOW_MS, now) >= MAX_PER_IP) return "limit";
  return null;
}

/** Counted before the send: a failed attempt still costs a caller its budget. */
function noteAttempt(request, now) {
  note("all", now);
  note(`ip:${callerAddress(request)}`, now);
}

/** Counted only once the row exists, so a retry after a failure is not refused. */
function noteDelivered(email, now) {
  note(`addr:${email}`, now);
}

/**
 * Reads the settings this function needs, and says plainly which are missing.
 *
 * Only the store is required. Mailtrap is a notification, so a deployment with
 * no mail settings still takes signups; it just does so quietly.
 */
function readConfig(env) {
  const storeToken = text(env.AIRTABLE_TOKEN);
  const baseId = text(env.AIRTABLE_BASE_ID);
  const missing = [];
  if (!storeToken) missing.push("AIRTABLE_TOKEN");
  if (!baseId) missing.push("AIRTABLE_BASE_ID");

  // A table per environment is how a preview deployment stays out of the real
  // list while pointing at the same base.
  const table = text(env.AIRTABLE_TABLE) || "Signups";

  const mailToken = text(env.MAILTRAP_TOKEN);
  const notifyTo = text(env.SIGNUP_NOTIFY_TO);
  const inbox = text(env.MAILTRAP_INBOX_ID);

  return {
    missing,
    store: {
      token: storeToken,
      url: `https://api.airtable.com/v0/${encodeURIComponent(baseId)}/${encodeURIComponent(table)}`
    },
    notify: mailToken && notifyTo
      ? {
          token: mailToken,
          to: notifyTo,
          from: text(env.MAILTRAP_FROM) || "early-access@uncloud.life",
          // An inbox id routes to Mailtrap's sandbox, which captures mail
          // instead of delivering it — useful before a sending domain is
          // verified. It says nothing about whether the signup was recorded.
          url: inbox
            ? `https://sandbox.api.mailtrap.io/api/send/${encodeURIComponent(inbox)}`
            : "https://send.api.mailtrap.io/api/send"
        }
      : null
  };
}

/**
 * Writes the signup to Airtable, upserting on the address.
 *
 * Upserting is what makes the table a list rather than a log: a second signup
 * updates the row that already exists instead of adding a twin, and it costs
 * one request where a search followed by a create would cost two. That matters
 * on a plan that counts API calls by the month.
 */
async function storeSignup(fields, config, fetchImpl) {
  const response = await fetchImpl(config.store.url, {
    method: "PATCH",
    headers: {
      Authorization: `Bearer ${config.store.token}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      performUpsert: { fieldsToMergeOn: ["Email"] },
      records: [{ fields }]
    }),
    signal: AbortSignal.timeout(STORE_TIMEOUT_MS)
  });
  if (!response.ok) {
    const detail = await response.text().catch(() => "");
    const error = new Error(`Airtable responded ${response.status}: ${detail.slice(0, 300)}`);
    error.status = response.status;
    throw error;
  }
  return true;
}

async function sendToMailtrap(email, notify, fetchImpl) {
  const response = await fetchImpl(notify.url, {
    method: "POST",
    headers: {
      "Api-Token": notify.token,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      from: { email: notify.from, name: "Uncloud" },
      to: [{ email: notify.to }],
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
  let parsed;
  try {
    parsed = JSON.parse(asText);
  } catch {
    throw new Error("not json");
  }
  // "null", "7" and "[]" are all valid JSON and none of them has fields to read.
  // Treated as an empty body, they fail validation instead of the function.
  return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
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
  if (text(payload.website)) return response.status(200).json({ ok: true, accepted: false });

  const email = normalizeEmail(text(payload.email));
  const problem = validateEmail(email);
  if (problem) return response.status(400).json({ error: problem });

  const now = Date.now();
  const held = throttle(email, request, now);
  // A repeat of an address we just took is already on the list; saying so
  // costs nothing and sending a second identical email costs quota.
  if (held === "repeat") return response.status(200).json({ ok: true, accepted: false });
  if (held === "limit") {
    response.setHeader("Retry-After", String(Math.ceil(RATE_WINDOW_MS / 1000)));
    return response.status(429).json({
      error: "That’s a few too many at once. Try again in a minute."
    });
  }

  const config = readConfig(env);
  if (config.missing.length > 0) {
    console.error(`Signup not configured; missing ${config.missing.join(", ")}`);
    return response.status(503).json({
      error: "Early access isn’t open just yet. Try again shortly."
    });
  }

  noteAttempt(request, now);

  const fields = { Email: email, "Signed Up": new Date(now).toISOString() };
  const source = contextValue(payload.source);
  const referrer = contextValue(payload.referrer);
  // Left out rather than sent empty: a repeat signup upserts the same row, and
  // a blank value would erase what the first visit recorded.
  if (source) fields.Source = source;
  if (referrer) fields.Referrer = referrer;

  try {
    await storeSignup(fields, config, fetchImpl);
  } catch (failure) {
    // The visitor gets nothing actionable; the detail belongs in the log.
    console.error("Signup not stored:", failure.message);
    return response.status(502).json({
      error: "That didn’t go through. Try again in a moment."
    });
  }

  // The record is the list; this email is only how it gets noticed. Failing the
  // request here would ask someone already on the list to sign up again, so the
  // failure is logged and the visitor is told what is true: they are on it.
  if (config.notify) {
    try {
      await sendToMailtrap(email, config.notify, fetchImpl);
    } catch (failure) {
      console.error("Signup stored but notification failed:", failure.message);
    }
  }

  noteDelivered(email, now);
  return response.status(200).json({ ok: true, accepted: true });
}

module.exports = handler;
module.exports.validateEmail = validateEmail;
module.exports.normalizeEmail = normalizeEmail;
module.exports.contextValue = contextValue;
module.exports.readConfig = readConfig;
// Tests share one module instance, so each needs a table nobody else filled.
module.exports.resetLimits = () => hits.clear();
