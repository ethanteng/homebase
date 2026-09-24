import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import {
  ArrowDown,
  ArrowDownToLine,
  ArrowUp,
  ChevronRight,
  File,
  FileArchive,
  FileText,
  Folder,
  FolderOpen,
  Plus,
  RefreshCw,
  Search,
  X,
} from "lucide-react";
import { api, downloadUrl, formatSize } from "./api";
import type { DirectoryListing, LibraryEntry } from "./api";

interface Props {
  rootPath: string;
  path: string;
  revision: number;
  navigate: (path: string) => void;
  onAdd: () => void;
  /** Whatever is on its way in, shown above the files it is on its way to. */
  status?: ReactNode;
}
type Sort = "name" | "modified" | "size";
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
  status,
}: Props) {
  const [listing, setListing] = useState<DirectoryListing | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<Sort>("name");
  const [descending, setDescending] = useState(false);
  const [selected, setSelected] = useState<LibraryEntry | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    setListing(null);
    setSelected(null);
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
  }, [path, rootPath]);

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
            ? (a.size ?? 0) - (b.size ?? 0)
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
          className="file-panel"
          aria-label="File browser"
          aria-busy={loading}
        >
          <div className="file-toolbar">
            <nav aria-label="Folder path" className="breadcrumbs">
              <button onClick={() => navigate("")}>My files</button>
              {path
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
                        navigate(parts.slice(0, index + 1).join("/"))
                      }
                    >
                      {part}
                    </button>
                  </span>
                ))}
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
                    {(["name", "modified", "size"] as const).map((value) => (
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
                        <button onClick={() => changeSort(value)}>
                          {value === "modified"
                            ? "Date modified"
                            : value[0].toUpperCase() + value.slice(1)}
                          {sort === value && <SortArrow size={13} />}
                        </button>
                      </th>
                    ))}
                    <th scope="col">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <tr
                      key={entry.path}
                      className={
                        selected?.path === entry.path ? "selected" : ""
                      }
                    >
                      <td>
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
                      </td>
                      <td className="date-cell">
                        {dateFormat.format(new Date(entry.modifiedAt))}
                      </td>
                      <td className="size-cell">{formatSize(entry.size)}</td>
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
