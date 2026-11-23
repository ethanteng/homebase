import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitForElementToBeRemoved,
  within,
} from "@testing-library/react";
import App from "./App";

/**
 * Arriving from a computer that asked to be added.
 *
 * The Uncloud app on a computer being set up opens this page in that computer's browser ("Sign in
 * to connect"), or shows a QR code a phone scans; either way the page arrives with the computer's
 * request in the address, often signed out. What matters is all in the arrival: the request has to
 * survive signing in, lead straight to the question and nothing else, and leave the address so a
 * reload doesn't ask again. So these render the app and arrive at it the way a browser does.
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
let requestState: "waiting" | "added" | "declined" = "waiting";
let reachAddress: string | null = "https://home.example.ts.net";
// Where the signed-in account's files are, or null on a host nobody has set up yet.
let rootPath: string | null = LIBRARY.rootPath;
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
      return json(
        {
          detail:
            "This request has run out or was already answered. Start again from the Uncloud app on the computer you’re adding.",
        },
        404,
      );
    if (method === "POST" && path.endsWith("/decline")) return json({ declined: true });
    if (method === "POST" && ownedElsewhere)
      return json(
        {
          detail:
            "That computer is already added to another account on this Uncloud. To add it to this account instead, sign in as the account that has it, open Computers and click Remove next to it, then try again.",
        },
        409,
      );
    if (method === "POST") return json({ ...SYNC, devices: [LAPTOP] });
    return json({ name: "Ada’s MacBook Air", expiresAt: "2026-10-03T12:10:00Z", state: requestState });
  }
  if (path === "/sync/pairing-codes" && method === "POST")
    return json({ code: "AB12C-DE34F", expiresAt: "2026-10-03T12:10:00Z" });
  if (path === "/sync/reach") return json({ address: reachAddress });
  if (path.startsWith("/sync")) return json(SYNC);
  // What My account reads about Dropbox: nothing set up.
  if (path === "/account/dropbox")
    return json({
      appKey: null,
      configured: false,
      source: "none",
      hostProvides: false,
      relayProvides: false,
      relayReachable: false,
      redirectUri: "http://localhost/api/providers/dropbox/callback",
      scopes: [],
    });
  if (path === "/host" && method === "PUT") {
    rootPath = JSON.parse(body ?? "{}").path;
    return json({ rootPath });
  }
  if (path === "/host") return json({ rootPath, canPickFolder: false });
  if (path.startsWith("/host/unclaimed")) return json({ entries: [] });
  if (path.startsWith("/library")) return json({ ...LIBRARY, rootPath });
  if (path.startsWith("/storage")) return json(STORAGE);
  if (path.startsWith("/imports/job")) return json({ job: null });
  // No uploads left unfinished by an earlier page.
  if (path === "/uploads") return json([]);
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
  requestState = "waiting";
  reachAddress = "https://home.example.ts.net";
  rootPath = LIBRARY.rootPath;
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

describe("arriving from a computer that asked to be added", () => {
  it("says why to sign in, asks once signed in, and adds the computer only when told to", async () => {
    await arriveAt("?pair=abc123");
    // Off the address straight away, so a reload later doesn't ask all over again.
    expect(window.location.search).toBe("");
    // Signed out, the sign-in says what it's for.
    expect(await screen.findByRole("heading", { name: "Sign in to add a computer" })).toBeTruthy();
    expect(screen.getByText(/A computer is asking to sync with your files/)).toBeTruthy();

    await signInAs("ada");

    // Says whose files it would join, so a phone signed in to somebody else's account is noticed.
    expect(
      await screen.findByRole("heading", { name: "Add Ada’s MacBook Air to Ada’s files?" }),
    ).toBeTruthy();
    expect(screen.getByText(/You’re signed in as Ada \(@ada\)\. Not you\?/)).toBeTruthy();
    // The question alone: nothing of Computers competes with it.
    expect(screen.queryByRole("heading", { name: "Computers" })).toBeNull();
    expect(sent.some((call) => call.path.endsWith("/approve"))).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Add this computer" }));

    expect(
      await screen.findByRole("heading", { name: "Ada’s MacBook Air is added to Ada’s files" }),
    ).toBeTruthy();
    expect(sent).toContainEqual({ method: "POST", path: "/sync/pairing-requests/abc123/approve" });

    fireEvent.click(screen.getByRole("button", { name: "Open Uncloud" }));
    expect(await screen.findByRole("heading", { name: "Computers" })).toBeTruthy();
  });

  it("tells the computer no when the answer is don't add", async () => {
    signedIn = USER;
    await arriveAt("?pair=abc123");

    fireEvent.click(await screen.findByRole("button", { name: "Don’t add" }));

    expect(await screen.findByRole("heading", { name: "Ada’s MacBook Air wasn’t added" })).toBeTruthy();
    expect(sent).toContainEqual({ method: "POST", path: "/sync/pairing-requests/abc123/decline" });
    expect(sent.some((call) => call.path.endsWith("/approve"))).toBe(false);
  });

  it("says what was answered when the same request is opened again", async () => {
    signedIn = USER;
    requestState = "added";
    await arriveAt("?pair=abc123");

    expect(
      await screen.findByRole("heading", { name: "Ada’s MacBook Air is added to Ada’s files" }),
    ).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add this computer" })).toBeNull();
    expect(sent.some((call) => call.method === "POST")).toBe(false);
  });

  it("says so when the request has run out, and offers nothing to approve", async () => {
    signedIn = USER;
    requestGone = true;
    await arriveAt("?pair=stale");

    expect(await screen.findByRole("heading", { name: "This request has ended" })).toBeTruthy();
    expect(screen.getByText(/run out or was already answered/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add this computer" })).toBeNull();
  });

  it("isn't put to whoever signs in next when it was left unanswered", async () => {
    signedIn = USER;
    await arriveAt("?pair=abc123");
    expect(await screen.findByRole("heading", { name: /Add Ada’s MacBook Air to/ })).toBeTruthy();

    // Whoever opened it signs out the usual way without answering, and somebody else signs in.
    fireEvent.click(screen.getByRole("button", { name: /^Account:/ }));
    await act(async () => {
      screen.getByRole("menuitem", { name: "Sign out" }).click();
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
    expect(refusal.textContent).toMatch(/open Computers and click Remove/);
    // Still asking, so it can be tried again once it's been removed there.
    expect(screen.getByRole("button", { name: "Add this computer" })).toBeTruthy();
  });

  it("lets an admin choose where files go first, then asks", async () => {
    signedIn = { ...USER, isAdmin: true };
    rootPath = null;
    await arriveAt("?pair=abc123");

    // Approving needs somewhere for the computer's files, so setup comes first.
    expect(await screen.findByRole("heading", { name: "Where should files be stored?" })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: /Add Ada’s MacBook Air/ })).toBeNull();

    fireEvent.change(screen.getByLabelText("Folder"), { target: { value: "/Users/ada/Uncloud" } });
    fireEvent.click(screen.getByRole("button", { name: /Continue/ }));

    expect(
      await screen.findByRole("heading", { name: "Add Ada’s MacBook Air to Ada’s files?" }),
    ).toBeTruthy();
  });

  it("is still asked after a look at My account", async () => {
    signedIn = USER;
    await arriveAt("?pair=abc123");
    expect(await screen.findByRole("heading", { name: /Add Ada’s MacBook Air to/ })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /^Account:/ }));
    await act(async () => {
      screen.getByRole("menuitem", { name: "My account" }).click();
    });
    expect(screen.queryByRole("heading", { name: /Add Ada’s MacBook Air to/ })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Computers" }));
    expect(await screen.findByRole("heading", { name: /Add Ada’s MacBook Air to/ })).toBeTruthy();
  });

  it("opens on My files as usual without one", async () => {
    signedIn = USER;
    await arriveAt("");

    expect(screen.queryByRole("heading", { name: "Computers" })).toBeNull();
    expect(sent.some((call) => call.path.startsWith("/sync/pairing-requests"))).toBe(false);
  });
});

async function openComputers() {
  signedIn = USER;
  await arriveAt("");
  fireEvent.click(screen.getByRole("button", { name: "Computers" }));
  await screen.findByRole("heading", { name: "Computers" });
}

describe("adding a computer from Computers", () => {
  it("is the page while there are none, with the app to get, and no button that does nothing", async () => {
    await openComputers();

    expect(await screen.findByRole("heading", { name: "Add a computer" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Add a computer" })).toBeNull();
    expect(screen.getByRole("link", { name: "Apple silicon" }).getAttribute("href")).toMatch(/arm64\.dmg$/);
    expect(screen.getByRole("link", { name: "Intel" }).getAttribute("href")).toMatch(/x64\.dmg$/);
    expect(screen.getByText("Open it and choose Connect to your Uncloud")).toBeTruthy();
    // Opened on the Uncloud computer itself, there's no app to hand a code to.
    expect(screen.queryByRole("button", { name: "Open in the Uncloud app" })).toBeNull();
  });

  it("says where computers reach this Uncloud", async () => {
    await openComputers();

    expect(await screen.findByText("home.example.ts.net")).toBeTruthy();
    expect(screen.getByText(/Computers can reach this Uncloud at/)).toBeTruthy();
  });

  it("says first when other computers can't reach it, and who can fix that", async () => {
    reachAddress = null;
    await openComputers();

    expect(await screen.findByText("Other computers can’t reach this Uncloud yet")).toBeTruthy();
    // Not an admin, so somebody else turns it on.
    expect(screen.getByText(/Ask whoever looks after Uncloud to turn it on in Settings/)).toBeTruthy();
  });
});

describe("getting a pairing code", () => {
  it("says whose files the computer will join, at the address other computers use", async () => {
    await openComputers();
    fireEvent.click(screen.getByText("Other ways to connect"));

    // Which account a code adds to is said before one is made, with the way out.
    expect(screen.getByText(/You’re signed in as Ada \(@ada\)\. Not you\?/)).toBeTruthy();
    fireEvent.click(await screen.findByRole("button", { name: /Get a pairing code/ }));

    expect(await screen.findByText("AB12C-DE34F")).toBeTruthy();
    expect(screen.getByText(/This code adds a computer to Ada’s files\./)).toBeTruthy();
    expect(screen.getByText("https://home.example.ts.net")).toBeTruthy();
    expect(screen.getByText(/You’re signed in as Ada \(@ada\)\. Not you\?/)).toBeTruthy();
    // The address shown works from elsewhere, so there's nothing to warn about.
    expect(screen.queryByText(/only works on the Uncloud computer itself/)).toBeNull();
  });
});
