import { useCallback, useEffect, useState } from "react";
import {
  ArrowUpRight,
  Files,
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
import { api, SignedOutError } from "./api";
import type { LibraryState, Session, StorageReport, User } from "./api";
import AddFiles from "./AddFiles";
import ImportStatus, { useImportJob } from "./ImportStatus";
import HostDropbox from "./HostDropbox";
import { StorageCard, StoragePill } from "./StorageMeter";
import FileBrowser from "./FileBrowser";
import SyncPanel from "./SyncPanel";
import HostSetup from "./HostSetup";
import SignIn from "./SignIn";
import UsersPanel from "./UsersPanel";
import AccountPanel from "./AccountPanel";

type View = "files" | "sync" | "users";
type Dialog = "add" | "host" | "account" | null;

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
  // Coming back from signing in to Dropbox means picking up where they were: adding files, at the
  // account they just signed in to, and told how it went. Read once and taken off the address, so
  // that reloading the page later isn't treated as arriving from Dropbox all over again.
  const [arriving] = useState(() => {
    const outcome = new URLSearchParams(window.location.search).get("dropbox");
    if (outcome)
      window.history.replaceState(null, "", window.location.pathname + window.location.hash);
    return outcome;
  });
  const [dialog, setDialog] = useState<Dialog>(() => (arriving ? "add" : null));
  // Somebody sent to set up Dropbox should land on that, not have to find it.
  const [dropboxFocus, setDropboxFocus] = useState(false);
  const [view, setView] = useState<View>("files");
  // Held in state rather than a ref alone: the dialog is only rendered once there is a session to
  // show, so an "open" decided before that — arriving back from Dropbox, say — would otherwise be
  // made against an element that does not exist yet, and nothing would reopen it when it appeared.
  const [dialogElement, setDialogElement] = useState<HTMLDialogElement | null>(null);

  const me = session?.user ?? null;
  const onImported = useCallback(() => setRevision((value) => value + 1), []);
  const imports = useImportJob(me != null && library?.rootPath != null, onImported);

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

  // How much room is left is on screen all the time, so it is kept current: every minute, and
  // every few seconds while files are arriving and the number is visibly moving.
  useEffect(() => {
    if (!library?.rootPath) return;
    const controller = new AbortController();
    const read = () =>
      api<StorageReport>("/storage", { signal: controller.signal })
        .then(setStorage)
        .catch(() => {
          // A drive that won't say how full it is shouldn't take the app down with it.
        });
    void read();
    const timer = window.setInterval(() => void read(), imports.running ? 5000 : 60000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [library?.rootPath, revision, imports.running]);

  useEffect(() => {
    const onHash = () => setPath(readPath());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  useEffect(() => {
    // Going from one dialog straight to another keeps the same one open; opening it twice throws.
    if (dialog) {
      if (!dialogElement?.open) dialogElement?.showModal();
    } else dialogElement?.close();
  }, [dialog, dialogElement]);

  useEffect(() => {
    if (dialog && dropboxFocus)
      document.getElementById("dropbox-setup")?.scrollIntoView({ block: "start" });
  }, [dialog, dropboxFocus]);

  function closeDialog() {
    setDialog(null);
    setDropboxFocus(false);
  }

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
            src="/uncloud.png"
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
                Settings
              </button>
            </>
          )}
          <button className="nav-item" onClick={() => setDialog("account")}>
            <UserRound size={18} />
            My account
          </button>
        </div>
        <div className="sidebar-bottom">
          {hasFolder ? (
            <StorageCard storage={storage} isAdmin={me.isAdmin} />
          ) : (
            <div className="drive-card">
              <div className="drive-icon">
                <HardDrive size={20} />
                <span className="status-dot" />
              </div>
              <strong>Nearly there</strong>
              <p>
                {me.isAdmin
                  ? "Choose where everyone’s files will live."
                  : "The host hasn’t chosen a folder yet."}
              </p>
              <button onClick={() => setDialog("account")}>
                My account
                <ArrowUpRight size={14} />
              </button>
            </div>
          )}
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
              {view === "sync"
                ? "My computers"
                : view === "users"
                  ? "People"
                  : "My files"}
            </strong>
          </div>
          <span className="topbar-status">
            <span className="private-label">
              <span className="status-dot" />
              Yours alone
            </span>
            {hasFolder && <StoragePill storage={storage} />}
          </span>
        </header>
        <div className="main-content">
          {view === "users" && me.isAdmin ? (
            <UsersPanel me={me} />
          ) : hasFolder && view === "sync" ? (
            <SyncPanel />
          ) : hasFolder ? (
            <FileBrowser
              rootPath={library!.rootPath!}
              path={path}
              revision={revision}
              navigate={navigate}
              onAdd={() => setDialog("add")}
              status={
                imports.job && (
                  <ImportStatus
                    job={imports.job}
                    onStop={() => void imports.stop()}
                    onDismiss={imports.dismiss}
                    onOpen={navigate}
                  />
                )
              }
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
        ref={setDialogElement}
        className={`settings-dialog${dialog === "add" ? " add-dialog" : ""}`}
        onCancel={closeDialog}
        onClose={closeDialog}
        aria-labelledby="settings-title"
      >
        <div className="dialog-header">
          <div>
            <span className="eyebrow">
              {dialog === "account"
                ? "YOUR ACCOUNT"
                : dialog === "add"
                  ? "MY FILES"
                  : "THIS UNCLOUD"}
            </span>
            <h2 id="settings-title">
              {dialog === "account"
                ? "Your account"
                : dialog === "add"
                  ? "Add files"
                  : "Settings"}
            </h2>
          </div>
          <button
            className="icon-button"
            aria-label="Close"
            onClick={closeDialog}
          >
            <X size={20} />
          </button>
        </div>
        {dialog === "add" && hasFolder && (
          <AddFiles
            importing={imports.running}
            isAdmin={me.isAdmin}
            storage={storage}
            arrivedFromDropbox={arriving}
            onStarted={(job) => {
              imports.start(job);
              closeDialog();
            }}
            // Whoever looks after this Uncloud sets Dropbox up once for everybody; anyone else
            // can set up their own, which needs nothing from anybody.
            onSetUpDropbox={() => {
              setDropboxFocus(true);
              setDialog(me.isAdmin ? "host" : "account");
            }}
          />
        )}
        {dialog === "account" && (
          <AccountPanel me={me} dropboxExpanded={dropboxFocus} />
        )}
        {dialog === "host" && me.isAdmin && (
          <>
            <HostSetup
              compact
              onSaved={() => setRevision((value) => value + 1)}
            />
            <HostDropbox expanded={dropboxFocus} />
          </>
        )}
      </dialog>
    </div>
  );
}
