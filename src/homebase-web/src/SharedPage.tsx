import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import {
  ArrowDownToLine,
  ChevronLeft,
  ChevronRight,
  Eye,
  File,
  FileText,
  Folder,
  FolderOpen,
  Image,
  Link2Off,
  LoaderCircle,
  Moon,
  Music,
  RefreshCw,
  Sun,
  Video,
  X,
} from "lucide-react";
import { api, formatSize, sharedPath, sharedUrl } from "./api";
import type { SharedEntry, SharedLinkInfo, SharedListing } from "./api";
import { useFolderSizes } from "./folderSizes";
import { useTheme } from "./theme";

interface Props {
  /** What the link's address ends in, which is the whole of what lets this page in. */
  token: string;
}

/** Where in what was shared the page is, kept in the address so back and forward work. */
function readPath() {
  try {
    return decodeURIComponent(window.location.hash.slice(1));
  } catch {
    return "";
  }
}

const dateFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
});

/** Text bigger than this is offered to download rather than put on the page. */
const MAX_TEXT = 2 * 1024 * 1024;

function EntryIcon({ entry, size = 21 }: { entry: SharedEntry; size?: number }) {
  if (entry.isDirectory)
    return <Folder size={size} strokeWidth={1.6} fill="currentColor" fillOpacity={0.14} />;
  switch (entry.preview) {
    case "image":
      return <Image size={size} strokeWidth={1.6} />;
    case "video":
      return <Video size={size} strokeWidth={1.6} />;
    case "audio":
      return <Music size={size} strokeWidth={1.6} />;
    case "pdf":
    case "text":
      return <FileText size={size} strokeWidth={1.6} />;
    default:
      return <File size={size} strokeWidth={1.6} />;
  }
}

/** The logo and the theme switch. Nothing here leads into Uncloud: whoever has a link may have no account. */
function SharedHeader() {
  const { theme, toggle } = useTheme();
  const label = theme === "dark" ? "Switch to light theme" : "Switch to dark theme";
  return (
    <header className="app-header">
      <span className="brand">
        <img className="brand-mark" src="/uncloud.png" width="36" height="36" alt="" />
        <span className="brand-text">
          <span className="brand-name">uncloud</span>
        </span>
      </span>
      <div className="header-actions">
        <button className="round-button" onClick={toggle} aria-label={label} title={label}>
          {theme === "dark" ? <Sun size={20} /> : <Moon size={20} />}
        </button>
      </div>
    </header>
  );
}

function Frame({ children }: { children: ReactNode }) {
  return (
    <div className="app-shell shared-shell">
      <SharedHeader />
      <main>
        <div className="main-content">{children}</div>
      </main>
    </div>
  );
}

/**
 * What a shared link leads to, for anybody who has it, signed in or not: a file, a folder to look
 * through, or several things somebody picked together. Pictures, video, sound, PDFs and text are
 * shown here; anything can be downloaded, a folder as a zip, only when whoever shared it said so.
 */
export default function SharedPage({ token }: Props) {
  const [info, setInfo] = useState<SharedLinkInfo | null>(null);
  const [failure, setFailure] = useState("");
  const [path, setPath] = useState(readPath);

  useEffect(() => {
    const controller = new AbortController();
    api<SharedLinkInfo>(`/shared/${encodeURIComponent(token)}`, { signal: controller.signal })
      .then(setInfo)
      .catch((error: unknown) => {
        if (!controller.signal.aborted)
          setFailure(error instanceof Error ? error.message : "Uncloud didn’t answer.");
      });
    return () => controller.abort();
  }, [token]);

  useEffect(() => {
    const onHash = () => setPath(readPath());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const only = info?.items.length === 1 ? info.items[0] : null;
  const title = only ? only.name : info ? `${info.items.length} items` : "";
  useEffect(() => {
    if (title) document.title = `${title} · Uncloud`;
  }, [title]);

  const navigate = useCallback((next: string) => {
    window.location.hash = encodeURIComponent(next);
    setPath(next);
    window.scrollTo({ top: 0 });
  }, []);

  if (failure)
    return (
      <Frame>
        <div className="empty-state connection-error" role="alert">
          <Link2Off size={36} />
          <h1>This link doesn’t work</h1>
          <p>{failure}</p>
          <p className="muted">Ask whoever sent it for a new one.</p>
        </div>
      </Frame>
    );

  if (!info)
    return (
      <Frame>
        <div className="file-loading" role="status">
          <LoaderCircle className="spin" size={24} />
          Opening…
        </div>
      </Frame>
    );

  if (info.items.length === 0)
    return (
      <Frame>
        <div className="empty-state connection-error" role="status">
          <FolderOpen size={36} />
          <h1>Nothing here any more</h1>
          <p>What was shared by this link has been moved or deleted.</p>
        </div>
      </Frame>
    );

  const access = (
    <span className="private-chip">
      {info.canDownload ? <ArrowDownToLine size={15} /> : <Eye size={15} />}
      {info.canDownload ? "You can download" : "View only"}
    </span>
  );
  const heading = (actions?: ReactNode) => (
    <div className="page-heading">
      <div>
        <div className="page-title">
          <h1>{title}</h1>
          {access}
        </div>
        <p>Shared by {info.sharedBy}</p>
      </div>
      {actions && <div className="heading-actions">{actions}</div>}
    </div>
  );

  // One file: it's the page.
  if (only && !only.isDirectory)
    return (
      <Frame>
        {heading(
          info.canDownload && (
            <a className="button primary" href={sharedUrl(token, "download", only.path)} download>
              <ArrowDownToLine size={16} />
              Download
            </a>
          ),
        )}
        <div className="shared-single">
          <Preview token={token} entry={only} canDownload={info.canDownload} />
        </div>
      </Frame>
    );

  // A folder, or several things: looked through like My files. A link to one folder starts in it.
  const base = only ? only.path : "";
  return (
    <Frame>
      <SharedFolder
        token={token}
        info={info}
        base={base}
        path={path || base}
        navigate={navigate}
        heading={heading}
      />
    </Frame>
  );
}

function SharedFolder({
  token,
  info,
  base,
  path,
  navigate,
  heading,
}: {
  token: string;
  info: SharedLinkInfo;
  /** Where the link starts: its one folder, or the list of what it shares. */
  base: string;
  path: string;
  navigate: (path: string) => void;
  heading: (actions?: ReactNode) => ReactNode;
}) {
  const [listing, setListing] = useState<SharedListing | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [viewing, setViewing] = useState<number | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setListing(null);
    setError("");
    setViewing(null);
    api<SharedListing>(sharedPath(token, "files", path), { signal: controller.signal })
      .then((result) => {
        if (!controller.signal.aborted) setListing(result);
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setError(failure instanceof Error ? failure.message : "Couldn’t open this folder.");
      });
    return () => controller.abort();
  }, [token, path, refresh]);

  const sizes = useFolderSizes(
    (listing?.entries ?? []).filter((entry) => entry.isDirectory).map((entry) => entry.path),
    (folder) => sharedPath(token, "size", folder),
  );
  const entries = listing?.entries ?? [];
  const files = entries.filter((entry) => !entry.isDirectory);

  // The way back up, starting from where the link starts rather than anywhere above it.
  const rootLabel = base || "Shared files";
  const below = base ? (path === base ? "" : path.slice(base.length + 1)) : path;
  const crumbs = below.split("/").filter(Boolean);

  return (
    <>
      {heading(
        info.canDownload && entries.length > 0 && (
          <a className="button primary" href={sharedUrl(token, "zip", path)} download>
            <ArrowDownToLine size={16} />
            {path === base ? "Download all" : "Download this folder"}
          </a>
        ),
      )}
      <div className="browser-layout">
        <section className="file-panel" aria-label="Shared files" aria-busy={!listing && !error}>
          <div className="file-toolbar">
            <nav aria-label="Folder path" className="breadcrumbs">
              <button
                aria-current={crumbs.length === 0 ? "location" : undefined}
                onClick={() => navigate(base)}
              >
                {rootLabel.split("/").at(-1)}
              </button>
              {crumbs.map((part, index) => {
                const folder = [base, ...crumbs.slice(0, index + 1)].filter(Boolean).join("/");
                return (
                  <span key={index}>
                    <ChevronRight size={14} />
                    <button
                      aria-current={index === crumbs.length - 1 ? "location" : undefined}
                      onClick={() => navigate(folder)}
                    >
                      {part}
                    </button>
                  </span>
                );
              })}
            </nav>
          </div>
          {error ? (
            <div className="empty-state" role="alert">
              <FolderOpen size={36} />
              <h2>Couldn’t open this folder</h2>
              <p>{error}</p>
              <div className="empty-actions">
                <button className="button secondary" onClick={() => setRefresh((value) => value + 1)}>
                  Try again
                </button>
                {path !== base && (
                  <button className="button secondary" onClick={() => navigate(base)}>
                    Back to the start
                  </button>
                )}
              </div>
            </div>
          ) : !listing ? (
            <div className="file-loading" role="status">
              <RefreshCw className="spin" size={23} />
              <span>Opening…</span>
            </div>
          ) : entries.length === 0 ? (
            <div className="empty-state">
              <FolderOpen size={38} />
              <h2>This folder is empty</h2>
            </div>
          ) : (
            <div className="table-scroll">
              <table className="shared-table">
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Date modified</th>
                    <th scope="col">Size</th>
                    <th scope="col">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <tr key={entry.path}>
                      <td>
                        <button
                          className="file-name"
                          aria-label={entry.isDirectory ? `Open ${entry.name}` : `View ${entry.name}`}
                          onClick={() =>
                            entry.isDirectory ? navigate(entry.path) : setViewing(files.indexOf(entry))
                          }
                        >
                          <span className={`file-icon ${entry.isDirectory ? "directory" : ""}`}>
                            <EntryIcon entry={entry} />
                          </span>
                          <span>{entry.name}</span>
                        </button>
                      </td>
                      <td className="date-cell">{dateFormat.format(new Date(entry.modifiedAt))}</td>
                      <td className="size-cell">
                        {entry.isDirectory && sizes[entry.path] === undefined ? (
                          <span className="measuring" title="Measuring…">
                            …
                          </span>
                        ) : (
                          formatSize(entry.isDirectory ? (sizes[entry.path]?.bytes ?? null) : entry.size)
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
                          info.canDownload && (
                            <a
                              className="icon-button"
                              href={sharedUrl(token, "download", entry.path)}
                              download
                              aria-label={`Download ${entry.name}`}
                            >
                              <ArrowDownToLine size={17} />
                            </a>
                          )
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="file-footer">
            <span>{listing ? `${entries.length} ${entries.length === 1 ? "item" : "items"}` : ""}</span>
            <span>
              {listing?.skippedCount
                ? `${listing.skippedCount} item${listing.skippedCount === 1 ? "" : "s"} can’t be shown here`
                : ""}
            </span>
          </div>
        </section>
      </div>
      <Viewer
        token={token}
        files={files}
        index={viewing}
        canDownload={info.canDownload}
        onIndex={setViewing}
      />
    </>
  );
}

/** One file at a time, over the folder it's in, with the others in that folder a key press away. */
function Viewer({
  token,
  files,
  index,
  canDownload,
  onIndex,
}: {
  token: string;
  files: SharedEntry[];
  index: number | null;
  canDownload: boolean;
  onIndex: (index: number | null) => void;
}) {
  const [element, setElement] = useState<HTMLDialogElement | null>(null);
  const entry = index === null ? null : files[index];

  useEffect(() => {
    if (entry) {
      if (!element?.open) element?.showModal();
    } else element?.close();
  }, [entry, element]);

  useEffect(() => {
    if (index === null) return;
    const step = (event: KeyboardEvent) => {
      if (event.key === "ArrowLeft" && index > 0) onIndex(index - 1);
      if (event.key === "ArrowRight" && index < files.length - 1) onIndex(index + 1);
    };
    window.addEventListener("keydown", step);
    return () => window.removeEventListener("keydown", step);
  }, [index, files.length, onIndex]);

  return (
    <dialog
      ref={setElement}
      className="viewer-dialog"
      onClose={() => onIndex(null)}
      aria-label={entry ? entry.name : "File"}
    >
      {entry && index !== null && (
        <>
          <div className="viewer-header">
            <div className="viewer-title">
              <strong>{entry.name}</strong>
              <span>
                {formatSize(entry.size)}
                {files.length > 1 && ` · ${index + 1} of ${files.length}`}
              </span>
            </div>
            <div className="viewer-actions">
              {files.length > 1 && (
                <>
                  <button
                    className="icon-button"
                    aria-label="Previous"
                    title="Previous"
                    disabled={index === 0}
                    onClick={() => onIndex(index - 1)}
                  >
                    <ChevronLeft size={20} />
                  </button>
                  <button
                    className="icon-button"
                    aria-label="Next"
                    title="Next"
                    disabled={index === files.length - 1}
                    onClick={() => onIndex(index + 1)}
                  >
                    <ChevronRight size={20} />
                  </button>
                </>
              )}
              {canDownload && (
                <a
                  className="icon-button"
                  href={sharedUrl(token, "download", entry.path)}
                  download
                  aria-label={`Download ${entry.name}`}
                  title="Download"
                >
                  <ArrowDownToLine size={19} />
                </a>
              )}
              <button className="icon-button" aria-label="Close" title="Close" onClick={() => onIndex(null)}>
                <X size={20} />
              </button>
            </div>
          </div>
          <div className="viewer-body">
            <Preview key={entry.path} token={token} entry={entry} canDownload={canDownload} />
          </div>
        </>
      )}
    </dialog>
  );
}

/**
 * A file shown as what it is. With a link that's only to look at, there's no download button and
 * the browser's own save menu is kept out of the way, though anything on a screen can still be kept.
 */
function Preview({
  token,
  entry,
  canDownload,
}: {
  token: string;
  entry: SharedEntry;
  canDownload: boolean;
}) {
  const [broken, setBroken] = useState(false);
  const src = sharedUrl(token, "file", entry.path);
  const lookOnly = canDownload ? {} : { onContextMenu: (event: { preventDefault: () => void }) => event.preventDefault() };

  if (broken)
    return <Unshowable token={token} entry={entry} canDownload={canDownload} why="This browser can’t show this file." />;
  switch (entry.preview) {
    case "image":
      return (
        <img
          className="preview-image"
          src={src}
          alt={entry.name}
          draggable={canDownload}
          onError={() => setBroken(true)}
          {...lookOnly}
        />
      );
    case "video":
      return (
        <video
          className="preview-video"
          src={src}
          controls
          preload="metadata"
          controlsList={canDownload ? undefined : "nodownload"}
          onError={() => setBroken(true)}
          {...lookOnly}
        />
      );
    case "audio":
      return (
        <div className="preview-audio">
          <Music size={42} strokeWidth={1.4} />
          <audio
            src={src}
            controls
            preload="metadata"
            controlsList={canDownload ? undefined : "nodownload"}
            onError={() => setBroken(true)}
            {...lookOnly}
          />
        </div>
      );
    case "pdf":
      // Chrome's and Edge's PDF viewers have their own save button, which they leave out when asked.
      return <iframe className="preview-pdf" src={canDownload ? src : `${src}#toolbar=0`} title={entry.name} />;
    case "text":
      return entry.size !== null && entry.size > MAX_TEXT ? (
        <Unshowable token={token} entry={entry} canDownload={canDownload} why="This file is too long to show here." />
      ) : (
        <TextPreview src={src} />
      );
    default:
      return <Unshowable token={token} entry={entry} canDownload={canDownload} />;
  }
}

/** Plain text, put on the page as text whatever it holds. */
function TextPreview({ src }: { src: string }) {
  const [text, setText] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    fetch(src, { signal: controller.signal, credentials: "same-origin" })
      .then((response) => {
        if (!response.ok) throw new Error();
        return response.text();
      })
      .then(setText)
      .catch(() => {
        if (!controller.signal.aborted) setFailed(true);
      });
    return () => controller.abort();
  }, [src]);
  if (failed) return <p className="preview-note">Couldn’t open this file.</p>;
  if (text === null)
    return (
      <div className="file-loading" role="status">
        <LoaderCircle className="spin" size={22} />
        Opening…
      </div>
    );
  return <pre className="preview-text">{text}</pre>;
}

function Unshowable({
  token,
  entry,
  canDownload,
  why = "This kind of file can’t be shown in a browser.",
}: {
  token: string;
  entry: SharedEntry;
  canDownload: boolean;
  why?: string;
}) {
  return (
    <div className="preview-unshowable">
      <span className="file-icon">
        <EntryIcon entry={entry} size={30} />
      </span>
      <strong>{entry.name}</strong>
      <p>
        {why}{" "}
        {canDownload
          ? "Download it to open it."
          : "Whoever shared it lets people look but not download, so it can’t be opened here."}
      </p>
      {canDownload && (
        <a className="button primary" href={sharedUrl(token, "download", entry.path)} download>
          <ArrowDownToLine size={16} />
          Download
        </a>
      )}
    </div>
  );
}
