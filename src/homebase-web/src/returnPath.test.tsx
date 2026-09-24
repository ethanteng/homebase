import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, waitForElementToBeRemoved } from "@testing-library/react";
import App from "./App";

/**
 * Coming back from somewhere else.
 *
 * Signing in to Dropbox leaves Uncloud entirely: the browser goes to dropbox.com and returns to an
 * address carrying how it went. Everything that has gone wrong with it has gone wrong in that
 * return — a dialog decided on before there was one to open, a tab whose handle was thrown away, an
 * outcome that outlived the dialog it belonged to — and none of it in a function worth calling
 * directly. So these render the app and arrive at it the way a browser does.
 *
 * What the server answers is stood in for; what the app does with the answer is not.
 */

const SESSION = {
  setupNeeded: false,
  hostConfigured: true,
  user: {
    id: "u1",
    username: "ethan",
    displayName: "Ethan",
    isAdmin: true,
    createdAt: "2026-01-01T00:00:00Z",
    disabledAt: null,
  },
};

const LIBRARY = { rootPath: "/Users/ethan/Uncloud", name: "Uncloud", hostRoot: "/Users/ethan", canPickFolder: false };
const STORAGE = { totalBytes: 1000, freeBytes: 900, usedBytes: 100, usersBytes: 0 };

/** Dropbox as the sources endpoint describes it, connected or not. */
function dropbox(connected: boolean) {
  return {
    accounts: [
      {
        id: "dropbox",
        name: "Dropbox",
        configured: true,
        connected,
        accountName: connected ? "Ethan Teng" : null,
        oneClickHere: true,
        destination: "Files/Dropbox",
      },
    ],
    places: [],
    suggestions: [],
  };
}

let connected = false;

/** Every call the app makes on the way to showing a dialog, answered the way a host would. */
function serve(path: string) {
  if (path.startsWith("/session")) return SESSION;
  if (path.startsWith("/library")) return LIBRARY;
  if (path.startsWith("/storage")) return STORAGE;
  // Browsing inside a source answers with a bare list; the sources list answers with the shape
  // above. Both start "/imports/sources", so the longer one is matched first.
  if (/^\/imports\/sources\/[^/]+\/files/.test(path)) return [];
  if (path.startsWith("/imports/sources")) return dropbox(connected);
  if (path.startsWith("/imports/job")) return { job: null };
  if (path.startsWith("/files")) return { path: "", entries: [], parent: null };
  if (path.startsWith("/sync")) return { folders: [], devices: [], available: false };
  return {};
}

/** Arrive at Uncloud the way the browser does when Dropbox sends it back. */
async function arriveAt(search: string) {
  window.history.replaceState(null, "", `/${search}`);
  render(<App />);
  // The first paint is always the loading screen: the session has to be fetched before there is
  // anything to show, which is the whole reason the dialog had nothing to open against. Waiting
  // for it to go is waiting for the render where the dialog element finally exists.
  await waitForElementToBeRemoved(() => screen.queryByText(/Opening Uncloud/i), { timeout: 4000 });
  await act(async () => {
    await Promise.resolve();
  });
}

const dialog = () => document.querySelector("dialog");

beforeEach(() => {
  connected = false;
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(typeof input === "string" ? input : input.toString());
    const path = url.replace(/^.*\/api/, "");
    return Promise.resolve(
      new Response(JSON.stringify(serve(path)), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});

describe("arriving back from Dropbox", () => {
  it("opens the dialog even though it was decided on before there was one", async () => {
    // The element does not exist on the render that decides to open it, and nothing about which
    // dialog is wanted changes afterwards. Watching for the element to appear is what makes this
    // work; watching only the decision leaves a finished sign-in looking like nothing happened.
    await arriveAt("?dropbox=connected");

    await waitFor(() => expect(dialog()?.open).toBe(true));
  });

  it("leaves the dialog shut on an ordinary visit", async () => {
    await arriveAt("");

    expect(dialog()?.open ?? false).toBe(false);
  });

  it("lands on the account that was signed in to, not the list of places", async () => {
    connected = true;
    await arriveAt("?dropbox=connected");

    // Coming back to where they started reads as having got nowhere.
    await screen.findByText(/Signed in to Dropbox as Ethan Teng/i);
    expect(screen.queryByText(/Where are your files\?/i)).toBeNull();
  });

  it("says so when the sign-in was refused", async () => {
    await arriveAt("?dropbox=denied");

    await screen.findByText(/sign-in was cancelled/i);
  });

  it("says so when the sign-in failed", async () => {
    await arriveAt("?dropbox=failed");

    await screen.findByText(/couldn.t be connected/i);
  });

  it("only claims success once the account agrees it is connected", async () => {
    // The callback's word and the account's own state should never differ, but "signed in" above a
    // button asking you to sign in would be worse than letting the folders speak for themselves.
    connected = false;
    await arriveAt("?dropbox=connected");
    await waitFor(() => expect(dialog()?.open).toBe(true));

    expect(screen.queryByText(/Uncloud is signed in to Dropbox/i)).toBeNull();
  });

  it("takes the outcome off the address, so a reload is not another arrival", async () => {
    await arriveAt("?dropbox=connected");

    expect(window.location.search).toBe("");
  });

  it("does not repeat the outcome when Add files is opened again later", async () => {
    // The address was cleaned, but the value read from it outlived the dialog it opened: closing
    // and reopening replayed an old failure as though a sign-in had just finished.
    await arriveAt("?dropbox=failed");
    await screen.findByText(/couldn.t be connected/i);

    const close = screen.getByRole("button", { name: /close/i });
    await act(async () => {
      close.click();
    });
    // More than one way in to the same dialog; any of them must be an ordinary visit now.
    const [addFiles] = await screen.findAllByRole("button", { name: /add files/i });
    await act(async () => {
      addFiles.click();
    });

    await screen.findByText(/Where are your files\?/i);
    expect(screen.queryByText(/couldn.t be connected/i)).toBeNull();
  });
});
