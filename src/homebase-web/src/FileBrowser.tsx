import { useEffect, useRef, useState } from "react";
import type { DragEvent, ReactNode } from "react";
import {
  ArrowDown,
  ArrowDownToLine,
  ArrowUp,
  ChevronRight,
  CircleCheck,
  Copy,
  Eye,
  File,
  FileArchive,
  FileText,
  Folder,
  FolderInput,
  FolderOpen,
  FolderPlus,
  Image,
  Link2,
  Lock,
  Music,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Trash2,
  TriangleAlert,
  Upload,
  Video,
  X,
} from "lucide-react";
import { api, downloadUrl, fileUrl, formatSize } from "./api";
import type { DirectoryListing, EditAction, EditedEntry, LibraryEntry, ShareList } from "./api";
import EditDialog, { describe, summarize } from "./FileEdits";
import type { Edit } from "./FileEdits";
import { useFolderSizes } from "./folderSizes";
import { Viewer } from "./Preview";
import ShareDialog from "./ShareDialog";
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
   * it, when they were dropped on that folder's row. They go after whatever is already on its way.
   * Throws with the reason when there's nothing in them to send.
   */
  onDropFiles: (folder: string, picked: Picked[]) => Promise<void>;
  isAdmin?: boolean;
  /** Opens the list of every shared link. */
  onShowLinks?: () => void;
  onOpenSettings?: () => void;
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
  if (entry.preview === "image") return <Image size={21} strokeWidth={1.6} />;
  if (entry.preview === "video") return <Video size={21} strokeWidth={1.6} />;
  if (entry.preview === "audio") return <Music size={21} strokeWidth={1.6} />;
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
  isAdmin = false,
  onShowLinks,
  onOpenSettings,
}: Props) {
  const [listing, setListing] = useState<DirectoryListing | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<Sort>("name");
  const [descending, setDescending] = useState(false);
  const [selected, setSelected] = useState<LibraryEntry | null>(null);
  // The file open in the viewer, over the list, by path.
  const [viewing, setViewing] = useState<string | null>(null);
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
  // What's being shared by link, while the dialog for it is open.
  const [sharing, setSharing] = useState<LibraryEntry[] | null>(null);
  // Everything this account shares by link, by path, so a shared row can say so.
  const [shared, setShared] = useState<ReadonlySet<string>>(new Set());
  const [linksChanged, setLinksChanged] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    api<ShareList>("/shares", { signal: controller.signal })
      .then((list) => {
        if (!controller.signal.aborted)
          setShared(new Set(list.links.flatMap((link) => link.items.map((item) => item.path))));
      })
      .catch(() => {
        // Only a marker beside a name; the files themselves are what matter.
      });
    return () => controller.abort();
  }, [rootPath, revision, refresh, linksChanged]);
  /** Whether this, or something inside it, is shared by link. */
  const sharesAny = (entries: LibraryEntry[]) =>
    entries.some((entry) =>
      [...shared].some((path) => path === entry.path || path.startsWith(`${entry.path}/`)),
    );

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    setListing(null);
    setSelected(null);
    setViewing(null);
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
  // Everything under this folder, not only what sits in it: a folder counts once its size has
  // been measured, so the totals run upwards as sizes arrive, and say so until they're done.
  const total = { files: 0, bytes: 0, measuring: 0, unmeasured: 0 };
  for (const entry of listing?.entries ?? []) {
    const size = entry.isDirectory
      ? folderSizes[entry.path]
      : { bytes: entry.size ?? 0, files: 1 };
    if (size === undefined) total.measuring++;
    else if (size === null) total.unmeasured++;
    else {
      total.files += size.files;
      total.bytes += size.bytes;
    }
  }
  const counting = total.measuring > 0 && total.files === 0;
  // Every folder failed and nothing sits loose beside them: a 0 would read as an empty folder,
  // when it's only that nothing could be counted.
  const uncounted = !counting && total.unmeasured > 0 && total.files === 0;
  const soFar = total.measuring > 0 ? " so far" : "";
  const SortArrow = descending ? ArrowDown : ArrowUp;
  // Only what is on screen is acted on: a tick the filter has hidden isn't something anybody can
  // see they are about to delete.
  const chosen = entries.filter((entry) => checked.has(entry.path));
  // The viewer steps through the files as they're listed, filter and order included.
  const files = entries.filter((entry) => !entry.isDirectory);
  const viewIndex = viewing === null ? -1 : files.findIndex((entry) => entry.path === viewing);
  /** Shows one file in the viewer, and keeps its details open beside the list once it's closed. */
  function view(index: number | null) {
    const entry = index === null ? null : files[index];
    setViewing(entry?.path ?? null);
    if (entry) setSelected(entry);
  }
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
    : `Drop to add to ${here}`;

  return (
    <>
      <div className="page-heading">
        <div className="page-title">
          <h1>{path ? path.split("/").at(-1) : "My files"}</h1>
          {!path && (
            <span className="private-chip">
              <Lock size={15} />
              Private
            </span>
          )}
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
          {loading || uncounted
            ? "—"
            : counting
              ? "Counting files…"
              : `${total.files.toLocaleString()} ${total.files === 1 ? "file" : "files"}${soFar}`}
        </span>
        <span>
          {loading || uncounted
            ? "—"
            : counting
              ? "Measuring…"
              : `${formatSize(total.bytes)} in files${soFar}`}
        </span>
        {!loading && total.unmeasured > 0 && (
          <span className="summary-unmeasured">
            {`${total.unmeasured} ${total.unmeasured === 1 ? "folder" : "folders"} couldn’t be measured`}
          </span>
        )}
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
              event.dataTransfer.dropEffect = "copy";
              under = folderUnder(event);
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
                <button
                  className="row-action"
                  onClick={() => {
                    setNotice("");
                    setSharing(chosen);
                  }}
                >
                  <Link2 size={15} />
                  Share…
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
                    : "Add files from this device, Dropbox or a drive. Only you can see them."}
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
                            {shared.has(entry.path) && (
                              <Link2
                                size={15}
                                className="shared-mark"
                                aria-label="Shared by link"
                              >
                                <title>Shared by link</title>
                              </Link2>
                            )}
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
            <DetailsPicture
              key={selected.path}
              entry={selected}
              onOpen={() => setViewing(selected.path)}
            />
            <h2>{selected.name}</h2>
            <dl>
              <dt>Size</dt>
              <dd>{formatSize(selected.size)}</dd>
              <dt>Modified</dt>
              <dd>{new Date(selected.modifiedAt).toLocaleString()}</dd>
              <dt>Location</dt>
              <dd className="detail-path">{selected.path}</dd>
            </dl>
            <div className="details-buttons">
              {selected.preview && (
                <button
                  className="button secondary"
                  onClick={() => setViewing(selected.path)}
                >
                  <Eye size={16} />
                  Preview
                </button>
              )}
              <a
                className="button primary"
                href={downloadUrl(selected.path)}
                download
              >
                <ArrowDownToLine size={16} />
                Download file
              </a>
            </div>
            <div className="details-actions">
              <button
                className="row-action"
                onClick={() => setSharing([selected])}
              >
                <Link2 size={15} />
                {shared.has(selected.path) ? "Shared…" : "Share…"}
              </button>
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
          </aside>
        )}
      </div>
      <Viewer
        files={files}
        index={viewIndex < 0 ? null : viewIndex}
        onIndex={view}
        src={(entry) => fileUrl(entry.path)}
        download={(entry) => downloadUrl(entry.path)}
      />
      <ShareDialog
        entries={sharing}
        isAdmin={isAdmin}
        onClose={() => setSharing(null)}
        onChanged={() => setLinksChanged((value) => value + 1)}
        onShowLinks={() => {
          setSharing(null);
          onShowLinks?.();
        }}
        onOpenSettings={
          onOpenSettings &&
          (() => {
            setSharing(null);
            onOpenSettings();
          })
        }
      />
      <EditDialog
        edit={edit}
        from={path}
        shared={edit?.action === "delete" && sharesAny(edit.entries)}
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

/**
 * The file itself at the top of its details, when it's a picture or a video this browser can draw;
 * otherwise an icon for its kind. Either way, what can be shown opens in the viewer from here.
 */
function DetailsPicture({ entry, onOpen }: { entry: LibraryEntry; onOpen: () => void }) {
  const [broken, setBroken] = useState(false);
  const icon = (
    <div className="details-icon">
      <FileIcon entry={entry} />
    </div>
  );
  if (broken || (entry.preview !== "image" && entry.preview !== "video")) return icon;
  const src = fileUrl(entry.path);
  return (
    <button className="details-preview" aria-label={`Preview ${entry.name}`} onClick={onOpen}>
      {entry.preview === "image" ? (
        <img src={src} alt="" onError={() => setBroken(true)} />
      ) : (
        // Its first frame, as a still: Safari draws nothing until it's told where to start.
        <video src={`${src}#t=0.1`} preload="metadata" muted playsInline onError={() => setBroken(true)} />
      )}
    </button>
  );
}
