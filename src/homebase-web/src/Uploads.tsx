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
import type { EditedEntry, UnfinishedUpload, UploadComparison } from "./api";

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
  /** Files that had arrived on an earlier try that never finished, so weren't sent again. */
  resumed: number;
  /** Why it's waiting to carry on, while it is: this device is offline, or Uncloud isn't answering. */
  lost: "offline" | "unanswered" | null;
}

/** Several files at once, so a folder of small ones doesn't wait on a request per file. */
const AT_ONCE = 3;
/**
 * How long a file Uncloud isn't answering for keeps being tried, counted in the waits between tries,
 * before the connection is given up on. Only time awake and online counts: a computer asleep, its
 * lid closed, isn't trying, and one offline waits to be back before it tries again.
 */
export const GIVE_UP_AFTER = 10 * 60_000;
/** The wait before trying a file again: soon at first, for a blip, then every half a minute. */
const backOff = (attempt: number) => Math.min(1000 * 2 ** (attempt - 1), 30_000);
/** How often a file on its way is looked at, to notice it has stopped moving. */
const LOOK_EVERY = 1000;
/**
 * How long a file can go with none of it moving before it's given up on and tried again. A
 * connection can stop carrying anything without either end hearing that it has gone, and the
 * request would otherwise wait for an answer for ever — and the whole upload with it.
 */
export const STALLED_AFTER = 60_000;
/**
 * How soon, and for how long, an unfinished upload still arriving is asked about again. Uncloud gives
 * up on a file it has heard nothing more of after 45 seconds, and for a few seconds after a file
 * arrives it still counts the upload as arriving, so the gap before the next isn't taken for the page
 * having gone. This keeps asking a little past both. Past that it's another page's, still sending.
 */
const RECHECK = 15_000;
const RECHECKS = 5;

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
    // Each piece of it sent puts off giving up. Once the last piece has gone, the answer gets the
    // same long: a connection can go quiet after the body has left, too. Measured by the clock, not
    // a timer, which stops while the computer sleeps: one woken — its lid opened — sends it again at
    // once, since its connections went to sleep with it and can take as long again to admit it.
    let moved = Date.now();
    let stalled = false;
    const watch = window.setInterval(() => {
      if (Date.now() - moved < STALLED_AFTER) return;
      stalled = true;
      request.abort();
    }, LOOK_EVERY);
    request.upload.onprogress = (event) => {
      moved = Date.now();
      onProgress(event.loaded);
    };
    request.onload = () => {
      window.clearInterval(watch);
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
      window.clearInterval(watch);
      requests.delete(request);
      reject(new Refused("", 0));
    };
    request.onabort = () => {
      window.clearInterval(watch);
      requests.delete(request);
      // Given up on for going quiet, it's tried again like any connection that dropped.
      reject(new Refused("", stalled ? 0 : -1));
    };
    request.send(item.file);
  });
}

/** A file about to be sent, as Uncloud is told of it beforehand. */
const described = (item: Picked) => ({ path: item.path, bytes: item.file.size, modified: item.file.lastModified });

/** Whether this device can still read a file, which is the other reason a request can fail without an answer. */
async function readable(file: File): Promise<boolean> {
  try {
    await file.slice(0, 1).arrayBuffer();
    return true;
  } catch {
    return false;
  }
}

/**
 * Waits a while, or until the upload is over. One over already doesn't wait at all: its signal
 * won't say so again.
 */
const pause = (ms: number, quit: AbortSignal) =>
  new Promise<void>((resolve) => {
    const done = () => {
      window.clearTimeout(timer);
      quit.removeEventListener("abort", done);
      resolve();
    };
    const timer = window.setTimeout(done, ms);
    quit.addEventListener("abort", done);
    if (quit.aborted) done();
  });

/** Waits for this device to be back online, or for the upload to be over — at once, if it is already. */
const online = (quit: AbortSignal) =>
  new Promise<void>((resolve) => {
    const done = () => {
      window.removeEventListener("online", done);
      quit.removeEventListener("abort", done);
      resolve();
    };
    window.addEventListener("online", done);
    quit.addEventListener("abort", done);
    if (quit.aborted) done();
  });

/** Whether this device says it's offline: between networks, or just woken and not yet reconnected. */
const offline = () => navigator.onLine === false;

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
 * room left. What arrived before the page went away is kept on the host for a while: the next page
 * says so, and adding the same files again sends only the rest.
 */
export function useUpload(active: boolean, onUploaded: () => void) {
  const [uploads, setUploads] = useState<Upload[]>([]);
  // In the order they were added, which is the order they go in.
  const jobs = useRef(new Map<string, Job>());
  const working = useRef(false);
  const added = useRef(0);
  const running = uploads.some(isPending);
  // Begun by a page that went away before they were in — this one, before it was refreshed, say.
  const [unfinished, setUnfinished] = useState<UnfinishedUpload[]>([]);
  // The ones this page began, which are never offered back to it as somebody else's.
  const ours = useRef(new Set<string>());

  useEffect(() => {
    if (!active) {
      // Whoever signs in next isn't shown an upload this page began. A new fetch, once they're in,
      // says what's still unfinished for them.
      ours.current.clear();
      setUnfinished([]);
      return;
    }
    let gone = false;
    let again = 0;
    let looked = 0;
    const look = async () => {
      looked++;
      let list: UnfinishedUpload[];
      try {
        list = await api<UnfinishedUpload[]>("/uploads");
      } catch {
        // Nothing to offer, then: the page is as it always was.
        return;
      }
      if (gone) return;
      const theirs = list.filter((upload) => !ours.current.has(upload.id));
      setUnfinished(theirs.filter((upload) => !upload.arriving));
      // Still arriving: another page's, or this one's from just before it was refreshed, its last
      // files not yet noticed gone. Asked about again, so it's offered once it has stopped.
      if (looked <= RECHECKS && theirs.some((upload) => upload.arriving))
        again = window.setTimeout(() => void look(), RECHECK);
    };
    void look();
    return () => {
      gone = true;
      window.clearTimeout(again);
    };
  }, [active]);

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
      let already: Set<string>;
      try {
        // Asked first, so a drive without room — or a folder that's gone — says so before anything
        // is sent. Named, so that if the same files were being added before and didn't all arrive,
        // Uncloud picks that up instead and says which are there already.
        const begun = await api<{ id: string; arrived?: string[] }>("/uploads", {
          method: "POST",
          body: JSON.stringify({ destination: folder, bytes, merge, files: items.map(described) }),
        });
        id = begun.id;
        already = new Set(begun.arrived ?? []);
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
      ours.current.add(id);
      // Picked up again, it's this page's now rather than one to offer.
      setUnfinished((list) => list.filter((upload) => upload.id !== id));
      // Stopped, or signed out, while Uncloud was being asked: nobody wants them sent.
      if (job.stopped) {
        await discard(id);
        update(key, { stage: "stopped" });
        return;
      }

      // What arrived on an earlier try is done from the start, and isn't sent again.
      const rest = items.filter((item) => !already.has(item.path));
      const resumed = items.length - rest.length;
      let next = 0;
      let doneFiles = resumed;
      let doneBytes = bytes - rest.reduce((sum, item) => sum + item.file.size, 0);
      let arrived = resumed;
      let arrivedBytes = doneBytes;
      // Set by the workers, out of the type checker's sight, so it mustn't be taken as always null here.
      let fatal = null as string | null;
      // The connection gave out, rather than Uncloud refusing something.
      let unanswered = false;
      if (resumed > 0) update(key, { resumed, doneFiles, sentBytes: doneBytes, arrivedBytes });
      const skipped: Upload["skipped"] = [];
      // Files waiting to be tried again, and whether any is waiting for this device to be back online.
      let waiting = 0;
      let waitingOnline = 0;
      // Ends any wait for the connection: Stop, signing out, or another file finding it can't go on.
      const quit = new AbortController();
      job.cancel.signal.addEventListener("abort", () => quit.abort(), { once: true });
      if (job.cancel.signal.aborted) quit.abort();
      // What was last shown, so a tick with nothing new doesn't redraw the page under it.
      let shown = "";
      const progress = () => {
        let inFlight = 0;
        for (const loaded of job.loaded.values()) inFlight += loaded;
        const sentBytes = doneBytes + inFlight;
        const lost = waitingOnline > 0 ? "offline" : waiting > 0 ? "unanswered" : null;
        const now = `${doneFiles}:${sentBytes}:${skipped.length}:${lost}`;
        if (now === shown) return;
        shown = now;
        update(key, { doneFiles, sentBytes, arrivedBytes, skipped: [...skipped], lost });
      };
      const timer = window.setInterval(progress, 250);
      const giveUp = (reason: string, connection: boolean) => {
        fatal = reason;
        unanswered = connection;
        job.requests.forEach((request) => request.abort());
        quit.abort();
      };

      const worker = async () => {
        while (!job.stopped && fatal === null && next < rest.length) {
          const item = rest[next++];
          // How long this file has waited to be tried again while Uncloud wasn't answering.
          let waited = 0;
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
              // No answer, or Uncloud saying the file stopped arriving: the connection, not the file.
              if (refused.status === 0 || refused.status === 408 || refused.status >= 500) {
                // Offline — just woken, say, and not yet reconnected — it waits to be back, which
                // isn't Uncloud failing to answer and doesn't count towards giving up. Before asking
                // whether the file can still be read: one that isn't on this device yet can't be
                // read while it's offline, and that isn't a reason to leave it out.
                if (offline()) {
                  waiting++;
                  waitingOnline++;
                  await online(quit.signal);
                  waiting--;
                  waitingOnline--;
                  if (job.stopped || fatal !== null) return;
                  continue;
                }
                if (!(await readable(item.file))) {
                  skipped.push({
                    path: item.path,
                    reason: "This device couldn’t read it. It may have been moved or deleted after it was chosen.",
                  });
                  break;
                }
                // Stopped, or given up on by another file, while this one was being read.
                if (job.stopped || fatal !== null) return;
                if (waited < GIVE_UP_AFTER) {
                  const wait = backOff(attempt);
                  waited += wait;
                  waiting++;
                  await pause(wait, quit.signal);
                  waiting--;
                  continue;
                }
                giveUp("Uncloud stopped answering, so nothing was added. Check you’re still connected, then try again.", true);
              } else
                giveUp(
                  refused.status === 401
                    ? signedOut
                    : refused.message || "The upload was interrupted, so nothing was added.",
                  false,
                );
              return;
            }
          }
          job.loaded.delete(item.path);
          doneFiles++;
          doneBytes += item.file.size;
        }
      };
      await Promise.all(Array.from({ length: Math.min(AT_ONCE, rest.length) }, worker));
      window.clearInterval(timer);
      progress();

      if (job.stopped) {
        await discard(id);
        update(key, { stage: "stopped" });
        return;
      }
      // Lost to the connection, what arrived is kept, so adding them again needn't send it all again.
      if (unanswered && arrived > 0) {
        update(key, {
          stage: "failed",
          error:
            "Uncloud stopped answering, so not all of it was added. What arrived is kept for a day: check " +
            "you’re still connected, then add the same files again and only the rest is sent.",
        });
        return;
      }
      // Whatever went wrong this time, what an earlier try left was never this one's to throw away.
      if (fatal !== null && resumed > 0) {
        // "Nothing was added" would deny the files this try picked up.
        update(key, {
          stage: "failed",
          error:
            fatal === signedOut
              ? "You were signed out, so the rest wasn’t added. What had arrived is kept for a day. Sign in, then add the same files again and only the rest is sent."
              : `${fatal.replace("so nothing was added", "so not all of it was added")} What had arrived is kept for a day, so adding the same files again sends only the rest.`,
        });
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
        // No answer at all, rather than a refusal: Uncloud may well have put them in place and
        // only the reply was lost. Saying nothing was added would have them added twice.
        const unanswered = problem instanceof TypeError;
        // What an earlier try left isn't this one's to throw away when it never got into My files.
        // If the host did finish and only the reply was lost, the upload is already gone.
        if (resumed === 0) await discard(id);
        if (unanswered) onUploaded();
        const kept =
          " What had arrived is kept for a day, so adding the same files again sends only the rest.";
        const refused =
          problem instanceof SignedOutError
            ? "You were signed out before they were put in place. Sign in, then add the same files again."
            : problem instanceof Error
              ? problem.message
              : "The files arrived but couldn’t be put in place.";
        update(key, {
          stage: "failed",
          error: unanswered
            ? "Uncloud didn’t answer while putting them in place, so they may already be in your files. Look there before adding them again." +
              (resumed > 0 ? " If they aren’t, what had arrived is kept for a day." : "")
            : resumed > 0
              ? refused + kept
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
            body: JSON.stringify({ destination: job.folder, files: (job.plan?.items ?? job.items).map(described) }),
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
          resumed: 0,
          lost: null,
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
    // And any file waiting for the connection to come back stops waiting.
    job.cancel.abort();
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

  /** Puts what arrived of an unfinished upload where it was going, as it is, without the rest. */
  const place = useCallback(
    async (upload: UnfinishedUpload) => {
      await api(`/uploads/${encodeURIComponent(upload.id)}/finish`, {
        method: "POST",
        body: JSON.stringify({ destination: upload.destination, merge: upload.merge }),
      });
      setUnfinished((list) => list.filter((other) => other.id !== upload.id));
      onUploaded();
    },
    [onUploaded],
  );

  /** Throws away what arrived of an unfinished upload, giving its room back. */
  const throwAway = useCallback(async (id: string) => {
    await api(`/uploads/${encodeURIComponent(id)}`, { method: "DELETE" });
    setUnfinished((list) => list.filter((other) => other.id !== id));
  }, []);

  return { uploads, running, start, decide, stop, dismiss, unfinished, place, throwAway };
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
            {upload.lost ? <Clock size={16} /> : <LoaderCircle size={16} className="spin" />}
            {upload.stage === "placing"
              ? `Putting ${upload.label} in place…`
              : upload.lost
                ? `Waiting to carry on adding ${upload.label}`
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
          {formatSize(upload.sentBytes)} of {formatSize(upload.bytes)} ·{" "}
          {upload.resumed > 0 && `${upload.resumed} had arrived on an earlier try. `}
          {upload.lost === "offline"
            ? "This device is offline. They carry on by themselves once it’s back."
            : upload.lost === "unanswered"
              ? "Uncloud isn’t answering. They carry on by themselves once it does."
              : "Keep this page open until they’re in. If this computer sleeps, they carry on when it wakes."}
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
          (upload.resumed > 0
            ? ` ${upload.resumed} had arrived on an earlier try, so ${upload.resumed === 1 ? "it wasn’t" : "they weren’t"} sent again.`
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

const until = new Intl.DateTimeFormat(undefined, { weekday: "short", hour: "numeric", minute: "2-digit" });

interface UnfinishedProps {
  upload: UnfinishedUpload;
  onPlace: () => Promise<void>;
  onThrowAway: () => Promise<void>;
}

/**
 * Files a page was sending that never all arrived, because it was closed or refreshed partway or
 * its connection gave out. What did arrive is kept on the host for a day, so adding the same files
 * again sends only the rest. It can also be put in place as it is, or thrown away to give its room back.
 */
export function UnfinishedStatus({ upload, onPlace, onThrowAway }: UnfinishedProps) {
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const label = nameTogether(upload.names);
  const run = (action: () => Promise<void>) => {
    setBusy(true);
    setProblem(null);
    action().catch((error: unknown) => {
      setBusy(false);
      setProblem(error instanceof Error ? error.message : `Couldn’t do that with ${label}. Try again.`);
    });
  };

  return (
    <section className="import-progress import-outcome import-decision" role="status">
      <div className="import-progress-head">
        <strong>
          <TriangleAlert size={16} />
          Adding {label} didn’t finish.
        </strong>
      </div>
      <span className="muted">
        {files(upload.files)}, {formatSize(upload.bytes)}, arrived
        {upload.destination !== null && ` for ${called(upload.destination)}`} before it stopped. Add the same
        files again and only the rest is sent. Uncloud keeps what arrived until {until.format(new Date(upload.keptUntil))}.
      </span>
      {problem && (
        <p className="error-message" role="alert">
          {problem}
        </p>
      )}
      <div className="card-actions">
        {/* Begun before Uncloud kept where an upload was going, it can only be picked up by adding it again. */}
        {upload.destination !== null && (
          <button
            className="button secondary"
            onClick={() => run(onPlace)}
            disabled={busy}
            title={`Put what arrived in ${called(upload.destination)} now, without the rest`}
          >
            Add what arrived
          </button>
        )}
        <button className="button secondary" onClick={() => run(onThrowAway)} disabled={busy}>
          Throw away
        </button>
      </div>
    </section>
  );
}

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
