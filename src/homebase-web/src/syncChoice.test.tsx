import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import SyncPanel from "./SyncPanel";

/**
 * Choosing what a computer keeps in sync. A folder can't sync inside one that already does, so
 * the choice is offered only where it can work, and from the folders there are rather than a
 * name somebody has to type right.
 *
 * What the server answers is stood in for; what the page sends it is not.
 */

const ME = {
  id: "u1",
  username: "ada",
  displayName: "Ada",
  isAdmin: false,
  createdAt: "2026-01-01T00:00:00Z",
  disabledAt: null,
};
const LAPTOP = { deviceId: "LAPTOP", name: "Ada’s MacBook Air", connected: true };
const synced = (path: string) => ({
  id: path || "root",
  path,
  label: path || "Uncloud",
  deviceIds: ["LAPTOP"],
  state: "idle",
  error: null,
  files: 32720,
  bytes: 4096,
  waitingForRoom: 0,
});
const entry = (name: string, isDirectory = true) => ({
  name,
  path: name,
  isDirectory,
  size: null,
  modifiedAt: "2026-10-01T00:00:00Z",
});

let folders: ReturnType<typeof synced>[] = [];
let sent: { method: string; path: string; body: unknown }[] = [];

const json = (body: unknown) =>
  new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

beforeEach(() => {
  folders = [];
  sent = [];
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    const method = init?.method ?? "GET";
    sent.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : null });
    if (path.startsWith("/files"))
      return Promise.resolve(
        json({
          path: "",
          entries: [entry("Art"), entry("Documents"), entry("notes.txt", false)],
          skippedCount: 0,
          indexedAt: "2026-10-01T00:00:00Z",
        }),
      );
    if (path === "/sync/folders" && method === "POST") return Promise.resolve(json({}));
    return Promise.resolve(
      json({ available: true, detail: null, hostDeviceId: "HOST", devices: [LAPTOP], folders, offers: [] }),
    );
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("what stays in sync", () => {
  it("offers nothing more while all of your files already sync", async () => {
    folders = [synced("")];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    expect(await screen.findByText("All of your files")).toBeInTheDocument();
    expect(screen.getByText(/Up to date · 32,720 files/)).toBeInTheDocument();
    expect(screen.getByText("Kept the same here and on your computer.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /sync another folder|choose what to sync/i })).toBeNull();
    expect(screen.queryByRole("textbox")).toBeNull();
  });

  it("starts from all of your files when nothing syncs yet, or one of the folders", async () => {
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: "Choose what to sync" }));
    const choice = (await screen.findByLabelText("Folder to sync")) as HTMLSelectElement;
    expect(Array.from(choice.options, (option) => option.text)).toEqual([
      "All of your files",
      "Art",
      "Documents",
    ]);
    expect(choice.value).toBe("");

    fireEvent.change(choice, { target: { value: "Documents" } });
    fireEvent.click(screen.getByRole("button", { name: "Start syncing" }));

    await waitFor(() =>
      expect(sent).toContainEqual({
        method: "POST",
        path: "/sync/folders",
        body: { path: "Documents", deviceIds: null },
      }),
    );
  });

  it("leaves out what already syncs, and all of your files once any folder does", async () => {
    folders = [synced("Art")];
    render(<SyncPanel me={ME} onSignOut={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: "Sync another folder" }));
    const choice = (await screen.findByLabelText("Folder to sync")) as HTMLSelectElement;
    expect(Array.from(choice.options, (option) => option.text)).toEqual(["Documents"]);
    expect(choice.value).toBe("Documents");
  });
});
