import { useCallback, useEffect, useRef, useState } from "react";
import {
  ChevronRight,
  CircleCheck,
  CircleStop,
  LoaderCircle,
  TriangleAlert,
  X,
} from "lucide-react";
import { api, formatSize, nameTogether, SignedOutError } from "./api";
import type { EditedEntry } from "./api";

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

export type UploadStage = "sending" | "placing" | "done" | "stopped" | "failed";

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

/** One upload as it runs: the requests in flight, and how far each has got. */
interface Run {
  stopped: boolean;
  requests: Set<XMLHttpRequest>;
  loaded: Map<string, number>;
}

/**
 * Files on their way from this device into My files, watched from My files like an import. Unlike
 * an import, this page is what sends them, so it lasts only as long as the page does — and says so.
 */
export function useUpload(active: boolean, onUploaded: () => void) {
  const [upload, setUpload] = useState<Upload | null>(null);
  const current = useRef<Run | null>(null);
  const running = upload != null && isRunning(upload);

  // Signing out ends whatever was on its way, and whoever signs in next doesn't see what it was.
  useEffect(() => {
    if (active) return;
    const run = current.current;
    if (run) {
      run.stopped = true;
      run.requests.forEach((request) => request.abort());
    }
    setUpload(null);
  }, [active]);

  // Leaving now would stop it, so the browser asks first.
  useEffect(() => {
    if (!running) return;
    const stay = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", stay);
    return () => window.removeEventListener("beforeunload", stay);
  }, [running]);

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
      const run: Run = { stopped: false, requests: new Set(), loaded: new Map() };
      current.current = run;
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
      const tops = [...new Set(items.map((item) => item.path.split("/")[0]))];
      const update = (change: Partial<Upload>) =>
        setUpload((value) => (value?.id === id ? { ...value, ...change } : value));
      setUpload({
        id,
        label: nameTogether(tops),
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
            const { items: placed } = await api<{ items: EditedEntry[] }>(
              `/uploads/${encodeURIComponent(id)}/finish`,
              { method: "POST", body: JSON.stringify({ destination: folder }) },
            );
            const one = placed.length === 1 ? placed[0] : null;
            update({
              stage: "done",
              placed,
              openTo:
                one && items.some((item) => item.path.startsWith(`${one.from}/`)) ? one.to : folder,
            });
            onUploaded();
          } catch (problem: unknown) {
            await discard(id);
            update({
              stage: "failed",
              error:
                problem instanceof SignedOutError
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

  const stop = useCallback(() => {
    const run = current.current;
    if (!run) return;
    run.stopped = true;
    run.requests.forEach((request) => request.abort());
  }, []);

  const dismiss = useCallback(() => setUpload((value) => (value && !isRunning(value) ? null : value)), []);

  return { upload, running, start, stop, dismiss };
}

interface Props {
  upload: Upload;
  onStop: () => void;
  onDismiss: () => void;
  onOpen: (path: string) => void;
}

/** How an upload from this device is going, above the files it's on its way to. */
export default function UploadStatus({ upload, onStop, onDismiss, onOpen }: Props) {
  const [stopping, setStopping] = useState(false);
  const [showProblems, setShowProblems] = useState(false);

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
