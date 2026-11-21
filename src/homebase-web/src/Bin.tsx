import { useEffect, useState } from "react";
import {
  CircleCheck,
  File,
  Folder,
  RefreshCw,
  RotateCcw,
  Trash2,
  X,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { BinEntry, BinListing, EditedEntry } from "./api";

interface Props {
  /** After something left the bin for good, so the storage meter reads again. */
  onChanged: () => void;
  /** Opens a folder in My files. */
  open: (folder: string) => void;
}

interface Notice {
  text: string;
  // Where something was put back, so it can be gone to.
  folder?: string;
}

const dateFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
});
const folderOf = (path: string) =>
  path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "";
const nameOf = (path: string) => path.split("/").at(-1) || "My files";

function timeLeft(entry: BinEntry) {
  const days = Math.ceil(
    (new Date(entry.expiresAt).getTime() - Date.now()) / 86_400_000,
  );
  return days <= 1 ? "Deleted forever today" : `${days} days left`;
}

/**
 * What was deleted from My files, kept for 30 days. Anything here can be put back where it came
 * from, or deleted for good to make room.
 */
export default function Bin({ onChanged, open }: Props) {
  const [listing, setListing] = useState<BinListing | null>(null);
  // Kept apart: a load that failed has finished, so trying again must be possible, and it says
  // nothing about the bin being empty.
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState<Notice | null>(null);
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setLoadError("");
    api<BinListing>("/bin", { signal: controller.signal })
      .then((result) => {
        if (!controller.signal.aborted) setListing(result);
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        setListing(null);
        setLoadError(
          failure instanceof Error ? failure.message : "Couldn’t open the bin.",
        );
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [refresh]);

  async function change(work: () => Promise<Notice>) {
    setBusy(true);
    setError("");
    setNotice(null);
    try {
      setNotice(await work());
      onChanged();
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Uncloud couldn’t make that change.",
      );
    } finally {
      setBusy(false);
      setRefresh((value) => value + 1);
    }
  }

  function putBack(entry: BinEntry) {
    void change(async () => {
      const { items } = await api<{ items: EditedEntry[] }>("/bin/restore", {
        method: "POST",
        body: JSON.stringify({ ids: [entry.id] }),
      });
      const to = items[0]?.to ?? entry.path;
      const folder = folderOf(to);
      const renamed =
        nameOf(to) !== entry.name
          ? ` Renamed to “${nameOf(to)}” because “${entry.name}” already exists there.`
          : "";
      return {
        text: `Restored “${entry.name}” to ${nameOf(folder)}.${renamed}`,
        folder,
      };
    });
  }

  function deleteForGood(entry: BinEntry) {
    if (
      !window.confirm(
        `Delete “${entry.name}” forever? This can’t be undone.`,
      )
    )
      return;
    void change(async () => {
      await api("/bin/delete", {
        method: "POST",
        body: JSON.stringify({ ids: [entry.id] }),
      });
      return { text: `Deleted “${entry.name}” forever.` };
    });
  }

  function empty() {
    const count = listing?.entries.length ?? 0;
    if (
      !window.confirm(
        `Empty Trash? ${count === 1 ? "1 item" : `${count} items`} ` +
          `(${formatSize(listing?.bytes ?? 0)}) will be deleted forever. This can’t be undone.`,
      )
    )
      return;
    void change(async () => {
      await api("/bin/empty", { method: "POST" });
      return { text: "Trash emptied." };
    });
  }

  const entries = listing?.entries ?? [];

  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Trash</h1>
          <p>Deleted items are kept for 30 days.</p>
        </div>
        <div className="heading-actions">
          <button
            className="button secondary refresh-button"
            onClick={() => setRefresh((value) => value + 1)}
            disabled={loading || busy}
            aria-label="Refresh"
            title="Refresh"
          >
            <RefreshCw size={16} className={loading ? "spin" : ""} />
          </button>
          <button
            className="button danger"
            onClick={empty}
            disabled={entries.length === 0 || busy}
          >
            <Trash2 size={16} />
            Empty Trash
          </button>
        </div>
      </div>
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      <div className="browser-layout">
        <section
          className="file-panel"
          aria-label="Trash"
          aria-busy={listing === null}
        >
          {notice && (
            <div className="edit-notice" role="status">
              <CircleCheck size={16} />
              <span>{notice.text}</span>
              {notice.folder !== undefined && (
                <button
                  className="row-action"
                  onClick={() => open(notice.folder!)}
                >
                  Open {nameOf(notice.folder)}
                </button>
              )}
              <button
                className="icon-button"
                aria-label="Dismiss"
                onClick={() => setNotice(null)}
              >
                <X size={16} />
              </button>
            </div>
          )}
          {loadError ? (
            <div className="empty-state" role="alert">
              <Trash2 size={36} />
              <h2>Couldn’t open Trash</h2>
              <p>{loadError}</p>
              <button
                className="button secondary"
                onClick={() => setRefresh((value) => value + 1)}
                disabled={loading}
              >
                Try again
              </button>
            </div>
          ) : listing === null ? (
            <div className="file-loading" role="status">
              <RefreshCw className="spin" size={23} />
              <span>Opening Trash…</span>
            </div>
          ) : entries.length === 0 ? (
            <div className="empty-state">
              <Trash2 size={36} />
              <h2>Trash is empty</h2>
              <p>Deleted items stay here for 30 days.</p>
            </div>
          ) : (
            <div className="table-scroll">
              <table className="bin-table">
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Deleted</th>
                    <th scope="col">Size</th>
                    <th scope="col">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <tr key={entry.id}>
                      <td>
                        <div className="bin-name">
                          <span
                            className={`file-icon ${entry.isDirectory ? "directory" : ""}`}
                          >
                            {entry.isDirectory ? (
                              <Folder
                                size={21}
                                strokeWidth={1.6}
                                fill="currentColor"
                                fillOpacity={0.14}
                              />
                            ) : (
                              <File size={21} strokeWidth={1.6} />
                            )}
                          </span>
                          <span>
                            <strong>{entry.name}</strong>
                            <span className="muted">
                              From {nameOf(folderOf(entry.path))}
                            </span>
                          </span>
                        </div>
                      </td>
                      <td className="date-cell">
                        {dateFormat.format(new Date(entry.deletedAt))}
                        <span className="muted">{timeLeft(entry)}</span>
                      </td>
                      <td className="size-cell">{formatSize(entry.size)}</td>
                      <td>
                        <div className="row-actions">
                          <button
                            className="row-action"
                            onClick={() => putBack(entry)}
                            disabled={busy}
                            aria-label={`Restore ${entry.name}`}
                          >
                            <RotateCcw size={15} />
                            Restore
                          </button>
                          <button
                            className="row-action danger"
                            onClick={() => deleteForGood(entry)}
                            disabled={busy}
                            aria-label={`Delete forever ${entry.name}`}
                          >
                            <Trash2 size={15} />
                            Delete forever
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
              {listing === null
                ? loadError
                  ? "Trash unavailable"
                  : "Loading"
                : `${entries.length} ${entries.length === 1 ? "item" : "items"} · ${formatSize(listing.bytes)}`}
            </span>
          </div>
        </section>
      </div>
    </>
  );
}
