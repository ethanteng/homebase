import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowUpRight,
  CloudDownload,
  Files,
  FolderTree,
  Laptop,
  LoaderCircle,
  LogOut,
  HardDrive,
  House,
  Settings2,
  UserRound,
  Users,
  X,
} from "lucide-react";
import { api, formatSize, SignedOutError } from "./api";
import type { LibraryState, Session, StorageReport, User } from "./api";
import ImportPanel from "./ImportPanel";
import ImportSettings from "./ImportSettings";
import FileBrowser from "./FileBrowser";
import SyncPanel from "./SyncPanel";
import HostSetup from "./HostSetup";
import SignIn from "./SignIn";
import UsersPanel from "./UsersPanel";
import AccountPanel from "./AccountPanel";

type View = "files" | "import" | "sync" | "users";
type Dialog = "host" | "imports" | "account" | null;

function readPath() {
  try {
    return decodeURIComponent(window.location.hash.slice(1));
  } catch {
    return "";
  }
}

export default function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [library, setLibrary] = useState<LibraryState | null>(null);
  const [storage, setStorage] = useState<StorageReport | null>(null);
  const [error, setError] = useState("");
  const [path, setPath] = useState(readPath);
  const [revision, setRevision] = useState(0);
  const [importSettings, setImportSettings] = useState(0);
  const [dialog, setDialog] = useState<Dialog>(null);
  const [view, setView] = useState<View>(() =>
    window.location.search.includes("dropbox=") ? "import" : "files",
  );
  const dialogRef = useRef<HTMLDialogElement>(null);

  const me = session?.user ?? null;

  // A session that ends while the app is open returns everyone to the sign-in screen rather
  // than to a page of errors nobody can act on.
  const signedOut = useCallback(() => {
    setSession((current) =>
      current ? { ...current, user: null } : { setupNeeded: false, hostConfigured: false, user: null },
    );
    setLibrary(null);
    setStorage(null);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    api<Session>("/session", { signal: controller.signal })
      .then(setSession)
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setError(
            failure instanceof Error
              ? failure.message
              : "Couldn’t reach this Uncloud.",
          );
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!me) return;
    const controller = new AbortController();
    api<LibraryState>("/library", { signal: controller.signal })
      .then(setLibrary)
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        if (failure instanceof SignedOutError) signedOut();
        // A host with no folder yet is not an error for a member; the screen below says so.
        else setLibrary(null);
      });
    return () => controller.abort();
  }, [me, revision, signedOut]);

  useEffect(() => {
    if (!library?.rootPath) return;
    const controller = new AbortController();
    api<StorageReport>("/storage", { signal: controller.signal })
      .then(setStorage)
      .catch(() => {
        // A drive that won't say how full it is shouldn't take the app down with it.
      });
    return () => controller.abort();
  }, [library?.rootPath, revision]);

  useEffect(() => {
    const onHash = () => setPath(readPath());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  useEffect(() => {
    if (dialog) dialogRef.current?.showModal();
    else dialogRef.current?.close();
  }, [dialog]);

  function navigate(nextPath: string) {
    window.location.hash = encodeURIComponent(nextPath);
    setPath(nextPath);
    window.scrollTo({ top: 0 });
  }

  function onSignedIn(user: User) {
    setSession({ setupNeeded: false, hostConfigured: true, user });
    setError("");
    setView("files");
    navigate("");
  }

  async function signOut() {
    try {
      await api("/session", { method: "DELETE" });
    } catch {
      // Whether or not the host heard, this browser is done with the session.
    }
    signedOut();
  }

  if (error)
    return (
      <div className="app-shell">
        <main>
          <div className="main-content">
            <div className="empty-state connection-error" role="alert">
              <HardDrive size={35} />
              <h1>Let’s reconnect</h1>
              <p>{error}</p>
              <p className="muted">Make sure this Uncloud is still running.</p>
              <button
                className="button primary"
                onClick={() => window.location.reload()}
              >
                Try again
              </button>
            </div>
          </div>
        </main>
      </div>
    );

  if (!session)
    return (
      <div className="app-shell">
        <main>
          <div className="main-content">
            <div className="file-loading" role="status">
              <LoaderCircle className="spin" size={24} />
              Opening Uncloud…
            </div>
          </div>
        </main>
      </div>
    );

  if (!me)
    return (
      <div className="app-shell">
        <main>
          <div className="main-content">
            <SignIn session={session} onSignedIn={onSignedIn} />
          </div>
        </main>
      </div>
    );

  const hasFolder = library?.rootPath != null;

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a href="#" className="brand" aria-label="Uncloud home">
          <img
            className="brand-mark"
            src="/uncloud.svg"
            width="34"
            height="34"
            alt=""
          />
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
            My files<span className="nav-shortcut">⌂</span>
          </button>
          <button
            className={`nav-item${view === "import" ? " active" : ""}`}
            onClick={() => setView("import")}
            disabled={!hasFolder}
          >
            <CloudDownload size={18} />
            Bring files in
          </button>
          <button
            className={`nav-item${view === "sync" ? " active" : ""}`}
            onClick={() => setView("sync")}
            disabled={!hasFolder}
          >
            <Laptop size={18} />
            My computers
          </button>
          {me.isAdmin && (
            <>
              <button
                className={`nav-item${view === "users" ? " active" : ""}`}
                onClick={() => setView("users")}
              >
                <Users size={18} />
                People
              </button>
              <button className="nav-item" onClick={() => setDialog("host")}>
                <Settings2 size={18} />
                Storage settings
              </button>
              <button className="nav-item" onClick={() => setDialog("imports")}>
                <FolderTree size={18} />
                Where files come from
              </button>
            </>
          )}
          <button className="nav-item" onClick={() => setDialog("account")}>
            <UserRound size={18} />
            My account
          </button>
        </div>
        <div className="sidebar-bottom">
          <div className="drive-card">
            <div className="drive-icon">
              <HardDrive size={20} />
              <span className="status-dot" />
            </div>
            <strong>
              {hasFolder ? me.displayName : "Nearly there"}
            </strong>
            <p>
              {hasFolder
                ? "Your folder on this Uncloud"
                : me.isAdmin
                  ? "Choose where everyone’s files will live."
                  : "The host hasn’t chosen a folder yet."}
            </p>
            {library?.rootPath && (
              <code title={library.rootPath}>{library.rootPath}</code>
            )}
            {hasFolder && storage && (
              <span className="drive-space">
                {formatSize(storage.usedBytes)} yours
                {storage.freeBytes != null
                  ? ` · ${formatSize(storage.freeBytes)} free on the drive`
                  : ""}
              </span>
            )}
            <button onClick={() => setDialog("account")}>
              My account
              <ArrowUpRight size={14} />
            </button>
          </div>
          <div className="local-status">
            <span className="status-dot" />
            <span>@{me.username}</span>
            <button
              className="icon-button"
              onClick={() => void signOut()}
              aria-label="Sign out"
              title="Sign out"
            >
              <LogOut size={16} />
            </button>
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
              {view === "import"
                ? "Bring files in"
                : view === "sync"
                  ? "My computers"
                  : view === "users"
                    ? "People"
                    : "My files"}
            </strong>
          </div>
          <span className="private-label">
            <span className="status-dot" />
            Yours alone
          </span>
        </header>
        <div className="main-content">
          {view === "users" && me.isAdmin ? (
            <UsersPanel me={me} />
          ) : hasFolder && view === "import" ? (
            <ImportPanel
              onImported={() => setRevision((value) => value + 1)}
              // An administrator setting the host's key helps everybody, so that is where
              // they land; anybody else sets their own, which needs nothing from them.
              onConfigure={() => setDialog(me.isAdmin ? "imports" : "account")}
              canConfigure={me.isAdmin}
              settingsRevision={importSettings}
            />
          ) : hasFolder && view === "sync" ? (
            <SyncPanel />
          ) : hasFolder ? (
            <FileBrowser
              rootPath={library!.rootPath!}
              path={path}
              revision={revision}
              navigate={navigate}
            />
          ) : me.isAdmin ? (
            <>
              <div className="page-heading welcome-heading">
                <div>
                  <span className="eyebrow">WELCOME TO UNCLOUD</span>
                  <h1>
                    Everyone’s files, at home
                    <span className="heading-dot">.</span>
                  </h1>
                  <p>
                    A quieter place for your household’s digital life. Let’s
                    make it yours.
                  </p>
                </div>
                <span className="welcome-icon">
                  <HardDrive size={24} strokeWidth={1.4} />
                </span>
              </div>
              <HostSetup
                onSaved={() => setRevision((value) => value + 1)}
              />
              <div className="welcome-footer">
                <span>Ordinary files. Your own storage.</span>
                <span>A small beginning. A place to grow.</span>
              </div>
            </>
          ) : (
            <div className="empty-state" role="status">
              <HardDrive size={35} />
              <h1>Almost ready</h1>
              <p>
                Whoever looks after this Uncloud hasn’t chosen a folder for
                everyone’s files yet.
              </p>
              <p className="muted">
                Once they have, yours will be waiting here.
              </p>
            </div>
          )}
        </div>
      </main>
      <dialog
        ref={dialogRef}
        className="settings-dialog"
        onCancel={() => setDialog(null)}
        onClose={() => setDialog(null)}
        aria-labelledby="settings-title"
      >
        <div className="dialog-header">
          <div>
            <span className="eyebrow">
              {dialog === "account" ? "YOUR ACCOUNT" : "THIS UNCLOUD"}
            </span>
            <h2 id="settings-title">
              {dialog === "account"
                ? "Your sign-in"
                : dialog === "imports"
                  ? "Where files come from"
                  : "Where everyone’s files live"}
            </h2>
          </div>
          <button
            className="icon-button"
            aria-label="Close settings"
            onClick={() => setDialog(null)}
          >
            <X size={20} />
          </button>
        </div>
        {dialog === "account" && (
          <AccountPanel
            me={me}
            onDropboxChanged={() => setImportSettings((value) => value + 1)}
          />
        )}
        {dialog === "host" && me.isAdmin && (
          <HostSetup
            compact
            onSaved={() => setRevision((value) => value + 1)}
          />
        )}
        {dialog === "imports" && me.isAdmin && (
          <ImportSettings
            onChanged={() => setImportSettings((value) => value + 1)}
          />
        )}
      </dialog>
    </div>
  );
}
