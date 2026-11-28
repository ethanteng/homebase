import { useEffect, useState } from "react";
import {
  ArrowDownToLine,
  ChevronLeft,
  ChevronRight,
  File,
  FileText,
  Folder,
  Image,
  LoaderCircle,
  Music,
  Video,
  X,
} from "lucide-react";
import { formatSize } from "./api";
import type { PreviewKind } from "./api";

/** What the page needs to know of a file to show it, whether it's in My files or a shared link. */
export interface Showable {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number | null;
  preview: PreviewKind | null;
}

/** Text bigger than this is offered to download rather than put on the page. */
const MAX_TEXT = 2 * 1024 * 1024;

export function EntryIcon({ entry, size = 21 }: { entry: Showable; size?: number }) {
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

/** One file at a time, over the folder it's in, with the others in that folder a key press away. */
export function Viewer<Entry extends Showable>({
  files,
  index,
  onIndex,
  src,
  download,
}: {
  files: Entry[];
  index: number | null;
  onIndex: (index: number | null) => void;
  /** Where a file is shown from. */
  src: (entry: Entry) => string;
  /** Where a file is downloaded from, or null when it can only be looked at. */
  download: ((entry: Entry) => string) | null;
}) {
  const [element, setElement] = useState<HTMLDialogElement | null>(null);
  const entry = index === null ? null : (files[index] ?? null);

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
              {download && (
                <a
                  className="icon-button"
                  href={download(entry)}
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
            <Preview
              key={entry.path}
              entry={entry}
              src={src(entry)}
              download={download && download(entry)}
            />
          </div>
        </>
      )}
    </dialog>
  );
}

/**
 * A file shown as what it is. With nowhere to download it from — a link that's only to look at —
 * the browser's own save menu is kept out of the way too, though anything on a screen can still be kept.
 */
export function Preview({
  entry,
  src,
  download,
}: {
  entry: Showable;
  src: string;
  download: string | null;
}) {
  const [broken, setBroken] = useState(false);
  const lookOnly = download ? {} : { onContextMenu: (event: { preventDefault: () => void }) => event.preventDefault() };

  if (broken) return <Unshowable entry={entry} download={download} why="This browser can’t show this file." />;
  switch (entry.preview) {
    case "image":
      return (
        <img
          className="preview-image"
          src={src}
          alt={entry.name}
          draggable={!!download}
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
          controlsList={download ? undefined : "nodownload"}
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
            controlsList={download ? undefined : "nodownload"}
            onError={() => setBroken(true)}
            {...lookOnly}
          />
        </div>
      );
    case "pdf":
      // Chrome's and Edge's PDF viewers have their own save button, which they leave out when asked.
      return <iframe className="preview-pdf" src={download ? src : `${src}#toolbar=0`} title={entry.name} />;
    case "text":
      return entry.size !== null && entry.size > MAX_TEXT ? (
        <Unshowable entry={entry} download={download} why="This file is too long to show here." />
      ) : (
        <TextPreview src={src} />
      );
    default:
      return <Unshowable entry={entry} download={download} />;
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
  entry,
  download,
  why = "This kind of file can’t be shown in a browser.",
}: {
  entry: Showable;
  download: string | null;
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
        {download
          ? "Download it to open it."
          : "Whoever shared it lets people look but not download, so it can’t be opened here."}
      </p>
      {download && (
        <a className="button primary" href={download} download>
          <ArrowDownToLine size={16} />
          Download
        </a>
      )}
    </div>
  );
}
