import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import FileBrowser from "./FileBrowser";
import SharedPage from "./SharedPage";
import type { ShareLink } from "./api";

/**
 * Sharing by link, from both ends: the owner making a link in My files, changing what it allows
 * and turning it off; and whoever has the link opening it, seeing only what they're allowed to.
 * What the host answers is stood in for; what the page asks it, and what it shows, is not.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const entry = (path: string, isDirectory = false) => ({
  name: path.split("/").at(-1)!,
  path,
  isDirectory,
  size: isDirectory ? null : 10,
  modifiedAt: "2026-01-01T00:00:00Z",
});

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  window.location.hash = "";
});

describe("sharing from My files", () => {
  let links: ShareLink[];
  let reachable: boolean;
  let sent: { method: string; path: string; body: unknown }[];

  beforeEach(() => {
    links = [];
    reachable = true;
    sent = [];
    vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).replace(/^.*\/api/, "");
      const method = init?.method ?? "GET";
      if (method !== "GET") sent.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : null });
      if (path === "/shares" && method === "GET") return Promise.resolve(json({ reachable, links }));
      if (path === "/shares" && method === "POST") {
        const body = JSON.parse(String(init!.body)) as { paths: string[]; canDownload: boolean };
        const link: ShareLink = {
          id: "link-1",
          url: "https://uncloud.example.ts.net/s/abc",
          canDownload: body.canDownload,
          createdAt: "2026-11-01T00:00:00Z",
          items: body.paths.map((item) => ({ path: item, name: item, isDirectory: false, here: true })),
        };
        links = [link];
        return Promise.resolve(json(link));
      }
      if (path.startsWith("/shares/") && method === "PATCH") {
        const { canDownload } = JSON.parse(String(init!.body)) as { canDownload: boolean };
        links = links.map((link) => ({ ...link, canDownload }));
        return Promise.resolve(json(links[0]));
      }
      if (path.startsWith("/shares/") && method === "DELETE") {
        links = [];
        return Promise.resolve(json({ removed: true }));
      }
      if (path.startsWith("/files/delete")) return Promise.resolve(json({ deleted: 1 }));
      return Promise.resolve(
        json({ path: "", entries: [entry("Docs", true), entry("a.txt"), entry("b.txt")], skippedCount: 0, indexedAt: "" }),
      );
    });
  });

  async function open(isAdmin = true) {
    const onShowLinks = vi.fn();
    render(
      <FileBrowser
        rootPath="/Users/sam/Uncloud"
        path=""
        revision={0}
        navigate={() => {}}
        onAdd={() => {}}
        onChanged={() => {}}
        onDropFiles={async () => {}}
        isAdmin={isAdmin}
        onShowLinks={onShowLinks}
      />,
    );
    await screen.findByText("a.txt");
    return onShowLinks;
  }

  it("makes one link for what's ticked, only to look at unless asked, and copies it", async () => {
    const copied: string[] = [];
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: async (text: string) => void copied.push(text) } });
    await open();
    await click(screen.getByRole("checkbox", { name: "Select a.txt" }));
    await click(screen.getByRole("checkbox", { name: "Select b.txt" }));
    await click(screen.getByRole("button", { name: "Share…" }));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByRole("heading")).toHaveTextContent("Share 2 items");
    await within(dialog).findByRole("button", { name: /Create link/ });
    // Looking only is what a new link allows until somebody chooses otherwise.
    expect(within(dialog).getByRole("radio", { name: /View only/ })).toBeChecked();
    expect(within(dialog).getByRole("radio", { name: /View and download/ })).not.toBeChecked();
    await click(within(dialog).getByRole("button", { name: /Create link/ }));

    expect(sent).toEqual([{ method: "POST", path: "/shares", body: { paths: ["a.txt", "b.txt"], canDownload: false } }]);
    const field = await within(dialog).findByRole("textbox", { name: "Link" });
    expect(field).toHaveValue("https://uncloud.example.ts.net/s/abc");
    expect(dialog).toHaveTextContent("Anyone with this link can look at them, without an account.");
    await click(within(dialog).getByRole("button", { name: /Copy link/ }));
    expect(copied).toEqual(["https://uncloud.example.ts.net/s/abc"]);
    await within(dialog).findByRole("button", { name: /Copied/ });

    // Back in the list, what's shared says so.
    await click(within(dialog).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.getAllByLabelText("Shared by link")).toHaveLength(2));
  });

  it("lets a link be changed to allow downloading, and turned off", async () => {
    links = [
      {
        id: "link-1",
        url: "https://uncloud.example.ts.net/s/abc",
        canDownload: false,
        createdAt: "2026-11-01T00:00:00Z",
        items: [{ path: "a.txt", name: "a.txt", isDirectory: false, here: true }],
      },
    ];
    await open();
    await screen.findByLabelText("Shared by link");
    await click(screen.getByRole("button", { name: "Details for a.txt" }));
    await click(screen.getByRole("button", { name: "Shared…" }));
    const dialog = screen.getByRole("dialog");
    await within(dialog).findByRole("textbox", { name: "Link" });

    await click(within(dialog).getByRole("radio", { name: /View and download/ }));
    await waitFor(() =>
      expect(sent).toEqual([{ method: "PATCH", path: "/shares/link-1", body: { canDownload: true } }]),
    );
    await waitFor(() => expect(within(dialog).getByRole("radio", { name: /View and download/ })).toBeChecked());
    expect(dialog).toHaveTextContent("Anyone with this link can look at and download it");

    await click(within(dialog).getByRole("button", { name: /Stop sharing/ }));
    expect(sent.at(-1)).toEqual({ method: "DELETE", path: "/shares/link-1", body: null });
    await within(dialog).findByText("Stopped sharing. That link doesn’t work any more.");
    // Sharing it again is one click away.
    expect(within(dialog).getByRole("button", { name: /Create link/ })).toBeInTheDocument();
  });

  it("says nobody else can open a link while this Uncloud can't be reached", async () => {
    reachable = false;
    await open(false);
    await click(screen.getByRole("checkbox", { name: "Select a.txt" }));
    await click(screen.getByRole("button", { name: "Share…" }));
    const dialog = screen.getByRole("dialog");

    await within(dialog).findByText(/Nobody else can open links yet/);
    expect(dialog).toHaveTextContent("Ask whoever looks after this Uncloud to turn it on.");
  });

  it("says deleting something shared stops its link", async () => {
    links = [
      {
        id: "link-1",
        url: "https://uncloud.example.ts.net/s/abc",
        canDownload: true,
        createdAt: "2026-11-01T00:00:00Z",
        items: [{ path: "Docs/report.pdf", name: "report.pdf", isDirectory: false, here: true }],
      },
    ];
    await open();
    await waitFor(() => expect(screen.queryByLabelText("Shared by link")).toBeNull());
    await click(screen.getByRole("checkbox", { name: "Select Docs" }));
    await click(screen.getByRole("button", { name: /Delete/ }));

    // What's inside a folder being deleted counts as much as the folder itself.
    await waitFor(() =>
      expect(screen.getByRole("dialog")).toHaveTextContent(
        "What’s deleted comes off its link, which keeps working if it still shares something else.",
      ),
    );
  });
});

describe("opening a shared link", () => {
  let canDownload: boolean;
  let missing: boolean;

  beforeEach(() => {
    canDownload = false;
    missing = false;
    window.location.hash = "";
    vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
      const url = String(input);
      const path = url.replace(/^.*\/api/, "");
      if (missing)
        return Promise.resolve(
          json({ detail: "Nothing is shared at this address. The link may have been turned off, or part of it may be missing." }, 404),
        );
      if (path === "/shared/T")
        return Promise.resolve(
          json({ sharedBy: "Sam", canDownload, items: [{ ...entry("Photos", true), preview: null }] }),
        );
      if (path.startsWith("/shared/T/files"))
        return Promise.resolve(
          json({
            path: "Photos",
            entries: [
              { ...entry("Photos/Day 2", true), preview: null },
              { ...entry("Photos/beach.jpg"), preview: "image" },
              { ...entry("Photos/notes.txt"), preview: "text" },
              { ...entry("Photos/page.html"), preview: null },
            ],
            skippedCount: 0,
          }),
        );
      if (path.startsWith("/shared/T/size")) return Promise.resolve(json({ bytes: 100, files: 2 }));
      if (path.startsWith("/shared/T/file")) return Promise.resolve(new Response("<b>not bold</b>"));
      return Promise.resolve(json({ detail: "No." }, 404));
    });
  });

  it("shows a folder that's only to look at, with nothing to download", async () => {
    render(<SharedPage token="T" />);

    expect(await screen.findByRole("heading", { level: 1 })).toHaveTextContent("Photos");
    expect(screen.getByText("Shared by Sam")).toBeInTheDocument();
    expect(screen.getByText("View only")).toBeInTheDocument();
    await screen.findByText("beach.jpg");
    expect(screen.queryByRole("link", { name: /Download/ })).toBeNull();

    await click(screen.getByRole("button", { name: "View beach.jpg" }));
    const viewer = screen.getByRole("dialog");
    expect(within(viewer).getByRole("img", { name: "beach.jpg" })).toHaveAttribute(
      "src",
      "/api/shared/T/file?path=Photos%2Fbeach.jpg",
    );
    expect(within(viewer).queryByRole("link", { name: /Download/ })).toBeNull();
    expect(viewer).toHaveTextContent("1 of 3");

    // Text is shown as text, whatever it says.
    await click(within(viewer).getByRole("button", { name: "Next" }));
    await within(viewer).findByText("<b>not bold</b>");

    // What can't be shown says why, and offers nothing it isn't allowed to.
    await click(within(viewer).getByRole("button", { name: "Next" }));
    expect(viewer).toHaveTextContent("This kind of file can’t be shown in a browser.");
    expect(viewer).toHaveTextContent("lets people look but not download");
    expect(within(viewer).queryByRole("link", { name: /Download/ })).toBeNull();
  });

  it("offers each file, and the whole folder as a zip, when downloading is allowed", async () => {
    canDownload = true;
    render(<SharedPage token="T" />);

    await screen.findByText("beach.jpg");
    expect(screen.getByText("You can download")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Download all/ })).toHaveAttribute("href", "/api/shared/T/zip?path=Photos");
    expect(screen.getByRole("link", { name: "Download beach.jpg" })).toHaveAttribute(
      "href",
      "/api/shared/T/download?path=Photos%2Fbeach.jpg",
    );

    await click(screen.getByRole("button", { name: "View page.html" }));
    expect(within(screen.getByRole("dialog")).getAllByRole("link", { name: /Download/ }).length).toBeGreaterThan(0);
  });

  it("says plainly when a link doesn't work", async () => {
    missing = true;
    render(<SharedPage token="T" />);

    expect(await screen.findByRole("heading", { level: 1 })).toHaveTextContent("This link doesn’t work");
    expect(screen.getByRole("alert")).toHaveTextContent("The link may have been turned off");
  });
});
