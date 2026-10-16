import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import SyncPanel from "./SyncPanel";

/**
 * Files a computer sent that Syncthing is holding back because this Uncloud has no room for them.
 * From the computer they look sent, so the folder says so here rather than "up to date".
 */

const folder = (waitingForRoom: number) => ({
  id: "docs",
  path: "Documents",
  label: "Documents",
  deviceIds: ["LAPTOP"],
  state: "idle",
  error: null,
  files: 12,
  bytes: 4096,
  waitingForRoom,
});

let folders = [folder(0)];

const ME = {
  id: "u1",
  username: "ada",
  displayName: "Ada",
  isAdmin: false,
  createdAt: "2026-01-01T00:00:00Z",
  disabledAt: null,
};

const json = (body: unknown) =>
  new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

beforeEach(() => {
  vi.stubGlobal("fetch", () =>
    Promise.resolve(
      json({ available: true, detail: null, hostDeviceId: "HOST", devices: [], folders, offers: [] }),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("a synced folder short of room", () => {
  it("says how many files are waiting for room instead of being up to date", async () => {
    folders = [folder(2)];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    const line = await screen.findByText(/2 files are waiting for room on this Uncloud/);
    expect(line).toHaveClass("error-message");
    expect(screen.queryByText(/up to date/)).not.toBeInTheDocument();
  });

  it("is up to date as ever when nothing is waiting", async () => {
    folders = [folder(0)];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/up to date · 12 files/)).toHaveClass("muted");
  });
});
