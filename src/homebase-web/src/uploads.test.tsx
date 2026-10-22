import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import AddFiles from "./AddFiles";
import FileBrowser from "./FileBrowser";
import UploadStatus, { pickedFrom, STALLED_AFTER, UnfinishedStatus, useUpload } from "./Uploads";
import type { Picked } from "./Uploads";
import type { UploadComparison } from "./api";

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
let answer: (path: string, method: string, body: unknown) => Response | Promise<Response>;
/**
 * Uploads a page went away from, as Uncloud lists them whenever a page opens. Answered apart from
 * everything else, and left out of what was asked, which is about what an upload does.
 */
let unfinished: () => unknown[];
let listed = 0;

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
  unfinished = () => [];
  listed = 0;
  FakeRequest.sent = [];
  FakeRequest.respond = (request) => request.finish(200, { bytes: 1 });
  vi.stubGlobal("XMLHttpRequest", FakeRequest);
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace(/^.*\/api/, "");
    const method = init?.method ?? "GET";
    if (path === "/uploads" && method === "GET") {
      listed++;
      return Promise.resolve(json(unfinished()));
    }
    const body: unknown = init?.body ? JSON.parse(String(init.body)) : null;
    asked.push({ method, path, body });
    const signal = init?.signal;
    if (signal?.aborted)
      return Promise.reject(new DOMException("The operation was aborted.", "AbortError"));
    const result = Promise.resolve(answer(path, method, body));
    if (!signal) return result;
    // A request taken back — one removed while Uncloud is still being asked — ends here, rather
    // than whenever the answer shows up.
    return new Promise((resolve, reject) => {
      const fail = () => reject(new DOMException("The operation was aborted.", "AbortError"));
      const onAbort = () => fail();
      signal.addEventListener("abort", onAbort, { once: true });
      result.then(
        (response) => {
          signal.removeEventListener("abort", onAbort);
          if (signal.aborted) fail();
          else resolve(response);
        },
        (error: unknown) => {
          signal.removeEventListener("abort", onAbort);
          reject(error);
        },
      );
    });
  });
});

afterEach(() => vi.unstubAllGlobals());

/** A file as a browser hands it over: where it sits in a chosen folder, if it was in one. */
function file(path: string, content = "x"): File {
  const made = new File([content], path.split("/").at(-1)!, { lastModified: 1577934245000 });
  Object.defineProperty(made, "webkitRelativePath", { value: path.includes("/") ? path : "" });
  return made;
}

/** What a comparison was asked about: the paths of the files about to be sent. */
const comparing = (body: unknown) => (body as { files: { path: string }[] }).files.map((file) => file.path);

/**
 * Uncloud agreeing to uploads — u1, u2… — taking each file, and putting what arrived in place,
 * having found none of it there already unless told what is.
 */
function accepting(
  placed: { from: string; to: string }[] = [],
  there: Partial<UploadComparison> | ((paths: string[]) => Partial<UploadComparison>) = {},
  replaced = 0,
) {
  let begun = 0;
  return (path: string, method: string, body?: unknown) => {
    if (path === "/uploads/compare")
      return json({
        same: [],
        changed: [],
        newer: [],
        folders: [],
        ...(typeof there === "function" ? there(comparing(body)) : there),
      });
    if (path === "/uploads" && method === "POST") return json({ id: `u${++begun}` });
    if (/^\/uploads\/u\d+\/finish$/.test(path)) return json({ items: placed, replaced });
    return json({});
  };
}

function Harness({
  chosen,
  more = [],
  folder = "Trips",
  signedIn = true,
  onUploaded = () => {},
  onOpen = () => {},
}: {
  chosen: Picked[];
  /** Chosen afterwards, with "Send more", into the same folder. */
  more?: Picked[];
  folder?: string;
  signedIn?: boolean;
  onUploaded?: () => void;
  onOpen?: (path: string) => void;
}) {
  const uploads = useUpload(signedIn, onUploaded);
  const [problem, setProblem] = useState("");
  const send = (picked: Picked[]) =>
    void uploads.start(folder, picked).catch((error: Error) => setProblem(error.message));
  return (
    <>
      <button onClick={() => send(chosen)}>Send</button>
      <button onClick={() => send(more)}>Send more</button>
      {problem && <p role="alert">{problem}</p>}
      {uploads.unfinished.map((upload) => (
        <UnfinishedStatus
          key={upload.id}
          upload={upload}
          onPlace={() => uploads.place(upload)}
          onThrowAway={() => uploads.throwAway(upload.id)}
        />
      ))}
      {uploads.uploads.map((upload) => (
        <UploadStatus
          key={upload.key}
          upload={upload}
          onStop={() => uploads.stop(upload.key)}
          onDismiss={() => uploads.dismiss(upload.key)}
          onOpen={onOpen}
          onDecide={(merge) => uploads.decide(upload.key, merge)}
        />
      ))}
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
    expect(screen.queryByText("Host computer")).not.toBeInTheDocument();
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

  it("is the only way to the computer somebody is sitting at, even when that's the host", async () => {
    const sources = (atTheComputer: boolean) =>
      json({ accounts: [], places: [], suggestions: [], canAddFolders: true, canPickFolder: true, atTheComputer });
    const dialog = () => (
      <AddFiles
        importing={false}
        uploading={false}
        isAdmin
        folder=""
        storage={null}
        arrivedFromDropbox={null}
        onStarted={() => {}}
        onUpload={async () => {}}
        onSetUpDropbox={() => {}}
      />
    );
    // Somebody who went to the host's folders last time, now sitting at the host: they start from
    // the list of places, not inside one they can no longer see the way back to.
    window.localStorage.setItem("uncloud.add-files.source", "computer");
    try {
      answer = () => sources(true);
      const here = render(dialog());
      expect(await screen.findByText("Where are your files?")).toBeInTheDocument();
      expect(screen.getByText("This device")).toBeInTheDocument();
      expect(screen.queryByText("Host computer")).not.toBeInTheDocument();
      here.unmount();
    } finally {
      window.localStorage.removeItem("uncloud.add-files.source");
    }

    // From anywhere else the host is somewhere different, so it is offered as well.
    answer = () => sources(false);
    render(dialog());
    expect(await screen.findByText("Host computer")).toBeInTheDocument();
    expect(screen.getByText("This device")).toBeInTheDocument();
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

    await screen.findByText(/from this device: 2 files,/);
    // Uncloud is asked what of it is there already, and then, finding none of it, agrees to take it all.
    expect(asked[0]).toEqual({
      method: "POST",
      path: "/uploads/compare",
      body: {
        destination: "Trips",
        files: [
          { path: "Holiday/sea.jpg", bytes: 4, modified: 1577934245000 },
          { path: "Holiday/day 1/map.pdf", bytes: 5, modified: 1577934245000 },
        ],
      },
    });
    // Told what's coming, too, in case these are files an earlier page didn't finish sending.
    expect(asked[1]).toEqual({
      method: "POST",
      path: "/uploads",
      body: {
        destination: "Trips",
        bytes: 9,
        merge: false,
        files: [
          { path: "Holiday/sea.jpg", bytes: 4, modified: 1577934245000 },
          { path: "Holiday/day 1/map.pdf", bytes: 5, modified: 1577934245000 },
        ],
      },
    });
    expect(FakeRequest.sent.map((request) => [request.method, request.path])).toEqual([
      ["PUT", "Holiday/sea.jpg"],
      ["PUT", "Holiday/day 1/map.pdf"],
    ]);
    expect(FakeRequest.sent[0].url).toContain("/api/uploads/u1?");
    expect(FakeRequest.sent[0].url).toContain("modified=1577934245000");
    expect(FakeRequest.sent[0].headers["X-Homebase-Request"]).toBe("1");
    expect(FakeRequest.sent[0].body).toBe(chosen[0].file);
    expect(asked.at(-1)).toEqual({
      method: "POST",
      path: "/uploads/u1/finish",
      body: { destination: "Trips", merge: false },
    });
    expect(uploaded).toHaveBeenCalledTimes(1);

    // One folder was chosen, so that's where "Show them" goes.
    await click(screen.getByRole("button", { name: /Show them/ }));
    expect(opened).toHaveBeenCalledWith("Trips/Holiday");
  });

  it("says when a name was already taken where they went", async () => {
    answer = accepting([{ from: "notes.txt", to: "Trips/notes 2.txt" }]);
    render(<Harness chosen={pickedFrom([file("notes.txt")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByText(/from this device: 1 file,/)).toHaveTextContent(
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

    await screen.findByText(/from this device: 1 file,/);
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

    // Stopping one leaves the way clear for the next.
    FakeRequest.respond = (request) => request.finish(200, { bytes: 1 });
    await click(screen.getByText("Send"));
    await screen.findByText(/from this device: 2 files,/);
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

  it("doesn't say nothing was added when the answer to putting them in place never came", async () => {
    const accept = accepting();
    answer = (path, method) =>
      path === "/uploads/u1/finish"
        ? Promise.reject(new TypeError("Failed to fetch"))
        : accept(path, method);
    const uploaded = vi.fn();
    render(<Harness chosen={pickedFrom([file("notes.txt")])} onUploaded={uploaded} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByRole("status")).toHaveTextContent("they may already be in your files");
    // What's there is read again, so if they arrived they're in sight.
    expect(uploaded).toHaveBeenCalledTimes(1);
  });

  it("refuses up front, before sending anything, what Uncloud has no room for", async () => {
    const accept = accepting();
    answer = (path, method, body) =>
      path === "/uploads" && method === "POST"
        ? json({ detail: "This would add 4 GB and only 1 GB is free on your Uncloud drive." }, 409)
        : accept(path, method, body);
    render(<Harness chosen={pickedFrom([file("film.mov")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByRole("status")).toHaveTextContent("only 1 GB is free");
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

describe("adding more while some are on their way", () => {
  /** The first file held on its way until the test lets it go; every other one arrives at once. */
  function holding(path: string) {
    let release = () => {};
    FakeRequest.respond = (request) => {
      if (request.path === path) release = () => request.finish(200, { bytes: 1 });
      else request.finish(200, { bytes: 1 });
    };
    return async () => {
      await act(async () => release());
    };
  }

  it("lines up what's added meanwhile, and sends it once what's ahead of it is in", async () => {
    answer = accepting([{ from: "film.mov", to: "Trips/film.mov" }]);
    const release = holding("film.mov");
    render(
      <Harness
        chosen={pickedFrom([file("film.mov")])}
        more={pickedFrom([file("Taxes/receipt.pdf"), file("Taxes/w2.pdf")])}
      />,
    );

    await click(screen.getByText("Send"));
    await screen.findByText(/Adding film.mov from this device/);
    await click(screen.getByText("Send more"));

    expect(await screen.findByText("Waiting to add Taxes")).toBeInTheDocument();
    expect(screen.getByText(/2 files · 2 B · into Trips, once\s+what’s ahead of it is in\./)).toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
    // Nothing of it is asked about, let alone sent, until its turn: what's ahead of it may change what's there.
    expect(asked.filter((request) => request.path === "/uploads/compare")).toHaveLength(1);

    await release();

    await waitFor(() => expect(screen.getAllByText(/from this device: \d files?,/)).toHaveLength(2));
    // Each says which it was, since they're all in sight together.
    expect(screen.getByText("Added film.mov from this device: 1 file, 1 B.")).toBeInTheDocument();
    expect(screen.getByText("Added Taxes from this device: 2 files, 2 B.")).toBeInTheDocument();
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["film.mov", "Taxes/receipt.pdf", "Taxes/w2.pdf"]);
    expect(
      asked
        .filter((request) => request.path === "/uploads")
        .map((request) => comparing(request.body)),
    ).toEqual([["film.mov"], ["Taxes/receipt.pdf", "Taxes/w2.pdf"]]);
    expect(asked.map((request) => request.path)).toContain("/uploads/u2/finish");
  });

  it("takes away one still being looked at, and lets the one behind it go", async () => {
    let releaseCompare: (response: Response) => void = () => {};
    const accept = accepting();
    let compares = 0;
    answer = (path, method, body) => {
      if (path === "/uploads/compare" && compares++ === 0)
        return new Promise<Response>((resolve) => {
          releaseCompare = resolve;
        });
      return accept(path, method, body);
    };
    render(<Harness chosen={pickedFrom([file("film.mov")])} more={pickedFrom([file("notes.txt")])} />);

    await click(screen.getByText("Send"));
    const looking = await screen.findByText(/Looking at what’s already/);
    await click(screen.getByText("Send more"));
    await screen.findByText("Waiting to add notes.txt");

    await click(within(looking.closest("section")!).getByRole("button", { name: "Remove" }));

    // The one behind doesn't wait for an answer about the one that was taken away.
    await screen.findByText("Added notes.txt from this device: 1 file, 1 B.");
    expect(screen.queryByText(/film\.mov/)).toBeNull();
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["notes.txt"]);

    // The answer showing up afterwards doesn't bring it back, or call it a failure.
    await act(async () => {
      releaseCompare(json({ same: [], changed: [], newer: [], folders: [] }));
    });
    expect(screen.queryByText(/film\.mov/)).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("takes one that's still waiting away without sending any of it", async () => {
    answer = accepting();
    const release = holding("film.mov");
    render(<Harness chosen={pickedFrom([file("film.mov")])} more={pickedFrom([file("notes.txt")])} />);

    await click(screen.getByText("Send"));
    await screen.findByText(/Adding film.mov from this device/);
    await click(screen.getByText("Send more"));
    await click(await screen.findByRole("button", { name: "Remove" }));

    expect(screen.queryByText(/notes.txt/)).toBeNull();
    await release();
    await screen.findByText(/from this device: 1 file,/);
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["film.mov"]);
    expect(asked.filter((request) => request.path === "/uploads/compare")).toHaveLength(1);
  });

  it("sends the ones behind while one waits for somebody to say what to do with it", async () => {
    answer = accepting([], (paths) =>
      paths.includes("Holiday/sea.jpg") ? { same: ["Holiday/sea.jpg"], folders: ["Holiday"] } : {},
    );
    render(
      <Harness
        chosen={pickedFrom([file("Holiday/sea.jpg"), file("Holiday/boat.jpg")])}
        more={pickedFrom([file("notes.txt")])}
      />,
    );

    await click(screen.getByText("Send"));
    const add = await screen.findByRole("button", { name: "Add 1 file" });
    await click(screen.getByText("Send more"));

    await screen.findByText(/from this device: 1 file,/);
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["notes.txt"]);
    // Still waiting for an answer, and still there to be given one.
    await click(add);
    await waitFor(() => expect(screen.getAllByText(/from this device: 1 file,/)).toHaveLength(2));
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["notes.txt", "Holiday/boat.jpg"]);
  });

  it("doesn't send again what another upload brought in while it waited for an answer", async () => {
    // Uncloud as it is: sea.jpg there from before, and whatever was put in place since.
    const landed = new Set(["Holiday/sea.jpg"]);
    const accept = accepting([{ from: "Holiday", to: "Trips/Holiday" }], (paths) => ({
      same: paths.filter((path) => landed.has(path)),
      folders: ["Holiday"],
    }));
    answer = (path, method, body) => {
      if (/\/finish$/.test(path)) landed.add("Holiday/boat.jpg");
      return accept(path, method, body);
    };
    render(
      <Harness
        chosen={pickedFrom([file("Holiday/sea.jpg"), file("Holiday/boat.jpg")])}
        more={pickedFrom([file("Holiday/boat.jpg")])}
      />,
    );

    await click(screen.getByText("Send"));
    await screen.findByRole("button", { name: "Add 1 file" });
    await click(screen.getByText("Send more"));
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Add 1 file" })).toHaveLength(2));
    // The second is answered first, and brings boat.jpg in.
    await click(screen.getAllByRole("button", { name: "Add 1 file" })[1]);
    await screen.findByText(/from this device: 1 file,/);

    // Answered now, the first finds boat.jpg there already, and has nothing left to send.
    await click(screen.getByRole("button", { name: "Add 1 file" }));

    expect(
      await screen.findByText("Nothing left to add from Holiday: it’s all in Trips already."),
    ).toBeInTheDocument();
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["Holiday/boat.jpg"]);
    expect(asked.filter((request) => request.path === "/uploads")).toHaveLength(1);
  });

  it("ends everything on its way or waiting when its account signs out", async () => {
    answer = accepting();
    FakeRequest.respond = () => {};
    const chosen = pickedFrom([file("diary.txt")]);
    const { rerender } = render(<Harness chosen={chosen} more={pickedFrom([file("letters.txt")])} />);
    await click(screen.getByText("Send"));
    await waitFor(() => expect(FakeRequest.sent).toHaveLength(1));
    await click(screen.getByText("Send more"));
    await screen.findByText("Waiting to add letters.txt");

    rerender(<Harness chosen={chosen} signedIn={false} />);

    await waitFor(() => expect(screen.queryByText(/diary.txt|letters.txt/)).not.toBeInTheDocument());
    expect(FakeRequest.sent).toHaveLength(1);
    expect(asked.filter((request) => request.path === "/uploads/compare")).toHaveLength(1);
  });
});

describe("adding a folder that's already there", () => {
  const holiday = () =>
    pickedFrom([
      file("Holiday/sea.jpg", "sea!"),
      file("Holiday/map.pdf", "a new map"),
      file("Holiday/day 2/boat.jpg", "boat"),
      file("Holiday/edited there.jpg", "old"),
    ]);
  const there = {
    same: ["Holiday/sea.jpg"],
    changed: ["Holiday/map.pdf"],
    newer: ["Holiday/edited there.jpg"],
    folders: ["Holiday"],
  };

  it("says what of it is new or changed before sending anything, then sends only that", async () => {
    answer = accepting([{ from: "Holiday", to: "Trips/Holiday" }], there, 1);
    const opened = vi.fn();
    render(<Harness chosen={holiday()} onOpen={opened} />);

    await click(screen.getByText("Send"));

    const decision = await screen.findByRole("status");
    expect(decision).toHaveTextContent("Holiday is already in Trips.");
    expect(decision).toHaveTextContent("2 files to add: 1 new and 1 changed.");
    expect(decision).toHaveTextContent("1 file is the same in both and won’t be sent again.");
    expect(decision).toHaveTextContent("1 file was changed in Uncloud since, so it’s left as it is.");
    expect(decision).toHaveTextContent("A changed file takes the place of the one there, which goes to the bin.");
    // Nothing is begun, let alone sent, until somebody says what to do.
    expect(asked.map((request) => request.path)).toEqual(["/uploads/compare"]);
    expect(FakeRequest.sent).toHaveLength(0);

    await click(screen.getByRole("button", { name: "See which" }));
    expect(within(decision).getByText("Holiday/day 2/boat.jpg").nextSibling).toHaveTextContent("New");
    expect(within(decision).getByText("Holiday/map.pdf").nextSibling).toHaveTextContent(/^Changed\./);
    expect(within(decision).getByText("Holiday/edited there.jpg").nextSibling).toHaveTextContent(
      "left as it is",
    );
    expect(within(decision).queryByText("Holiday/sea.jpg")).toBeNull();

    await click(screen.getByRole("button", { name: "Add 2 files" }));

    await screen.findByText(/from this device: 2 files,/);
    // Asked again about what it's sending when its turn comes, in case anything landed meanwhile.
    expect(asked[1].path).toBe("/uploads/compare");
    expect(comparing(asked[1].body)).toEqual(["Holiday/map.pdf", "Holiday/day 2/boat.jpg"]);
    expect(asked[2]).toMatchObject({ method: "POST", path: "/uploads", body: { destination: "Trips", bytes: 13, merge: true } });
    expect(comparing(asked[2].body)).toEqual(["Holiday/map.pdf", "Holiday/day 2/boat.jpg"]);
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["Holiday/map.pdf", "Holiday/day 2/boat.jpg"]);
    expect(asked.at(-1)).toEqual({
      method: "POST",
      path: "/uploads/u1/finish",
      body: { destination: "Trips", merge: true },
    });
    expect(screen.getByRole("status")).toHaveTextContent(
      "One took the place of an older copy, which is in the bin. 2 were already there, so they weren’t sent again.",
    );
    await click(screen.getByRole("button", { name: /Show them/ }));
    expect(opened).toHaveBeenCalledWith("Trips/Holiday");
  });

  it("names a file inside the folder that had to take a new name, rather than its path", async () => {
    answer = accepting(
      [{ from: "Holiday/map.pdf", to: "Trips/Holiday/map 2.pdf" }],
      { changed: ["Holiday/map.pdf"], folders: ["Holiday"] },
    );
    render(<Harness chosen={pickedFrom([file("Holiday/map.pdf", "a new map")])} />);

    await click(screen.getByText("Send"));
    await click(await screen.findByRole("button", { name: "Add 1 file" }));

    expect(await screen.findByText(/from this device: 1 file,/)).toHaveTextContent(
      "There was already something called “map.pdf” there, so this one is “map 2.pdf”.",
    );
  });

  it("adds all of it again beside what's there when asked to keep both", async () => {
    answer = accepting([{ from: "notes.txt", to: "Trips/notes 2.txt" }], { same: ["notes.txt"] });
    render(<Harness chosen={pickedFrom([file("notes.txt")])} />);

    await click(screen.getByText("Send"));

    const decision = await screen.findByRole("status");
    expect(decision).toHaveTextContent("Nothing new to add: notes.txt is already in Trips.");
    expect(within(decision).queryByRole("button", { name: /^Add/ })).toBeNull();
    await click(within(decision).getByRole("button", { name: "Keep both" }));

    expect(await screen.findByText(/from this device: 1 file,/)).toHaveTextContent("this one is “notes 2.txt”");
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["notes.txt"]);
    expect(asked.at(-1)?.body).toEqual({ destination: "Trips", merge: false });
  });

  it("adds nothing when somebody decides not to, and is ready for the next", async () => {
    answer = accepting([], there);
    render(<Harness chosen={holiday()} />);

    await click(screen.getByText("Send"));
    await click(await screen.findByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("status")).toBeNull();
    expect(asked.map((request) => request.path)).toEqual(["/uploads/compare"]);
    await click(screen.getByText("Send"));
    expect(await screen.findByRole("button", { name: "Add 2 files" })).toBeInTheDocument();
  });

  it("lets go of what was waiting to be decided when its account signs out", async () => {
    answer = accepting([], there);
    const chosen = holiday();
    const { rerender } = render(<Harness chosen={chosen} />);
    await click(screen.getByText("Send"));
    await screen.findByRole("button", { name: "Add 2 files" });

    rerender(<Harness chosen={chosen} signedIn={false} />);
    await waitFor(() => expect(screen.queryByText(/Holiday/)).not.toBeInTheDocument());
    rerender(<Harness chosen={chosen} />);

    // Whoever signs in next can add their own, rather than finding something still on its way.
    await click(screen.getByText("Send"));
    expect(await screen.findByRole("button", { name: "Add 2 files" })).toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says why when Uncloud won't take what was decided on", async () => {
    const accept = accepting([], there);
    answer = (path, method) =>
      path === "/uploads" && method === "POST"
        ? json({ detail: "This would add 4 GB and only 1 GB is free on your Uncloud drive." }, 409)
        : accept(path, method);
    render(<Harness chosen={holiday()} />);

    await click(screen.getByText("Send"));
    await click(await screen.findByRole("button", { name: "Add 2 files" }));

    expect(await screen.findByRole("status")).toHaveTextContent("only 1 GB is free");
    expect(FakeRequest.sent).toHaveLength(0);
    // And the next can still go.
    answer = accept;
    await click(screen.getByText("Send"));
    expect(await screen.findByRole("button", { name: "Add 2 files" })).toBeInTheDocument();
  });
});

/** A file as a browser hands over a dropped one: something to ask for the file. */
function fileEntry(name: string, content = "x") {
  return {
    name,
    isFile: true,
    isDirectory: false,
    file: (resolve: (file: File) => void) => resolve(new File([content], name)),
  };
}

/** A dropped folder, which a browser reads out a batch at a time until a batch comes back empty. */
function folderEntry(name: string, ...batches: unknown[][]) {
  return {
    name,
    isFile: false,
    isDirectory: true,
    createReader: () => {
      const left = [...batches];
      return { readEntries: (resolve: (entries: unknown[]) => void) => resolve(left.shift() ?? []) };
    },
  };
}

/** What a drag carries, the way a browser describes it to the page. */
function carrying(...entries: unknown[]) {
  return {
    dataTransfer: {
      types: ["Files"],
      items: entries.map((entry) => ({ kind: "file", webkitGetAsEntry: () => entry })),
      files: [],
      dropEffect: "none",
    },
  };
}

describe("when the connection or the page goes away partway", () => {
  /** Time going by, as far as the page can tell, a little at a time so what it starts meanwhile gets its turn. */
  async function passes(ms: number) {
    for (let gone = 0; gone < ms; gone += 500)
      await act(async () => {
        await vi.advanceTimersByTimeAsync(500);
      });
  }

  /** What a page that went away left behind, as Uncloud lists it. */
  const left = (id: string, names: string[], more: object = {}) => ({
    id,
    destination: "Trips",
    merge: false,
    names,
    files: 201,
    bytes: 4_000_000_000,
    lastArrived: "2026-10-22T18:46:35Z",
    keptUntil: "2026-10-23T18:46:35Z",
    arriving: false,
    ...more,
  });

  it("sends a file again when it stops moving, whether the page or Uncloud notices", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      answer = accepting();
      const tries = new Map<string, number>();
      FakeRequest.respond = (request) => {
        const attempt = (tries.get(request.path!) ?? 0) + 1;
        tries.set(request.path!, attempt);
        if (attempt > 1) request.finish(200, { bytes: 1 });
        // Some of it goes, then nothing more, and no answer: only the page can tell.
        else if (request.path === "film.mov") request.upload.onprogress?.({ loaded: 1 });
        // Uncloud heard nothing more of it for a while, and said so.
        else request.finish(408, { detail: "Nothing more of “song.mp3” arrived for a while. Try adding it again." });
      };
      render(<Harness chosen={pickedFrom([file("film.mov"), file("song.mp3")])} />);

      await click(screen.getByText("Send"));
      await waitFor(() => expect(FakeRequest.sent).toHaveLength(2));
      await passes(5000);
      expect(tries.get("song.mp3")).toBe(2);
      // Waiting on the one that went quiet, which is never going to answer by itself.
      expect(screen.getByText(/Adding film.mov and song.mp3/)).toBeInTheDocument();
      expect(FakeRequest.sent[0].aborted).toBe(false);

      await passes(STALLED_AFTER);

      await screen.findByText(/from this device: 2 files,/);
      expect(FakeRequest.sent[0].aborted).toBe(true);
      expect(tries.get("film.mov")).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps what arrived when Uncloud stops answering partway, rather than throwing it away", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      answer = accepting();
      FakeRequest.respond = (request) =>
        request.path === "first.txt" ? request.finish(200, { bytes: 1 }) : request.onerror?.();
      render(<Harness chosen={pickedFrom([file("first.txt"), file("second.txt")])} />);

      await click(screen.getByText("Send"));
      await waitFor(() => expect(FakeRequest.sent.filter((request) => request.path === "second.txt")).toHaveLength(1));
      await passes(4000);

      expect(await screen.findByRole("status")).toHaveTextContent(
        "What arrived is kept for a day: check you’re still connected, then add the same files again and only the rest is sent.",
      );
      expect(FakeRequest.sent.filter((request) => request.path === "second.txt")).toHaveLength(3);
      expect(asked.some((request) => request.method === "DELETE")).toBe(false);
      expect(asked.some((request) => request.path.endsWith("/finish"))).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("picks up what an earlier page didn't finish when the same files are added again, sending only the rest", async () => {
    unfinished = () => [left("u7", ["Holiday"], { files: 1, bytes: 4 })];
    const accept = accepting([{ from: "Holiday", to: "Trips/Holiday" }]);
    answer = (path, method, body) =>
      path === "/uploads" && method === "POST" ? json({ id: "u7", arrived: ["Holiday/sea.jpg"] }) : accept(path, method, body);
    render(<Harness chosen={pickedFrom([file("Holiday/sea.jpg", "sea!"), file("Holiday/day 1/map.pdf", "a map")])} />);
    // Offered as soon as the page opens.
    expect(await screen.findByText("Adding Holiday didn’t finish.")).toBeInTheDocument();

    await click(screen.getByText("Send"));

    expect(await screen.findByText(/from this device: 2 files,/)).toHaveTextContent(
      "Added Holiday from this device: 2 files, 9 B. 1 had arrived on an earlier try, so it wasn’t sent again.",
    );
    expect(FakeRequest.sent.map((request) => request.path)).toEqual(["Holiday/day 1/map.pdf"]);
    expect(FakeRequest.sent[0].url).toContain("/api/uploads/u7?");
    expect(asked.at(-1)).toEqual({ method: "POST", path: "/uploads/u7/finish", body: { destination: "Trips", merge: false } });
    // It's this page's now, so it isn't offered any more.
    expect(screen.queryByText("Adding Holiday didn’t finish.")).not.toBeInTheDocument();
  });

  it("keeps what an earlier try left when Uncloud refuses something this time", async () => {
    const accept = accepting();
    answer = (path, method, body) =>
      path === "/uploads" && method === "POST" ? json({ id: "u7", arrived: ["Holiday/sea.jpg"] }) : accept(path, method, body);
    FakeRequest.respond = (request) =>
      request.finish(409, { detail: "Too many files are arriving at once. Wait for some to finish, then try again." });
    render(<Harness chosen={pickedFrom([file("Holiday/sea.jpg", "sea!"), file("Holiday/day 1/map.pdf", "a map")])} />);

    await click(screen.getByText("Send"));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Too many files are arriving at once. Wait for some to finish, then try again. What had arrived is kept for a day",
    );
    expect(asked.some((request) => request.method === "DELETE")).toBe(false);
    expect(asked.some((request) => request.path.endsWith("/finish"))).toBe(false);
  });

  it("puts what arrived in place as it is, or throws it away, when somebody would rather", async () => {
    unfinished = () => [left("u9", ["Holiday"]), left("u8", ["Old photos"], { destination: null, files: 1, bytes: 5 })];
    answer = accepting([{ from: "Holiday", to: "Trips/Holiday" }]);
    const uploaded = vi.fn();
    render(<Harness chosen={[]} onUploaded={uploaded} />);

    const holiday = (await screen.findByText("Adding Holiday didn’t finish.")).closest("section")!;
    expect(holiday).toHaveTextContent("201 files, 3.7 GB, arrived for Trips before it stopped.");
    const old = screen.getByText("Adding Old photos didn’t finish.").closest("section")!;
    // Where that one was going was never kept, so it can only be picked up, or thrown away.
    expect(within(old).queryByRole("button", { name: "Add what arrived" })).toBeNull();

    await click(within(holiday).getByRole("button", { name: "Add what arrived" }));
    await waitFor(() => expect(screen.queryByText("Adding Holiday didn’t finish.")).not.toBeInTheDocument());
    expect(asked).toContainEqual({ method: "POST", path: "/uploads/u9/finish", body: { destination: "Trips", merge: false } });
    expect(uploaded).toHaveBeenCalledTimes(1);

    await click(within(old).getByRole("button", { name: "Throw away" }));
    await waitFor(() => expect(screen.queryByText("Adding Old photos didn’t finish.")).not.toBeInTheDocument());
    expect(asked.at(-1)).toEqual({ method: "DELETE", path: "/uploads/u8", body: null });
  });

  it("says why, and offers it still, when what arrived couldn't be put in place", async () => {
    unfinished = () => [left("u9", ["Holiday"])];
    const accept = accepting();
    answer = (path, method, body) =>
      path === "/uploads/u9/finish"
        ? json({ detail: "That folder is no longer here. Refresh and try again." }, 404)
        : accept(path, method, body);
    render(<Harness chosen={[]} />);

    await click(await screen.findByRole("button", { name: "Add what arrived" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("That folder is no longer here.");
    expect(screen.getByRole("button", { name: "Add what arrived" })).toBeEnabled();
  });

  it("waits to offer one that's still arriving, and offers it once it has stopped", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      // Another page still sending it, or this one's last files from before a refresh not yet noticed gone.
      let arriving = true;
      unfinished = () => [left("u9", ["Holiday"], { arriving })];
      answer = accepting();
      render(<Harness chosen={[]} />);
      await waitFor(() => expect(listed).toBe(1));
      expect(screen.queryByText("Adding Holiday didn’t finish.")).not.toBeInTheDocument();

      await passes(15_000);
      expect(listed).toBe(2);
      expect(screen.queryByText("Adding Holiday didn’t finish.")).not.toBeInTheDocument();

      arriving = false;
      await passes(15_000);

      expect(await screen.findByText("Adding Holiday didn’t finish.")).toBeInTheDocument();
      expect(listed).toBe(3);
      // Stopped, it isn't asked about again.
      await passes(15_000);
      expect(listed).toBe(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it("offers a kept upload again after this page signs out and somebody signs back in", async () => {
    unfinished = () => [left("u1", ["Holiday"], { files: 1, bytes: 4 })];
    answer = accepting();
    const view = render(<Harness chosen={pickedFrom([file("Holiday/sea.jpg", "sea!")])} />);
    expect(await screen.findByText("Adding Holiday didn’t finish.")).toBeInTheDocument();

    await click(screen.getByText("Send"));
    // Picked up by this page, so it isn't offered here while the page is still signed in.
    await waitFor(() => expect(screen.queryByText("Adding Holiday didn’t finish.")).not.toBeInTheDocument());

    view.rerender(<Harness chosen={[]} signedIn={false} />);
    view.rerender(<Harness chosen={[]} signedIn />);

    expect(await screen.findByText("Adding Holiday didn’t finish.")).toBeInTheDocument();
  });

  it("stops asking, once its minute and a bit is up, about one another page is still sending", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      unfinished = () => [left("u9", ["Holiday"], { arriving: true })];
      answer = accepting();
      render(<Harness chosen={[]} />);
      await waitFor(() => expect(listed).toBe(1));

      await passes(120_000);

      expect(listed).toBe(6);
      expect(screen.queryByText("Adding Holiday didn’t finish.")).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("dropping onto My files", () => {
  const onDropFiles = vi.fn<(folder: string, picked: Picked[]) => Promise<void>>();

  beforeEach(() => {
    onDropFiles.mockReset();
    onDropFiles.mockResolvedValue();
    answer = () => json({ path: "Photos/Trips", entries: [], skippedCount: 0, indexedAt: "" });
  });

  function list() {
    render(
      <FileBrowser
        rootPath="/u"
        path="Photos/Trips"
        revision={0}
        navigate={() => {}}
        onAdd={() => {}}
        onChanged={() => {}}
        onDropFiles={onDropFiles}
      />,
    );
    return screen.getByRole("region", { name: "File browser" });
  }

  it("takes a folder dropped onto the list, everything in it, into the folder that's open", async () => {
    const panel = list();
    await screen.findByText("This folder is empty");
    const holiday = folderEntry(
      "Holiday",
      [fileEntry("sea.jpg"), fileEntry(".DS_Store"), folderEntry(".git", [fileEntry("config")])],
      [folderEntry("day 1", [fileEntry("map.pdf")])],
    );

    fireEvent.dragEnter(panel, carrying(holiday));
    expect(screen.getByText("Drop to add to Trips")).toBeInTheDocument();
    await act(async () => {
      fireEvent.drop(panel, carrying(holiday, fileEntry("notes.txt")));
    });

    expect(screen.queryByText("Drop to add to Trips")).not.toBeInTheDocument();
    await waitFor(() => expect(onDropFiles).toHaveBeenCalledTimes(1));
    expect(onDropFiles.mock.calls[0][0]).toBe("Photos/Trips");
    // Hidden things are left where they are, a hidden folder without even being opened.
    expect(onDropFiles.mock.calls[0][1].map((item) => item.path)).toEqual([
      "Holiday/sea.jpg",
      "Holiday/day 1/map.pdf",
      "notes.txt",
    ]);
  });

  it("puts what's dropped on a folder's row into that folder, and anywhere else into the one open", async () => {
    answer = (path) =>
      path.startsWith("/files/size")
        ? json({ bytes: 0, files: 0 })
        : json({
            path: "Photos/Trips",
            entries: [
              { name: "Rome", path: "Photos/Trips/Rome", isDirectory: true, size: null, modifiedAt: "2026-01-01T00:00:00Z" },
              { name: "plan.txt", path: "Photos/Trips/plan.txt", isDirectory: false, size: 4, modifiedAt: "2026-01-01T00:00:00Z" },
            ],
            skippedCount: 0,
            indexedAt: "",
          });
    const panel = list();
    const rome = (await screen.findByText("Rome")).closest("tr")!;
    const plan = screen.getByText("plan.txt").closest("tr")!;

    fireEvent.dragEnter(panel, carrying(fileEntry("colosseum.jpg")));
    fireEvent.dragOver(within(rome).getByText("Rome"), carrying(fileEntry("colosseum.jpg")));
    // The folder lights up, and the banner names it.
    expect(rome).toHaveClass("drop-target");
    expect(screen.getByText("Drop to add to Rome")).toBeInTheDocument();
    // Over a file, it's the folder on screen again.
    fireEvent.dragOver(within(plan).getByText("plan.txt"), carrying(fileEntry("colosseum.jpg")));
    expect(rome).not.toHaveClass("drop-target");
    expect(screen.getByText("Drop to add to Trips")).toBeInTheDocument();

    await act(async () => {
      fireEvent.drop(within(rome).getByText("Rome"), carrying(fileEntry("colosseum.jpg")));
    });
    await waitFor(() => expect(onDropFiles).toHaveBeenCalledTimes(1));
    expect(onDropFiles.mock.calls[0][0]).toBe("Photos/Trips/Rome");
    expect(rome).not.toHaveClass("drop-target");

    await act(async () => {
      fireEvent.drop(within(plan).getByText("plan.txt"), carrying(fileEntry("ticket.pdf")));
    });
    await waitFor(() => expect(onDropFiles).toHaveBeenCalledTimes(2));
    expect(onDropFiles.mock.calls[1][0]).toBe("Photos/Trips");
  });

  it("says why, above the list, when Uncloud won't take what was dropped", async () => {
    onDropFiles.mockRejectedValue(new Error("This would add 4 GB and only 1 GB is free on your Uncloud drive."));
    const panel = list();
    await screen.findByText("This folder is empty");

    await act(async () => {
      fireEvent.drop(panel, carrying(fileEntry("film.mov")));
    });

    expect(await screen.findByRole("alert")).toHaveTextContent("only 1 GB is free");
  });

  it("leaves alone a drag that isn't carrying files", async () => {
    const panel = list();
    await screen.findByText("This folder is empty");

    fireEvent.dragEnter(panel, { dataTransfer: { types: ["text/plain"], items: [], files: [] } });

    expect(screen.queryByText(/Drop to add/)).not.toBeInTheDocument();
  });
});
