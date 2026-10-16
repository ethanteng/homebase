import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  waitForElementToBeRemoved,
  within,
} from "@testing-library/react";
import App from "./App";

/**
 * Arriving from a QR code.
 *
 * The Uncloud app on a computer being set up shows a code; a phone scans it and lands here with
 * the computer's request in the address, often signed out. What matters is all in the arrival:
 * the request has to survive signing in, lead straight to the question, and leave the address so
 * a reload doesn't ask again. So these render the app and arrive at it the way a phone does.
 *
 * What the server answers is stood in for; what the app sends it, and does with the answer, is not.
 */

const USER = {
  id: "u1",
  username: "ada",
  displayName: "Ada",
  isAdmin: false,
  createdAt: "2026-01-01T00:00:00Z",
  disabledAt: null,
};
const BO = { ...USER, id: "u2", username: "bo", displayName: "Bo" };
const LIBRARY = { rootPath: "/Users/ada/Uncloud", name: "Uncloud", hostRoot: "/Users/ada", canPickFolder: false };
const STORAGE = { totalBytes: 1000, freeBytes: 900, usedBytes: 100, usersBytes: 0 };
const SYNC = { available: true, detail: null, hostDeviceId: "HOST", devices: [], folders: [], offers: [] };
const LAPTOP = { deviceId: "LAPTOP", name: "Ada’s MacBook Air", connected: false };

let signedIn: typeof USER | null = null;
let requestGone = false;
let ownedElsewhere = false;
let sent: { method: string; path: string }[] = [];

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function serve(method: string, path: string, body: string | undefined) {
  if (path === "/session" && method === "POST") {
    signedIn = JSON.parse(body ?? "{}").username === "bo" ? BO : USER;
    return json({ user: signedIn });
  }
  if (path === "/session" && method === "DELETE") {
    signedIn = null;
    return json({});
  }
  if (path.startsWith("/session"))
    return json({ setupNeeded: false, hostConfigured: true, user: signedIn });
  if (path.startsWith("/sync/pairing-requests/")) {
    if (requestGone)
      return json({ detail: "This computer’s code has run out or was already used." }, 404);
    if (method === "POST" && ownedElsewhere)
      return json(
        {
          detail:
            "That computer is already added to another account on this Uncloud. To add it to this account instead, sign in as the account that has it, open My computers and click Remove next to it, then try again.",
        },
        409,
      );
    if (method === "POST") return json({ ...SYNC, devices: [LAPTOP] });
    return json({ name: "Ada’s MacBook Air", expiresAt: "2026-10-03T12:10:00Z" });
  }
  if (path === "/sync/pairing-codes" && method === "POST")
    return json({ code: "AB12C-DE34F", expiresAt: "2026-10-03T12:10:00Z" });
  if (path.startsWith("/sync")) return json(SYNC);
  if (path.startsWith("/library")) return json(LIBRARY);
  if (path.startsWith("/storage")) return json(STORAGE);
  if (path.startsWith("/imports/job")) return json({ job: null });
  if (path.startsWith("/files")) return json({ path: "", entries: [], parent: null });
  return json({});
}

async function arriveAt(search: string) {
  window.history.replaceState(null, "", `/${search}`);
  render(<App />);
  await waitForElementToBeRemoved(() => screen.queryByText(/Opening Uncloud/i), { timeout: 4000 });
  await act(async () => {
    await Promise.resolve();
  });
}

async function signInAs(username: string) {
  fireEvent.change(await screen.findByLabelText("Username"), { target: { value: username } });
  fireEvent.change(screen.getByLabelText("Password"), { target: { value: "correct horse" } });
  fireEvent.submit(screen.getByLabelText("Password").closest("form")!);
}

beforeEach(() => {
  signedIn = null;
  requestGone = false;
  ownedElsewhere = false;
  sent = [];
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    const method = init?.method ?? "GET";
    sent.push({ method, path });
    return Promise.resolve(serve(method, path, init?.body as string | undefined));
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});

describe("arriving from a computer's QR code", () => {
  it("asks once signed in, and adds the computer only when told to", async () => {
    await arriveAt("?pair=abc123");
    // Off the address straight away, so a reload later doesn't ask all over again.
    expect(window.location.search).toBe("");

    await signInAs("ada");

    // Says whose files it would join, so a phone signed in to somebody else's account is noticed.
    expect(
      await screen.findByRole("heading", { name: "Add Ada’s MacBook Air to Ada’s files?" }),
    ).toBeTruthy();
    expect(screen.getByText(/You’re signed in as Ada \(@ada\)\. Not you\?/)).toBeTruthy();
    expect(sent.some((call) => call.path.endsWith("/approve"))).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Add this computer" }));

    expect(
      await screen.findByRole("heading", { name: "Ada’s MacBook Air is added to Ada’s files" }),
    ).toBeTruthy();
    expect(sent).toContainEqual({ method: "POST", path: "/sync/pairing-requests/abc123/approve" });
    // The list below shows it too, from what approving answered.
    expect(screen.getAllByText("Ada’s MacBook Air").length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByText(/is added/)).toBeNull());
  });

  it("leaves the computer alone when the answer is not now", async () => {
    signedIn = USER;
    await arriveAt("?pair=abc123");

    fireEvent.click(await screen.findByRole("button", { name: "Not now" }));

    await waitFor(() => expect(screen.queryByRole("heading", { name: /Add Ada’s/ })).toBeNull());
    expect(sent.some((call) => call.method === "POST")).toBe(false);
    // Still on My computers, where adding one is explained.
    expect(screen.getByRole("heading", { name: /My computers/ })).toBeTruthy();
  });

  it("says so when the code has run out, and offers nothing to approve", async () => {
    signedIn = USER;
    requestGone = true;
    await arriveAt("?pair=stale");

    expect(await screen.findByRole("heading", { name: /Couldn’t find that computer/ })).toBeTruthy();
    expect(screen.getByText(/run out or was already used/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add this computer" })).toBeNull();
  });

  it("isn't put to whoever signs in next when it was left unanswered", async () => {
    signedIn = USER;
    await arriveAt("?pair=abc123");
    expect(await screen.findByRole("heading", { name: /Add Ada’s MacBook Air to/ })).toBeTruthy();

    // Whoever opened the code signs out the usual way without answering, and somebody else signs in.
    await act(async () => {
      screen.getByTitle("Sign out").click();
    });
    await signInAs("bo");

    await screen.findByText("Nothing here yet");
    expect(screen.queryByRole("heading", { name: /Add Ada’s MacBook Air/ })).toBeNull();
    expect(sent.filter((call) => call.path.startsWith("/sync/pairing-requests"))).toHaveLength(1);
  });

  it("waits for the right account when whoever opened it says the one signed in isn't theirs", async () => {
    // The phone was left signed in as Ada; it's Bo setting up his computer.
    signedIn = USER;
    await arriveAt("?pair=abc123");
    const card = (
      await screen.findByRole("heading", { name: "Add Ada’s MacBook Air to Ada’s files?" })
    ).closest("section")!;

    await act(async () => {
      within(card).getByRole("button", { name: "Sign out" }).click();
    });
    expect(sent).toContainEqual({ method: "DELETE", path: "/session" });
    await signInAs("bo");

    // Asked again, as Bo, and still nothing added until he says so.
    expect(
      await screen.findByRole("heading", { name: "Add Ada’s MacBook Air to Bo’s files?" }),
    ).toBeTruthy();
    expect(screen.getByText(/You’re signed in as Bo \(@bo\)/)).toBeTruthy();
    expect(sent.some((call) => call.path.endsWith("/approve"))).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Add this computer" }));
    expect(
      await screen.findByRole("heading", { name: "Ada’s MacBook Air is added to Bo’s files" }),
    ).toBeTruthy();
  });

  it("says how to move a computer another account already has", async () => {
    signedIn = BO;
    ownedElsewhere = true;
    await arriveAt("?pair=abc123");

    fireEvent.click(await screen.findByRole("button", { name: "Add this computer" }));

    const refusal = await screen.findByRole("alert");
    expect(refusal.textContent).toMatch(/already added to another account/);
    expect(refusal.textContent).toMatch(/open My computers and click Remove/);
    // Still asking, so it can be tried again once it's been removed there.
    expect(screen.getByRole("button", { name: "Add this computer" })).toBeTruthy();
  });

  it("opens on My files as usual without one", async () => {
    signedIn = USER;
    await arriveAt("");

    expect(screen.queryByRole("heading", { name: /My computers/ })).toBeNull();
    expect(sent.some((call) => call.path.startsWith("/sync/pairing-requests"))).toBe(false);
  });
});

describe("getting a pairing code", () => {
  it("says whose files the computer will join", async () => {
    signedIn = USER;
    await arriveAt("");
    fireEvent.click(screen.getByRole("button", { name: /My computers/ }));

    fireEvent.click(await screen.findByRole("button", { name: /Get a pairing code/ }));

    expect(await screen.findByText("AB12C-DE34F")).toBeTruthy();
    expect(screen.getByText(/This code adds a computer to Ada’s files\./)).toBeTruthy();
    expect(screen.getByText(/You’re signed in as Ada \(@ada\)\. Not you\?/)).toBeTruthy();
  });
});
