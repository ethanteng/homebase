import { useCallback, useEffect, useRef, useState } from "react";
import {
  ChevronRight,
  CircleCheck,
  CircleStop,
  LoaderCircle,
  TriangleAlert,
  X,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { ImportJob } from "./api";

const DISMISSED = "uncloud.dismissed-import";
// Long enough to leave the page while files arrive and come back to see how it went; short enough
// that an old result doesn't greet somebody every time they open their files.
const RECENT = 30 * 60 * 1000;

function readDismissed(): string | null {
  try {
    return window.localStorage.getItem(DISMISSED);
  } catch {
    return null;
  }
}

function writeDismissed(id: string) {
  try {
    window.localStorage.setItem(DISMISSED, id);
  } catch {
    // A browser that won't remember just shows the message again next time.
  }
}

/**
 * The one import this account can have going, watched from My files, which is where its files
 * arrive. It outlives the page that started it, so a reload picks it up where it is.
 */
export function useImportJob(active: boolean, onImported: () => void) {
  const [job, setJob] = useState<ImportJob | null>(null);
  const [dismissed, setDismissed] = useState<string | null>(readDismissed);
  // Jobs watched while running in this page are always worth reporting on, however long they took.
  const watched = useRef(new Set<string>());
  const settled = useRef<string | null>(null);

  useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    api<{ job: ImportJob | null }>("/imports/job", { signal: controller.signal })
      .then(({ job: existing }) => {
        if (!existing) return;
        // Already finished before this page opened: nothing arrives now, so the list is current.
        if (!existing.running) settled.current = existing.id;
        setJob(existing);
      })
      .catch(() => {
        // Nothing to pick up is the ordinary case, not a problem to report.
      });
    return () => controller.abort();
  }, [active]);

  useEffect(() => {
    if (!job?.running) return;
    watched.current.add(job.id);
    let watching = true;
    const timer = window.setInterval(async () => {
      try {
        const { job: latest } = await api<{ job: ImportJob | null }>("/imports/job");
        if (watching && latest) setJob(latest);
      } catch {
        // A missed poll isn't worth reporting; the next one will do.
      }
    }, 700);
    return () => {
      watching = false;
      window.clearInterval(timer);
    };
  }, [job?.running, job?.id]);

  // Files landed, so what My files shows is out of date — once per import, however often polled.
  useEffect(() => {
    if (!job || job.running || settled.current === job.id) return;
    settled.current = job.id;
    onImported();
  }, [job, onImported]);

  const start = useCallback((started: ImportJob) => setJob(started), []);

  const stop = useCallback(async () => {
    try {
      await api("/imports/job/cancel", { method: "POST" });
    } catch {
      // The next poll says whether it stopped.
    }
  }, []);

  const dismiss = useCallback(() => {
    if (!job) return;
    writeDismissed(job.id);
    setDismissed(job.id);
  }, [job]);

  const recent =
    job?.finishedAt != null &&
    Date.now() - new Date(job.finishedAt).getTime() < RECENT;
  const visible =
    job != null &&
    job.id !== dismissed &&
    (job.running || watched.current.has(job.id) || recent);

  return { job: visible ? job : null, running: job?.running ?? false, start, stop, dismiss };
}

/** The folder everything in an import landed under, so "Show them" can go straight there. */
function arrivedIn(job: ImportJob): string | null {
  const paths = job.result?.imported.map((item) => item.localPath) ?? [];
  if (paths.length === 0) return null;
  let common = paths[0].split("/").slice(0, -1);
  for (const path of paths.slice(1)) {
    const parts = path.split("/");
    let same = 0;
    while (same < common.length && common[same] === parts[same]) same++;
    common = common.slice(0, same);
  }
  return common.join("/");
}

interface Props {
  job: ImportJob;
  onStop: () => void;
  onDismiss: () => void;
  onOpen: (path: string) => void;
}

export default function ImportStatus({ job, onStop, onDismiss, onOpen }: Props) {
  const [stopping, setStopping] = useState(false);
  const [showProblems, setShowProblems] = useState(false);

  useEffect(() => {
    if (!job.running) setStopping(false);
  }, [job.running]);

  if (job.running)
    return (
      <section className="import-progress" aria-live="polite">
        <div className="import-progress-head">
          <strong>
            <LoaderCircle size={16} className="spin" />
            {job.stage === "Measuring"
              ? `Getting ready to add ${job.label}…`
              : `Adding ${job.label} to your files`}
          </strong>
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
        </div>
        <div
          className="progress-track"
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={job.totalFiles || undefined}
          aria-valuenow={job.totalFiles ? job.completedFiles : undefined}
          aria-valuetext={
            job.totalFiles
              ? `${job.completedFiles} of ${job.totalFiles} files`
              : "Counting the files"
          }
        >
          <div
            className={`progress-fill${job.totalFiles ? "" : " counting"}`}
            style={
              job.totalFiles
                ? { width: `${Math.round((job.completedFiles / job.totalFiles) * 100)}%` }
                : undefined
            }
          />
        </div>
        <span className="muted">
          {job.totalFiles === 0
            ? "Counting the files first."
            : `${job.completedFiles} of ${job.totalFiles} file${job.totalFiles === 1 ? "" : "s"} · ${formatSize(job.bytes)}`}
          {" · "}You can close this page. It keeps going.
        </span>
      </section>
    );

  const problems = (job.result?.skipped ?? []).filter((skip) => !skip.expected);
  const folder = arrivedIn(job);
  const count = job.result?.importedCount ?? 0;

  const message =
    job.stage === "Failed"
      ? (job.error ?? `Couldn’t add ${job.label}.`)
      : job.stage === "Stopped"
        ? job.bytes > 0
          ? `Stopped adding ${job.label}. What had already arrived (${formatSize(job.bytes)}) is in your files.`
          : `Stopped adding ${job.label}.`
        : count === 0
          ? `You already have everything in ${job.label}.`
          : `Added ${count} file${count === 1 ? "" : "s"} from ${job.label} (${formatSize(job.result!.bytes)}).`;

  return (
    <section
      className={`import-progress import-outcome${job.stage === "Failed" ? " failed" : ""}`}
      role="status"
    >
      <div className="import-progress-head">
        <strong>
          {job.stage === "Failed" || problems.length > 0 ? (
            <TriangleAlert size={16} />
          ) : (
            <CircleCheck size={16} />
          )}
          {message}
        </strong>
        <span className="import-outcome-actions">
          {folder && job.stage !== "Failed" && (
            <button className="button secondary refresh-button" onClick={() => onOpen(folder)}>
              Show them
              <ChevronRight size={14} />
            </button>
          )}
          <button className="icon-button" onClick={onDismiss} aria-label="Dismiss" title="Dismiss">
            <X size={16} />
          </button>
        </span>
      </div>
      {problems.length > 0 && (
        <>
          <button
            className="link-button"
            onClick={() => setShowProblems((open) => !open)}
            aria-expanded={showProblems}
          >
            {problems.length} item{problems.length === 1 ? "" : "s"} couldn’t be added
            {showProblems ? " — hide" : " — see which"}
          </button>
          {showProblems && (
            <ul className="import-list">
              {problems.map((skip) => (
                <li key={skip.remotePath}>
                  <TriangleAlert size={18} strokeWidth={1.6} />
                  <div>
                    <strong>{skip.remotePath}</strong>
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
