import { useCallback, useEffect, useState } from "react";
import {
  ArrowUpRight,
  Check,
  ChevronRight,
  CloudDownload,
  CloudOff,
  File,
  Folder,
  LoaderCircle,
  PencilLine,
  RefreshCw,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type {
  DropboxEntry,
  DropboxStatus,
  SyncOutcome,
  SyncState,
  SyncedFileStatus,
} from "./api";

const STATE_LABELS: Record<SyncState, string> = {
  current: "Up to date",
  remoteChanged: "New version on Dropbox",
  localEdited: "You changed this copy",
  localMissing: "Missing from your folder",
  remoteUnavailable: "Couldn’t check Dropbox",
};

function StateBadge({ state }: { state: SyncState }) {
  const icon =
    state === "current" ? (
      <Check size={14} />
    ) : state === "localEdited" ? (
      <PencilLine size={14} />
    ) : state === "remoteUnavailable" ? (
      <CloudOff size={14} />
    ) : (
      <TriangleAlert size={14} />
    );
  return (
    <span className={`sync-badge sync-${state}`}>
      {icon}
      {STATE_LABELS[state]}
    </span>
  );
}

export default function DropboxPanel() {
  const [status, setStatus] = useState<DropboxStatus | null>(null);
  const [tracked, setTracked] = useState<SyncedFileStatus[]>([]);
  const [entries, setEntries] = useState<DropboxEntry[] | null>(null);
  const [remotePath, setRemotePath] = useState("");
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const loadTracked = useCallback(async () => {
    try {
      setTracked(await api<SyncedFileStatus[]>("/sync"));
    } catch {
      // A failed poll shouldn't replace what's already on screen.
    }
  }, []);

  useEffect(() => {
    const result = new URLSearchParams(window.location.search).get("dropbox");
    if (result) {
      setNotice(
        result === "connected"
          ? "Dropbox connected."
          : result === "denied"
            ? "Dropbox sign-in was cancelled."
            : "Dropbox sign-in didn’t finish. Try again.",
      );
      window.history.replaceState(null, "", window.location.pathname);
    }
    api<DropboxStatus>("/providers/dropbox")
      .then(setStatus)
      .catch((problem: unknown) =>
        setError(
          problem instanceof Error ? problem.message : "Couldn’t reach Homebase.",
        ),
      );
  }, []);

  useEffect(() => {
    if (!status?.connected) return;
    void loadTracked();
    // Polling is what makes a change made elsewhere show up without a reload.
    const timer = window.setInterval(() => void loadTracked(), 10000);
    return () => window.clearInterval(timer);
  }, [status?.connected, loadTracked]);

  async function run(label: string, action: () => Promise<void>) {
    setBusy(label);
    setError("");
    try {
      await action();
    } catch (problem: unknown) {
      setError(
        problem instanceof Error ? problem.message : "That didn’t work.",
      );
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

  const sync = (entry: DropboxEntry) =>
    run(entry.pathLower, async () => {
      await api<SyncOutcome>("/sync", {
        method: "POST",
        body: JSON.stringify({ remotePath: entry.pathLower }),
      });
      setNotice(`${entry.name} is now in your Homebase folder.`);
      await loadTracked();
    });

  const checkForChanges = () =>
    run("refresh", async () => {
      const outcomes = await api<SyncOutcome[]>("/sync/refresh", {
        method: "POST",
      });
      const updated = outcomes.filter((outcome) => outcome.downloaded).length;
      setNotice(
        updated === 0
          ? "Everything is already up to date."
          : `Updated ${updated} file${updated === 1 ? "" : "s"} from Dropbox.`,
      );
      await loadTracked();
    });

  const forget = (remote: string) =>
    run(remote, async () => {
      await api("/sync/forget", {
        method: "POST",
        body: JSON.stringify({ remotePath: remote }),
      });
      await loadTracked();
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
            Copy a file onto storage you own, and keep that copy current as the
            original changes.
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
        <div className="setup-card">
          <h2>Homebase needs a Dropbox app key</h2>
          <p className="field-help">
            Create an app at dropbox.com/developers/apps with the{" "}
            <code>account_info.read</code>, <code>files.metadata.read</code> and{" "}
            <code>files.content.read</code> permissions, then start Homebase
            with <code>Homebase__Dropbox__AppKey</code> set to its app key.
            Homebase only ever reads from Dropbox.
          </p>
        </div>
      ) : !status.connected ? (
        <div className="setup-card">
          <h2>Connect your Dropbox</h2>
          <p className="field-help">
            Homebase asks for read-only access. It can copy files down to your
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
          <section className="sync-section">
            <div className="sync-section-head">
              <h2>
                In your Homebase folder
                {status.accountName ? ` · from ${status.accountName}` : ""}
              </h2>
              <button
                className="refresh-button"
                onClick={() => void checkForChanges()}
                disabled={busy === "refresh" || tracked.length === 0}
              >
                <RefreshCw
                  size={15}
                  className={busy === "refresh" ? "spin" : undefined}
                />
                Check for changes
              </button>
            </div>
            {tracked.length === 0 ? (
              <p className="field-help">
                Nothing yet. Choose a file from Dropbox below to bring it home.
              </p>
            ) : (
              <ul className="sync-list">
                {tracked.map((item) => (
                  <li key={item.file.remotePath}>
                    <File size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{item.file.localPath}</strong>
                      <span className="muted">
                        {formatSize(item.file.size)} · from{" "}
                        {item.file.remotePath}
                      </span>
                    </div>
                    <StateBadge state={item.state} />
                    <button
                      className="button"
                      onClick={() => void forget(item.file.remotePath)}
                      disabled={busy === item.file.remotePath}
                    >
                      Stop syncing
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="sync-section">
            <div className="sync-section-head">
              <h2>On Dropbox</h2>
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
              <ul className="sync-list">
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
                    {entry.isFolder ? (
                      <button
                        className="button"
                        onClick={() => void browse(entry.pathLower)}
                        disabled={busy === "browse"}
                      >
                        Open
                        <ChevronRight size={15} />
                      </button>
                    ) : (
                      <button
                        className="button primary"
                        onClick={() => void sync(entry)}
                        disabled={busy === entry.pathLower}
                      >
                        <CloudDownload size={15} />
                        {busy === entry.pathLower ? "Bringing…" : "Bring home"}
                      </button>
                    )}
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
