import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import AddFiles from "./AddFiles";
import UploadStatus, { pickedFrom, useUpload } from "./Uploads";
import type { Picked } from "./Uploads";

/**
 * Sending files from the computer or phone somebody is using. It is the one way into Uncloud that
 * needs nothing set up and nobody's permission, so every account is offered it — and what they
 * choose goes into the folder they have open, a file at a time, put in place together at the end.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

interface Asked {
  method: string;
  path: string;
  body: unknown;
}

let asked: Asked[] = [];
let answer: (path: string, method: string) => Response | Promise<Response>;

/** A request for one file, held until the test says how it went. */
class FakeRequest {
  static sent: FakeRequest[] = [];
  static respond: (request: FakeRequest) => void = (request) => request.finish(200, { bytes: 1 });

  method = "";
  url = "";
  headers: Record<string, string> = {};
  body: unknown = null;
  status = 0;
  responseText = "";
  aborted = false;
  upload: { onprogress: ((event: { loaded: number }) => void) | null } = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;

  open(method: string, url: string) {
    this.method = method;
    this.url = url;
  }
  setRequestHeader(name: string, value: string) {
    this.headers[name] = value;
  }
  send(body: unknown) {
    this.body = body;
    FakeRequest.sent.push(this);
    queueMicrotask(() => FakeRequest.respond(this));
  }
  finish(status: number, body: unknown) {
    this.status = status;
    this.responseText = JSON.stringify(body);
    this.onload?.();
  }
  abort() {
    this.aborted = true;
    this.onabort?.();
  }
  get path() {
    return new URLSearchParams(this.url.split("?")[1]).get("path");
  }
}

beforeEach(() => {
  asked = [];
  FakeRequest.sent = [];
  FakeRequest.respond = (request) => request.finish(200, { bytes: 1 });
  vi.stubGlobal("XMLHttpRequest", FakeRequest);
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    const method = init?.method ?? "GET";
    asked.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : null });
    return Promise.resolve(answer(path, method));
  });
});

afterEach(() => vi.unstubAllGlobals());

/** A file as a browser hands it over: where it sits in a chosen folder, if it was in one. */
function file(path: string, content = "x"): File {
  const made = new File([content], path.split("/").at(-1)!, { lastModified: 1577934245000 });
  Object.defineProperty(made, "webkitRelativePath", { value: path.includes("/") ? path : "" });
  return made;
}

/** Uncloud agreeing to an upload, taking each file, and putting what arrived in place. */
function accepting(placed: { from: string; to: string }[] = []) {
  return (path: string, method: string) => {
    if (path === "/uploads" && method === "POST") return json({ id: "u1" });
    if (path === "/uploads/u1/finish") return json({ items: placed });
    if (path === "/uploads/u1" && method === "DELETE") return json({});
    return json({});
  };
}

function Harness({
  chosen,
  folder = "Trips",
  signedIn = true,
  onUploaded = () => {},
  onOpen = () => {},
}: {
  chosen: Picked[];
  folder?: string;
  signedIn?: boolean;
  onUploaded?: () => void;
  onOpen?: (path: string) => void;
}) {
  const uploads = useUpload(signedIn, onUploaded);
  const [problem, setProblem] = useState("");
  return (
    <>
      <button
        onClick={() =>
          void uploads.start(folder, chosen).catch((error: Error) => setProblem(error.message))
        }
      >
        Send
      </button>
      {problem && <p role="alert">{problem}</p>}
      {uploads.upload && (
        <UploadStatus
          upload={uploads.upload}
          onStop={uploads.stop}
          onDismiss={uploads.dismiss}
          onOpen={onOpen}
        />
      )}
    </>
  );
}

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

describe("adding files from this device", () => {
  it("is offered to somebody who doesn't look after Uncloud, and sends what they choose to the folder they have open", async () => {
    answer = () =>
      json({ accounts: [], places: [], suggestions: [], canAddFolders: false, canPickFolder: false });
    const onUpload = vi.fn(async () => {});
    render(
      <AddFiles
        importing={false}
        uploading={false}
        isAdmin={false}
        folder="Photos/Trips"
        storage={null}
        arrivedFromDropbox={null}
        onStarted={() => {}}
        onUpload={onUpload}
        onSetUpDropbox={() => {}}
      />,
    );

    // The host's own folders stay with whoever looks after it; this device is everybody's.
    expect(await screen.findByText("This device")).toBeInTheDocument();
    expect(screen.queryByText("This computer")).not.toBeInTheDocument();
    await click(screen.getByText("This device"));
    expect(screen.getByText("Trips", { selector: "strong" })).toBeInTheDocument();

    const loose = [file("beach.jpg"), file("notes.txt")];
    await act(async () => {
      fireEvent.change(screen.getByTestId("choose-files"), { target: { files: loose } });
    });
    await waitFor(() => expect(onUpload).toHaveBeenCalledTimes(1));
    expect(onUpload.mock.calls[0]).toEqual([
      [
        { file: loose[0], path: "beach.jpg" },
        { file: loose[1], path: "notes.txt" },
      ],
    ]);

    // A folder's files come with where each sits in it.
    const folder = [file("Holiday/day 1/sea.jpg"), file("Holiday/map.pdf")];
    await act(async () => {
      fireEvent.change(screen.getByTestId("choose-folder"), { target: { files: folder } });
    });
    await waitFor(() => expect(onUpload).toHaveBeenCalledTimes(2));
    expect((onUpload.mock.calls[1] as unknown as [Picked[]])[0].map((item) => item.path)).toEqual([
      "Holiday/day 1/sea.jpg",
      "Holiday/map.pdf",
    ]);
  });

  it("says why, in the dialog, when Uncloud won't take them", async () => {
    answer = () =>
      json({ accounts: [], places: [], suggestions: [], canAddFolders: false, canPickFolder: false });
    render(
      <AddFiles
        importing={false}
        uploading={false}
        isAdmin={false}
        folder=""
        storage={null}
        arrivedFromDropbox={null}
        onStarted={() => {}}
        onUpload={async () => {
          throw new Error("This would add 4 GB and only 1 GB is free on your Uncloud drive.");
        }}
        onSetUpDropbox={() => {}}
      />,
    );
    await click(await screen.findByText("This device"));
    expect(screen.getByText("My files", { selector: "strong" })).toBeInTheDocument();

    await act(async () => {
      fireEvent.change(screen.getByTestId("choose-files"), { target: { files: [file("film.mov")] } });
    });

    expect(await screen.findByRole("alert")).toHaveTextContent("only 1 GB is free");
  });

  it("sends each file, then puts them in place together, leaving hidden files behind", async () => {
    answer = accepting([{ from: "Holiday", to: "Trips/Holiday" }]);
    const uploaded = vi.fn();
    const opened = vi.fn();
    const chosen = pickedFrom([
      file("Holiday/sea.jpg", "sea!"),
      file("Holiday/day 1/map.pdf", "a map"),
      file("Holiday/.DS_Store", "finder"),
    ]);
    render(<Harness chosen={chosen} onUploaded={uploaded} onOpen={opened} />);

    await click(screen.getByText("Send"));

    await screen.findByText(/Added 2 files from this device/);
    expect(asked[0]).toEqual({ method: "POST", path: "/uploads", body: { destination: "Trips", bytes: 9 } });
    expect(FakeRequest.sent.map((request) => [request.method, request.path])).toEqual([
      ["PUT", "Holiday/sea.jpg"],
      ["PUT", "Holiday/day 1/map.pdf"],
    ]);
    expect(FakeRequest.sent[0].url).toContain("/api/uploads/u1?");
    expect(FakeRequest.sent[0].url).toContain("modified=1577934245000");
    expect(FakeRequest.sent[0].headers["X-Homebase-Request"]).toBe("1");
    expect(FakeRequest.sent[0].body).toBe(chosen[0].file);
    expect(asked.at(-1)).toEqual({ method: "POST", path: "/uploads/u1/finish", body: { destination: "Trips" } });
    expect(uploaded).toHaveBeenCalledTimes(1);

    // One folder was chosen, so that's where "Show them" goes.
    await click(screen.getByRole("button", { name: /Show them/ }));
    expect(opened).toHaveBeenCalledWith("Trips/Holiday");
  });

  it("says when a name was already taken where they went", async () => {
    answer = accepting([{ from: "notes.txt", to: "Trips/notes 2.txt" }]);
    render(<Harness chosen={pickedFrom([file("notes.txt")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByText(/Added 1 file from this device/)).toHaveTextContent(
      "There was already something called “notes.txt” there, so this one is “notes 2.txt”.",
    );
  });

  it("adds the rest when Uncloud won't keep one of them, and says which", async () => {
    answer = accepting([{ from: "good.txt", to: "Trips/good.txt" }]);
    FakeRequest.respond = (request) =>
      request.path === "bad\\name.txt"
        ? request.finish(400, { detail: "A file can only go inside the folder it’s being added to." })
        : request.finish(200, { bytes: 1 });
    render(<Harness chosen={pickedFrom([file("good.txt"), file("bad\\name.txt")])} />);

    await click(screen.getByText("Send"));

    await screen.findByText(/Added 1 file from this device/);
    await click(screen.getByRole("button", { name: /1 file couldn’t be added/ }));
    expect(screen.getByText("bad\\name.txt")).toBeInTheDocument();
    expect(screen.getByText("A file can only go inside the folder it’s being added to.")).toBeInTheDocument();
  });

  it("stops, and leaves nothing behind, when somebody presses Stop", async () => {
    answer = accepting();
    // Held: the file is still on its way when Stop is pressed.
    FakeRequest.respond = () => {};
    render(<Harness chosen={pickedFrom([file("film.mov"), file("other.mov")])} />);

    await click(screen.getByText("Send"));
    await screen.findByText(/Adding film.mov and other.mov from this device/);
    await waitFor(() => expect(FakeRequest.sent.length).toBeGreaterThan(0));
    await click(screen.getByRole("button", { name: "Stop" }));

    await screen.findByText("Stopped adding film.mov and other.mov. Nothing from it was added.");
    expect(FakeRequest.sent.every((request) => request.aborted)).toBe(true);
    expect(asked.at(-1)).toEqual({ method: "DELETE", path: "/uploads/u1", body: null });
    expect(asked.some((request) => request.path.endsWith("/finish"))).toBe(false);
  });

  it("gives up on the whole upload, throwing away what arrived, when Uncloud runs out of room partway", async () => {
    answer = accepting();
    FakeRequest.respond = (request) =>
      request.path === "big.mov"
        ? request.finish(409, { detail: "This would add 4 GB and only 1 GB is free on your Uncloud drive." })
        : request.finish(200, { bytes: 1 });
    render(<Harness chosen={pickedFrom([file("small.txt"), file("big.mov")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByRole("status")).toHaveTextContent("only 1 GB is free on your Uncloud drive");
    expect(asked.at(-1)).toEqual({ method: "DELETE", path: "/uploads/u1", body: null });
    expect(asked.some((request) => request.path.endsWith("/finish"))).toBe(false);
  });

  it("refuses up front, before sending anything, what Uncloud has no room for", async () => {
    answer = () => json({ detail: "This would add 4 GB and only 1 GB is free on your Uncloud drive." }, 409);
    render(<Harness chosen={pickedFrom([file("film.mov")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByRole("alert")).toHaveTextContent("only 1 GB is free");
    expect(FakeRequest.sent).toHaveLength(0);
  });

  it("ends when its account signs out, and shows nothing of it to whoever signs in next", async () => {
    answer = accepting();
    FakeRequest.respond = () => {};
    const chosen = pickedFrom([file("diary.txt")]);
    const { rerender } = render(<Harness chosen={chosen} />);
    await click(screen.getByText("Send"));
    await screen.findByText(/Adding diary.txt from this device/);
    await waitFor(() => expect(FakeRequest.sent).toHaveLength(1));

    rerender(<Harness chosen={chosen} signedIn={false} />);

    await waitFor(() => expect(screen.queryByText(/diary.txt/)).not.toBeInTheDocument());
    expect(FakeRequest.sent[0].aborted).toBe(true);
    await waitFor(() => expect(asked.at(-1)).toEqual({ method: "DELETE", path: "/uploads/u1", body: null }));
    rerender(<Harness chosen={chosen} />);
    expect(screen.queryByText(/diary.txt/)).not.toBeInTheDocument();
  });

  it("has nothing to send when all that was chosen is hidden", async () => {
    answer = accepting();
    render(<Harness chosen={pickedFrom([file(".DS_Store")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByRole("alert")).toHaveTextContent("Uncloud leaves hidden files out");
    expect(asked).toHaveLength(0);
  });
});
