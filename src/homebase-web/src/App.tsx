import { useEffect, useRef, useState } from "react";
import {
  ArrowUpRight,
  CloudDownload,
  Files,
  Laptop,
  FolderOpen,
  HardDrive,
  House,
  LoaderCircle,
  Settings2,
  X,
} from "lucide-react";
import { api } from "./api";
import type { LibraryState } from "./api";
import DropboxPanel from "./DropboxPanel";
import NodesPanel from "./NodesPanel";
import FileBrowser from "./FileBrowser";
import FolderSetup from "./FolderSetup";

function readPath() {
  try {
    return decodeURIComponent(window.location.hash.slice(1));
  } catch {
    return "";
  }
}

export default function App() {
  const [library, setLibrary] = useState<LibraryState | null>(null);
  const [error, setError] = useState("");
  const [path, setPath] = useState(readPath);
  const [revision, setRevision] = useState(0);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [view, setView] = useState<"files" | "dropbox" | "nodes">(
    () => (window.location.search.includes("dropbox=") ? "dropbox" : "files"),
  );
  const dialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    api<LibraryState>("/library", { signal: controller.signal })
      .then(setLibrary)
      .catch((error: unknown) => {
        if (!controller.signal.aborted)
          setError(
            error instanceof Error
              ? error.message
              : "Couldn’t connect to Uncloud.",
          );
      });
    return () => controller.abort();
  }, []);
  useEffect(() => {
    const onHash = () => setPath(readPath());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);
  useEffect(() => {
    if (settingsOpen) dialog.current?.showModal();
    else dialog.current?.close();
  }, [settingsOpen]);

  function navigate(nextPath: string) {
    window.location.hash = encodeURIComponent(nextPath);
    setPath(nextPath);
    window.scrollTo({ top: 0 });
  }
  function onSaved(next: LibraryState) {
    setLibrary(next);
    setRevision((value) => value + 1);
    navigate("");
    setSettingsOpen(false);
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a href="#" className="brand" aria-label="Uncloud home">
          <img className="brand-mark" src="/uncloud.svg" width="34" height="34" alt="" />
          uncloud<span className="version">v0</span>
        </a>
        <div className="sidebar-section">
          <span className="nav-label">YOUR UNCLOUD</span>
          <button
            className={`nav-item${view === "files" ? " active" : ""}`}
            onClick={() => {
              setView("files");
              navigate("");
            }}
          >
            <Files size={19} />
            All files<span className="nav-shortcut">⌂</span>
          </button>
          <button
            className={`nav-item${view === "dropbox" ? " active" : ""}`}
            onClick={() => setView("dropbox")}
            disabled={!library?.rootPath}
          >
            <CloudDownload size={18} />
            Dropbox
          </button>
          <button
            className={`nav-item${view === "nodes" ? " active" : ""}`}
            onClick={() => setView("nodes")}
            disabled={!library?.rootPath}
          >
            <Laptop size={18} />
            Nodes
          </button>
          <button
            className="nav-item"
            onClick={() => setSettingsOpen(true)}
            disabled={!library}
          >
            <Settings2 size={18} />
            Storage settings
          </button>
        </div>
        <div className="sidebar-bottom">
          <div className="drive-card">
            <div className="drive-icon">
              <HardDrive size={20} />
              <span className="status-dot" />
            </div>
            <strong>
              {library?.rootPath
                ? library.name || "Uncloud folder"
                : "Your own little corner"}
            </strong>
            <p>
              {library?.rootPath
                ? "Your Uncloud folder"
                : "A home for your files, on a computer you call your own."}
            </p>
            {library?.rootPath && (
              <code title={library.rootPath}>{library.rootPath}</code>
            )}
            <button onClick={() => setSettingsOpen(true)} disabled={!library}>
              {library?.rootPath ? "Manage folder" : "Choose a folder"}
              <ArrowUpRight size={14} />
            </button>
          </div>
          <div className="local-status">
            <span className="status-dot" />
            <span>Local on this Mac</span>
            <span className="version-number">0.1.0</span>
          </div>
        </div>
      </aside>
      <main>
        <header className="topbar">
          <div>
            <House size={15} />
            <span>Uncloud</span>
            <span className="slash">/</span>
            <strong>
              {view === "dropbox" ? "Dropbox" : view === "nodes" ? "Nodes" : "Files"}
            </strong>
          </div>
          <span className="private-label">
            <span className="status-dot" />
            Just you & your files
          </span>
        </header>
        <div className="main-content">
          {error ? (
            <div className="empty-state connection-error" role="alert">
              <HardDrive size={35} />
              <h1>Let’s reconnect</h1>
              <p>{error}</p>
              <p className="muted">
                Make sure the Uncloud app is running on this computer.
              </p>
              <button
                className="button primary"
                onClick={() => window.location.reload()}
              >
                Try again
              </button>
            </div>
          ) : !library ? (
            <div className="file-loading" role="status">
              <LoaderCircle className="spin" size={24} />
              Opening Uncloud…
            </div>
          ) : library.rootPath && view === "nodes" ? (
            <NodesPanel />
          ) : library.rootPath && view === "dropbox" ? (
            <DropboxPanel />
          ) : library.rootPath ? (
            <FileBrowser
              rootPath={library.rootPath}
              path={path}
              revision={revision}
              navigate={navigate}
            />
          ) : (
            <>
              <div className="page-heading welcome-heading">
                <div>
                  <span className="eyebrow">WELCOME TO UNCLOUD</span>
                  <h1>
                    Your files, at home<span className="heading-dot">.</span>
                  </h1>
                  <p>
                    A quieter place for your digital life. Let’s make it yours.
                  </p>
                </div>
                <span className="welcome-icon">
                  <FolderOpen size={24} strokeWidth={1.4} />
                </span>
              </div>
              <FolderSetup library={library} onSaved={onSaved} />
              <div className="welcome-footer">
                <span>Ordinary files. Your own storage.</span>
                <span>A small beginning. A place to grow.</span>
              </div>
            </>
          )}
        </div>
      </main>
      <dialog
        ref={dialog}
        className="settings-dialog"
        onCancel={() => setSettingsOpen(false)}
        onClose={() => setSettingsOpen(false)}
        aria-labelledby="settings-title"
      >
        <div className="dialog-header">
          <div>
            <span className="eyebrow">ON THIS COMPUTER</span>
            <h2 id="settings-title">Your Uncloud folder</h2>
          </div>
          <button
            className="icon-button"
            aria-label="Close storage settings"
            onClick={() => setSettingsOpen(false)}
          >
            <X size={20} />
          </button>
        </div>
        {settingsOpen && library && (
          <FolderSetup library={library} onSaved={onSaved} compact />
        )}
      </dialog>
    </div>
  );
}
