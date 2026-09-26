"use strict";

// Interest from storage companies, sent from the form at the foot of /partners.
//
// The page is static, and a Mailtrap token in browser JavaScript is a published
// token, so the form posts here and only this function talks to Mailtrap. Each
// submission becomes an email to PARTNER_NOTIFY_TO (or SIGNUP_NOTIFY_TO when that
// isn't set), with Reply-To set to the sender so answering it reaches them.

const MAX_BODY_BYTES = 8192;
const MAX_EMAIL_LENGTH = 254; // RFC 5321
const MAX_NAME_LENGTH = 120;
const MAX_NOTE_LENGTH = 2000;
const SEND_TIMEOUT_MS = 8000;

// The choices the form offers. Anything else in `tests` is dropped, not echoed.
const TESTS = ["Bundle", "Co-marketing page", "Customer education", "Not sure yet"];

// Deliberately strict rather than clever: one @, no delimiters that would let an
// address smuggle a second recipient, and a plausible domain.
const EMAIL_PATTERN = /^[^\s@,;:<>"'\\]{1,64}@[^\s@,;:<>"'\\]{1,185}\.[A-Za-z]{2,24}$/;

function text(value) {
  return typeof value === "string" ? value.trim() : "";
}

/** A single line of at most `max` characters: no line breaks to start a header with. */
function line(value, max) {
  return text(value).replace(/[\r\n\t\0]+/g, " ").slice(0, max);
}

/** Returns a message to show the visitor, or null when the address is usable. */
function validateEmail(email) {
  if (!email) return "Please add a valid work email.";
  if (email.length > MAX_EMAIL_LENGTH) return "That email address is too long.";
  // A carriage return in an address is where header injection begins.
  if (/[\r\n\t\0]/.test(email)) return "Please add a valid work email.";
  if (!EMAIL_PATTERN.test(email)) return "Please add a valid work email.";
  return null;
}

/** The submission as it will be sent, or { error } to show the visitor. */
function readSubmission(payload) {
  const name = line(payload.name, MAX_NAME_LENGTH);
  if (!name) return { error: "Please add your name." };
  const email = text(payload.email);
  const problem = validateEmail(email);
  if (problem) return { error: problem };
  const tests = Array.isArray(payload.tests) ? TESTS.filter((t) => payload.tests.includes(t)) : [];
  return {
    name,
    email,
    company: line(payload.company, MAX_NAME_LENGTH),
    tests,
    note: text(payload.note).replace(/\0/g, "").slice(0, MAX_NOTE_LENGTH),
    partner: line(payload.partner, MAX_NAME_LENGTH)
  };
}

/** Reads the settings this function needs, and says plainly which are missing. */
function readConfig(env) {
  const token = text(env.MAILTRAP_TOKEN);
  const notify = text(env.PARTNER_NOTIFY_TO) || text(env.SIGNUP_NOTIFY_TO);
  const missing = [];
  if (!token) missing.push("MAILTRAP_TOKEN");
  if (!notify) missing.push("PARTNER_NOTIFY_TO or SIGNUP_NOTIFY_TO");
  const inbox = text(env.MAILTRAP_INBOX_ID);
  return {
    missing,
    token,
    notify,
    from: text(env.MAILTRAP_FROM) || "partners@uncloud.life",
    // An inbox id routes to Mailtrap's sandbox, which captures mail instead of
    // delivering it — useful for trying the form out on a preview deployment.
    url: inbox
      ? `https://sandbox.api.mailtrap.io/api/send/${encodeURIComponent(inbox)}`
      : "https://send.api.mailtrap.io/api/send",
    sandbox: Boolean(inbox)
  };
}

function message(s) {
  return [
    `Name: ${s.name}`,
    `Company: ${s.company || "—"}`,
    `Email: ${s.email}`,
    `Wants to test: ${s.tests.length ? s.tests.join(", ") : "—"}`,
    ...(s.partner ? [`Page personalised for: ${s.partner}`] : []),
    "",
    s.note || "(No note.)",
    "",
    `Sent from uncloud.life/partners at ${new Date().toISOString()}`,
    ""
  ].join("\n");
}

async function sendToMailtrap(submission, config, fetchImpl) {
  const who = submission.company ? `${submission.name}, ${submission.company}` : submission.name;
  const response = await fetchImpl(config.url, {
    method: "POST",
    headers: {
      "Api-Token": config.token,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      from: { email: config.from, name: "Uncloud Partners" },
      to: [{ email: config.notify }],
      headers: { "Reply-To": submission.email },
      subject: `Partner interest: ${who}`,
      text: message(submission),
      category: "partner-interest"
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
  if (!payload || typeof payload !== "object") payload = {};

  // Bots fill in every field they find. A real visitor never sees this one, so
  // anything in it is answered with a cheerful nothing.
  if (text(payload.website)) return response.status(200).json({ ok: true });

  const submission = readSubmission(payload);
  if (submission.error) return response.status(400).json({ error: submission.error });

  const config = readConfig(env);
  if (config.missing.length > 0) {
    console.error(`Partner form not configured; missing ${config.missing.join(", ")}`);
    return response.status(503).json({
      error: "That didn’t go through. Please email ethan@uncloud.life instead."
    });
  }

  try {
    await sendToMailtrap(submission, config, fetchImpl);
  } catch (failure) {
    // The visitor gets nothing actionable; the detail belongs in the log.
    console.error("Partner interest send failed:", failure.message);
    return response.status(502).json({
      error: "That didn’t go through. Try again, or email ethan@uncloud.life."
    });
  }

  return response.status(200).json({ ok: true, sandbox: config.sandbox });
}

module.exports = handler;
module.exports.validateEmail = validateEmail;
module.exports.readSubmission = readSubmission;
module.exports.readConfig = readConfig;
