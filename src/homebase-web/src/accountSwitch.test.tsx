import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import App from "./App";

/**
 * One browser, more than one person. A household shares computers, so somebody signing out and
 * somebody else signing in on the same page is ordinary — and nothing the first one was doing may
 * still be on screen for the second.
 */

const user = (username: string, isAdmin: boolean) => ({
  id: username,
  username,
  displayName: username,
  isAdmin,
  createdAt: "2026-01-01T00:00:00Z",
  disabledAt: null,
});

const LIBRARY = { rootPath: "/host/users/x", name: "x", hostRoot: null, canPickFolder: false };
const STORAGE = { totalBytes: 1000, freeBytes: 900, usedBytes: 100 };

/** What Ada brought in from a folder on the host a moment ago. Only she should ever see it. */
const ADAS_IMPORT = {
  id: "job-ada",
  sourceId: "place-1",
  remotePaths: ["/From UncloudHost.rtf"],
  label: "From UncloudHost.rtf",
  stage: "Done",
  totalFiles: 1,
  completedFiles: 1,
  bytes: 375,
  currentFile: null,
  result: {
    imported: [{ localPath: "Documents/From UncloudHost.rtf", remotePath: "/From UncloudHost.rtf", size: 375 }],
    skipped: [],
    bytes: 375,
    alreadyHere: 0,
    importedCount: 1,
    skippedCount: 0,
  },
  error: null,
  startedAt: new Date().toISOString(),
  finishedAt: new Date().toISOString(),
  running: false,
};

let signedIn: string | null = null;
const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });

beforeEach(() => {
  signedIn = "ada";
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    const method = init?.method ?? "GET";
    if (path === "/session" && method === "DELETE") {
      signedIn = null;
      return Promise.resolve(json({}));
    }
    if (path === "/session" && method === "POST") {
      signedIn = (JSON.parse(String(init!.body)) as { username: string }).username;
      return Promise.resolve(json({ user: user(signedIn, false) }));
    }
    if (path === "/session")
      return Promise.resolve(
        json({ setupNeeded: false, hostConfigured: true, user: signedIn ? user(signedIn, signedIn === "ada") : null }),
      );
    if (path.startsWith("/library")) return Promise.resolve(json(LIBRARY));
    if (path.startsWith("/storage")) return Promise.resolve(json(STORAGE));
    // Each account is told about its own import and nobody else's, as the server does.
    if (path.startsWith("/imports/job")) return Promise.resolve(json({ job: signedIn === "ada" ? ADAS_IMPORT : null }));
    if (path.startsWith("/files")) return Promise.resolve(json({ path: "", entries: [], skippedCount: 0, indexedAt: "" }));
    return Promise.resolve(json({}));
  });
});

afterEach(() => vi.unstubAllGlobals());

describe("somebody else signing in on the same browser", () => {
  it("sees nothing of what the last person brought in", async () => {
    render(<App />);
    expect(await screen.findByText(/Added 1 file from From UncloudHost\.rtf/)).toBeInTheDocument();

    await act(async () => {
      screen.getByRole("button", { name: "Sign out" }).click();
    });
    fireEvent.change(await screen.findByLabelText("Username"), { target: { value: "ethan" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "correct horse battery" } });
    await act(async () => {
      screen.getByRole("button", { name: /Sign in/ }).click();
    });

    await screen.findByText("Nothing here yet");
    await waitFor(() => expect(screen.getByText("@ethan")).toBeInTheDocument());
    expect(screen.queryByText(/From UncloudHost\.rtf/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Show them/ })).not.toBeInTheDocument();
  });
});
