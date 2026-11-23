import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import SyncPanel from "./SyncPanel";
import type { SyncProgress } from "./api";

/**
 * This Uncloud having all of a folder says nothing about whether your computers do: a big folder
 * takes a laptop far longer than it took here. "Up to date" waits for them too.
 */

const GB = 1024 ** 3;

const progress = (overrides: Partial<SyncProgress> = {}): SyncProgress => ({
  deviceId: "LAPTOP",
  state: "valid",
  completion: 100,
  needBytes: 0,
  needItems: 0,
  needDeletes: 0,
  ...overrides,
});

let computers: SyncProgress[] | undefined;
let connected = true;
let deviceIds = ["LAPTOP"];

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
      json({
        available: true,
        detail: null,
        hostDeviceId: "HOST",
        devices: [
          { deviceId: "LAPTOP", name: "Ada’s MacBook", connected, address: null },
          { deviceId: "IMAC", name: "Ada’s iMac", connected, address: null },
          { deviceId: "OLD", name: "Ada’s old laptop", connected, address: null },
        ],
        folders: [
          {
            id: "desktop",
            path: "Desktop",
            label: "Desktop",
            deviceIds,
            state: "idle",
            error: null,
            files: 2332,
            bytes: 24.7 * GB,
            waitingForRoom: 0,
            computers,
          },
        ],
        offers: [],
      }),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  connected = true;
  deviceIds = ["LAPTOP"];
});

describe("a synced folder this Uncloud has all of", () => {
  it("says how far a computer still downloading it has got, not that it's up to date", async () => {
    computers = [progress({ completion: 10.9, needBytes: 22 * GB, needItems: 2139 })];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(
      await screen.findByText(/Sending to Ada’s MacBook: 10% there, 22 GB to go · 2,332 files · 24\.7 GB/),
    ).toHaveClass("muted");
    expect(screen.queryByText(/Up to date/)).not.toBeInTheDocument();
  });

  it("is up to date once the computer has all of it too", async () => {
    computers = [progress()];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/Up to date · 2,332 files/)).toBeInTheDocument();
  });

  it("never says 100% while something is left", async () => {
    computers = [progress({ completion: 99.97, needBytes: 0, needItems: 3 })];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/Sending to Ada’s MacBook: 99% there ·/)).toBeInTheDocument();
  });

  it("says a computer that hasn't taken the folder yet is still to accept it", async () => {
    computers = [progress({ state: "notSharing", completion: 0 })];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/Waiting for Ada’s MacBook to accept it/)).toBeInTheDocument();
  });

  it("says a computer that's away catches up when it's back", async () => {
    connected = false;
    // What Syncthing says of a computer that's away, along with how far it had got.
    computers = [progress({ state: "unknown", completion: 40, needBytes: 2 * GB, needItems: 10 })];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(
      await screen.findByText(/Ada’s MacBook is 40% there, 2 GB to go — it catches up when it’s back online/),
    ).toBeInTheDocument();
  });

  it("is up to date with a computer that's asleep with all of it", async () => {
    connected = false;
    computers = [progress({ state: "unknown" })];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/Up to date · 2,332 files/)).toBeInTheDocument();
  });

  it("isn't up to date while a computer still has deletions to make", async () => {
    // Syncthing counts deletions apart from files, and they take none of the bytes.
    computers = [progress({ completion: 95, needBytes: 0, needItems: 0, needDeletes: 4 })];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/Sending to Ada’s MacBook: 95% there ·/)).toBeInTheDocument();
    expect(screen.queryByText(/Up to date/)).not.toBeInTheDocument();
  });

  it("counts only what's on its way to computers that have taken the folder up", async () => {
    deviceIds = ["LAPTOP", "IMAC", "OLD"];
    computers = [
      progress({ completion: 50, needBytes: 2 * GB, needItems: 10 }),
      // Hasn't accepted it, so all 24.7 GB of it is "needed" there; none of that is on its way.
      progress({ deviceId: "IMAC", state: "notSharing", completion: 0, needBytes: 24.7 * GB, needItems: 2332 }),
      progress({ deviceId: "OLD", state: "paused", completion: 0, needBytes: 24.7 * GB, needItems: 2332 }),
    ];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(
      await screen.findByText(
        /Sending to 1 computer, 2 GB to go; 1 computer still to accept it; paused on 1 computer · 2,332 files/,
      ),
    ).toBeInTheDocument();
  });

  it("doesn't claim a computer it couldn't ask about is done", async () => {
    computers = [];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText(/Waiting to hear from Ada’s MacBook/)).toBeInTheDocument();
  });
});
