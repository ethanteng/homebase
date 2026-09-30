import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import Bin from "./Bin";

/**
 * The bin: putting things back where they came from, and letting go of them for good only when
 * somebody has said yes. What the server answers is stood in for; what the page sends it, and
 * what it does with the answer, is not.
 */

const DAY = 86_400_000;
const entry = (id: string, path: string, isDirectory = false) => ({
  id,
  name: path.split("/").at(-1)!,
  path,
  isDirectory,
  size: 2048,
  deletedAt: new Date(Date.now() - 2 * DAY).toISOString(),
  expiresAt: new Date(Date.now() + 28 * DAY).toISOString(),
});

let entries = [entry("a".repeat(32), "Documents/notes.txt"), entry("b".repeat(32), "Photos", true)];
let sent: { path: string; body: unknown }[] = [];
let restoredTo = "Documents/notes.txt";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

beforeEach(() => {
  entries = [entry("a".repeat(32), "Documents/notes.txt"), entry("b".repeat(32), "Photos", true)];
  sent = [];
  restoredTo = "Documents/notes.txt";
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    if (init?.method === "POST") {
      const body = init.body ? JSON.parse(String(init.body)) : null;
      sent.push({ path, body });
      if (path === "/bin/restore") {
        entries = entries.filter((item) => !body.ids.includes(item.id));
        return Promise.resolve(json({ items: [{ from: "Documents/notes.txt", to: restoredTo }] }));
      }
      entries = path === "/bin/empty" ? [] : entries.filter((item) => !body.ids.includes(item.id));
      return Promise.resolve(json({ deleted: 1 }));
    }
    return Promise.resolve(
      json({ entries, bytes: entries.reduce((sum, item) => sum + item.size, 0) }),
    );
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function show(open = vi.fn(), onChanged = vi.fn()) {
  render(<Bin open={open} onChanged={onChanged} />);
  await screen.findByText("notes.txt");
  return { open, onChanged };
}

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

describe("the bin", () => {
  it("lists what was deleted, where it came from and how long it has left", async () => {
    await show();

    expect(screen.getByText("From Documents")).toBeInTheDocument();
    expect(screen.getByText("From My files")).toBeInTheDocument();
    expect(screen.getAllByText("28 days left")).toHaveLength(2);
    expect(screen.getByText("2 items · 4 KB")).toBeInTheDocument();
  });

  it("puts a thing back and offers to go to it", async () => {
    const { open, onChanged } = await show();

    await click(screen.getByRole("button", { name: "Put back notes.txt" }));

    expect(sent).toEqual([{ path: "/bin/restore", body: { ids: ["a".repeat(32)] } }]);
    await screen.findByText("Put “notes.txt” back in Documents.");
    expect(screen.queryByText("notes.txt")).toBeNull();
    expect(onChanged).toHaveBeenCalled();
    await click(screen.getByRole("button", { name: "Open Documents" }));
    expect(open).toHaveBeenCalledWith("Documents");
  });

  it("says so when what came back had to take a new name", async () => {
    restoredTo = "Documents/notes 2.txt";
    await show();

    await click(screen.getByRole("button", { name: "Put back notes.txt" }));

    await screen.findByText(/It’s called “notes 2.txt”, because “notes.txt” was taken there/);
  });

  it("deletes for good only once somebody has said yes", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    await show();

    await click(screen.getByRole("button", { name: "Delete forever notes.txt" }));
    await click(screen.getByRole("button", { name: /empty bin/i }));
    expect(sent).toEqual([]);

    confirm.mockReturnValue(true);
    await click(screen.getByRole("button", { name: "Delete forever notes.txt" }));
    await screen.findByText("Deleted “notes.txt” for good.");
    await click(screen.getByRole("button", { name: /empty bin/i }));
    await screen.findByText("The bin is empty");

    expect(sent).toEqual([
      { path: "/bin/delete", body: { ids: ["a".repeat(32)] } },
      { path: "/bin/empty", body: null },
    ]);
    expect(screen.getByRole("button", { name: /empty bin/i })).toBeDisabled();
  });
});
