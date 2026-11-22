import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import FileBrowser from "./FileBrowser";
import AddFiles from "./AddFiles";

/**
 * Folder sizes appear by themselves, in My files and in a place files come from. Nobody has to
 * press anything: the listing comes first, then each folder's size as it is measured.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

let asked: string[] = [];
let answer: (path: string) => Response | Promise<Response>;

beforeEach(() => {
  asked = [];
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const path = String(input).replace(/^.*\/api/, "");
    asked.push(path);
    return Promise.resolve(answer(path));
  });
});

afterEach(() => vi.unstubAllGlobals());

const entry = (path: string, isDirectory: boolean, size: number | null) => ({
  name: path.split("/").at(-1)!,
  path,
  isDirectory,
  size,
  modifiedAt: "2026-01-01T00:00:00Z",
});

describe("folder sizes", () => {
  it("fill in beside each folder in My files, with nothing to click", async () => {
    let finishPhotos: (response: Response) => void = () => {};
    answer = (path) => {
      if (path.startsWith("/files/size")) {
        const folder = new URLSearchParams(path.split("?")[1]).get("path");
        if (folder === "Photos")
          return new Promise<Response>((resolve) => {
            finishPhotos = resolve;
          });
        return folder === "Docs" ? json({ bytes: 2048, files: 3 }) : json({ detail: "No." }, 409);
      }
      return json({
        path: "",
        entries: [entry("Docs", true, null), entry("Photos", true, null), entry("Stuck", true, null), entry("a.txt", false, 10)],
        skippedCount: 0,
        indexedAt: "",
      });
    };

    render(
      <FileBrowser
        rootPath="/u"
        path=""
        revision={0}
        navigate={() => {}}
        onAdd={() => {}}
        onChanged={() => {}}
        onDropFiles={async () => {}}
      />,
    );
    const row = async (name: string) => (await screen.findByText(name)).closest("tr")!;

    await waitFor(async () => expect(await row("Docs")).toHaveTextContent("2 KB"));
    // Still being measured reads as such, and one that couldn't be measured has no size to show.
    expect(within(await row("Photos")).getByTitle("Measuring…")).toBeInTheDocument();
    await waitFor(async () => expect(await row("Stuck")).toHaveTextContent("—"));
    finishPhotos(json({ bytes: 5 * 1024 * 1024, files: 40 }));
    await waitFor(async () => expect(await row("Photos")).toHaveTextContent("5 MB"));
    expect(asked.filter((path) => path.startsWith("/files/size"))).toHaveLength(3);
  });

  it("fill in beside each folder in Dropbox, with how many files each holds", async () => {
    answer = dropbox();
    renderAddFiles();

    await screen.findByText("3 MB · 1,204 files");
    expect(screen.getByText("10 B")).toBeInTheDocument();
    expect(asked).toContain("/imports/sources/dropbox/size?path=%2Fphotos");
  });
});

describe("the strip above My files", () => {
  it("counts everything inside its folders, and says it's still counting until they're measured", async () => {
    const finish: Record<string, (response: Response) => void> = {};
    answer = (path) => {
      if (path.startsWith("/files/size")) {
        const folder = new URLSearchParams(path.split("?")[1]).get("path")!;
        if (folder === "Stuck") return json({ detail: "No." }, 409);
        return new Promise<Response>((resolve) => {
          finish[folder] = resolve;
        });
      }
      return listingOf([entry("Docs", true, null), entry("Photos", true, null), entry("Stuck", true, null)]);
    };
    const summary = renderBrowser();

    await waitFor(() => expect(summary()).toHaveTextContent("3 folders"));
    // Nothing is measured yet, so there's no total to show, and no 0 passed off as one.
    expect(summary()).toHaveTextContent("Counting files…");
    expect(summary()).toHaveTextContent("Measuring…");
    await waitFor(() => expect(summary()).toHaveTextContent("1 folder couldn’t be measured"));

    await waitFor(() => expect(finish.Docs).toBeDefined());
    await act(async () => finish.Docs(json({ bytes: 2048, files: 3 })));
    await waitFor(() => expect(summary()).toHaveTextContent("3 files so far"));
    expect(summary()).toHaveTextContent("2 KB in files so far");

    await waitFor(() => expect(finish.Photos).toBeDefined());
    await act(async () => finish.Photos(json({ bytes: 5 * 1024 * 1024, files: 1200 })));
    await waitFor(() => expect(summary()).toHaveTextContent("1,203 files"));
    expect(summary()).toHaveTextContent("5 MB in files");
    expect(summary()).not.toHaveTextContent("so far");
  });

  it("shows no total, not a 0, when none of its folders could be measured", async () => {
    answer = (path) =>
      path.startsWith("/files/size")
        ? json({ detail: "No." }, 409)
        : listingOf([entry("Docs", true, null), entry("Photos", true, null)]);
    const summary = renderBrowser();

    await waitFor(() => expect(summary()).toHaveTextContent("2 folders couldn’t be measured"));
    expect(summary()).not.toHaveTextContent("0 files");
    expect(summary()).not.toHaveTextContent("0 B");
    expect(summary()).not.toHaveTextContent("Counting files…");
  });

  it("counts the files sitting in a folder with no folders in it", async () => {
    answer = () => listingOf([entry("a.txt", false, 10), entry("b.txt", false, 30)]);
    const summary = renderBrowser();

    await waitFor(() => expect(summary()).toHaveTextContent("0 folders"));
    expect(summary()).toHaveTextContent("2 files");
    expect(summary()).toHaveTextContent("40 B in files");
    expect(summary()).not.toHaveTextContent("so far");
  });
});

function listingOf(entries: ReturnType<typeof entry>[]) {
  return json({ path: "", entries, skippedCount: 0, indexedAt: "" });
}

/** My files at its top, and a way to read the strip above the list. */
function renderBrowser() {
  const { container } = render(
    <FileBrowser
      rootPath="/u"
      path=""
      revision={0}
      navigate={() => {}}
      onAdd={() => {}}
      onChanged={() => {}}
      onDropFiles={async () => {}}
    />,
  );
  return () => container.querySelector(".library-summary") as HTMLElement;
}

describe("everything in a folder", () => {
  it("shows what the whole of Dropbox holds, and adds all of it at once", async () => {
    const posted: unknown[] = [];
    const listing = dropbox();
    answer = (path) => (path === "/imports" ? json({ job: { id: "j", running: true } }) : listing(path));
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).replace(/^.*\/api/, "");
      asked.push(path);
      if (init?.method === "POST") posted.push(JSON.parse(String(init.body)));
      return Promise.resolve(answer(path));
    });
    const started = vi.fn();
    renderAddFiles(started);

    const whole = (await screen.findByText("Everything in Dropbox")).closest(".add-whole") as HTMLElement;
    await waitFor(() => expect(whole).toHaveTextContent("5 MB · 1,205 files"));
    // The top of the account is measured with the folders in it, and first.
    expect(asked.filter((path) => path.includes("/size"))[0]).toBe("/imports/sources/dropbox/size?path=");

    await click(within(whole).getByRole("button", { name: "Add all" }));
    await waitFor(() => expect(started).toHaveBeenCalled());
    expect(posted).toEqual([{ source: "dropbox", remotePaths: ["/"], label: "Dropbox" }]);
  });

  it("shows the total of whichever folder is open, and adds that folder whole", async () => {
    answer = dropbox();
    renderAddFiles();

    await click(await screen.findByText("Photos"));

    const whole = (await screen.findByText("Everything in Photos")).closest(".add-whole") as HTMLElement;
    await waitFor(() => expect(whole).toHaveTextContent("3 MB · 1,204 files"));
    expect(asked).toContain("/imports/sources/dropbox/files?path=%2Fphotos");
  });
});

describe("choosing some of a folder", () => {
  it("keeps a running total of what's ticked, and adds just those together", async () => {
    let finishDocs: (response: Response) => void = () => {};
    const posted: unknown[] = [];
    const listing = dropbox();
    answer = (path) => {
      if (path === "/imports") return json({ job: { id: "j", running: true } });
      if (path === "/imports/sources/dropbox/size?path=%2Fdocs")
        return new Promise<Response>((resolve) => {
          finishDocs = resolve;
        });
      if (path.startsWith("/imports/sources/dropbox/files?path="))
        return path.endsWith("path=")
          ? json([
              folderEntry("Photos"),
              folderEntry("Docs"),
              folderEntry("Music"),
              { id: "n", name: "notes.txt", path: "/notes.txt", displayPath: "/notes.txt", isFolder: false, size: 10, rev: "a", modified: null },
            ])
          : listing(path);
      return listing(path);
    };
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).replace(/^.*\/api/, "");
      asked.push(path);
      if (init?.method === "POST") posted.push(JSON.parse(String(init.body)));
      return Promise.resolve(answer(path));
    });
    const started = vi.fn();
    renderAddFiles(started);

    const whole = (await screen.findByText("Everything in Dropbox")).closest(".add-whole") as HTMLElement;
    await click(screen.getByLabelText("Select Photos"));
    await click(screen.getByLabelText("Select Docs"));

    // Photos is measured; Docs isn't yet, so the total says it isn't finished.
    expect(within(whole).getByText("Photos and Docs")).toBeInTheDocument();
    await waitFor(() => expect(whole).toHaveTextContent("2 selected · 3 MB · 1,204 files so far · measuring…"));
    await act(async () => finishDocs(json({ bytes: 2 * 1024 * 1024, files: 6 })));
    await waitFor(() => expect(whole).toHaveTextContent("2 selected · 5 MB · 1,210 files"));

    await click(within(whole).getByRole("button", { name: "Add selected" }));
    await waitFor(() => expect(started).toHaveBeenCalled());
    expect(posted).toEqual([{ source: "dropbox", remotePaths: ["/photos", "/docs"], label: "Photos and Docs" }]);
  });

  it("reads as the whole folder once everything in it is ticked", async () => {
    answer = dropbox();
    renderAddFiles();

    await click(await screen.findByLabelText("Select everything in Dropbox"));

    expect(screen.getByLabelText("Select Photos")).toBeChecked();
    expect(screen.getByLabelText("Select notes.txt")).toBeChecked();
    expect(screen.getByText("Everything in Dropbox")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add all" })).toBeInTheDocument();

    await click(screen.getByLabelText("Select notes.txt"));
    const whole = screen.getByLabelText("Select everything in Dropbox").closest(".add-whole") as HTMLElement;
    expect(within(whole).getByText("Photos")).toBeInTheDocument();
    expect(whole).toHaveTextContent("1 selected · 3 MB · 1,204 files");
    expect(screen.getByRole("button", { name: "Add selected" })).toBeInTheDocument();
    await click(screen.getByRole("button", { name: "Clear selection" }));
    expect(screen.getByLabelText("Select Photos")).not.toBeChecked();
    expect(screen.getByText("Everything in Dropbox")).toBeInTheDocument();
  });
});

function folderEntry(name: string) {
  const path = `/${name.toLowerCase()}`;
  return { id: path, name, path, displayPath: `/${name}`, isFolder: true, size: null, rev: null, modified: null };
}

/** A signed-in Dropbox holding a Photos folder and a note, and sizes for both it and the folder. */
function dropbox() {
  return (path: string) => {
    if (path.startsWith("/imports/sources/dropbox/size"))
      return new URLSearchParams(path.split("?")[1]).get("path") === ""
        ? json({ bytes: 5 * 1024 * 1024, files: 1205 })
        : json({ bytes: 3 * 1024 * 1024, files: 1204 });
    if (path.startsWith("/imports/sources/dropbox/files?path=%2Fphotos"))
      return json([
        { id: "3", name: "beach.jpg", path: "/photos/beach.jpg", displayPath: "/Photos/beach.jpg", isFolder: false, size: 2048, rev: "b", modified: null },
      ]);
    if (path.startsWith("/imports/sources/dropbox/files"))
      return json([
        { id: "1", name: "Photos", path: "/photos", displayPath: "/Photos", isFolder: true, size: null, rev: null, modified: null },
        { id: "2", name: "notes.txt", path: "/notes.txt", displayPath: "/notes.txt", isFolder: false, size: 10, rev: "a", modified: null },
      ]);
    if (path.startsWith("/imports/sources"))
      return json({
        accounts: [
          { id: "dropbox", name: "Dropbox", configured: true, connected: true, accountName: "Ethan", oneClickHere: true, destination: "Dropbox" },
        ],
        places: [],
        suggestions: [],
        canAddFolders: false,
        canPickFolder: false,
      });
    return json({});
  };
}

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

function renderAddFiles(onStarted: () => void = () => {}) {
  render(
    <AddFiles
      importing={false}
      uploading={false}
      isAdmin={false}
      folder=""
      storage={null}
      arrivedFromDropbox="connected"
      onStarted={onStarted}
      onUpload={async () => {}}
      onUploadEvernote={async () => {}}
      onUploadExport={async () => {}}
      onSetUpDropbox={() => {}}
    />,
  );
}
