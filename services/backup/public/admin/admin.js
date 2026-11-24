// The operator's page for making invites to Uncloud Backup. It talks only to this service, with the
// operator token typed into it, which it keeps in this page while it's open and nowhere else: not
// in storage, not in the address. Everything it shows is set as text, never as markup.

const $ = (id) => document.getElementById(id);
const date = new Intl.DateTimeFormat(undefined, { dateStyle: "medium" });

let token = null;
// A slower list answer must not paint over a newer one, or sign the operator out after they've moved on.
let listEpoch = 0;

class Refused extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function call(path, { method = "GET", body } = {}) {
  let response;
  try {
    response = await fetch(path, {
      method,
      cache: "no-store",
      headers: { authorization: `Bearer ${token}`, ...(body ? { "content-type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new Refused(0, "Couldn’t reach Uncloud Backup. Check your connection, then try again.");
  }
  const answer = await response.json().catch(() => null);
  if (!response.ok) throw new Refused(response.status, answer?.error?.message ?? `Uncloud Backup answered ${response.status}.`);
  return answer;
}

function say(element, text) {
  element.textContent = text;
  element.hidden = !text;
}

function signIn() {
  $("sign-in").hidden = true;
  $("signed-in").hidden = false;
  $("note").focus();
}

/** Back to asking for the token, saying why when there's a reason. */
function signOut(reason = "") {
  token = null;
  listEpoch += 1;
  $("token").value = "";
  $("made").hidden = true;
  $("code").textContent = "";
  $("message").value = "";
  $("signed-in").hidden = true;
  $("sign-in").hidden = false;
  say($("token-error"), reason);
  $("token").focus();
}

function refusedBecauseOfToken(error) {
  if (error.status !== 401) return false;
  signOut("That operator token doesn’t work any more. Enter the current one.");
  return true;
}

function status(invite) {
  if (invite.state === "used") return `Used ${date.format(new Date(invite.redeemedAt))}`;
  if (invite.state === "expired") return `Expired ${date.format(new Date(invite.expiresAt))}`;
  return `Open until ${date.format(new Date(invite.expiresAt))}`;
}

function render({ invites }) {
  const rows = $("invites").querySelector("tbody");
  rows.replaceChildren(...invites.map((invite) => {
    const row = document.createElement("tr");
    const who = document.createElement("td");
    who.textContent = invite.note || "—";
    if (!invite.note) who.className = "muted";
    const made = document.createElement("td");
    made.textContent = date.format(new Date(invite.createdAt));
    const state = document.createElement("td");
    const badge = document.createElement("span");
    badge.className = `state ${invite.state}`;
    badge.textContent = status(invite);
    state.append(badge);
    row.append(who, made, state);
    return row;
  }));
  $("invites").hidden = invites.length === 0;
  $("none").hidden = invites.length > 0;
}

/** Fetches the list and paints it, unless a newer fetch or a sign-out has since taken over. */
async function listed() {
  const epoch = ++listEpoch;
  try {
    const answer = await call("/api/operator/invites");
    if (epoch !== listEpoch) return false;
    render(answer);
    return true;
  } catch (error) {
    if (epoch !== listEpoch) return false;
    throw error;
  }
}

async function refresh({ keepCode = false } = {}) {
  say($("list-error"), "");
  try {
    await listed();
  } catch (error) {
    // The code is shown only once. Signing out here would hide the one this page just made.
    if (keepCode && error.status === 401) {
      say($("list-error"), "That operator token doesn’t work any more. Copy the code, then enter it again.");
      return;
    }
    if (!refusedBecauseOfToken(error)) say($("list-error"), error.message);
  }
}

/** What to send whoever the invite is for, in words they can act on. */
function message(invite) {
  return [
    "You’re invited to try Uncloud Backup: an encrypted copy of everyone’s files on your Uncloud, kept somewhere else and updated every night.",
    "",
    `Your invite code: ${invite.code}`,
    "",
    `On your Uncloud, go to Settings → Offsite backup, enter the code and click Join. The code works once, until ${date.format(new Date(invite.expiresAt))}.`,
    "",
    "More in the guide: https://uncloud.life/help#offsite-backup",
  ].join("\n");
}

function showMade(invite) {
  $("code").textContent = invite.code;
  $("made-for").textContent = invite.note ? `for ${invite.note}` : "";
  $("message").value = message(invite);
  $("made").hidden = false;
  $("copy-code").focus();
}

/** Copies, and says so on the button; where the browser won't let the page copy, selects the text to copy by hand. */
async function copy(button, text, select) {
  const label = button.textContent;
  try {
    await navigator.clipboard.writeText(text);
    button.textContent = "Copied";
    setTimeout(() => (button.textContent = label), 2000);
  } catch {
    select();
  }
}

$("token-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  button.disabled = true;
  say($("token-error"), "");
  token = $("token").value.trim();
  try {
    if (!(await listed())) {
      token = null;
      return;
    }
    signIn();
  } catch (error) {
    token = null;
    say($("token-error"), error.status === 401 ? "That isn’t the operator token." : error.message);
  } finally {
    button.disabled = false;
  }
});

$("invite-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = $("make");
  button.disabled = true;
  // Forget is disabled until the code is on screen. Otherwise it can land during this request
  // and clear the only copy of a code the service has already given out.
  $("sign-out").disabled = true;
  $("refresh").disabled = true;
  say($("invite-error"), "");
  try {
    const invite = await call("/api/operator/invites", {
      method: "POST",
      body: { note: $("note").value.trim() || null, days: Number($("days").value) },
    });
    showMade(invite);
    $("note").value = "";
    await refresh({ keepCode: true });
  } catch (error) {
    if (!refusedBecauseOfToken(error)) say($("invite-error"), error.message);
  } finally {
    button.disabled = false;
    $("sign-out").disabled = false;
    $("refresh").disabled = false;
  }
});

$("copy-code").addEventListener("click", (event) =>
  copy(event.currentTarget, $("code").textContent, () => getSelection().selectAllChildren($("code"))));
$("copy-message").addEventListener("click", (event) =>
  copy(event.currentTarget, $("message").value, () => $("message").select()));
$("refresh").addEventListener("click", () => void refresh());
$("sign-out").addEventListener("click", () => signOut());
