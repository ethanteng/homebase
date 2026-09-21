import { useCallback, useEffect, useState } from "react";
import {
  ArrowUpRight,
  ChevronRight,
  CloudDownload,
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
    if (status?.connected) {
      void loadImported();
      void loadStorage();
    }
  }, [status?.connected, loadImported, loadStorage]);

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
      const outcome = await api<ImportResult>("/imports", {
        method: "POST",
        body: JSON.stringify({ remotePath: entry.pathLower }),
      });
      setResult(outcome);
      const brought =
        outcome.importedCount === 0
          ? `Nothing new to bring home from ${entry.name}.`
          : `Brought ${outcome.importedCount} file${outcome.importedCount === 1 ? "" : "s"} home (${formatSize(outcome.bytes)}).`;
      // A folder left behind is the part worth saying out loud: the rest of the import succeeded,
      // so nothing else on screen would tell you that anything is missing.
      const problems = outcome.skipped.filter((skip) => !skip.expected).length;
      setNotice(
        problems === 0
          ? brought
          : `${brought} ${problems} item${problems === 1 ? "" : "s"} not brought home — see below.`,
      );
      await loadImported();
      await loadStorage();
      // Files landed in the library, so the browser's view of it is now out of date.
      onImported();
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
                      disabled={busy === entry.pathLower}
                    >
                      <CloudDownload size={15} />
                      {busy === entry.pathLower ? "Bringing…" : "Bring home"}
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
