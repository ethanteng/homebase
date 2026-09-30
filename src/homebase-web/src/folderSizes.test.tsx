import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
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
      <FileBrowser rootPath="/u" path="" revision={0} navigate={() => {}} onAdd={() => {}} onChanged={() => {}} />,
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
    answer = (path) => {
      if (path.startsWith("/imports/sources/dropbox/size"))
        return json({ bytes: 3 * 1024 * 1024, files: 1204 });
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

    render(
      <AddFiles
        importing={false}
        isAdmin={false}
        storage={null}
        arrivedFromDropbox="connected"
        onStarted={() => {}}
        onSetUpDropbox={() => {}}
      />,
    );

    await screen.findByText("3 MB · 1,204 files");
    expect(screen.getByText("10 B")).toBeInTheDocument();
    expect(asked).toContain("/imports/sources/dropbox/size?path=%2Fphotos");
  });
});
