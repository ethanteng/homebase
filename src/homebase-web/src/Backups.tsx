import { useEffect, useState } from "react";
import {
  ChevronRight,
  CircleCheck,
  File,
  Folder,
  History,
  RefreshCw,
  RotateCcw,
  X,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { BackupFolderListing, BackupList, EditedEntry } from "./api";

interface Props {
  /** After something came back, so the storage meter reads again. */
  onChanged: () => void;
  /** Opens a folder in My files. */
  open: (folder: string) => void;
}

interface Notice {
  text: string;
  folder: string;
}

const when = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const dateFormat = new Intl.DateTimeFormat(undefined, { year: "numeric", month: "short", day: "numeric" });
const folderOf = (path: string) => (path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "");
const nameOf = (path: string) => path.split("/").at(-1) || "My files";

/**
 * Your own files as they were in one of this Uncloud's nightly backups. Anything can be put back
 * where it was; nothing there now is overwritten, so what comes back beside a newer copy gets a
 * name of its own, as in the bin.
 */
export default function Backups({ onChanged, open }: Props) {
  const [list, setList] = useState<BackupList | null>(null);
  const [listError, setListError] = useState("");
  const [backup, setBackup] = useState<string | null>(null);
  const [path, setPath] = useState("");
  const [folder, setFolder] = useState<BackupFolderListing | null>(null);
  const [loading, setLoading] = useState(false);
  const [folderError, setFolderError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState<Notice | null>(null);
  const [restoring, setRestoring] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setListError("");
    api<BackupList>("/backups", { signal: controller.signal })
      .then((result) => {
        if (controller.signal.aborted) return;
        setList(result);
        setBackup((chosen) =>
          chosen && result.backups.some((known) => known.id === chosen) ? chosen : (result.backups[0]?.id ?? null),
        );
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        setListError(failure instanceof Error ? failure.message : "Couldn’t read this Uncloud’s backups.");
      });
    return () => controller.abort();
  }, [refresh]);

  useEffect(() => {
    if (!backup) return;
    const controller = new AbortController();
    // What was showing is from another backup or folder: it goes, rather than staying to be
    // restored from while the new one is read.
    setFolder(null);
    setLoading(true);
    setFolderError("");
    api<BackupFolderListing>(`/backups/${backup}/files?path=${encodeURIComponent(path)}`, { signal: controller.signal })
      .then((result) => {
        if (!controller.signal.aborted) setFolder(result);
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        setFolder(null);
        setFolderError(failure instanceof Error ? failure.message : "Couldn’t open that folder in the backup.");
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [backup, path, refresh]);

  async function restore(entry: BackupFolderListing["entries"][number]) {
    // From the backup this listing came from, whatever is chosen meanwhile.
    const from = folder?.snapshot;
    if (!from) return;
    setRestoring(entry.path);
    setError("");
    setNotice(null);
    try {
      const { items } = await api<{ items: EditedEntry[] }>(`/backups/${from}/restore`, {
        method: "POST",
        body: JSON.stringify({ paths: [entry.path] }),
      });
      const to = items[0]?.to ?? entry.path;
      const renamed =
        nameOf(to) !== entry.name
          ? ` It’s called “${nameOf(to)}”, because “${entry.name}” is still there.`
          : "";
      setNotice({ text: `Put “${entry.name}” back in ${nameOf(folderOf(to))}.${renamed}`, folder: folderOf(to) });
      onChanged();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Uncloud couldn’t restore that.");
    } finally {
      setRestoring(null);
    }
  }

  const backups = list?.backups ?? [];
  const crumbs = path ? path.split("/") : [];

  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">BACKED UP EVERY NIGHT</span>
          <h1>
            Backups<span className="heading-dot">.</span>
          </h1>
          <p>
            Your files as they were in a backup. Put anything back where it was: whatever’s there now
            stays, and what comes back sits beside it.
          </p>
        </div>
        <div className="heading-actions">
          {backups.length > 0 && (
            <label className="backup-chooser">
              <span className="sr-only">Backup from</span>
              <select
                className="path-input"
                value={backup ?? ""}
                onChange={(event) => {
                  setBackup(event.target.value);
                  setNotice(null);
                }}
              >
                {backups.map((known, index) => (
                  <option key={known.id} value={known.id}>
                    {index === 0 ? "Latest, " : ""}
                    {when.format(new Date(known.time))}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button
            className="button secondary refresh-button"
            onClick={() => setRefresh((value) => value + 1)}
            disabled={loading}
            aria-label="Refresh"
            title="Refresh"
          >
            <RefreshCw size={16} className={loading ? "spin" : ""} />
          </button>
        </div>
      </div>
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      <div className="browser-layout">
        <section className="file-panel" aria-label="Backups" aria-busy={loading}>
          {notice && (
            <div className="edit-notice" role="status">
              <CircleCheck size={16} />
              <span>{notice.text}</span>
              <button className="row-action" onClick={() => open(notice.folder)}>
                Open {nameOf(notice.folder)}
              </button>
              <button className="icon-button" aria-label="Dismiss" onClick={() => setNotice(null)}>
                <X size={16} />
              </button>
            </div>
          )}
          {backup && (
            <nav className="backup-crumbs" aria-label="Folder in the backup">
              <button className="row-action" onClick={() => setPath("")} disabled={!path}>
                My files
              </button>
              {crumbs.map((crumb, index) => (
                <span key={crumbs.slice(0, index + 1).join("/")}>
                  <ChevronRight size={14} />
                  <button
                    className="row-action"
                    onClick={() => setPath(crumbs.slice(0, index + 1).join("/"))}
                    disabled={index === crumbs.length - 1}
                  >
                    {crumb}
                  </button>
                </span>
              ))}
            </nav>
          )}
          {listError ? (
            <div className="empty-state" role="alert">
              <History size={36} />
              <h2>Couldn’t read the backups</h2>
              <p>{listError}</p>
              <button className="button secondary" onClick={() => setRefresh((value) => value + 1)}>
                Try again
              </button>
            </div>
          ) : list === null ? (
            <div className="file-loading" role="status">
              <RefreshCw className="spin" size={23} />
              <span>Reading the backups…</span>
            </div>
          ) : !list.configured ? (
            <div className="empty-state">
              <History size={36} />
              <h2>Backups aren’t set up</h2>
              <p>Whoever looks after this Uncloud can set them up in Settings, under Backing up offsite.</p>
            </div>
          ) : backups.length === 0 ? (
            <div className="empty-state">
              <History size={36} />
              <h2>No backups yet</h2>
              <p>The first one is made soon after backups are set up, then one every night.</p>
            </div>
          ) : folderError ? (
            <div className="empty-state" role="alert">
              <Folder size={36} />
              <h2>Couldn’t open that folder</h2>
              <p>{folderError}</p>
              <button className="button secondary" onClick={() => setPath("")}>
                Back to My files
              </button>
            </div>
          ) : folder === null ? (
            <div className="file-loading" role="status">
              <RefreshCw className="spin" size={23} />
              <span>Opening the backup…</span>
            </div>
          ) : folder.entries.length === 0 ? (
            <div className="empty-state">
              <Folder size={36} />
              <h2>Nothing here in this backup</h2>
              <p>This folder was empty when the backup was made.</p>
            </div>
          ) : (
            <div className="table-scroll">
              <table className="bin-table">
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Modified</th>
                    <th scope="col">Size</th>
                    <th scope="col">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {folder.entries.map((entry) => (
                    <tr key={entry.path}>
                      <td>
                        <div className="bin-name">
                          <span className={`file-icon ${entry.isDirectory ? "directory" : ""}`}>
                            {entry.isDirectory ? (
                              <Folder size={21} strokeWidth={1.6} fill="currentColor" fillOpacity={0.14} />
                            ) : (
                              <File size={21} strokeWidth={1.6} />
                            )}
                          </span>
                          {entry.isDirectory ? (
                            <button className="link-button" onClick={() => setPath(entry.path)}>
                              <strong>{entry.name}</strong>
                            </button>
                          ) : (
                            <strong>{entry.name}</strong>
                          )}
                        </div>
                      </td>
                      <td className="date-cell">{dateFormat.format(new Date(entry.modifiedAt))}</td>
                      <td className="size-cell">{entry.isDirectory ? "—" : formatSize(entry.size)}</td>
                      <td>
                        <div className="row-actions">
                          <button
                            className="row-action"
                            onClick={() => void restore(entry)}
                            disabled={restoring !== null || loading}
                            aria-label={`Restore ${entry.name}`}
                          >
                            {restoring === entry.path ? (
                              <RefreshCw size={15} className="spin" />
                            ) : (
                              <RotateCcw size={15} />
                            )}
                            Restore
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="file-footer">
            <span>
              {folder && backups.length > 0
                ? `From the backup of ${when.format(new Date(folder.time))} · ${folder.entries.length} ${folder.entries.length === 1 ? "item" : "items"}`
                : list?.configured
                  ? `${backups.length} ${backups.length === 1 ? "backup" : "backups"} kept`
                  : "Backups"}
            </span>
            <span>Only you can see your files in the backups.</span>
          </div>
        </section>
      </div>
    </>
  );
}
