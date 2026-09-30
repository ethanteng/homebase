import { useEffect, useState } from "react";
import type { SyntheticEvent } from "react";
import { ChevronRight, Folder, RefreshCw, X } from "lucide-react";
import { api } from "./api";
import type {
  DirectoryListing,
  EditAction,
  EditedEntry,
  LibraryEntry,
} from "./api";

export interface Edit {
  action: EditAction;
  entries: LibraryEntry[];
}

interface Props {
  /** What is being done, and to what. Null while nothing is. */
  edit: Edit | null;
  /** The folder the entries are in, which is where choosing somewhere for them starts. */
  from: string;
  onClose: () => void;
  /** Called once the change is made, with a sentence saying what happened. */
  onDone: (summary: string) => void;
}

const nameOf = (path: string) => path.split("/").at(-1) || "My files";

function describe(entries: LibraryEntry[]) {
  return entries.length === 1
    ? `“${entries[0].name}”`
    : `${entries.length} items`;
}

/**
 * What happened, in a sentence. Nothing is ever overwritten, so an item arriving where its name
 * is taken gets a new one — and somebody looking for it under the old name needs to be told.
 */
function summarize(edit: Edit, destination: string, items: EditedEntry[]) {
  if (edit.action === "delete") return `Deleted ${describe(edit.entries)}.`;
  const done = `${edit.action === "copy" ? "Copied" : "Moved"} ${describe(edit.entries)} to ${nameOf(destination)}.`;
  const renamed = items.filter((item) => nameOf(item.from) !== nameOf(item.to));
  if (renamed.length === 0) return done;
  if (renamed.length === 1)
    return `${done} It’s called “${nameOf(renamed[0].to)}” there, because “${nameOf(renamed[0].from)}” was taken.`;
  return `${done} ${renamed.length} of them have new names there, because theirs were taken.`;
}

/** Copying or moving files somewhere else in My files, or deleting them, one dialog for each. */
export default function EditDialog({ edit, from, onClose, onDone }: Props) {
  const [element, setElement] = useState<HTMLDialogElement | null>(null);
  const [folder, setFolder] = useState(from);
  const [folders, setFolders] = useState<LibraryEntry[] | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const choosing = edit !== null && edit.action !== "delete";

  // Every edit starts afresh, looking at the folder its files are in.
  useEffect(() => {
    if (!edit) return;
    setFolder(from);
    setError("");
  }, [edit, from]);

  useEffect(() => {
    if (edit) {
      if (!element?.open) element?.showModal();
    } else element?.close();
  }, [edit, element]);

  useEffect(() => {
    if (!choosing) return;
    const controller = new AbortController();
    setFolders(null);
    setError("");
    api<DirectoryListing>(`/files?${new URLSearchParams({ path: folder })}`, {
      signal: controller.signal,
    })
      .then((listing) => {
        if (!controller.signal.aborted)
          setFolders(listing.entries.filter((entry) => entry.isDirectory));
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        setFolders([]);
        setError(
          failure instanceof Error
            ? failure.message
            : "Couldn’t open this folder.",
        );
      });
    return () => controller.abort();
  }, [choosing, folder]);

  // Walking away halfway through a change would leave nobody told how it ended.
  function cancel(event: SyntheticEvent) {
    if (busy) event.preventDefault();
  }

  async function submit() {
    if (!edit) return;
    setBusy(true);
    setError("");
    try {
      const paths = edit.entries.map((entry) => entry.path);
      const result = await api<{ items?: EditedEntry[] }>(
        `/files/${edit.action}`,
        {
          method: "POST",
          body: JSON.stringify(
            edit.action === "delete" ? { paths } : { paths, destination: folder },
          ),
        },
      );
      onDone(summarize(edit, folder, result.items ?? []));
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Uncloud couldn’t make that change.",
      );
    } finally {
      setBusy(false);
    }
  }

  // A folder can't be moved into itself, so the ones on the move can't be opened as somewhere to go.
  const moving = new Set(
    edit?.action === "move"
      ? edit.entries.filter((entry) => entry.isDirectory).map((entry) => entry.path)
      : [],
  );
  const alreadyThere = edit?.action === "move" && folder === from;
  const one = edit?.entries.length === 1;
  const folderCount = edit?.entries.filter((entry) => entry.isDirectory).length ?? 0;

  return (
    <dialog
      ref={setElement}
      className="settings-dialog edit-dialog"
      onCancel={cancel}
      onClose={onClose}
      aria-labelledby="edit-title"
    >
      {edit && (
        <>
          <div className="dialog-header">
            <div>
              <span className="eyebrow">MY FILES</span>
              <h2 id="edit-title">
                {edit.action === "delete"
                  ? `Delete ${describe(edit.entries)}?`
                  : `${edit.action === "copy" ? "Copy" : "Move"} ${describe(edit.entries)}`}
              </h2>
            </div>
            <button
              className="icon-button"
              aria-label="Close"
              onClick={onClose}
              disabled={busy}
            >
              <X size={20} />
            </button>
          </div>
          {edit.action === "delete" ? (
            <div className="edit-body">
              <p>
                {one
                  ? edit.entries[0].isDirectory
                    ? `“${edit.entries[0].name}” and everything in it will be deleted from Uncloud.`
                    : `“${edit.entries[0].name}” will be deleted from Uncloud.`
                  : folderCount > 0
                    ? `These ${edit.entries.length} items, and everything in the folders among them, will be deleted from Uncloud.`
                    : `These ${edit.entries.length} files will be deleted from Uncloud.`}{" "}
                <strong>This can’t be undone.</strong>
              </p>
              <p className="field-help">
                Anything that syncs with your computers is deleted there too.
              </p>
            </div>
          ) : (
            <div className="edit-body">
              <p className="field-help">
                {edit.action === "copy"
                  ? "Choose the folder to put the copies in."
                  : `Choose the folder to move ${one ? "it" : "them"} to.`}
              </p>
              <nav aria-label="Destination folder" className="breadcrumbs">
                <button
                  aria-current={folder === "" ? "location" : undefined}
                  onClick={() => setFolder("")}
                >
                  My files
                </button>
                {folder
                  .split("/")
                  .filter(Boolean)
                  .map((part, index, parts) => (
                    <span key={index}>
                      <ChevronRight size={14} />
                      <button
                        aria-current={
                          index === parts.length - 1 ? "location" : undefined
                        }
                        onClick={() =>
                          setFolder(parts.slice(0, index + 1).join("/"))
                        }
                      >
                        {part}
                      </button>
                    </span>
                  ))}
              </nav>
              <ul className="folder-list" aria-label="Folders" aria-busy={folders === null}>
                {folders === null ? (
                  <li className="folder-list-note">
                    <RefreshCw className="spin" size={15} />
                    Opening…
                  </li>
                ) : folders.length === 0 ? (
                  <li className="folder-list-note">No folders in here.</li>
                ) : (
                  folders.map((entry) => (
                    <li key={entry.path}>
                      <button
                        className="folder-choice"
                        disabled={moving.has(entry.path) || busy}
                        title={
                          moving.has(entry.path)
                            ? "A folder can’t be moved into itself."
                            : undefined
                        }
                        onClick={() => setFolder(entry.path)}
                      >
                        <Folder
                          size={18}
                          strokeWidth={1.6}
                          fill="currentColor"
                          fillOpacity={0.14}
                        />
                        <span>{entry.name}</span>
                        <ChevronRight size={15} />
                      </button>
                    </li>
                  ))
                )}
              </ul>
              {alreadyThere && (
                <p className="field-help">
                  {one ? "It’s" : "They’re"} already in this folder. Choose
                  another one.
                </p>
              )}
            </div>
          )}
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <div className="edit-actions">
            <button
              className="button secondary"
              onClick={onClose}
              disabled={busy}
            >
              Cancel
            </button>
            <button
              className={`button ${edit.action === "delete" ? "danger" : "primary"}`}
              onClick={() => void submit()}
              disabled={busy || alreadyThere || (choosing && folders === null)}
            >
              {busy && <RefreshCw className="spin" size={15} />}
              {edit.action === "delete"
                ? busy
                  ? "Deleting…"
                  : "Delete"
                : edit.action === "copy"
                  ? busy
                    ? "Copying…"
                    : "Copy here"
                  : busy
                    ? "Moving…"
                    : "Move here"}
            </button>
          </div>
        </>
      )}
    </dialog>
  );
}
