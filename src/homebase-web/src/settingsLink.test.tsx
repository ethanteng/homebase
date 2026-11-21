import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitForElementToBeRemoved } from "@testing-library/react";
import App from "./App";

/**
 * Arriving from the Uncloud menu on the host Mac, which opens `/?settings` so whoever looks after
 * this Uncloud lands on Settings. The browser it opens is often signed out, so the destination has
 * to survive signing in, and be spent once it has been used.
 *
 * What the server answers is stood in for; what the app does with the answer is not.
 */

const ADMIN = {
  id: "u1",
  username: "ada",
  displayName: "Ada",
  isAdmin: true,
  createdAt: "2026-01-01T00:00:00Z",
  disabledAt: null,
};
const LIBRARY = { rootPath: "/Users/ada/Uncloud", name: "Uncloud", hostRoot: "/Users/ada", canPickFolder: false };
const STORAGE = { totalBytes: 1000, freeBytes: 900, usedBytes: 100, usersBytes: 0 };

let signedIn: typeof ADMIN | null = null;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function serve(method: string, path: string) {
  if (path === "/session" && method === "POST") {
    signedIn = ADMIN;
    return json({ user: signedIn });
  }
  if (path === "/session" && method === "DELETE") {
    signedIn = null;
    return json({});
  }
  if (path.startsWith("/session"))
    return json({ setupNeeded: false, hostConfigured: true, user: signedIn });
  if (path.startsWith("/library")) return json(LIBRARY);
  if (path.startsWith("/storage")) return json(STORAGE);
  if (path.startsWith("/imports/job")) return json({ job: null });
  if (path === "/uploads") return json([]);
  if (path.startsWith("/files")) return json({ path: "", entries: [], parent: null });
  // The settings rows ask about the host; here it can't say, which each row copes with.
  return json({ detail: "Not available here." }, 404);
}

async function arriveAt(search: string) {
  window.history.replaceState(null, "", `/${search}`);
  render(<App />);
  await waitForElementToBeRemoved(() => screen.queryByText(/Opening Uncloud/i), { timeout: 4000 });
  await act(async () => {
    await Promise.resolve();
  });
}

async function signIn() {
  fireEvent.change(await screen.findByLabelText("Username"), { target: { value: "ada" } });
  fireEvent.change(screen.getByLabelText("Password"), { target: { value: "correct horse" } });
  fireEvent.submit(screen.getByLabelText("Password").closest("form")!);
}

async function signOut() {
  fireEvent.click(await screen.findByRole("button", { name: /^Account:/ }));
  await act(async () => {
    screen.getByRole("menuitem", { name: "Sign out" }).click();
  });
}

beforeEach(() => {
  signedIn = null;
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    return Promise.resolve(serve(init?.method ?? "GET", path));
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});

describe("arriving from the Uncloud menu's Settings link", () => {
  it("opens Settings after signing in, and only that once", async () => {
    await arriveAt("?settings");
    // Off the address straight away, so a reload later doesn't reopen it.
    expect(window.location.search).toBe("");

    await signIn();
    expect(await screen.findByRole("heading", { name: "Settings", level: 1 })).toBeTruthy();

    await signOut();
    await signIn();
    expect(await screen.findByRole("heading", { name: "My files", level: 1 })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Settings", level: 1 })).toBeNull();
  });

  it("opens Settings straight away when already signed in", async () => {
    signedIn = ADMIN;
    await arriveAt("?settings");
    expect(await screen.findByRole("heading", { name: "Settings", level: 1 })).toBeTruthy();
  });
});
