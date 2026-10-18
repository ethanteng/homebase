import { afterEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import Backups from "./Backups";
import type { BackupFolderListing, BackupList } from "./api";

/**
 * One's own files in the backups, in My files: which backup, what was in a folder then, and
 * putting something back. The host's answers are stood in for; what the page makes of them is not.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const two: BackupList = {
  configured: true,
  backups: [
    { id: "newest00", time: "2026-10-17T09:00:00Z" },
    { id: "older000", time: "2026-10-16T09:00:00Z" },
  ],
};

const top = (snapshot: string): BackupFolderListing => ({
  snapshot,
  time: snapshot === "newest00" ? "2026-10-17T09:00:00Z" : "2026-10-16T09:00:00Z",
  path: "",
  entries: [
    { name: "Photos", path: "Photos", isDirectory: true, size: null, modifiedAt: "2026-10-15T09:00:00Z" },
    { name: "notes.txt", path: "notes.txt", isDirectory: false, size: 2048, modifiedAt: "2026-10-15T09:00:00Z" },
  ],
});

const photos: BackupFolderListing = {
  snapshot: "newest00",
  time: "2026-10-17T09:00:00Z",
  path: "Photos",
  entries: [{ name: "beach.jpg", path: "Photos/beach.jpg", isDirectory: false, size: 300_000, modifiedAt: "2026-10-14T09:00:00Z" }],
};

type Route = (body: unknown) => Response;

function host(routes: Record<string, Route>) {
  const asked: { route: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", (url: string, init?: RequestInit) => {
    const route = `${init?.method ?? "GET"} ${url}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    asked.push({ route, body });
    const answer = routes[route];
    return Promise.resolve(answer ? answer(body) : json({ detail: "No." }, 404));
  });
  return asked;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function show(open = vi.fn(), onChanged = vi.fn()) {
  await act(async () => {
    render(<Backups open={open} onChanged={onChanged} />);
  });
  return { open, onChanged };
}

describe("backups in My files", () => {
  it("shows your files in the newest backup, and goes into a folder and back", async () => {
    host({
      "GET /api/backups": () => json(two),
      "GET /api/backups/newest00/files?path=": () => json(top("newest00")),
      "GET /api/backups/newest00/files?path=Photos": () => json(photos),
    });
    await show();

    expect(screen.getByText("notes.txt")).toBeTruthy();
    expect(screen.getByText(/Latest, /)).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Photos" }));
    });
    expect(screen.getByText("beach.jpg")).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "My files" }));
    });
    expect(screen.getByText("notes.txt")).toBeTruthy();
  });

  it("looks in an older backup when one is chosen", async () => {
    const asked = host({
      "GET /api/backups": () => json(two),
      "GET /api/backups/newest00/files?path=": () => json(top("newest00")),
      "GET /api/backups/older000/files?path=": () => json(top("older000")),
    });
    await show();

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Backup from"), { target: { value: "older000" } });
    });

    expect(asked.some((request) => request.route === "GET /api/backups/older000/files?path=")).toBe(true);
    expect(screen.getByText(/From the backup of Oct 16, 2026/)).toBeTruthy();
  });

  it("leaves nothing from one backup to restore while another is being read", async () => {
    host({
      "GET /api/backups": () => json(two),
      "GET /api/backups/newest00/files?path=": () => json(top("newest00")),
    });
    const fetch = globalThis.fetch;
    vi.stubGlobal("fetch", (url: string, init?: RequestInit) =>
      url.startsWith("/api/backups/older000") ? new Promise<Response>(() => {}) : fetch(url, init));
    await show();
    expect(screen.getByRole("button", { name: "Restore notes.txt" })).toBeTruthy();

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Backup from"), { target: { value: "older000" } });
    });

    expect(screen.queryByRole("button", { name: "Restore notes.txt" })).toBeNull();
    expect(screen.getByText("Opening the backup…")).toBeTruthy();
  });

  it("puts something back, saying where and under what name", async () => {
    const asked = host({
      "GET /api/backups": () => json(two),
      "GET /api/backups/newest00/files?path=": () => json(top("newest00")),
      "POST /api/backups/newest00/restore": () => json({ items: [{ from: "notes.txt", to: "notes 2.txt" }] }),
    });
    const { open, onChanged } = await show();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Restore notes.txt" }));
    });

    expect(asked.find((request) => request.route === "POST /api/backups/newest00/restore")?.body).toEqual({ paths: ["notes.txt"] });
    expect(screen.getByRole("status").textContent).toMatch(
      /Put “notes\.txt” back in My files\. It’s called “notes 2\.txt”, because “notes\.txt” is still there\./,
    );
    expect(onChanged).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Open My files" }));
    expect(open).toHaveBeenCalledWith("");
  });

  it("says why something couldn't be restored", async () => {
    host({
      "GET /api/backups": () => json(two),
      "GET /api/backups/newest00/files?path=": () => json(top("newest00")),
      "POST /api/backups/newest00/restore": () =>
        json({ detail: "This would add 2 KB and only 1 KB is free on your Uncloud drive. Make some room, or restore less at once." }, 409),
    });
    await show();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Restore notes.txt" }));
    });

    expect(screen.getByRole("alert").textContent).toMatch(/restore less at once/);
  });

  it("says when there are no backups to look in, and why", async () => {
    host({ "GET /api/backups": () => json({ configured: false, backups: [] }) });
    await show();
    expect(screen.getByText("Backups aren’t set up")).toBeTruthy();
  });

  it("says when the first backup hasn't been made yet", async () => {
    host({ "GET /api/backups": () => json({ configured: true, backups: [] }) });
    await show();
    expect(screen.getByText("No backups yet")).toBeTruthy();
  });
});
