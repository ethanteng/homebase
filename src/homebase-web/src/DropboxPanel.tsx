import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowUpRight,
  ChevronRight,
  CloudDownload,
  CircleStop,
  File,
  Folder,
  HardDrive,
  LoaderCircle,
  Ruler,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type {
  DropboxEntry,
  DropboxStatus,
  ImportEstimate,
  ImportJob,
  ImportResult,
  ImportedFile,
  StorageReport,
} from "./api";

const dateFormat = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

// A folder’s size costs a walk of the whole tree, so it reads as "Folder" until asked for.
function describeFolder(estimate: ImportEstimate | undefined): string {
  if (!estimate) return "Folder";
  const files = `${estimate.fileCount} file${estimate.fileCount === 1 ? "" : "s"}`;
  const total = `${formatSize(estimate.bytes)} · ${files}`;
  if (estimate.newFileCount === 0) return `${total} · all already home`;
  const arriving = `${formatSize(estimate.newBytes)} to bring home`;
  return estimate.fits
    ? `${total} · ${arriving}`
    : `${total} · ${arriving} · won’t fit on your drive`;
}

interface Props {
  onImported: () => void;
}

export default function DropboxPanel({ onImported }: Props) {
  const [status, setStatus] = useState<DropboxStatus | null>(null);
  const [storage, setStorage] = useState<StorageReport | null>(null);
  const [sizes, setSizes] = useState<Record<string, ImportEstimate>>({});
  const [imported, setImported] = useState<ImportedFile[]>([]);
  const [entries, setEntries] = useState<DropboxEntry[] | null>(null);
  const [remotePath, setRemotePath] = useState("");
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [result, setResult] = useState<ImportResult | null>(null);
  const [job, setJob] = useState<ImportJob | null>(null);
  // An import is finished with once its outcome has been shown, however often it is polled after.
  const settled = useRef<string | null>(null);

  const loadImported = useCallback(async () => {
    try {
      setImported(await api<ImportedFile[]>("/imports"));
    } catch {
      // A failed read shouldn't replace what's already on screen.
    }
  }, []);

  const loadStorage = useCallback(async () => {
    try {
      setStorage(await api<StorageReport>("/storage"));
    } catch {
      // Knowing the free space is a help, not a precondition.
    }
  }, []);

  useEffect(() => {
    const outcome = new URLSearchParams(window.location.search).get("dropbox");
    if (outcome) {
      setNotice(
        outcome === "connected"
          ? "Dropbox connected."
          : outcome === "denied"
            ? "Dropbox sign-in was cancelled."
            : "Dropbox sign-in didn’t finish. Try again.",
      );
      window.history.replaceState(null, "", window.location.pathname);
    }
    api<DropboxStatus>("/providers/dropbox")
      .then(setStatus)
      .catch((problem: unknown) =>
        setError(
          problem instanceof Error ? problem.message : "Couldn’t reach Uncloud.",
        ),
      );
  }, []);

  useEffect(() => {
    if (!status?.connected) return;
    void loadImported();
    void loadStorage();
    // An import outlives the page that started it, so a reload finds it rather than losing it —
    // including one that finished while the panel was closed. Leaving the page is encouraged, so
    // what an import ended up doing has to survive coming back to look.
    api<{ job: ImportJob | null }>("/imports/job")
      .then(({ job: existing }) => {
        if (existing) setJob(existing);
      })
      .catch(() => {
        // Nothing to pick up is the ordinary case, not a problem to report.
      });
  }, [status?.connected, loadImported, loadStorage]);

  useEffect(() => {
    if (!job?.running) return;
    let watching = true;
    const timer = window.setInterval(async () => {
      try {
        const { job: latest } = await api<{ job: ImportJob | null }>("/imports/job");
        if (watching && latest) setJob(latest);
      } catch {
        // A missed poll isn't worth tearing the panel down for; the next one will do.
      }
    }, 700);
    return () => {
      watching = false;
      window.clearInterval(timer);
    };
  }, [job?.running]);

  useEffect(() => {
    if (!job || job.running || settled.current === job.id) return;
    settled.current = job.id;
    if (job.stage === "Failed") {
      setError(job.error ?? "Uncloud couldn’t bring these files home.");
    } else if (job.stage === "Stopped") {
      setNotice(
        `Stopped bringing ${job.label} home. The ${formatSize(job.bytes)} that arrived is yours and stays.`,
      );
    } else if (job.result) {
      setResult(job.result);
      const brought =
        job.result.importedCount === 0
          ? `Nothing new to bring home from ${job.label}.`
          : `Brought ${job.result.importedCount} file${job.result.importedCount === 1 ? "" : "s"} home (${formatSize(job.result.bytes)}).`;
      // A folder left behind is the part worth saying out loud: the rest of the import succeeded,
      // so nothing else on screen would tell you that anything is missing.
      const problemCount = job.result.skipped.filter((skip) => !skip.expected).length;
      setNotice(
        problemCount === 0
          ? brought
          : `${brought} ${problemCount} item${problemCount === 1 ? "" : "s"} not brought home — see below.`,
      );
    }
    void loadImported();
    void loadStorage();
    // Every measurement was taken against the old set of imported files: a folder just brought
    // home would still offer to bring it home again. Measuring is cheap to ask for a second time.
    setSizes({});
    // Files landed in the library, so the browser's view of it is now out of date.
    onImported();
  }, [job, loadImported, loadStorage, onImported]);

  async function run(label: string, action: () => Promise<void>) {
    setBusy(label);
    setError("");
    try {
      await action();
    } catch (problem: unknown) {
      setError(problem instanceof Error ? problem.message : "That didn’t work.");
    } finally {
      setBusy("");
    }
  }

  const connect = () =>
    run("connect", async () => {
      const { authorizeUrl } = await api<{ authorizeUrl: string }>(
        "/providers/dropbox/connect",
        { method: "POST" },
      );
      window.location.href = authorizeUrl;
    });

  const browse = (path: string) =>
    run("browse", async () => {
      setEntries(
        await api<DropboxEntry[]>(
          `/providers/dropbox/files?${new URLSearchParams({ path })}`,
        ),
      );
      setRemotePath(path);
    });

  const measure = (entry: DropboxEntry) =>
    run(`measure:${entry.pathLower}`, async () => {
      const estimate = await api<ImportEstimate>(
        `/imports/estimate?${new URLSearchParams({ remotePath: entry.pathLower })}`,
      );
      setSizes((current) => ({ ...current, [entry.pathLower]: estimate }));
    });

  const bringHome = (entry: DropboxEntry) =>
    run(entry.pathLower, async () => {
      setNotice("");
      setResult(null);
      const { job: started } = await api<{ job: ImportJob }>("/imports", {
        method: "POST",
        body: JSON.stringify({ remotePath: entry.pathLower, label: entry.name }),
      });
      setJob(started);
    });

  const stop = () =>
    run("stop", async () => {
      await api<{ job: ImportJob | null }>("/imports/job/cancel", { method: "POST" });
    });

  // Only the skips a person can act on; "already imported" is the ordinary case.
  const problems = (result?.skipped ?? []).filter((skip) => !skip.expected);

  if (error && !status)
    return (
      <div className="empty-state connection-error" role="alert">
        <TriangleAlert size={32} />
        <h1>Couldn’t load Dropbox</h1>
        <p>{error}</p>
      </div>
    );
  if (!status)
    return (
      <div className="file-loading" role="status">
        <LoaderCircle className="spin" size={24} />
        Checking Dropbox…
      </div>
    );

  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">BRING YOUR FILES HOME</span>
          <h1>
            Dropbox<span className="heading-dot">.</span>
          </h1>
          <p>
            Copy files and folders onto storage you own. Once a file is here,
            this copy is the one that counts — Uncloud won’t go back to Dropbox
            for it.
          </p>
        </div>
      </div>

      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}

      {!status.configured ? (
        <div className="notice-card">
          <h2>Uncloud needs a Dropbox app key</h2>
          <p className="field-help">
            Create an app at dropbox.com/developers/apps with the{" "}
            <code>account_info.read</code>, <code>files.metadata.read</code> and{" "}
            <code>files.content.read</code> permissions, then start Uncloud
            with <code>Homebase__Dropbox__AppKey</code> set to its app key.
            Uncloud only ever reads from Dropbox.
          </p>
        </div>
      ) : !status.connected ? (
        <div className="notice-card">
          <h2>Connect your Dropbox</h2>
          <p className="field-help">
            Uncloud asks for read-only access. It can copy files down to your
            folder, and it cannot change anything in your Dropbox.
          </p>
          <button
            className="button primary"
            onClick={() => void connect()}
            disabled={busy === "connect"}
          >
            {busy === "connect" ? "Opening Dropbox…" : "Connect Dropbox"}
            <ArrowUpRight size={15} />
          </button>
        </div>
      ) : (
        <>
          {problems.length > 0 && (
            <section className="import-section">
              <h2>Not brought home</h2>
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
            </section>
          )}

          {job?.running && (
            <section className="import-progress" aria-live="polite">
              <div className="import-progress-head">
                <strong>
                  {job.stage === "Measuring"
                    ? `Looking through ${job.label}…`
                    : `Bringing ${job.label} home`}
                </strong>
                <button
                  className="button"
                  onClick={() => void stop()}
                  disabled={busy === "stop"}
                >
                  <CircleStop size={15} />
                  {busy === "stop" ? "Stopping…" : "Stop"}
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
                      ? {
                          width: `${Math.round((job.completedFiles / job.totalFiles) * 100)}%`,
                        }
                      : undefined
                  }
                />
              </div>
              <span className="muted">
                {job.totalFiles === 0
                  ? "Counting the files, before anything is downloaded."
                  : `${job.completedFiles} of ${job.totalFiles} file${job.totalFiles === 1 ? "" : "s"} · ${formatSize(job.bytes)} brought home`}
              </span>
              {job.currentFile && (
                <span className="muted current-file">{job.currentFile}</span>
              )}
              <p className="field-help">
                You can leave this page. The import keeps going, and comes back
                here when you return.
              </p>
            </section>
          )}

          {storage?.freeBytes != null && (
            <p className="library-note storage-note">
              <HardDrive size={17} />
              <span>
                {formatSize(storage.freeBytes)} free
                {storage.totalBytes != null
                  ? ` of ${formatSize(storage.totalBytes)}`
                  : ""}{" "}
                on your Uncloud drive. Uncloud checks that a folder fits before
                it brings anything home.
              </span>
            </p>
          )}

          <section className="import-section">
            <div className="import-section-head">
              <h2>
                On Dropbox
                {status.accountName ? ` · ${status.accountName}` : ""}
                {remotePath ? ` · ${remotePath}` : ""}
              </h2>
              {remotePath && (
                <button
                  className="refresh-button"
                  onClick={() =>
                    void browse(
                      remotePath.slice(0, remotePath.lastIndexOf("/")) || "",
                    )
                  }
                >
                  Up one folder
                </button>
              )}
            </div>
            {entries === null ? (
              <button
                className="button primary"
                onClick={() => void browse("")}
                disabled={busy === "browse"}
              >
                {busy === "browse" ? "Loading…" : "Show my Dropbox files"}
              </button>
            ) : entries.length === 0 ? (
              <p className="field-help">This Dropbox folder is empty.</p>
            ) : (
              <ul className="import-list">
                {entries.map((entry) => (
                  <li key={entry.id}>
                    {entry.isFolder ? (
                      <Folder size={19} strokeWidth={1.6} />
                    ) : (
                      <File size={19} strokeWidth={1.6} />
                    )}
                    <div>
                      <strong>{entry.name}</strong>
                      <span
                        className={`muted${sizes[entry.pathLower] && !sizes[entry.pathLower].fits ? " will-not-fit" : ""}`}
                      >
                        {entry.isFolder
                          ? describeFolder(sizes[entry.pathLower])
                          : formatSize(entry.size)}
                      </span>
                    </div>
                    {entry.isFolder && !sizes[entry.pathLower] && (
                      <button
                        className="button"
                        onClick={() => void measure(entry)}
                        disabled={busy === `measure:${entry.pathLower}`}
                      >
                        <Ruler size={15} />
                        {busy === `measure:${entry.pathLower}`
                          ? "Measuring…"
                          : "Check size"}
                      </button>
                    )}
                    {entry.isFolder && (
                      <button
                        className="button"
                        onClick={() => void browse(entry.pathLower)}
                        disabled={busy === "browse"}
                      >
                        Open
                        <ChevronRight size={15} />
                      </button>
                    )}
                    <button
                      className="button primary"
                      onClick={() => void bringHome(entry)}
                      disabled={job?.running || busy === entry.pathLower}
                    >
                      <CloudDownload size={15} />
                      {job?.running && job.remotePath === entry.pathLower
                        ? "Bringing…"
                        : "Bring home"}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="import-section">
            <div className="import-section-head">
              <h2>In your Uncloud folder</h2>
            </div>
            {imported.length === 0 ? (
              <p className="field-help">
                Nothing yet. Choose a file or folder above to bring it home.
              </p>
            ) : (
              <ul className="import-list">
                {imported.map((file) => (
                  <li key={file.remotePath}>
                    <File size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{file.localPath}</strong>
                      <span className="muted">
                        {formatSize(file.size)} · imported{" "}
                        {dateFormat.format(new Date(file.importedAt))} · from{" "}
                        {file.remotePath}
                      </span>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </>
  );
}
