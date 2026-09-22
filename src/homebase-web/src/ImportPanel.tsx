import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowUpRight,
  ChevronRight,
  CloudDownload,
  CircleStop,
  File,
  Folder,
  FolderTree,
  HardDrive,
  LoaderCircle,
  Ruler,
  TriangleAlert,
} from "lucide-react";
import { api, DROPBOX, formatSize } from "./api";
import type {
  ImportEstimate,
  ImportJob,
  ImportResult,
  ImportSources,
  ImportedFile,
  SourceEntry,
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
  onConfigure: () => void;
  canConfigure: boolean;
  /** Bumped when an administrator changes the places or the Dropbox app key. */
  settingsRevision: number;
}

/**
 * Bringing files in, from wherever they are. A folder on this computer and a Dropbox account are
 * the same thing here: a list to walk and files to copy onto storage you own. The only part that
 * differs is that Dropbox has to be signed in to first.
 */
export default function ImportPanel({
  onImported,
  onConfigure,
  canConfigure,
  settingsRevision,
}: Props) {
  const [sources, setSources] = useState<ImportSources | null>(null);
  const [sourceId, setSourceId] = useState<string | null>(null);
  const [storage, setStorage] = useState<StorageReport | null>(null);
  const [sizes, setSizes] = useState<Record<string, ImportEstimate>>({});
  const [imported, setImported] = useState<ImportedFile[]>([]);
  const [entries, setEntries] = useState<SourceEntry[] | null>(null);
  const [remotePath, setRemotePath] = useState("");
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [loadFailure, setLoadFailure] = useState("");
  const [notice, setNotice] = useState("");
  const [result, setResult] = useState<ImportResult | null>(null);
  const [job, setJob] = useState<ImportJob | null>(null);
  // An import is finished with once its outcome has been shown, however often it is polled after.
  const settled = useRef<string | null>(null);

  const place = sources?.places.find((candidate) => candidate.id === sourceId) ?? null;
  const onDropbox = sourceId === DROPBOX;
  const dropbox = sources?.dropbox ?? null;

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

  const loadSources = useCallback(async () => {
    try {
      const latest = await api<ImportSources>("/imports/sources");
      setSources(latest);
      return latest;
    } catch (problem: unknown) {
      setLoadFailure(
        problem instanceof Error ? problem.message : "Couldn’t reach Uncloud.",
      );
      return null;
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
    void loadSources().then((latest) => {
      if (!latest) return;
      // Coming back from a Dropbox sign-in means Dropbox is what they were doing. Otherwise the
      // first folder on this computer is the friendlier place to start: nothing to sign in to.
      if (outcome || latest.places.length === 0) setSourceId(DROPBOX);
      else setSourceId(latest.places[0].id);
    });
  }, [loadSources]);

  // A place added or removed in the settings dialog has to show up here without a reload, or
  // adding one and finding nothing changed reads as it not having worked.
  useEffect(() => {
    if (settingsRevision === 0) return;
    void loadSources().then((latest) => {
      if (!latest) return;
      setSourceId((current) => {
        // Somewhere that has just been taken away is no longer somewhere to be looking at.
        if (current !== DROPBOX && !latest.places.some((place) => place.id === current))
          return latest.places[0]?.id ?? DROPBOX;
        // Sitting on Dropbox with no way to connect it, when a folder has just been shared, is
        // looking at the one thing here that can't be used. Only moved from a choice nobody made.
        if (current === DROPBOX && !latest.dropbox.configured && latest.places.length > 0)
          return latest.places[0].id;
        return current;
      });
    });
  }, [settingsRevision, loadSources]);

  useEffect(() => {
    if (!sources) return;
    void loadImported();
    void loadStorage();
    // An import outlives the page that started it, so a reload finds it rather than losing it —
    // including one that finished while the panel was closed. Leaving the page is encouraged, so
    // what an import ended up doing has to survive coming back to look.
    api<{ job: ImportJob | null }>("/imports/job")
      .then(({ job: existing }) => {
        if (!existing) return;
        setJob(existing);
        // Show the place it is coming from, not whichever one happened to be selected.
        setSourceId(existing.sourceId);
      })
      .catch(() => {
        // Nothing to pick up is the ordinary case, not a problem to report.
      });
  }, [sources, loadImported, loadStorage]);

  // Switching places starts again: a listing and a set of measurements belong to one place only.
  useEffect(() => {
    setEntries(null);
    setRemotePath("");
    setSizes({});
    setResult(null);
    setError("");
  }, [sourceId]);

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
        await api<SourceEntry[]>(
          `/imports/sources/${encodeURIComponent(sourceId ?? DROPBOX)}/files?${new URLSearchParams({ path })}`,
        ),
      );
      setRemotePath(path);
    });

  const measure = (entry: SourceEntry) =>
    run(`measure:${entry.path}`, async () => {
      const estimate = await api<ImportEstimate>(
        `/imports/estimate?${new URLSearchParams({ remotePath: entry.path, source: sourceId ?? DROPBOX })}`,
      );
      setSizes((current) => ({ ...current, [entry.path]: estimate }));
    });

  const bringHome = (entry: SourceEntry) =>
    run(entry.path, async () => {
      setNotice("");
      setResult(null);
      const { job: started } = await api<{ job: ImportJob }>("/imports", {
        method: "POST",
        body: JSON.stringify({
          remotePath: entry.path,
          label: entry.name,
          source: sourceId ?? DROPBOX,
        }),
      });
      setJob(started);
    });

  const stop = () =>
    run("stop", async () => {
      await api<{ job: ImportJob | null }>("/imports/job/cancel", { method: "POST" });
    });

  // Only the skips a person can act on; "already imported" is the ordinary case.
  const problems = (result?.skipped ?? []).filter((skip) => !skip.expected);

  if (loadFailure)
    return (
      <div className="empty-state connection-error" role="alert">
        <TriangleAlert size={32} />
        <h1>Couldn’t see what’s available</h1>
        <p>{loadFailure}</p>
      </div>
    );
  if (!sources || sourceId === null)
    return (
      <div className="file-loading" role="status">
        <LoaderCircle className="spin" size={24} />
        Looking for places to bring files in from…
      </div>
    );

  const sourceName = onDropbox ? "Dropbox" : (place?.name ?? "this place");
  // Dropbox needs signing in to; a folder on this computer needs to actually be there.
  const blocked = onDropbox
    ? !dropbox?.configured
      ? "unconfigured"
      : !dropbox.connected
        ? "disconnected"
        : null
    : !place
      ? "gone"
      : !place.available
        ? "unavailable"
        : null;

  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">BRING YOUR FILES HOME</span>
          <h1>
            Bringing files in<span className="heading-dot">.</span>
          </h1>
          <p>
            Copy files and folders onto storage you own. Once a file is here,
            this copy is the one that counts — Uncloud won’t go back for it.
          </p>
        </div>
      </div>

      <section className="import-section">
        <div className="import-section-head">
          <h2>Where from</h2>
          {canConfigure && (
            <button className="refresh-button" onClick={onConfigure}>
              Manage places
            </button>
          )}
        </div>
        <div className="source-picker" role="tablist" aria-label="Where to bring files in from">
          {sources.places.map((candidate) => (
            <button
              key={candidate.id}
              role="tab"
              aria-selected={sourceId === candidate.id}
              className={`source-chip${sourceId === candidate.id ? " active" : ""}${candidate.available ? "" : " unavailable"}`}
              onClick={() => setSourceId(candidate.id)}
              title={candidate.path}
            >
              <FolderTree size={17} strokeWidth={1.6} />
              <span>{candidate.name}</span>
              {!candidate.available && <em>not connected</em>}
            </button>
          ))}
          <button
            role="tab"
            aria-selected={onDropbox}
            className={`source-chip${onDropbox ? " active" : ""}`}
            onClick={() => setSourceId(DROPBOX)}
          >
            <CloudDownload size={17} strokeWidth={1.6} />
            <span>Dropbox</span>
            {/* A folder on this computer is very often called "Dropbox" too, so the online one
                has to say which it is rather than leave two identical chips side by side. */}
            <em>{(dropbox?.connected && dropbox.accountName) || "online"}</em>
          </button>
        </div>
        {sources.places.length === 0 && (
          <p className="field-help">
            {canConfigure
              ? "Uncloud can also bring files in straight from a folder on this computer — the one your Dropbox or Google Drive app already syncs, or an old backup drive. Add one under Manage places and nothing needs signing in to."
              : "Whoever looks after this Uncloud can also share folders from this computer to bring files in from, with nothing to sign in to."}
          </p>
        )}
      </section>

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

      {blocked === "unconfigured" ? (
        <div className="notice-card">
          <h2>Dropbox isn’t set up on this Uncloud yet</h2>
          <p className="field-help">
            {canConfigure
              ? "Connecting to Dropbox needs a Dropbox app key for this Uncloud. It takes a couple of minutes and Uncloud walks you through it."
              : "Ask whoever looks after this Uncloud to add a Dropbox app key. Until then, bring files in from a folder on this computer instead."}
          </p>
          {canConfigure && (
            <button className="button primary" onClick={onConfigure}>
              Set up Dropbox
              <ArrowUpRight size={15} />
            </button>
          )}
        </div>
      ) : blocked === "disconnected" ? (
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
      ) : blocked === "unavailable" ? (
        <div className="notice-card">
          <h2>{place!.name} isn’t here right now</h2>
          <p className="field-help">
            Uncloud can’t find <code>{place!.path}</code>. If it’s on a drive,
            plug it back in and reload this page.
          </p>
        </div>
      ) : blocked === "gone" ? (
        <div className="notice-card">
          <h2>That place has been removed</h2>
          <p className="field-help">Choose another one above.</p>
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
                  ? "Counting the files, before anything is copied."
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
                In {sourceName}
                {onDropbox && dropbox?.accountName
                  ? ` · ${dropbox.accountName}`
                  : ""}
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
                {busy === "browse" ? "Loading…" : `Show what’s in ${sourceName}`}
              </button>
            ) : entries.length === 0 ? (
              <p className="field-help">This folder is empty.</p>
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
                        className={`muted${sizes[entry.path] && !sizes[entry.path].fits ? " will-not-fit" : ""}`}
                      >
                        {entry.isFolder
                          ? describeFolder(sizes[entry.path])
                          : formatSize(entry.size)}
                      </span>
                    </div>
                    {entry.isFolder && !sizes[entry.path] && (
                      <button
                        className="button"
                        onClick={() => void measure(entry)}
                        disabled={busy === `measure:${entry.path}`}
                      >
                        <Ruler size={15} />
                        {busy === `measure:${entry.path}`
                          ? "Measuring…"
                          : "Check size"}
                      </button>
                    )}
                    {entry.isFolder && (
                      <button
                        className="button"
                        onClick={() => void browse(entry.path)}
                        disabled={busy === "browse"}
                      >
                        Open
                        <ChevronRight size={15} />
                      </button>
                    )}
                    <button
                      className="button primary"
                      onClick={() => void bringHome(entry)}
                      disabled={job?.running || busy === entry.path}
                    >
                      <CloudDownload size={15} />
                      {job?.running && job.remotePath === entry.path
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
                  <li key={`${file.provider}:${file.remotePath}`}>
                    <File size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{file.localPath}</strong>
                      <span className="muted">
                        {formatSize(file.size)} · brought home{" "}
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
