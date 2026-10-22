import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import FileBrowser from "./FileBrowser";

/**
 * Copying, moving and deleting from My files: ticking files, choosing where they go, and being
 * told what happened. What the server answers is stood in for; what the page sends it, and what it
 * does with the answer, is not.
 */

const entry = (path: string, isDirectory = false) => ({
  name: path.split("/").at(-1)!,
  path,
  isDirectory,
  size: isDirectory ? null : 10,
  modifiedAt: "2026-01-01T00:00:00Z",
});

const FOLDERS: Record<string, ReturnType<typeof entry>[]> = {
  "": [entry("Docs", true), entry("a.txt"), entry("b.txt")],
  Docs: [entry("Docs/Old", true), entry("Docs/New folder", true)],
  "Docs/Old": [],
};

let sent: { path: string; body: unknown }[] = [];
let answer: (path: string) => Response;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

beforeEach(() => {
  sent = [];
  answer = () => json({ items: [] });
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    if (init?.method === "POST") {
      sent.push({ path, body: JSON.parse(String(init.body)) });
      return Promise.resolve(answer(path));
    }
    const folder = new URLSearchParams(path.split("?")[1]).get("path") ?? "";
    return Promise.resolve(
      json({ path: folder, entries: FOLDERS[folder] ?? [], skippedCount: 0, indexedAt: "" }),
    );
  });
});

afterEach(() => vi.unstubAllGlobals());

async function open(onChanged = vi.fn(), path = "") {
  render(
    <FileBrowser
      rootPath="/Users/ethan/Uncloud"
      path={path}
      revision={0}
      navigate={() => {}}
      onAdd={() => {}}
      onChanged={onChanged}
      onDropFiles={async () => {}}
    />,
  );
  await screen.findByText(path ? "Old" : "a.txt");
  return onChanged;
}

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

describe("editing My files", () => {
  it("moves the ticked files into the folder chosen for them", async () => {
    const onChanged = await open();
    await click(screen.getByRole("checkbox", { name: "Select a.txt" }));
    await click(screen.getByRole("checkbox", { name: "Select b.txt" }));
    expect(screen.getByText("2 selected")).toBeInTheDocument();

    await click(screen.getByRole("button", { name: /move to/i }));
    const dialog = screen.getByRole("dialog");
    // They are already in the folder the dialog starts in, so moving them there means nothing.
    expect(within(dialog).getByRole("button", { name: "Move here" })).toBeDisabled();
    await click(await within(dialog).findByRole("button", { name: /Docs/ }));
    await within(dialog).findByRole("button", { name: /Old/ });
    await click(within(dialog).getByRole("button", { name: "Move here" }));

    expect(sent).toEqual([
      { path: "/files/move", body: { paths: ["a.txt", "b.txt"], destination: "Docs" } },
    ]);
    await screen.findByText("Moved 2 items to Docs.");
    expect(onChanged).toHaveBeenCalled();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("won't open a folder that is itself being moved", async () => {
    await open();
    await click(screen.getByRole("checkbox", { name: "Select Docs" }));
    await click(screen.getByRole("button", { name: /move to/i }));

    expect(await within(screen.getByRole("dialog")).findByRole("button", { name: /Docs/ })).toBeDisabled();
  });

  it("asks before sending things to the bin, and says where they went", async () => {
    await open();
    await click(screen.getByRole("checkbox", { name: "Select Docs" }));
    await click(screen.getByRole("button", { name: /delete/i }));

    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent("“Docs” and everything in it will go to the bin");
    expect(dialog).toHaveTextContent("put it back from there for 30 days");
    expect(sent).toEqual([]);
    await click(within(dialog).getByRole("button", { name: "Delete" }));

    expect(sent).toEqual([{ path: "/files/delete", body: { paths: ["Docs"] } }]);
    await screen.findByText("Moved “Docs” to the bin.");
  });

  it("says so when a copy arrives under a new name", async () => {
    answer = () => json({ items: [{ from: "a.txt", to: "a copy.txt" }] });
    await open();
    await click(screen.getByRole("checkbox", { name: "Select a.txt" }));
    await click(screen.getByRole("button", { name: /copy to/i }));
    const dialog = screen.getByRole("dialog");
    await within(dialog).findByRole("button", { name: /Docs/ });
    await click(within(dialog).getByRole("button", { name: "Copy here" }));

    expect(sent).toEqual([{ path: "/files/copy", body: { paths: ["a.txt"], destination: "" } }]);
    await screen.findByText(/It’s called “a copy.txt” there, because “a.txt” was taken/);
  });

  it("renames one thing at a time, with the name ready to type over", async () => {
    answer = () => json({ from: "a.txt", to: "Notes.txt" });
    await open();
    await click(screen.getByRole("checkbox", { name: "Select a.txt" }));
    await click(screen.getByRole("checkbox", { name: "Select b.txt" }));
    // There is no one new name for two things.
    expect(screen.queryByRole("button", { name: /rename/i })).toBeNull();
    await click(screen.getByRole("checkbox", { name: "Select b.txt" }));

    await click(screen.getByRole("button", { name: /rename/i }));
    const dialog = screen.getByRole("dialog");
    const field = within(dialog).getByRole("textbox", { name: "New name" }) as HTMLInputElement;
    const rename = within(dialog).getByRole("button", { name: "Rename" });
    // Everything but the extension is selected, and nothing changed is nothing to send.
    expect(field.value).toBe("a.txt");
    expect([field.selectionStart, field.selectionEnd]).toEqual([0, 1]);
    expect(rename).toBeDisabled();
    fireEvent.change(field, { target: { value: "  " } });
    expect(rename).toBeDisabled();

    fireEvent.change(field, { target: { value: "Notes.txt" } });
    await act(async () => {
      fireEvent.keyDown(field, { key: "Enter" });
    });

    expect(sent).toEqual([{ path: "/files/rename", body: { path: "a.txt", name: "Notes.txt" } }]);
    await screen.findByText("Renamed “a.txt” to “Notes.txt”.");
  });

  it("keeps the dialog open with the reason when a change is refused", async () => {
    answer = () =>
      json({ detail: "“Docs” syncs with your computers, so it can’t be deleted here." }, 409);
    const onChanged = await open();
    await click(screen.getByRole("checkbox", { name: "Select Docs" }));
    await click(screen.getByRole("button", { name: /delete/i }));
    await click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));

    await waitFor(() =>
      expect(within(screen.getByRole("dialog")).getByRole("alert")).toHaveTextContent(/syncs with your computers/),
    );
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("acts only on what the filter leaves showing", async () => {
    await open();
    await click(screen.getByRole("checkbox", { name: "Select everything shown" }));
    expect(screen.getByText("3 selected")).toBeInTheDocument();

    fireEvent.change(screen.getByRole("searchbox", { name: "Filter this folder" }), {
      target: { value: "a." },
    });
    expect(screen.getByText("1 selected")).toBeInTheDocument();
    await click(screen.getByRole("button", { name: /delete/i }));
    await click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));

    expect(sent).toEqual([{ path: "/files/delete", body: { paths: ["a.txt"] } }]);
  });

  it("makes a new folder in the one open, starting from a name nothing there has", async () => {
    answer = () => json({ path: "Docs/Receipts" });
    const onChanged = await open(vi.fn(), "Docs");

    await click(screen.getByRole("button", { name: "New folder" }));
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveTextContent("New folder in Docs");
    const field = within(dialog).getByRole("textbox", { name: "Name" }) as HTMLInputElement;
    const make = within(dialog).getByRole("button", { name: "Make folder" });
    // “New folder” is taken here, so it starts as the next one, ready to type over.
    expect(field.value).toBe("New folder 2");
    expect([field.selectionStart, field.selectionEnd]).toEqual([0, 12]);
    fireEvent.change(field, { target: { value: " " } });
    expect(make).toBeDisabled();

    fireEvent.change(field, { target: { value: "Receipts" } });
    await click(make);

    expect(sent).toEqual([{ path: "/files/folder", body: { path: "Docs", name: "Receipts" } }]);
    await screen.findByText("Made a new folder, “Receipts”.");
    expect(onChanged).toHaveBeenCalled();
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

/** What a drag in the list carries, as much of it as the page touches. */
function dragging(altKey = false) {
  return {
    altKey,
    dataTransfer: {
      types: ["application/x-uncloud-paths"],
      items: [],
      files: [],
      dropEffect: "none",
      effectAllowed: "all",
      setData: vi.fn(),
      setDragImage: vi.fn(),
    },
  };
}

describe("dragging within My files", () => {
  // Without one, an event can't say a key was held.
  beforeEach(() => vi.stubGlobal("DragEvent", MouseEvent));

  const row = (name: string) => screen.getByText(name).closest("tr")!;

  it("moves what's ticked into the folder it's dropped on, and copies with Option held", async () => {
    const onChanged = await open();
    const panel = screen.getByRole("region", { name: "File browser" });
    answer = () => json({ items: [{ from: "a.txt", to: "Docs/a.txt" }, { from: "b.txt", to: "Docs/b.txt" }] });
    await click(screen.getByRole("checkbox", { name: "Select a.txt" }));
    await click(screen.getByRole("checkbox", { name: "Select b.txt" }));

    // A ticked row brings the rest of what's ticked along.
    fireEvent.dragStart(row("a.txt"), dragging());
    expect(screen.getByText("Drop on a folder to move them there")).toBeInTheDocument();
    fireEvent.dragEnter(panel, dragging());
    fireEvent.dragOver(within(row("Docs")).getByText("Docs"), dragging());
    expect(row("Docs")).toHaveClass("drop-target");
    expect(screen.getByText("Move 2 items to Docs")).toBeInTheDocument();
    // A file is nowhere to put them.
    fireEvent.dragOver(within(row("b.txt")).getByText("b.txt"), dragging());
    expect(row("Docs")).not.toHaveClass("drop-target");
    fireEvent.dragOver(within(row("Docs")).getByText("Docs"), dragging());
    await act(async () => {
      fireEvent.drop(within(row("Docs")).getByText("Docs"), dragging());
    });

    expect(sent).toEqual([{ path: "/files/move", body: { paths: ["a.txt", "b.txt"], destination: "Docs" } }]);
    await screen.findByText("Moved 2 items to Docs.");
    expect(onChanged).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Drop on a folder/)).toBeNull();

    // One that isn't ticked comes alone, and with Option held it's copied instead.
    sent = [];
    answer = () => json({ items: [{ from: "b.txt", to: "Docs/b.txt" }] });
    fireEvent.dragStart(row("b.txt"), dragging(true));
    fireEvent.dragOver(within(row("Docs")).getByText("Docs"), dragging(true));
    expect(screen.getByText("Copy “b.txt” to Docs")).toBeInTheDocument();
    await act(async () => {
      fireEvent.drop(within(row("Docs")).getByText("Docs"), dragging(true));
    });
    expect(sent).toEqual([{ path: "/files/copy", body: { paths: ["b.txt"], destination: "Docs" } }]);
    await screen.findByText("Copied “b.txt” to Docs.");
  });

  it("puts things in a folder above when they're dropped on its place in the path, never into themselves", async () => {
    await open(vi.fn(), "Docs");
    answer = () => json({ items: [{ from: "Docs/Old", to: "Old" }] });

    fireEvent.dragStart(row("Old"), dragging());
    // A folder can't go into itself, nor into the folder it's already in.
    for (const target of [within(row("Old")).getByText("Old"), screen.getByRole("button", { name: "Docs" })]) {
      const over = dragging();
      fireEvent.dragOver(target, over);
      expect(screen.queryByText(/^Move/)).toBeNull();
      await act(async () => {
        fireEvent.drop(target, over);
      });
    }
    expect(sent).toEqual([]);

    fireEvent.dragStart(row("Old"), dragging());
    const home = screen.getByRole("button", { name: "My files" });
    fireEvent.dragOver(home, dragging());
    expect(home).toHaveClass("drop-target");
    expect(screen.getByText("Move “Old” to My files")).toBeInTheDocument();
    await act(async () => {
      fireEvent.drop(home, dragging());
    });

    expect(sent).toEqual([{ path: "/files/move", body: { paths: ["Docs/Old"], destination: "" } }]);
    await screen.findByText("Moved “Old” to My files.");
  });

  it("says why when what was dropped couldn't go there", async () => {
    const onChanged = await open();
    answer = () => json({ detail: "“a.txt” is no longer here. Refresh and try again." }, 404);

    fireEvent.dragStart(row("a.txt"), dragging());
    fireEvent.dragOver(within(row("Docs")).getByText("Docs"), dragging());
    await act(async () => {
      fireEvent.drop(within(row("Docs")).getByText("Docs"), dragging());
    });

    expect(await screen.findByRole("alert")).toHaveTextContent("is no longer here");
    expect(onChanged).not.toHaveBeenCalled();
  });
});
