import { useCallback, useEffect, useRef, useState } from "react";
import {
  ChevronRight,
  CircleCheck,
  CircleStop,
  FolderSync,
  LoaderCircle,
  TriangleAlert,
  X,
} from "lucide-react";
import { api, formatSize, nameTogether, SignedOutError } from "./api";
import type { EditedEntry, UploadComparison } from "./api";

/** One file chosen on this device, and where it sits in what was chosen: its name, or its place in a folder. */
export interface Picked {
  file: File;
  path: string;
}

/** Hidden files stay behind, as they do everywhere in Uncloud: a name starting with a dot is never shown. */
const hidden = (path: string) => path.split("/").some((part) => part.startsWith("."));

/** What a file input chose. A folder's files come with where each sits in it; loose files with just their names. */
export function pickedFrom(files: FileList | File[]): Picked[] {
  return Array.from(files).map((file) => ({ file, path: file.webkitRelativePath || file.name }));
}

/**
 * What was dropped: files, and folders with everything in them. A browser hands over a dropped
 * folder as something to walk rather than as its files, and only while the drop is happening, so
 * this has to be called from the drop itself — the entries are taken at once and walked after.
 */
export function pickedFromDrop(transfer: DataTransfer): Promise<Picked[]> {
  const entries = Array.from(transfer.items)
    .filter((item) => item.kind === "file")
    .map((item) => item.webkitGetAsEntry?.() ?? null);
  // A browser that can't hand over entries hands over the files themselves, folders left out.
  if (entries.length === 0 || entries.some((entry) => entry === null))
    return Promise.resolve(pickedFrom(transfer.files));
  return (async () => {
    const picked: Picked[] = [];
    for (const entry of entries) await walk(entry!, "", picked);
    return picked;
  })();
}

async function walk(entry: FileSystemEntry, above: string, picked: Picked[]) {
  // Not walked at all: a hidden folder can hold a great deal — a .git, say — none of which is kept.
  if (entry.name.startsWith(".")) return;
  const path = above ? `${above}/${entry.name}` : entry.name;
  if (entry.isFile) {
    try {
      const file = await new Promise<File>((resolve, reject) =>
        (entry as FileSystemFileEntry).file(resolve, reject),
      );
      picked.push({ file, path });
    } catch {
      // Gone, or not readable, since it was dropped: there is nothing to send.
    }
  } else if (entry.isDirectory) {
    const reader = (entry as FileSystemDirectoryEntry).createReader();
    // A folder is read a batch at a time, until a batch comes back empty.
    for (;;) {
      const batch = await new Promise<FileSystemEntry[]>((resolve, reject) =>
        reader.readEntries(resolve, reject),
      );
      if (batch.length === 0) break;
      for (const child of batch) await walk(child, path, picked);
    }
  }
}

export type UploadStage = "checking" | "review" | "sending" | "placing" | "done" | "stopped" | "failed";

/** What was chosen that's partly in the folder already, for whoever chose it to decide about. */
export interface Review {
  /** Files that aren't there yet. */
  fresh: string[];
  /** Files there already that this device has a different copy of. */
  changed: string[];
  /** How many are there already, just the same. */
  same: number;
  /** Files changed there since this device's copy was, which bringing it up to date leaves alone. */
  newer: string[];
  /** Whether all of it is there already, rather than some. */
  whole: boolean;
  /** Whether more than one thing was chosen. */
  several: boolean;
}

/** Files on their way from this device, or how that went. */
export interface Upload {
  id: string;
  label: string;
  /** The folder in My files they go into. */
  folder: string;
  files: number;
  bytes: number;
  /** Files dealt with so far, whether they arrived or couldn't be added. */
  doneFiles: number;
  /** Bytes dealt with so far, the same way, counting what's partway there. */
  sentBytes: number;
  /** What actually arrived, which is what the outcome reports. */
  arrivedBytes: number;
  stage: UploadStage;
  error: string | null;
  /** Where each thing that was chosen ended up, once it's in place. */
  placed: EditedEntry[];
  /** Where "Show them" goes: into the one folder that was chosen, or else where they all went. */
  openTo: string;
  skipped: { path: string; reason: string }[];
  /** While somebody decides: what of it is there already. */
  review: Review | null;
  /** Files already there that it took the place of, which are in the bin. */
  replaced: number;
  /** Files left out because they were there already, the same or changed there since. */
  unchanged: number;
}

/** Several files at once, so a folder of small ones doesn't wait on a request per file. */
const AT_ONCE = 3;
/** A file whose request fails partway is tried this many times before the connection is blamed. */
const ATTEMPTS = 3;

class Refused extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

/**
 * Sends one file. Not through fetch, because only XMLHttpRequest says how far along a request
 * body is, and for a large video that is the difference between a bar that moves and one that doesn't.
 */
function send(
  id: string,
  item: Picked,
  onProgress: (loaded: number) => void,
  requests: Set<XMLHttpRequest>,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    requests.add(request);
    request.open(
      "PUT",
      `/api/uploads/${encodeURIComponent(id)}?${new URLSearchParams({
        path: item.path,
        modified: String(item.file.lastModified),
      })}`,
    );
    request.setRequestHeader("X-Homebase-Request", "1");
    request.setRequestHeader("Content-Type", "application/octet-stream");
    request.upload.onprogress = (event) => onProgress(event.loaded);
    request.onload = () => {
      requests.delete(request);
      if (request.status >= 200 && request.status < 300) return resolve();
      let detail: string | undefined;
      try {
        detail = (JSON.parse(request.responseText) as { detail?: string }).detail;
      } catch {
        // Not a problem Uncloud described; the status says enough.
      }
      reject(new Refused(detail ?? `Uncloud couldn’t take this file (${request.status}).`, request.status));
    };
    request.onerror = () => {
      requests.delete(request);
      reject(new Refused("", 0));
    };
    request.onabort = () => {
      requests.delete(request);
      reject(new Refused("", -1));
    };
    request.send(item.file);
  });
}

/** Whether this device can still read a file, which is the other reason a request can fail without an answer. */
async function readable(file: File): Promise<boolean> {
  try {
    await file.slice(0, 1).arrayBuffer();
    return true;
  } catch {
    return false;
  }
}

const pause = (ms: number) => new Promise((resolve) => window.setTimeout(resolve, ms));

async function discard(id: string) {
  try {
    await api(`/uploads/${encodeURIComponent(id)}`, { method: "DELETE" });
  } catch {
    // Whatever arrived is cleared away by itself a day later.
  }
}

const isRunning = (upload: Upload) => upload.stage === "sending" || upload.stage === "placing";
/** Started, and not yet over: on its way, or still being asked about or decided on. */
const isUnder = (upload: Upload) =>
  isRunning(upload) || upload.stage === "checking" || upload.stage === "review";

/** One upload as it runs: the requests in flight, and how far each has got. */
interface Run {
  stopped: boolean;
  requests: Set<XMLHttpRequest>;
  loaded: Map<string, number>;
  /** What's waiting while somebody decides what to do about the part of it that's there already. */
  deciding: { folder: string; label: string; items: Picked[]; differ: Picked[] } | null;
}

/** The folder something went into, as somebody would call it. */
const called = (folder: string) => folder.split("/").at(-1) || "My files";

/**
 * Files on their way from this device into My files, watched from My files like an import. Unlike
 * an import, this page is what sends them, so it lasts only as long as the page does — and says so.
 */
export function useUpload(active: boolean, onUploaded: () => void) {
  const [upload, setUpload] = useState<Upload | null>(null);
  const current = useRef<Run | null>(null);
  const sending = upload != null && isRunning(upload);
  const running = upload != null && isUnder(upload);

  // Signing out ends whatever was on its way, and whoever signs in next doesn't see what it was.
  useEffect(() => {
    if (active) return;
    const run = current.current;
    if (run) {
      run.stopped = true;
      run.requests.forEach((request) => request.abort());
      // Nothing is on its way while somebody decides, so nothing else would let go of it.
      if (run.deciding) current.current = null;
    }
    setUpload(null);
  }, [active]);

  // Leaving now would stop it, so the browser asks first.
  useEffect(() => {
    if (!sending) return;
    const stay = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", stay);
    return () => window.removeEventListener("beforeunload", stay);
  }, [sending]);

  /**
   * Sends files once it's settled which: all of what was chosen, or — bringing a folder added again
   * up to date — only what differs from what's there. Settles once Uncloud has agreed to take them.
   */
  const begin = useCallback(
    async (run: Run, folder: string, label: string, items: Picked[], merge: boolean, unchanged: number) => {
      const bytes = items.reduce((sum, item) => sum + item.file.size, 0);
      let id: string;
      try {
        // Asked first, so a drive without room — or a folder that's gone — says so before anything is sent.
        ({ id } = await api<{ id: string }>("/uploads", {
          method: "POST",
          body: JSON.stringify({ destination: folder, bytes }),
        }));
      } catch (problem: unknown) {
        current.current = null;
        throw problem;
      }
      // Signed out while Uncloud was being asked: nobody is here to send them.
      if (run.stopped) {
        current.current = null;
        void discard(id);
        return;
      }
      const update = (change: Partial<Upload>) =>
        setUpload((value) => (value?.id === id ? { ...value, ...change } : value));
      setUpload({
        id,
        label,
        folder,
        files: items.length,
        bytes,
        doneFiles: 0,
        sentBytes: 0,
        arrivedBytes: 0,
        stage: "sending",
        error: null,
        placed: [],
        openTo: folder,
        skipped: [],
        review: null,
        replaced: 0,
        unchanged,
      });

      void (async () => {
        try {
          let next = 0;
          let doneFiles = 0;
          let doneBytes = 0;
          let arrived = 0;
          let arrivedBytes = 0;
          let fatal: string | null = null;
          const skipped: Upload["skipped"] = [];
          // What was last shown, so a tick with nothing new doesn't redraw the page under it.
          let shown = "";
          const progress = () => {
            let inFlight = 0;
            for (const loaded of run.loaded.values()) inFlight += loaded;
            const sentBytes = doneBytes + inFlight;
            const now = `${doneFiles}:${sentBytes}:${skipped.length}`;
            if (now === shown) return;
            shown = now;
            update({ doneFiles, sentBytes, arrivedBytes, skipped: [...skipped] });
          };
          const timer = window.setInterval(progress, 250);

          const worker = async () => {
            while (!run.stopped && fatal === null && next < items.length) {
              const item = items[next++];
              for (let attempt = 1; ; attempt++) {
                // Somebody pressed Stop, or another file found the upload can't go on, while this one waited.
                if (run.stopped || fatal !== null) return;
                try {
                  await send(id, item, (loaded) => run.loaded.set(item.path, loaded), run.requests);
                  arrived++;
                  arrivedBytes += item.file.size;
                  break;
                } catch (error) {
                  run.loaded.delete(item.path);
                  if (run.stopped || fatal !== null) return;
                  const refused = error instanceof Refused ? error : new Refused(String(error), 0);
                  // A name Uncloud won't keep is this file's problem, not the upload's.
                  if (refused.status === 400) {
                    skipped.push({ path: item.path, reason: refused.message });
                    break;
                  }
                  if (refused.status === 0 || refused.status >= 500) {
                    if (!(await readable(item.file))) {
                      skipped.push({
                        path: item.path,
                        reason: "This device couldn’t read it. It may have been moved or deleted after it was chosen.",
                      });
                      break;
                    }
                    if (attempt < ATTEMPTS) {
                      await pause(attempt * 1000);
                      continue;
                    }
                    fatal = "Uncloud stopped answering, so nothing was added. Check you’re still connected, then try again.";
                  } else
                    fatal =
                      refused.status === 401
                        ? "You were signed out, so nothing was added. Sign in, then add them again."
                        : refused.message || "The upload was interrupted, so nothing was added.";
                  run.requests.forEach((request) => request.abort());
                  return;
                }
              }
              run.loaded.delete(item.path);
              doneFiles++;
              doneBytes += item.file.size;
            }
          };
          await Promise.all(Array.from({ length: Math.min(AT_ONCE, items.length) }, worker));
          window.clearInterval(timer);
          progress();

          if (run.stopped) {
            await discard(id);
            update({ stage: "stopped" });
            return;
          }
          if (fatal !== null || arrived === 0) {
            await discard(id);
            update({
              stage: "failed",
              error: fatal ?? `None of ${items.length === 1 ? "it" : "these"} could be added.`,
            });
            return;
          }
          update({ stage: "placing" });
          try {
            const { items: placed, replaced } = await api<{ items: EditedEntry[]; replaced?: number }>(
              `/uploads/${encodeURIComponent(id)}/finish`,
              { method: "POST", body: JSON.stringify({ destination: folder, merge }) },
            );
            // A merge also names what deeper in had to take a new name; only the top says where it went.
            const tops = placed.filter((entry) => !entry.from.includes("/"));
            const one = tops.length === 1 ? tops[0] : null;
            update({
              stage: "done",
              placed,
              replaced: replaced ?? 0,
              openTo:
                one && items.some((item) => item.path.startsWith(`${one.from}/`)) ? one.to : folder,
            });
            onUploaded();
          } catch (problem: unknown) {
            await discard(id);
            // No answer at all, rather than a refusal: Uncloud may well have put them in place and
            // only the reply was lost. Saying nothing was added would have them added twice.
            const unanswered = problem instanceof TypeError;
            if (unanswered) onUploaded();
            update({
              stage: "failed",
              error: unanswered
                ? "Uncloud didn’t answer while putting them in place, so they may already be in your files. Look there before adding them again."
                : problem instanceof SignedOutError
                  ? "You were signed out, so nothing was added. Sign in, then add them again."
                  : problem instanceof Error
                    ? problem.message
                    : "The files arrived but couldn’t be put in place, so nothing was added.",
            });
          }
        } finally {
          // However it ended, the next upload can start.
          if (current.current === run) current.current = null;
        }
      })();
    },
    [onUploaded],
  );

  const start = useCallback(
    async (folder: string, chosen: Picked[]) => {
      const items = chosen.filter((item) => !hidden(item.path));
      if (items.length === 0)
        throw new Error(
          chosen.length > 0
            ? "There’s nothing here to add. Uncloud leaves hidden files out, and that’s all there was."
            : "Nothing was chosen.",
        );
      // One at a time, claimed before Uncloud is even asked: a second drop while the first is still
      // being agreed to would otherwise start an upload nobody could see or stop.
      if (current.current)
        throw new Error("Some files are still on their way. You can add more once they’re in.");
      const run: Run = { stopped: false, requests: new Set(), loaded: new Map(), deciding: null };
      current.current = run;
      const tops = [...new Set(items.map((item) => item.path.split("/")[0]))];
      const label = nameTogether(tops);
      const bytes = items.reduce((sum, item) => sum + item.file.size, 0);
      const waiting: Upload = {
        id: "",
        label,
        folder,
        files: items.length,
        bytes,
        doneFiles: 0,
        sentBytes: 0,
        arrivedBytes: 0,
        stage: "checking",
        error: null,
        placed: [],
        openTo: folder,
        skipped: [],
        review: null,
        replaced: 0,
        unchanged: 0,
      };
      setUpload(waiting);
      let there: UploadComparison;
      try {
        // What of it is in the folder already, so that a folder added again need only send what's
        // new or changed in it — the way everything about a file is known before it's sent.
        there = await api<UploadComparison>("/uploads/compare", {
          method: "POST",
          body: JSON.stringify({
            destination: folder,
            files: items.map((item) => ({
              path: item.path,
              bytes: item.file.size,
              modified: item.file.lastModified,
            })),
          }),
        });
        if (run.stopped) {
          current.current = null;
          return;
        }
        // None of it is there, so there's nothing to decide: it all goes, as it always has.
        if (there.same.length + there.changed.length + there.newer.length + there.folders.length === 0)
          return await begin(run, folder, label, items, false, 0);
      } catch (problem: unknown) {
        current.current = null;
        setUpload((value) => (value === waiting ? null : value));
        throw problem;
      }
      const same = new Set(there.same);
      const newer = new Set(there.newer);
      const changed = new Set(there.changed);
      const differ = items.filter((item) => !same.has(item.path) && !newer.has(item.path));
      const found = new Set([...there.folders, ...there.same, ...there.changed, ...there.newer]);
      run.deciding = { folder, label, items, differ };
      setUpload({
        ...waiting,
        stage: "review",
        review: {
          fresh: differ.filter((item) => !changed.has(item.path)).map((item) => item.path),
          changed: there.changed,
          same: there.same.length,
          newer: there.newer,
          whole: tops.every((top) => found.has(top)),
          several: tops.length > 1,
        },
      });
    },
    [begin],
  );

  /**
   * What to do about something chosen that's partly there already: bring what's there up to date
   * with it — only what's new or changed is sent, and a changed file takes the place of the one
   * there — or add all of it again beside what's there, the way Finder keeps both.
   */
  const decide = useCallback(
    (merge: boolean) => {
      const run = current.current;
      const waiting = run?.deciding;
      if (!run || !waiting) return;
      run.deciding = null;
      const items = merge ? waiting.differ : waiting.items;
      void begin(run, waiting.folder, waiting.label, items, merge, waiting.items.length - items.length).catch(
        (problem: unknown) =>
          setUpload((value) =>
            value?.stage === "review"
              ? {
                  ...value,
                  stage: "failed",
                  review: null,
                  error:
                    problem instanceof SignedOutError
                      ? "You were signed out, so nothing was added. Sign in, then add them again."
                      : problem instanceof Error
                        ? problem.message
                        : `Couldn’t add ${waiting.label}.`,
                }
              : value,
          ),
      );
    },
    [begin],
  );

  const stop = useCallback(() => {
    const run = current.current;
    if (!run) return;
    run.stopped = true;
    run.requests.forEach((request) => request.abort());
  }, []);

  const dismiss = useCallback(() => {
    // Not deciding is deciding not to add them.
    if (current.current?.deciding) {
      current.current = null;
      setUpload(null);
      return;
    }
    setUpload((value) => (value && !isUnder(value) ? null : value));
  }, []);

  return { upload, running, start, decide, stop, dismiss };
}

interface Props {
  upload: Upload;
  onStop: () => void;
  onDismiss: () => void;
  onOpen: (path: string) => void;
  /** Bring what's there up to date (true), or keep both (false), when some of it is there already. */
  onDecide: (merge: boolean) => void;
}

/** The most of a list worth showing, before it's only a count. */
const LISTED = 100;

/** How an upload from this device is going, above the files it's on its way to. */
export default function UploadStatus({ upload, onStop, onDismiss, onOpen, onDecide }: Props) {
  const [stopping, setStopping] = useState(false);
  const [showProblems, setShowProblems] = useState(false);

  if (upload.stage === "checking")
    return (
      <section className="import-progress" aria-live="polite">
        <div className="import-progress-head">
          <strong>
            <LoaderCircle size={16} className="spin" />
            Looking at what’s already in {called(upload.folder)}…
          </strong>
        </div>
      </section>
    );

  if (upload.stage === "review" && upload.review)
    return <Decision upload={upload} review={upload.review} onDecide={onDecide} onDismiss={onDismiss} />;

  if (isRunning(upload)) {
    const share = upload.bytes > 0 ? upload.sentBytes / upload.bytes : upload.doneFiles / upload.files;
    return (
      <section className="import-progress" aria-live="polite">
        <div className="import-progress-head">
          <strong>
            <LoaderCircle size={16} className="spin" />
            {upload.stage === "placing"
              ? `Putting ${upload.label} in place…`
              : `Adding ${upload.label} from this device`}
          </strong>
          {upload.stage === "sending" && (
            <button
              className="button secondary refresh-button"
              onClick={() => {
                setStopping(true);
                onStop();
              }}
              disabled={stopping}
            >
              <CircleStop size={15} />
              {stopping ? "Stopping…" : "Stop"}
            </button>
          )}
        </div>
        <div
          className="progress-track"
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(share * 100)}
          aria-valuetext={`${upload.doneFiles} of ${upload.files} files`}
        >
          <div className="progress-fill" style={{ width: `${Math.round(share * 100)}%` }} />
        </div>
        <span className="muted">
          {upload.doneFiles} of {upload.files} file{upload.files === 1 ? "" : "s"} ·{" "}
          {formatSize(upload.sentBytes)} of {formatSize(upload.bytes)} · Keep this page open until
          they’re in.
        </span>
      </section>
    );
  }

  const arrived = upload.files - upload.skipped.length;
  const renamed = upload.placed.filter((entry) => entry.from !== entry.to.split("/").at(-1));
  const message =
    upload.stage === "failed"
      ? (upload.error ?? `Couldn’t add ${upload.label}.`)
      : upload.stage === "stopped"
        ? `Stopped adding ${upload.label}. Nothing from it was added.`
        : `Added ${arrived} file${arrived === 1 ? "" : "s"} from this device (${formatSize(upload.arrivedBytes)}).` +
          (upload.replaced === 1
            ? " One took the place of an older copy, which is in the bin."
            : upload.replaced > 1
              ? ` ${upload.replaced} took the place of older copies, which are in the bin.`
              : "") +
          (upload.unchanged > 0
            ? ` ${upload.unchanged} ${upload.unchanged === 1 ? "was" : "were"} already there, so ${upload.unchanged === 1 ? "it wasn’t" : "they weren’t"} sent again.`
            : "") +
          (renamed.length === 1
            ? ` There was already something called “${renamed[0].from}” there, so this one is “${renamed[0].to.split("/").at(-1)}”.`
            : renamed.length > 1
              ? ` ${renamed.length} names were already taken there, so those arrived with a number after them.`
              : "");

  return (
    <section
      className={`import-progress import-outcome${upload.stage === "failed" ? " failed" : ""}`}
      role="status"
    >
      <div className="import-progress-head">
        <strong>
          {upload.stage === "failed" || upload.skipped.length > 0 ? (
            <TriangleAlert size={16} />
          ) : (
            <CircleCheck size={16} />
          )}
          {message}
        </strong>
        <span className="import-outcome-actions">
          {upload.stage === "done" && (
            <button className="button secondary refresh-button" onClick={() => onOpen(upload.openTo)}>
              Show them
              <ChevronRight size={14} />
            </button>
          )}
          <button className="icon-button" onClick={onDismiss} aria-label="Dismiss" title="Dismiss">
            <X size={16} />
          </button>
        </span>
      </div>
      {upload.skipped.length > 0 && (
        <>
          <button
            className="link-button"
            onClick={() => setShowProblems((open) => !open)}
            aria-expanded={showProblems}
          >
            {upload.skipped.length} file{upload.skipped.length === 1 ? "" : "s"} couldn’t be added
            {showProblems ? " — hide" : " — see which"}
          </button>
          {showProblems && (
            <ul className="import-list">
              {upload.skipped.map((skip) => (
                <li key={skip.path}>
                  <TriangleAlert size={18} strokeWidth={1.6} />
                  <div>
                    <strong>{skip.path}</strong>
                    <span className="muted">{skip.reason}</span>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}

interface DecisionProps {
  upload: Upload;
  review: Review;
  onDecide: (merge: boolean) => void;
  onDismiss: () => void;
}

const files = (count: number) => `${count} ${count === 1 ? "file" : "files"}`;

/**
 * Some of what was chosen is in the folder already — a folder added again, usually, with something
 * new in it. Said before anything is sent: what's new, what's changed and what's the same, and
 * whether to bring the copy there up to date or keep both.
 */
function Decision({ upload, review, onDecide, onDismiss }: DecisionProps) {
  const [chosen, setChosen] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const differ = review.fresh.length + review.changed.length;
  const where = called(upload.folder);
  const listed = [
    ...review.fresh.map((path) => ({ path, note: "New" })),
    ...review.changed.map((path) => ({
      path,
      note: "Changed. This copy takes the place of the one there, which goes to the bin.",
    })),
    ...review.newer.map((path) => ({
      path,
      note: "Changed in Uncloud since this copy was, so it’s left as it is.",
    })),
  ];
  const decide = (merge: boolean) => {
    setChosen(true);
    onDecide(merge);
  };

  return (
    <section className="import-progress import-outcome import-decision" role="status">
      <div className="import-progress-head">
        <strong>
          <FolderSync size={16} />
          {review.whole
            ? `${differ === 0 ? "Nothing new to add: " : ""}${upload.label} ${review.several ? "are" : "is"} already in ${where}.`
            : `Some of what you chose is already in ${where}.`}
        </strong>
        <span className="import-outcome-actions">
          <button className="icon-button" onClick={onDismiss} aria-label="Cancel" title="Cancel" disabled={chosen}>
            <X size={16} />
          </button>
        </span>
      </div>
      <span className="muted">
        {differ > 0
          ? `${files(differ)} to add: ${[
              review.fresh.length > 0 && `${review.fresh.length} new`,
              review.changed.length > 0 && `${review.changed.length} changed`,
            ]
              .filter(Boolean)
              .join(" and ")}. `
          : ""}
        {review.same > 0 &&
          `${files(review.same)} ${review.same === 1 ? "is" : "are"} the same in both and won’t be sent again. `}
        {review.newer.length > 0 &&
          `${files(review.newer.length)} ${review.newer.length === 1 ? "was" : "were"} changed in Uncloud since, so ${review.newer.length === 1 ? "it’s" : "they’re"} left as ${review.newer.length === 1 ? "it is" : "they are"}. `}
        {review.changed.length > 0 && "A changed file takes the place of the one there, which goes to the bin."}
      </span>
      <div className="card-actions">
        {differ > 0 && (
          <button className="button primary" onClick={() => decide(true)} disabled={chosen}>
            Add {files(differ)}
          </button>
        )}
        <button
          className="button secondary"
          onClick={() => decide(false)}
          disabled={chosen}
          title="Add all of it again, beside what’s there, under new names"
        >
          Keep both
        </button>
        {listed.length > 0 && (
          <button className="link-button" onClick={() => setShowFiles((open) => !open)} aria-expanded={showFiles}>
            {showFiles ? "Hide which" : "See which"}
          </button>
        )}
      </div>
      {showFiles && (
        <ul className="import-list">
          {listed.slice(0, LISTED).map((item) => (
            <li key={item.path}>
              <FolderSync size={18} strokeWidth={1.6} />
              <div>
                <strong>{item.path}</strong>
                <span className="muted">{item.note}</span>
              </div>
            </li>
          ))}
          {listed.length > LISTED && (
            <li className="muted">and {listed.length - LISTED} more</li>
          )}
        </ul>
      )}
    </section>
  );
}
