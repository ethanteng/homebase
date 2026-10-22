import { useEffect, useRef, useState } from "react";
import type { DragEvent, ReactNode } from "react";
import {
  ArrowDown,
  ArrowDownToLine,
  ArrowUp,
  ChevronRight,
  CircleCheck,
  Copy,
  File,
  FileArchive,
  FileText,
  Folder,
  FolderInput,
  FolderOpen,
  FolderPlus,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Trash2,
  TriangleAlert,
  Upload,
  X,
} from "lucide-react";
import { api, downloadUrl, formatSize } from "./api";
import type { DirectoryListing, EditAction, EditedEntry, LibraryEntry } from "./api";
import EditDialog, { describe, summarize } from "./FileEdits";
import type { Edit } from "./FileEdits";
import { useFolderSizes } from "./folderSizes";
import { pickedFromDrop } from "./Uploads";
import type { Picked } from "./Uploads";

interface Props {
  rootPath: string;
  path: string;
  revision: number;
  navigate: (path: string) => void;
  onAdd: () => void;
  /** After files were copied, moved or deleted, so everything showing them reads them again. */
  onChanged: () => void;
  /** Whatever is on its way in, shown above the files it is on its way to. */
  status?: ReactNode;
  /**
   * Files and folders dropped onto the list, sent into the folder it shows — or into a folder in
   * it, when they were dropped on that folder's row. Settles once Uncloud has agreed to take them,
   * and throws with the reason when it won't.
   */
  onDropFiles: (folder: string, picked: Picked[]) => Promise<void>;
  /** Whether files from this device are already on their way; one upload runs at a time. */
  uploading: boolean;
}
type Sort = "name" | "modified" | "size";
/** A folder something held over the list would go into: a folder's row, or one in the path above. */
interface Target {
  path: string;
  name: string;
}
const nameOf = (folder: string) => (folder ? folder.split("/").at(-1)! : "My files");
const dateFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
});
function FileIcon({ entry }: { entry: LibraryEntry }) {
  if (entry.isDirectory)
    return (
      <Folder
        size={21}
        strokeWidth={1.6}
        fill="currentColor"
        fillOpacity={0.14}
      />
    );
  if (/\.(zip|tar|gz|7z)$/i.test(entry.name))
    return <FileArchive size={21} strokeWidth={1.6} />;
  if (/\.(txt|md|pdf|docx?|csv|json|html)$/i.test(entry.name))
    return <FileText size={21} strokeWidth={1.6} />;
  return <File size={21} strokeWidth={1.6} />;
}

export default function FileBrowser({
  rootPath,
  path,
  revision,
  navigate,
  onAdd,
  onChanged,
  status,
  onDropFiles,
  uploading,
}: Props) {
  const [listing, setListing] = useState<DirectoryListing | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<Sort>("name");
  const [descending, setDescending] = useState(false);
  const [selected, setSelected] = useState<LibraryEntry | null>(null);
  // Ticked for copying, moving or deleting, by path. Separate from the one file whose details
  // are open, which is only ever one.
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  const [edit, setEdit] = useState<Edit | null>(null);
  const [notice, setNotice] = useState("");
  // Why files dropped here weren't taken, such as there being no room for them, or why rows
  // dragged onto a folder couldn't go there.
  const [problem, setProblem] = useState("");
  // Files held over the list. Counted rather than tracked by where they went: entering a row is
  // also leaving the list, and not every browser says where a drag went when it left.
  const [dropping, setDropping] = useState(false);
  const depth = useRef(0);
  // The folder they're held over, which is where they'd go instead of the folder on screen.
  const [dropInto, setDropInto] = useState<Target | null>(null);
  // Rows picked up to put in another folder, and whether they'd be copied there rather than moved.
  const [dragged, setDragged] = useState<LibraryEntry[] | null>(null);
  const [copying, setCopying] = useState(false);
  // What dropping them set going, while it's under way.
  const [moving, setMoving] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    setListing(null);
    setSelected(null);
    setChecked(new Set());
    api<DirectoryListing>(`/files?${new URLSearchParams({ path })}`, {
      signal: controller.signal,
    })
      .then((result) => {
        if (!controller.signal.aborted) setListing(result);
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted)
          setError(
            error instanceof Error
              ? error.message
              : "Couldn’t read this folder.",
          );
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [rootPath, path, refresh, revision]);
  useEffect(() => {
    setQuery("");
    setNotice("");
    setProblem("");
  }, [path, rootPath]);

  const folderSizes = useFolderSizes(
    (listing?.entries ?? [])
      .filter((entry) => entry.isDirectory)
      .map((entry) => entry.path),
    (folder) => `/files/size?${new URLSearchParams({ path: folder })}`,
  );
  // A folder's size is whatever has been measured of it so far, which is nothing to start with.
  const sizeOf = (entry: LibraryEntry) =>
    entry.isDirectory ? (folderSizes[entry.path]?.bytes ?? null) : entry.size;

  function changeSort(value: Sort) {
    if (sort === value) setDescending((current) => !current);
    else {
      setSort(value);
      setDescending(false);
    }
  }
  const entries = (listing?.entries ?? [])
    .filter((entry) =>
      entry.name.toLocaleLowerCase().includes(query.toLocaleLowerCase()),
    )
    .sort((a, b) => {
      if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
      const order =
        sort === "modified"
          ? new Date(a.modifiedAt).getTime() - new Date(b.modifiedAt).getTime()
          : sort === "size"
            ? (sizeOf(a) ?? 0) - (sizeOf(b) ?? 0)
            : a.name.localeCompare(b.name, undefined, {
                numeric: true,
                sensitivity: "base",
              });
      return descending ? -order : order;
    });
  const folderCount =
    listing?.entries.filter((entry) => entry.isDirectory).length ?? 0;
  const fileCount = (listing?.entries.length ?? 0) - folderCount;
  const bytes =
    listing?.entries.reduce((sum, entry) => sum + (entry.size ?? 0), 0) ?? 0;
  const SortArrow = descending ? ArrowDown : ArrowUp;
  // Only what is on screen is acted on: a tick the filter has hidden isn't something anybody can
  // see they are about to delete.
  const chosen = entries.filter((entry) => checked.has(entry.path));
  const allChosen = entries.length > 0 && chosen.length === entries.length;

  function toggle(entry: LibraryEntry) {
    setNotice("");
    setChecked((current) => {
      const next = new Set(current);
      if (!next.delete(entry.path)) next.add(entry.path);
      return next;
    });
  }
  function toggleAll() {
    setNotice("");
    setChecked(
      allChosen ? new Set() : new Set(entries.map((entry) => entry.path)),
    );
  }
  function start(action: EditAction, targets: LibraryEntry[]) {
    setNotice("");
    setEdit({ action, entries: targets });
  }
  /** A new folder, starting from a name nothing here has. */
  function newFolder() {
    const taken = new Set(listing?.entries.map((entry) => entry.name.toLocaleLowerCase()));
    let name = "New folder";
    for (let number = 2; taken.has(name.toLocaleLowerCase()); number++) name = `New folder ${number}`;
    setNotice("");
    setEdit({ action: "folder", entries: [], name });
  }

  // Rows picked up here are moving within My files; anything else held over the list is this
  // list's to take only if it carries files from outside the page.
  const holding = (event: DragEvent) =>
    dragged ? "rows" : event.dataTransfer.types.includes("Files") ? "files" : null;
  /** The folder whose row or path the pointer is on, if it's on one; anywhere else in the list is the folder on screen. */
  const folderUnder = (event: DragEvent): Target | null => {
    const folder = (event.target as Element).closest?.("[data-folder]")?.getAttribute("data-folder");
    return folder == null ? null : { path: folder, name: nameOf(folder) };
  };
  /** Where rows held here would go: a folder that isn't one of them, other than the one they're in. */
  const rowsInto = (event: DragEvent) => {
    const under = folderUnder(event);
    return under && under.path !== path && !dragged?.some((entry) => entry.path === under.path)
      ? under
      : null;
  };
  // As in Finder, holding Option copies rather than moves; Ctrl does it on Windows.
  const copies = (event: DragEvent) => event.altKey || event.ctrlKey;

  function pickUp(event: DragEvent, entry: LibraryEntry) {
    // A ticked row brings everything else ticked with it; any other comes alone.
    const rows = checked.has(entry.path) ? chosen : [entry];
    event.dataTransfer.effectAllowed = "copyMove";
    // Not every browser starts a drag that carries nothing, though only this page reads it.
    event.dataTransfer.setData(
      "application/x-uncloud-paths",
      JSON.stringify(rows.map((row) => row.path)),
    );
    // What's being carried, by the pointer: a row's own picture would show only one of several.
    const label = document.createElement("div");
    label.className = "drag-label";
    label.textContent = describe(rows);
    label.style.left = `${event.clientX - 12}px`;
    label.style.top = `${event.clientY - 12}px`;
    document.body.append(label);
    event.dataTransfer.setDragImage?.(label, 12, 12);
    window.setTimeout(() => label.remove());
    // Any notice above the list stays until they're dropped: taking it away now would move every
    // row up under the pointer, and they'd land in a folder nobody aimed for.
    setDragged(rows);
  }
  function letGo() {
    depth.current = 0;
    setDropping(false);
    setDropInto(null);
    setDragged(null);
    setCopying(false);
  }
  async function put(rows: LibraryEntry[], into: Target, copy: boolean) {
    const action = copy ? "copy" : "move";
    setChecked(new Set());
    setNotice("");
    setProblem("");
    setMoving(`${copy ? "Copying" : "Moving"} ${describe(rows)} to ${into.name}…`);
    try {
      const result = await api<{ items?: EditedEntry[] }>(`/files/${action}`, {
        method: "POST",
        body: JSON.stringify({ paths: rows.map((row) => row.path), destination: into.path }),
      });
      setNotice(summarize({ action, entries: rows }, into.path, result.items ?? []));
      onChanged();
    } catch (failure) {
      setProblem(failure instanceof Error ? failure.message : "Uncloud couldn’t make that change.");
    } finally {
      setMoving("");
    }
  }

  function drop(event: DragEvent) {
    const kind = holding(event);
    if (kind === "rows") {
      event.preventDefault();
      const into = rowsInto(event);
      const rows = dragged!;
      letGo();
      if (into) void put(rows, into, copies(event));
      return;
    }
    if (kind !== "files") return;
    event.preventDefault();
    letGo();
    if (uploading) return;
    setNotice("");
    setProblem("");
    const into = folderUnder(event)?.path ?? path;
    // Taken during the drop itself: a browser only lets a dropped folder be read while it happens.
    const picking = pickedFromDrop(event.dataTransfer);
    void picking
      .then((picked) => onDropFiles(into, picked))
      .catch((failure: unknown) =>
        setProblem(failure instanceof Error ? failure.message : "Those files couldn’t be added."),
      );
  }
  const here = dropInto?.name ?? nameOf(path);
  const banner = dragged
    ? dropInto
      ? `${copying ? "Copy" : "Move"} ${describe(dragged)} to ${dropInto.name}`
      : `Drop on a folder to ${copying ? "copy" : "move"} ${dragged.length === 1 ? "it" : "them"} there`
    : uploading
      ? "Some files are still on their way. Add more once they’re in."
      : `Drop to add to ${here}`;

  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">{path ? "MY FILES" : "ONLY YOU CAN SEE THESE"}</span>
          <h1>
            {path ? path.split("/").at(-1) : "My files"}
            <span className="heading-dot">.</span>
          </h1>
          <p>Kept at home, not in the cloud. Nobody else here can open them.</p>
        </div>
        <div className="heading-actions">
          <button
            className="button secondary refresh-button"
            onClick={() => setRefresh((value) => value + 1)}
            disabled={loading}
            aria-label="Refresh"
            title="Refresh"
          >
            <RefreshCw size={16} className={loading ? "spin" : ""} />
          </button>
          <button
            className="button secondary new-folder-button"
            onClick={newFolder}
            disabled={loading || !!error}
            aria-label="New folder"
            title="New folder"
          >
            <FolderPlus size={16} />
            <span>New folder</span>
          </button>
          <button className="button primary" onClick={onAdd}>
            <Plus size={16} />
            Add files
          </button>
        </div>
      </div>
      {status}
      <div className="library-summary">
        <span>
          <FolderOpen size={18} />
          {loading
            ? "Reading your folder…"
            : `${folderCount} ${folderCount === 1 ? "folder" : "folders"}`}
        </span>
        <span>
          {loading ? "—" : `${fileCount} ${fileCount === 1 ? "file" : "files"}`}
        </span>
        <span>{loading ? "—" : `${formatSize(bytes)} in files`}</span>
        <span className="summary-local">
          <i className="status-dot" />
          Stored at home
        </span>
      </div>
      <div className="browser-layout">
        <section
          className={`file-panel${dropping ? " dropping" : ""}`}
          aria-label="File browser"
          aria-busy={loading}
          onDragEnter={(event) => {
            const kind = holding(event);
            if (!kind) return;
            event.preventDefault();
            depth.current++;
            if (kind === "files") setDropping(true);
          }}
          onDragOver={(event) => {
            const kind = holding(event);
            if (!kind) return;
            let under: Target | null;
            if (kind === "rows") {
              under = rowsInto(event);
              setCopying(copies(event));
              // Anywhere but a folder they can go into, letting go does nothing, and the pointer says so.
              if (!under) {
                setDropInto(null);
                return;
              }
              event.preventDefault();
              event.dataTransfer.dropEffect = copies(event) ? "copy" : "move";
            } else {
              event.preventDefault();
              event.dataTransfer.dropEffect = uploading ? "none" : "copy";
              under = uploading ? null : folderUnder(event);
            }
            setDropInto((current) => (current?.path === under?.path ? current : under));
          }}
          onDragLeave={(event) => {
            if (!holding(event)) return;
            depth.current = Math.max(0, depth.current - 1);
            if (depth.current > 0) return;
            setDropping(false);
            setDropInto(null);
          }}
          onDrop={drop}
        >
          {/* The rows stay in sight, so the one being dropped on can be seen to light up. */}
          {(dropping || dragged) && (
            <div className="drop-banner" aria-hidden="true">
              {dragged ? (
                copying ? <Copy size={16} /> : <FolderInput size={16} />
              ) : (
                <Upload size={16} />
              )}
              <span>{banner}</span>
            </div>
          )}
          <div className="file-toolbar">
            {/* Each folder above this one is somewhere to drop things too. */}
            <nav aria-label="Folder path" className="breadcrumbs">
              <button
                data-folder={path ? "" : undefined}
                className={path && dropInto?.path === "" ? "drop-target" : undefined}
                onClick={() => navigate("")}
              >
                My files
              </button>
              {path
                .split("/")
                .filter(Boolean)
                .map((part, index, parts) => {
                  const folder = parts.slice(0, index + 1).join("/");
                  const current = index === parts.length - 1;
                  return (
                    <span key={index}>
                      <ChevronRight size={14} />
                      <button
                        aria-current={current ? "location" : undefined}
                        data-folder={current ? undefined : folder}
                        className={!current && dropInto?.path === folder ? "drop-target" : undefined}
                        onClick={() => navigate(folder)}
                      >
                        {part}
                      </button>
                    </span>
                  );
                })}
            </nav>
            <div className="filter-input">
              <Search size={16} />
              <input
                type="search"
                aria-label="Filter this folder"
                placeholder="Filter this folder…"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
              />
            </div>
          </div>
          {chosen.length > 0 && !moving ? (
            <div
              className="selection-bar"
              role="toolbar"
              aria-label="Selected items"
            >
              <span>{chosen.length} selected</span>
              <div className="selection-actions">
                <button
                  className="row-action"
                  onClick={() => start("copy", chosen)}
                >
                  <Copy size={15} />
                  Copy to…
                </button>
                <button
                  className="row-action"
                  onClick={() => start("move", chosen)}
                >
                  <FolderInput size={15} />
                  Move to…
                </button>
                {/* One name at a time: there is no one new name for several things. */}
                {chosen.length === 1 && (
                  <button
                    className="row-action"
                    onClick={() => start("rename", chosen)}
                  >
                    <Pencil size={15} />
                    Rename…
                  </button>
                )}
                <button
                  className="row-action danger"
                  onClick={() => start("delete", chosen)}
                >
                  <Trash2 size={15} />
                  Delete
                </button>
                <button
                  className="icon-button"
                  aria-label="Clear selection"
                  onClick={() => setChecked(new Set())}
                >
                  <X size={16} />
                </button>
              </div>
            </div>
          ) : moving ? (
            <div className="edit-notice" role="status">
              <RefreshCw size={16} className="spin" />
              <span>{moving}</span>
            </div>
          ) : problem ? (
            <div className="edit-notice failed" role="alert">
              <TriangleAlert size={16} />
              <span>{problem}</span>
              <button
                className="icon-button"
                aria-label="Dismiss"
                onClick={() => setProblem("")}
              >
                <X size={16} />
              </button>
            </div>
          ) : (
            notice && (
              <div className="edit-notice" role="status">
                <CircleCheck size={16} />
                <span>{notice}</span>
                <button
                  className="icon-button"
                  aria-label="Dismiss"
                  onClick={() => setNotice("")}
                >
                  <X size={16} />
                </button>
              </div>
            )
          )}
          {error ? (
            <div className="empty-state" role="alert">
              <FolderOpen size={36} />
              <h2>Couldn’t open this folder</h2>
              <p>{error}</p>
              <div className="empty-actions">
                <button
                  className="button secondary"
                  onClick={() => setRefresh((value) => value + 1)}
                >
                  Try again
                </button>
                {path && (
                  <button
                    className="button secondary"
                    onClick={() => navigate("")}
                  >
                    Back to My files
                  </button>
                )}
              </div>
            </div>
          ) : loading ? (
            <div className="file-loading" role="status">
              <RefreshCw className="spin" size={23} />
              <span>Opening your files…</span>
            </div>
          ) : entries.length === 0 ? (
            <div className="empty-state">
              <FolderOpen size={38} />
              <h2>
                {query
                  ? "No matching files"
                  : path
                    ? "This folder is empty"
                    : "Nothing here yet"}
              </h2>
              {(query || !path) && (
                <p>
                  {query
                    ? "Try a different name, or clear the filter."
                    : "Bring in the files you keep elsewhere — in Dropbox, on a drive, in a folder. Only you will be able to see them."}
                </p>
              )}
              {query ? (
                <button
                  className="button secondary"
                  onClick={() => setQuery("")}
                >
                  Clear filter
                </button>
              ) : (
                !path && (
                  <button className="button primary" onClick={onAdd}>
                    <Plus size={16} />
                    Add files
                  </button>
                )
              )}
            </div>
          ) : (
            <div className="table-scroll">
              <table>
                <thead>
                  <tr>
                    {(["name", "modified", "size"] as const).map((value) => {
                      const sortButton = (
                        <button onClick={() => changeSort(value)}>
                          {value === "modified"
                            ? "Date modified"
                            : value[0].toUpperCase() + value.slice(1)}
                          {sort === value && <SortArrow size={13} />}
                        </button>
                      );
                      return (
                        <th
                          key={value}
                          scope="col"
                          aria-sort={
                            sort === value
                              ? descending
                                ? "descending"
                                : "ascending"
                              : "none"
                          }
                        >
                          {value === "name" ? (
                            <div className="name-head">
                              <input
                                type="checkbox"
                                className="check-box"
                                aria-label="Select everything shown"
                                checked={allChosen}
                                ref={(box) => {
                                  if (box)
                                    box.indeterminate =
                                      chosen.length > 0 && !allChosen;
                                }}
                                onChange={toggleAll}
                              />
                              {sortButton}
                            </div>
                          ) : (
                            sortButton
                          )}
                        </th>
                      );
                    })}
                    <th scope="col">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <tr
                      key={entry.path}
                      data-folder={entry.isDirectory ? entry.path : undefined}
                      // Picked up to put in a folder here or above, as a Move to… or Copy to… would.
                      draggable={!moving}
                      onDragStart={(event) => pickUp(event, entry)}
                      onDragEnd={letGo}
                      className={
                        dropInto?.path === entry.path
                          ? "drop-target"
                          : selected?.path === entry.path ||
                              checked.has(entry.path)
                            ? "selected"
                            : ""
                      }
                    >
                      <td>
                        <div className="name-cell">
                          <label className="check">
                            <input
                              type="checkbox"
                              className="check-box"
                              aria-label={`Select ${entry.name}`}
                              checked={checked.has(entry.path)}
                              onChange={() => toggle(entry)}
                            />
                          </label>
                          <button
                            className="file-name"
                            aria-label={
                              entry.isDirectory
                                ? `Open ${entry.name}`
                                : `Details for ${entry.name}`
                            }
                            onClick={() =>
                              entry.isDirectory
                                ? navigate(entry.path)
                                : setSelected(entry)
                            }
                          >
                            <span
                              className={`file-icon ${entry.isDirectory ? "directory" : ""}`}
                            >
                              <FileIcon entry={entry} />
                            </span>
                            <span>{entry.name}</span>
                          </button>
                        </div>
                      </td>
                      <td className="date-cell">
                        {dateFormat.format(new Date(entry.modifiedAt))}
                      </td>
                      <td className="size-cell">
                        {entry.isDirectory &&
                        folderSizes[entry.path] === undefined ? (
                          <span className="measuring" title="Measuring…">
                            …
                          </span>
                        ) : (
                          formatSize(sizeOf(entry))
                        )}
                      </td>
                      <td className="action-cell">
                        {entry.isDirectory ? (
                          <button
                            className="icon-button"
                            aria-label={`Browse ${entry.name}`}
                            onClick={() => navigate(entry.path)}
                          >
                            <ChevronRight size={17} />
                          </button>
                        ) : (
                          <a
                            className="icon-button"
                            href={downloadUrl(entry.path)}
                            download
                            aria-label={`Download ${entry.name}`}
                          >
                            <ArrowDownToLine size={17} />
                          </a>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="file-footer">
            <span>
              {loading
                ? "Reading folder"
                : error
                  ? "Folder unavailable"
                  : `${entries.length} ${entries.length === 1 ? "item" : "items"}${query ? ` of ${listing?.entries.length ?? 0}` : ""}`}
            </span>
            <span>
              {listing?.skippedCount
                ? `${listing.skippedCount} item${listing.skippedCount === 1 ? "" : "s"} can’t be shown here`
                : ""}
            </span>
          </div>
        </section>
        {selected && (
          <aside className="details-panel" aria-label="File details">
            <div className="details-header">
              <span>FILE DETAILS</span>
              <button
                className="icon-button"
                aria-label="Close file details"
                onClick={() => setSelected(null)}
              >
                <X size={17} />
              </button>
            </div>
            <div className="details-icon">
              <FileIcon entry={selected} />
            </div>
            <h2>{selected.name}</h2>
            <dl>
              <dt>Size</dt>
              <dd>{formatSize(selected.size)}</dd>
              <dt>Modified</dt>
              <dd>{new Date(selected.modifiedAt).toLocaleString()}</dd>
              <dt>Location</dt>
              <dd className="detail-path">{selected.path}</dd>
            </dl>
            <a
              className="button primary"
              href={downloadUrl(selected.path)}
              download
            >
              <ArrowDownToLine size={16} />
              Download file
            </a>
            <div className="details-actions">
              <button
                className="row-action"
                onClick={() => start("copy", [selected])}
              >
                <Copy size={15} />
                Copy to…
              </button>
              <button
                className="row-action"
                onClick={() => start("move", [selected])}
              >
                <FolderInput size={15} />
                Move to…
              </button>
              <button
                className="row-action"
                onClick={() => start("rename", [selected])}
              >
                <Pencil size={15} />
                Rename…
              </button>
              <button
                className="row-action danger"
                onClick={() => start("delete", [selected])}
              >
                <Trash2 size={15} />
                Delete
              </button>
            </div>
            <p className="field-help">
              An ordinary file, kept at home. Only you can see it.
            </p>
          </aside>
        )}
      </div>
      <div className="library-note">
        <ShieldMark />
        <p>
          Your files stay yours. They’re kept on this Uncloud at home, not
          with a cloud company.
        </p>
      </div>
      <EditDialog
        edit={edit}
        from={path}
        onClose={() => setEdit(null)}
        onDone={(summary) => {
          setEdit(null);
          setChecked(new Set());
          setNotice(summary);
          onChanged();
        }}
      />
    </>
  );
}

function ShieldMark() {
  return (
    <svg
      width="20"
      height="22"
      viewBox="0 0 20 22"
      fill="none"
      aria-hidden="true"
    >
      <path
        d="M10 2 3 5v6c0 4 7 8 7 8s7-4 7-8V5l-7-3Z"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <path
        d="m6.5 10 2.5 2.5 4.5-4.5"
        stroke="currentColor"
        strokeWidth="1.4"
      />
    </svg>
  );
}
