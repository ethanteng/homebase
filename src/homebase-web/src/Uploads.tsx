import { useCallback, useEffect, useRef, useState } from "react";
import {
  ChevronRight,
  CircleCheck,
  CircleStop,
  Clock,
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

export type UploadStage =
  | "waiting"
  | "checking"
  | "review"
  | "sending"
  | "placing"
  | "done"
  | "stopped"
  | "failed";

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

/** Files on their way from this device, or waiting their turn, or how that went. */
export interface Upload {
  /** This page's own name for it, from the moment it's chosen. */
  key: string;
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
/** Still to be sent, or being sent: what leaving the page now would lose. */
const isPending = (upload: Upload) =>
  isRunning(upload) || upload.stage === "waiting" || upload.stage === "checking";

/** One upload, from being chosen until it's over. */
interface Job {
  key: string;
  folder: string;
  label: string;
  /** Everything chosen, hidden files left out. */
  items: Picked[];
  /** What to send, once that's settled: all of it, or only what differs from what's there. */
  plan: { items: Picked[]; merge: boolean } | null;
  /** While somebody decides what to do about the part of it that's there already: what differs. */
  differ: Picked[] | null;
  /** Begun with Uncloud, after which it can be stopped but not simply taken away. */
  begun: boolean;
  stopped: boolean;
  /** Ends the question about what's already there, when this is taken away or the account signs out. */
  cancel: AbortController;
  /** The requests in flight, and how far each has got. */
  requests: Set<XMLHttpRequest>;
  loaded: Map<string, number>;
}

/** The folder something went into, as somebody would call it. */
const called = (folder: string) => folder.split("/").at(-1) || "My files";

const signedOut = "You were signed out, so nothing was added. Sign in, then add them again.";

/**
 * Files on their way from this device into My files, watched from My files like an import. Unlike
 * an import, this page is what sends them, so they last only as long as the page does — and it says
 * so. More can be added at any time: each waits its turn and goes once those ahead of it are in, one
 * at a time, so each is checked against what's there by then and none competes with another for the
 * room left.
 */
export function useUpload(active: boolean, onUploaded: () => void) {
  const [uploads, setUploads] = useState<Upload[]>([]);
  // In the order they were added, which is the order they go in.
  const jobs = useRef(new Map<string, Job>());
  const working = useRef(false);
  const added = useRef(0);
  const running = uploads.some(isPending);

  const update = useCallback(
    (key: string, change: Partial<Upload>) =>
      setUploads((list) => list.map((upload) => (upload.key === key ? { ...upload, ...change } : upload))),
    [],
  );

  // Signing out ends whatever was on its way or waiting, and whoever signs in next doesn't see any of it.
  useEffect(() => {
    if (active) return;
    for (const job of jobs.current.values()) {
      job.stopped = true;
      job.cancel.abort();
      job.requests.forEach((request) => request.abort());
    }
    jobs.current.clear();
    setUploads([]);
  }, [active]);

  // Leaving now would stop it, and lose whatever is waiting, so the browser asks first.
  useEffect(() => {
    if (!running) return;
    const stay = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", stay);
    return () => window.removeEventListener("beforeunload", stay);
  }, [running]);

  /**
   * Sends what's been settled on: all of what was chosen, or — bringing a folder added again up to
   * date — only what differs from what's there. Over once it's in place, or failed, or stopped.
   */
  const deliver = useCallback(
    async (job: Job, items: Picked[], merge: boolean) => {
      const { key, folder } = job;
      job.begun = true;
      update(key, { stage: "sending" });
      const bytes = items.reduce((sum, item) => sum + item.file.size, 0);
      let id: string;
      try {
        // Asked first, so a drive without room — or a folder that's gone — says so before anything is sent.
        ({ id } = await api<{ id: string }>("/uploads", {
          method: "POST",
          body: JSON.stringify({ destination: folder, bytes }),
        }));
      } catch (problem: unknown) {
        update(key, {
          stage: "failed",
          error:
            problem instanceof SignedOutError
              ? signedOut
              : problem instanceof Error
                ? problem.message
                : `Couldn’t add ${job.label}.`,
        });
        return;
      }
      // Stopped, or signed out, while Uncloud was being asked: nobody wants them sent.
      if (job.stopped) {
        await discard(id);
        update(key, { stage: "stopped" });
        return;
      }

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
        for (const loaded of job.loaded.values()) inFlight += loaded;
        const sentBytes = doneBytes + inFlight;
        const now = `${doneFiles}:${sentBytes}:${skipped.length}`;
        if (now === shown) return;
        shown = now;
        update(key, { doneFiles, sentBytes, arrivedBytes, skipped: [...skipped] });
      };
      const timer = window.setInterval(progress, 250);

      const worker = async () => {
        while (!job.stopped && fatal === null && next < items.length) {
          const item = items[next++];
          for (let attempt = 1; ; attempt++) {
            // Somebody pressed Stop, or another file found the upload can't go on, while this one waited.
            if (job.stopped || fatal !== null) return;
            try {
              await send(id, item, (loaded) => job.loaded.set(item.path, loaded), job.requests);
              arrived++;
              arrivedBytes += item.file.size;
              break;
            } catch (error) {
              job.loaded.delete(item.path);
              if (job.stopped || fatal !== null) return;
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
                    ? signedOut
                    : refused.message || "The upload was interrupted, so nothing was added.";
              job.requests.forEach((request) => request.abort());
              return;
            }
          }
          job.loaded.delete(item.path);
          doneFiles++;
          doneBytes += item.file.size;
        }
      };
      await Promise.all(Array.from({ length: Math.min(AT_ONCE, items.length) }, worker));
      window.clearInterval(timer);
      progress();

      if (job.stopped) {
        await discard(id);
        update(key, { stage: "stopped" });
        return;
      }
      if (fatal !== null || arrived === 0) {
        await discard(id);
        update(key, {
          stage: "failed",
          error: fatal ?? `None of ${items.length === 1 ? "it" : "these"} could be added.`,
        });
        return;
      }
      update(key, { stage: "placing" });
      try {
        const { items: placed, replaced } = await api<{ items: EditedEntry[]; replaced?: number }>(
          `/uploads/${encodeURIComponent(id)}/finish`,
          { method: "POST", body: JSON.stringify({ destination: folder, merge }) },
        );
        // A merge also names what deeper in had to take a new name; only the top says where it went.
        const tops = placed.filter((entry) => !entry.from.includes("/"));
        const one = tops.length === 1 ? tops[0] : null;
        update(key, {
          stage: "done",
          placed,
          replaced: replaced ?? 0,
          openTo: one && items.some((item) => item.path.startsWith(`${one.from}/`)) ? one.to : folder,
        });
        onUploaded();
      } catch (problem: unknown) {
        await discard(id);
        // No answer at all, rather than a refusal: Uncloud may well have put them in place and
        // only the reply was lost. Saying nothing was added would have them added twice.
        const unanswered = problem instanceof TypeError;
        if (unanswered) onUploaded();
        update(key, {
          stage: "failed",
          error: unanswered
            ? "Uncloud didn’t answer while putting them in place, so they may already be in your files. Look there before adding them again."
            : problem instanceof SignedOutError
              ? signedOut
              : problem instanceof Error
                ? problem.message
                : "The files arrived but couldn’t be put in place, so nothing was added.",
        });
      }
    },
    [onUploaded, update],
  );

  /**
   * Takes one upload as far as it can go without anybody: asks what of it is there already, and
   * then either sends it or leaves it for somebody to decide about.
   */
  const advance = useCallback(
    async (job: Job) => {
      // Already taken away: asking again would hold the line, and would never finish.
      if (job.stopped) {
        jobs.current.delete(job.key);
        return;
      }
      // Asked about again when it was decided on to bring what's there up to date: while it waited
      // for that answer, the ones behind it went ahead and may have brought some of the same in.
      if (!job.plan || job.plan.merge) {
        update(job.key, { stage: "checking" });
        let there: UploadComparison;
        try {
          // What of it is in the folder already, asked when its turn comes rather than when it was
          // chosen, so what went ahead of it counts — and a folder added again need only send
          // what's new or changed in it.
          there = await api<UploadComparison>("/uploads/compare", {
            method: "POST",
            signal: job.cancel.signal,
            body: JSON.stringify({
              destination: job.folder,
              files: (job.plan?.items ?? job.items).map((item) => ({
                path: item.path,
                bytes: item.file.size,
                modified: item.file.lastModified,
              })),
            }),
          });
        } catch (problem: unknown) {
          jobs.current.delete(job.key);
          // Taken away, or signed out, while Uncloud was being asked. Not a failure: nothing of it
          // was sent, and the ones behind it can go. The panel is already gone.
          if (job.stopped) return;
          update(job.key, {
            stage: "failed",
            error:
              problem instanceof SignedOutError
                ? signedOut
                : problem instanceof Error
                  ? problem.message
                  : `Couldn’t add ${job.label}.`,
          });
          return;
        }
        // Taken away, or signed out, while Uncloud was being asked. Left in the line it would be
        // asked again, forever, and hold up the ones behind it.
        if (job.stopped) {
          jobs.current.delete(job.key);
          return;
        }
        if (job.plan) {
          // Already decided: what's the same there now, or changed there since, isn't sent after all.
          const settled = new Set([...there.same, ...there.newer]);
          const items = job.plan.items.filter((item) => !settled.has(item.path));
          job.plan = { items, merge: true };
          update(job.key, {
            files: items.length,
            bytes: items.reduce((sum, item) => sum + item.file.size, 0),
            unchanged: job.items.length - items.length,
          });
          if (items.length === 0) {
            jobs.current.delete(job.key);
            update(job.key, { stage: "done" });
            return;
          }
        }
        // None of it is there, so there's nothing to decide: it all goes, as it always has.
        else if (there.same.length + there.changed.length + there.newer.length + there.folders.length === 0)
          job.plan = { items: job.items, merge: false };
        else {
          const same = new Set(there.same);
          const newer = new Set(there.newer);
          const changed = new Set(there.changed);
          const tops = [...new Set(job.items.map((item) => item.path.split("/")[0]))];
          const found = new Set([...there.folders, ...there.same, ...there.changed, ...there.newer]);
          job.differ = job.items.filter((item) => !same.has(item.path) && !newer.has(item.path));
          update(job.key, {
            stage: "review",
            review: {
              fresh: job.differ.filter((item) => !changed.has(item.path)).map((item) => item.path),
              changed: there.changed,
              same: there.same.length,
              newer: there.newer,
              whole: tops.every((top) => found.has(top)),
              several: tops.length > 1,
            },
          });
          // It waits for an answer; the ones behind it needn't.
          return;
        }
      }
      await deliver(job, job.plan.items, job.plan.merge);
      jobs.current.delete(job.key);
    },
    [deliver, update],
  );

  /** Works through what's waiting, in order and one at a time, skipping any waiting on an answer. */
  const pump = useCallback(async () => {
    if (working.current) return;
    working.current = true;
    try {
      for (;;) {
        const job = [...jobs.current.values()].find((candidate) => candidate.differ === null);
        if (!job) return;
        await advance(job);
      }
    } finally {
      working.current = false;
    }
  }, [advance]);

  /** Adds files to the end of the line. Settles at once: what happens to them shows in their own status. */
  const start = useCallback(
    async (folder: string, chosen: Picked[]) => {
      const items = chosen.filter((item) => !hidden(item.path));
      if (items.length === 0)
        throw new Error(
          chosen.length > 0
            ? "There’s nothing here to add. Uncloud leaves hidden files out, and that’s all there was."
            : "Nothing was chosen.",
        );
      const key = `upload-${++added.current}`;
      const label = nameTogether([...new Set(items.map((item) => item.path.split("/")[0]))]);
      jobs.current.set(key, {
        key,
        folder,
        label,
        items,
        plan: null,
        differ: null,
        begun: false,
        stopped: false,
        requests: new Set(),
        loaded: new Map(),
        cancel: new AbortController(),
      });
      setUploads((list) => [
        ...list,
        {
          key,
          label,
          folder,
          files: items.length,
          bytes: items.reduce((sum, item) => sum + item.file.size, 0),
          doneFiles: 0,
          sentBytes: 0,
          arrivedBytes: 0,
          stage: "waiting",
          error: null,
          placed: [],
          openTo: folder,
          skipped: [],
          review: null,
          replaced: 0,
          unchanged: 0,
        },
      ]);
      void pump();
    },
    [pump],
  );

  /**
   * What to do about something chosen that's partly there already: bring what's there up to date
   * with it — only what's new or changed is sent, and a changed file takes the place of the one
   * there — or add all of it again beside what's there, the way Finder keeps both. Either way it
   * takes its place in line again.
   */
  const decide = useCallback(
    (key: string, merge: boolean) => {
      const job = jobs.current.get(key);
      if (!job?.differ) return;
      const items = merge ? job.differ : job.items;
      job.plan = { items, merge };
      job.differ = null;
      update(key, {
        stage: "waiting",
        review: null,
        files: items.length,
        bytes: items.reduce((sum, item) => sum + item.file.size, 0),
        unchanged: job.items.length - items.length,
      });
      void pump();
    },
    [pump, update],
  );

  const stop = useCallback((key: string) => {
    const job = jobs.current.get(key);
    if (!job) return;
    job.stopped = true;
    job.requests.forEach((request) => request.abort());
  }, []);

  /**
   * Takes one away: one that's over, or one still waiting — its turn, or an answer — which is then
   * never sent. One on its way is stopped instead.
   */
  const dismiss = useCallback((key: string) => {
    const job = jobs.current.get(key);
    if (job?.begun) return;
    if (job) {
      job.stopped = true;
      job.cancel.abort();
      jobs.current.delete(key);
    }
    setUploads((list) => list.filter((upload) => upload.key !== key));
  }, []);

  return { uploads, running, start, decide, stop, dismiss };
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

  if (upload.stage === "waiting")
    return (
      <section className="import-progress upload-waiting" aria-live="polite">
        <div className="import-progress-head">
          <strong>
            <Clock size={16} />
            Waiting to add {upload.label}
          </strong>
          <button className="button secondary refresh-button" onClick={onDismiss}>
            Remove
          </button>
        </div>
        <span className="muted">
          {files(upload.files)} · {formatSize(upload.bytes)} · into {called(upload.folder)}, once
          what’s ahead of it is in.
        </span>
      </section>
    );

  if (upload.stage === "checking")
    return (
      <section className="import-progress" aria-live="polite">
        <div className="import-progress-head">
          <strong>
            <LoaderCircle size={16} className="spin" />
            Looking at what’s already in {called(upload.folder)}…
          </strong>
          {/* Nothing is sent during this, so it can still be taken out of the line. */}
          <button className="button secondary refresh-button" onClick={onDismiss}>
            Remove
          </button>
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
  // A merge reports a renamed file by its path inside the upload ("Holiday/map.pdf"), while a
  // folder kept whole reports only the name. Either way the clash is the last part of each path.
  const baseName = (itemPath: string) => itemPath.split("/").at(-1);
  const renamed = upload.placed.filter((entry) => baseName(entry.from) !== baseName(entry.to));
  const message =
    upload.stage === "failed"
      ? (upload.error ?? `Couldn’t add ${upload.label}.`)
      : upload.stage === "stopped"
        ? `Stopped adding ${upload.label}. Nothing from it was added.`
        : upload.files === 0
          ? // What differed came in some other way while it waited, likely another upload of the same.
            `Nothing left to add from ${upload.label}: it’s all in ${called(upload.folder)} already.`
          : // Named, since several can be in sight at once.
          `Added ${upload.label} from this device: ${files(arrived)}, ${formatSize(upload.arrivedBytes)}.` +
          (upload.replaced === 1
            ? " One took the place of an older copy, which is in the bin."
            : upload.replaced > 1
              ? ` ${upload.replaced} took the place of older copies, which are in the bin.`
              : "") +
          (upload.unchanged > 0
            ? ` ${upload.unchanged} ${upload.unchanged === 1 ? "was" : "were"} already there, so ${upload.unchanged === 1 ? "it wasn’t" : "they weren’t"} sent again.`
            : "") +
          (renamed.length === 1
            ? ` There was already something called “${baseName(renamed[0].from)}” there, so this one is “${baseName(renamed[0].to)}”.`
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
