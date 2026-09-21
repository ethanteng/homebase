import { useCallback, useEffect, useState } from "react";
import {
  ArrowUpRight,
  ChevronRight,
  CloudDownload,
  File,
  Folder,
  LoaderCircle,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type {
  DropboxEntry,
  DropboxStatus,
  ImportResult,
  ImportedFile,
} from "./api";

const dateFormat = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
});

export default function DropboxPanel() {
  const [status, setStatus] = useState<DropboxStatus | null>(null);
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
    if (status?.connected) void loadImported();
  }, [status?.connected, loadImported]);

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

  const bringHome = (entry: DropboxEntry) =>
    run(entry.pathLower, async () => {
      const outcome = await api<ImportResult>("/imports", {
        method: "POST",
        body: JSON.stringify({ remotePath: entry.pathLower }),
      });
      setResult(outcome);
      setNotice(
        outcome.importedCount === 0
          ? `Nothing new to bring home from ${entry.name}.`
          : `Brought ${outcome.importedCount} file${outcome.importedCount === 1 ? "" : "s"} home (${formatSize(outcome.bytes)}).`,
      );
      await loadImported();
    });

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
                      <span className="muted">
                        {entry.isFolder ? "Folder" : formatSize(entry.size)}
                      </span>
                    </div>
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

          {result && result.skipped.length > 0 && (
            <section className="import-section">
              <h2>Not brought home</h2>
              <ul className="import-list">
                {result.skipped.map((skip) => (
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
