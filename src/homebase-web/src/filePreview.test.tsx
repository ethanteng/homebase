import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import FileBrowser from "./FileBrowser";
import { Viewer } from "./Preview";

/**
 * Looking at your own files without downloading them: a picture in the file's details, and the
 * viewer over the list, which steps through the folder's files in the order they're listed.
 */

const entry = (path: string, preview: string | null, isDirectory = false) => ({
  name: path.split("/").at(-1)!,
  path,
  isDirectory,
  size: isDirectory ? null : 2048,
  modifiedAt: "2026-01-01T00:00:00Z",
  preview,
});

const PHOTOS = [
  entry("Photos/2008", null, true),
  entry("Photos/beach.jpg", "image"),
  entry("Photos/notes.txt", "text"),
  entry("Photos/page.html", null),
];

beforeEach(() => {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const path = String(input).replace(/^.*\/api/, "");
    if (path.startsWith("/files/file")) return Promise.resolve(new Response("<b>not bold</b>"));
    if (path.startsWith("/files/size")) return Promise.resolve(json({ bytes: 0, files: 0 }));
    if (path.startsWith("/shares")) return Promise.resolve(json({ reachable: false, links: [] }));
    return Promise.resolve(json({ path: "Photos", entries: PHOTOS, skippedCount: 0, indexedAt: "" }));
  });
});

afterEach(() => vi.unstubAllGlobals());

const json = (body: unknown) =>
  new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });

async function open() {
  render(
    <FileBrowser
      rootPath="/Users/ethan/Uncloud"
      path="Photos"
      revision={0}
      navigate={() => {}}
      onAdd={() => {}}
      onChanged={() => {}}
      onDropFiles={async () => {}}
    />,
  );
  await screen.findByText("beach.jpg");
}

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

describe("previewing My files", () => {
  it("shows a picture in its details, and opens it in the viewer from there", async () => {
    await open();
    await click(screen.getByRole("button", { name: "Details for beach.jpg" }));
    const details = screen.getByRole("complementary", { name: "File details" });
    const picture = within(details).getByRole("button", { name: "Preview beach.jpg" });
    expect(picture.querySelector("img")).toHaveAttribute("src", "/api/files/file?path=Photos%2Fbeach.jpg");

    await click(picture);
    const viewer = screen.getByRole("dialog");
    expect(viewer).toHaveAttribute("open");
    expect(within(viewer).getByRole("img", { name: "beach.jpg" })).toHaveAttribute(
      "src",
      "/api/files/file?path=Photos%2Fbeach.jpg",
    );
    expect(within(viewer).getByRole("link", { name: "Download beach.jpg" })).toHaveAttribute(
      "href",
      "/api/files/download?path=Photos%2Fbeach.jpg",
    );
    // Files only, in the order listed: the folder isn't one of them.
    expect(viewer).toHaveTextContent("1 of 3");
  });

  it("steps through the folder, and leaves the last file's details open when it closes", async () => {
    await open();
    await click(screen.getByRole("button", { name: "Details for beach.jpg" }));
    await click(screen.getByRole("button", { name: /^Preview$/ }));
    const viewer = screen.getByRole("dialog");

    await act(async () => {
      fireEvent.keyDown(window, { key: "ArrowRight" });
    });
    // Text is put on the page as text, whatever it holds.
    await within(viewer).findByText("<b>not bold</b>");

    await click(within(viewer).getByRole("button", { name: "Next" }));
    expect(viewer).toHaveTextContent("This kind of file can’t be shown in a browser.");
    expect(within(viewer).getAllByRole("link", { name: /Download/ }).length).toBeGreaterThan(0);

    await click(within(viewer).getByRole("button", { name: "Close" }));
    expect(viewer).not.toHaveAttribute("open");
    const details = screen.getByRole("complementary", { name: "File details" });
    expect(within(details).getByRole("heading", { name: "page.html" })).toBeInTheDocument();
    // Nothing to preview for a page: only the download.
    expect(within(details).queryByRole("button", { name: /^Preview/ })).toBeNull();
  });

  it("opens the file in the details even when a filter has hidden it", async () => {
    await open();
    await click(screen.getByRole("button", { name: "Details for beach.jpg" }));
    await act(async () => {
      fireEvent.change(screen.getByRole("searchbox", { name: "Filter this folder" }), {
        target: { value: "notes" },
      });
    });
    expect(screen.queryByRole("button", { name: "Details for beach.jpg" })).toBeNull();

    await click(screen.getByRole("button", { name: /^Preview$/ }));
    const viewer = screen.getByRole("dialog");
    expect(viewer).toHaveAttribute("open");
    expect(within(viewer).getByRole("img", { name: "beach.jpg" })).toBeInTheDocument();
    // Just that file: the filter has hidden it, so it isn't one of a run through the folder.
    expect(within(viewer).queryByRole("button", { name: "Next" })).toBeNull();
  });

  it("lets a video keep the arrow keys, and still steps from anywhere else", async () => {
    const files = [
      { name: "clip.mp4", path: "clip.mp4", isDirectory: false, size: 10, preview: "video" as const },
      { name: "next.mp4", path: "next.mp4", isDirectory: false, size: 10, preview: "video" as const },
    ];
    const onIndex = vi.fn();
    render(
      <Viewer
        files={files}
        index={0}
        onIndex={onIndex}
        src={(entry) => `/api/files/file?path=${entry.path}`}
        download={() => "/api/files/download?path=clip.mp4"}
      />,
    );
    const video = document.querySelector("video");
    expect(video).not.toBeNull();
    await act(async () => {
      fireEvent.keyDown(video!, { key: "ArrowRight" });
    });
    expect(onIndex).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.keyDown(window, { key: "ArrowRight" });
    });
    expect(onIndex).toHaveBeenCalledWith(1);
  });

  it("falls back to an icon when the browser can't draw the picture", async () => {
    await open();
    await click(screen.getByRole("button", { name: "Details for beach.jpg" }));
    const details = screen.getByRole("complementary", { name: "File details" });
    const image = within(details).getByRole("button", { name: "Preview beach.jpg" }).querySelector("img")!;
    await act(async () => {
      fireEvent.error(image);
    });
    expect(within(details).queryByRole("button", { name: "Preview beach.jpg" })).toBeNull();
    // It can still be opened in the viewer, which says the same thing in more room.
    expect(within(details).getByRole("button", { name: /^Preview$/ })).toBeInTheDocument();
  });
});
