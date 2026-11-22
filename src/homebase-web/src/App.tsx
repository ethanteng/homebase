import { useCallback, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  Files,
  HardDrive,
  History,
  Laptop,
  LoaderCircle,
  LogOut,
  Moon,
  Plus,
  Settings2,
  Sun,
  Trash2,
  TriangleAlert,
  UserRound,
  Users,
  X,
} from "lucide-react";
import { api, EVERNOTE, EVERNOTE_FOLDER, SignedOutError } from "./api";
import { bringNotesWhenFree, type FollowUp } from "./evernoteFollowUp";
import type { ImportJob, LibraryState, Session, StorageReport, User } from "./api";
import AddFiles from "./AddFiles";
import ImportStatus, { useImportJob } from "./ImportStatus";
import UploadStatus, { UnfinishedStatus, useUpload } from "./Uploads";
import HostDropbox from "./HostDropbox";
import HostStorage from "./HostStorage";
import HostPower from "./HostPower";
import HostBackup from "./HostBackup";
import { StorageCard, StoragePill } from "./StorageMeter";
import FileBrowser from "./FileBrowser";
import Bin from "./Bin";
import Backups from "./Backups";
import SyncPanel from "./SyncPanel";
import HostSetup, { LocationSetting, RemoteAccessSetting } from "./HostSetup";
import { SettingsGroup } from "./Settings";
import SignIn from "./SignIn";
import UsersPanel from "./UsersPanel";
import AccountPanel from "./AccountPanel";
import { useTheme } from "./theme";
import { versionLabel } from "./version";

type View = "files" | "bin" | "backups" | "sync" | "users" | "settings" | "account";
type Dialog = "add" | null;

function readPath() {
  try {
    return decodeURIComponent(window.location.hash.slice(1));
  } catch {
    return "";
  }
}

/** The logo with the version under it, and the theme switch: on every screen, signed in or not. */
function Header({ children }: { children?: ReactNode }) {
  const { theme, toggle } = useTheme();
  const label = theme === "dark" ? "Switch to light theme" : "Switch to dark theme";
  return (
    <header className="app-header">
      <a href="#" className="brand" aria-label="Uncloud home">
        <img className="brand-mark" src="/uncloud.png" width="36" height="36" alt="" />
        <span className="brand-text">
          <span className="brand-name">uncloud</span>
          {versionLabel && <span className="brand-version">{versionLabel}</span>}
        </span>
      </a>
      <div className="header-actions">
        {children}
        <button className="round-button" onClick={toggle} aria-label={label} title={label}>
          {theme === "dark" ? <Sun size={20} /> : <Moon size={20} />}
        </button>
      </div>
    </header>
  );
}

/** Whoever is signed in, behind their initial: their account, and the way out. */
function AccountMenu({
  me,
  onAccount,
  onSignOut,
}: {
  me: User;
  onAccount: () => void;
  onSignOut: () => void;
}) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (event: Event) => {
      if (!wrap.current?.contains(event.target as Node)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };
    document.addEventListener("pointerdown", away);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("pointerdown", away);
      document.removeEventListener("keydown", escape);
    };
  }, [open]);
  const name = me.displayName || me.username;
  return (
    <div className="account-menu-wrap" ref={wrap}>
      <button
        className="avatar-button"
        aria-label={`Account: ${name}`}
        aria-expanded={open}
        aria-haspopup="menu"
        onClick={() => setOpen((value) => !value)}
      >
        {name.slice(0, 1).toUpperCase()}
      </button>
      {open && (
        <div className="account-menu" role="menu">
          <div className="account-menu-who">
            <strong>{name}</strong>
            <span>@{me.username}</span>
          </div>
          <button
            role="menuitem"
            onClick={() => {
              setOpen(false);
              onAccount();
            }}
          >
            <UserRound size={18} />
            My account
          </button>
          <button
            role="menuitem"
            onClick={() => {
              setOpen(false);
              onSignOut();
            }}
          >
            <LogOut size={18} />
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}

export default function App() {
  const [session, setSession] = useState<Session | null>(null);
  // Undefined until the host has said; null when it has no folder yet.
  const [library, setLibrary] = useState<LibraryState | null | undefined>(undefined);
  // Whether this Uncloud has backups to look in, so My files offers them only where it does.
  const [backupsAvailable, setBackupsAvailable] = useState(false);
  const [storage, setStorage] = useState<StorageReport | null>(null);
  const [error, setError] = useState("");
  const [path, setPath] = useState(readPath);
  const [revision, setRevision] = useState(0);
  // Coming back from signing in to Dropbox means picking up where they were: adding files, at the
  // account they just signed in to, and told how it went. Read once and taken off the address, so
  // that reloading the page later isn't treated as arriving from Dropbox all over again.
  const [arriving, setArriving] = useState(() => {
    const outcome = new URLSearchParams(window.location.search).get("dropbox");
    if (outcome)
      window.history.replaceState(null, "", window.location.pathname + window.location.hash);
    return outcome;
  });
  const [dialog, setDialog] = useState<Dialog>(() => (arriving ? "add" : null));
  // Sent from the Uncloud menu on the host Mac, which opens Settings rather than leaving somebody
  // to find it — after signing in, if they have to. Taken off the address, like the others, so a
  // reload doesn't reopen it.
  const [askedForSettings, setAskedForSettings] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    if (!params.has("settings")) return false;
    params.delete("settings");
    const rest = params.toString();
    window.history.replaceState(
      null,
      "",
      window.location.pathname + (rest ? `?${rest}` : "") + window.location.hash,
    );
    return true;
  });
  // Arriving from the QR code the Uncloud app shows on a computer it's setting up: that computer
  // has asked to be added, and this person approves it on Computers — after signing in, if they
  // have to. Taken off the address too, so a reload later doesn't ask all over again.
  const [approving, setApproving] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    const id = params.get("pair");
    if (id) {
      params.delete("pair");
      const rest = params.toString();
      window.history.replaceState(
        null,
        "",
        window.location.pathname + (rest ? `?${rest}` : "") + window.location.hash,
      );
    }
    return id;
  });
  // Somebody sent to set up Dropbox should land on that, not have to find it.
  const [dropboxFocus, setDropboxFocus] = useState(false);
  const [view, setView] = useState<View>(() =>
    approving ? "sync" : askedForSettings ? "settings" : "files",
  );
  // Held in state rather than a ref alone: the dialog is only rendered once there is a session to
  // show, so an "open" decided before that — arriving back from Dropbox, say — would otherwise be
  // made against an element that does not exist yet, and nothing would reopen it when it appeared.
  const [dialogElement, setDialogElement] = useState<HTMLDialogElement | null>(null);

  const me = session?.user ?? null;
  const onImported = useCallback(() => setRevision((value) => value + 1), []);
  const imports = useImportJob(me != null && library?.rootPath != null, onImported);
  const uploads = useUpload(me != null, onImported);
  // Why notes from Evernote's export files didn't start coming in once the files had, when they didn't.
  const [evernoteProblem, setEvernoteProblem] = useState<string | null>(null);
  const startImport = imports.start;
  // One import at a time. An export that lands while one is going is remembered, not failed:
  // the one going may have looked before that file was in the folder.
  const evernoteFollowUp = useRef<FollowUp>({ going: false, again: false });

  /**
   * Turns every Evernote export file that has been added into notebooks of notes. Asked for as soon
   * as export files have finished arriving, so choosing them is the only step there is; any already
   * brought in are passed over, so it's never more than what's new. One asked for while notes are
   * already coming in waits, and runs when they finish, so a second notebook isn't left behind.
   */
  const bringEvernoteHome = useCallback(async () => {
    const pause = (ms: number) => new Promise((resolve) => window.setTimeout(resolve, ms));
    const running = async () => {
      const { job } = await api<{ job: ImportJob | null }>("/imports/job");
      return job?.running ? job : null;
    };
    try {
      const message = await bringNotesWhenFree(
        evernoteFollowUp,
        async () => {
          setEvernoteProblem(null);
          const { job } = await api<{ job: ImportJob }>("/imports", {
            method: "POST",
            body: JSON.stringify({ source: EVERNOTE, remotePaths: ["/"], label: "Evernote" }),
          });
          startImport(job);
          for (;;) {
            await pause(700);
            const latest = await running();
            if (latest?.id !== job.id) return;
          }
        },
        async () => {
          for (;;) {
            if (!(await running())) return;
            await pause(700);
          }
        },
      );
      if (message)
        setEvernoteProblem(
          `Your Evernote files uploaded, but the notes weren’t added. ${message} To try again, ` +
            "open Add files, then Evernote.",
        );
    } catch (problem: unknown) {
      if (problem instanceof SignedOutError) return;
      setEvernoteProblem(
        `Your Evernote files uploaded, but the notes weren’t added. ${
          problem instanceof Error ? problem.message : "Uncloud didn’t answer."
        } To try again, open Add files, then Evernote.`,
      );
    }
  }, [startImport]);

  // A session that ends while the app is open returns everyone to the sign-in screen rather
  // than to a page of errors nobody can act on.
  const signedOut = useCallback((keepAsking = false) => {
    setSession((current) =>
      current ? { ...current, user: null } : { setupNeeded: false, hostConfigured: false, user: null },
    );
    setLibrary(undefined);
    setStorage(null);
    // A computer asking to be added was asking whoever opened its code. Left unanswered, it isn't
    // put to whoever signs in next on this browser, who would be adding somebody else's computer
    // to their own account — unless whoever opened it signed out from the question itself, having
    // found this browser signed in to an account that isn't theirs, to answer it as themselves.
    if (!keepAsking) setApproving(null);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    api<Session>("/session", { signal: controller.signal })
      .then(setSession)
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setError(failure instanceof Error ? failure.message : "Couldn’t reach Uncloud.");
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
    if (!me) return;
    const controller = new AbortController();
    api<{ configured: boolean }>("/backups/available", { signal: controller.signal })
      .then((answer) => setBackupsAvailable(answer.configured))
      .catch(() => {
        // A host from before backups, or one that can't say: nothing to offer.
      });
    return () => controller.abort();
  }, [me, revision]);

  // How much room is left is on screen all the time, so it is kept current: every minute, and
  // every few seconds while files are arriving — imported or sent — and the number is visibly moving.
  const filling = imports.running || uploads.running;
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
    const timer = window.setInterval(() => void read(), filling ? 5000 : 60000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [library?.rootPath, revision, filling]);

  // A file dropped where nothing takes it would be opened by the browser in place of Uncloud, and
  // an upload under way would go with it. Only somewhere that takes files says it does.
  useEffect(() => {
    const refuse = (event: DragEvent) => {
      if (event.defaultPrevented || !event.dataTransfer?.types.includes("Files")) return;
      event.preventDefault();
      if (event.type === "dragover") event.dataTransfer.dropEffect = "none";
    };
    window.addEventListener("dragover", refuse);
    window.addEventListener("drop", refuse);
    return () => {
      window.removeEventListener("dragover", refuse);
      window.removeEventListener("drop", refuse);
    };
  }, []);

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

  // Settings and People are for whoever looks after this Uncloud. Anybody else sent to them gets
  // their files instead of an empty page.
  useEffect(() => {
    if ((view === "settings" || view === "users") && me && !me.isAdmin) setView("files");
  }, [view, me]);

  // Spent once somebody is signed in, whether they arrived signed in or signed in to get there, so
  // signing out and back in later lands on their files as usual.
  useEffect(() => {
    if (me) setAskedForSettings(false);
  }, [me]);

  useEffect(() => {
    if (dropboxFocus && (view === "settings" || view === "account"))
      document.getElementById("dropbox-setup")?.scrollIntoView({ block: "start" });
  }, [view, dropboxFocus]);

  function closeDialog() {
    setDialog(null);
    // Spent on the dialog it opened. Kept any longer, opening Add files again later would land on
    // Dropbox and repeat how that sign-in went, as though another had just finished.
    setArriving(null);
  }

  function navigate(nextPath: string) {
    window.location.hash = encodeURIComponent(nextPath);
    setPath(nextPath);
    window.scrollTo({ top: 0 });
  }

  function show(next: View) {
    setView(next);
    setDropboxFocus(false);
    if (next === "files") navigate("");
    else window.scrollTo({ top: 0 });
  }

  function onSignedIn(user: User) {
    setSession({ setupNeeded: false, hostConfigured: true, user });
    setError("");
    // The Mac menu opens /?settings before this browser is signed in. Keep that request, the way a
    // computer asking to be added is kept, instead of leaving an admin on My files.
    setView(approving ? "sync" : askedForSettings && user.isAdmin ? "settings" : "files");
    navigate("");
  }

  async function signOut(keepAsking = false) {
    try {
      await api("/session", { method: "DELETE" });
    } catch {
      // Whether or not the host heard, this browser is done with the session.
    }
    signedOut(keepAsking);
  }

  if (error)
    return (
      <div className="app-shell">
        <Header />
        <main>
          <div className="main-content">
            <div className="empty-state connection-error" role="alert">
              <HardDrive size={35} />
              <h1>Can’t reach Uncloud</h1>
              <p>{error}</p>
              <p className="muted">Check that the Uncloud computer is on.</p>
              <button className="button primary" onClick={() => window.location.reload()}>
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
        <Header />
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
        <Header />
        <main>
          <div className="main-content">
            <SignIn session={session} onSignedIn={onSignedIn} />
          </div>
        </main>
      </div>
    );

  if (library === undefined)
    return (
      <div className="app-shell">
        <Header />
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

  const hasFolder = library?.rootPath != null;

  // Setting up: one thing to do, so nothing else on screen.
  if (!hasFolder && me.isAdmin && view !== "account")
    return (
      <div className="app-shell">
        <Header>
          <AccountMenu me={me} onAccount={() => show("account")} onSignOut={() => void signOut()} />
        </Header>
        <main>
          <div className="main-content">
            <HostSetup onSaved={() => setRevision((value) => value + 1)} />
          </div>
        </main>
      </div>
    );

  const navItem = (target: View, icon: ReactNode, label: string, disabled = false) => (
    <button
      className={`nav-item${view === target ? " active" : ""}`}
      aria-current={view === target ? "page" : undefined}
      onClick={() => show(target)}
      disabled={disabled}
    >
      {icon}
      {label}
    </button>
  );

  return (
    <div className="app-shell">
      <Header>
        {hasFolder && (
          <button
            className="button primary header-add"
            aria-label="Add files"
            onClick={() => {
              // Files from this device go into the folder open in My files.
              if (view !== "files") setView("files");
              setDialog("add");
            }}
          >
            <Plus size={18} />
            <span className="header-add-label">Add files</span>
          </button>
        )}
        <AccountMenu me={me} onAccount={() => show("account")} onSignOut={() => void signOut()} />
      </Header>
      <div className="app-body">
        <aside className="sidebar">
          <nav className="sidebar-section" aria-label="Main">
            {navItem("files", <Files size={20} />, "My files")}
            {navItem("sync", <Laptop size={20} />, "Computers", !hasFolder)}
            {backupsAvailable && navItem("backups", <History size={20} />, "Backups", !hasFolder)}
            {navItem("bin", <Trash2 size={20} />, "Trash", !hasFolder)}
          </nav>
          {me.isAdmin && (
            <nav className="sidebar-section" aria-label="Manage">
              <span className="nav-label">Manage</span>
              {navItem("users", <Users size={20} />, "People")}
              {navItem("settings", <Settings2 size={20} />, "Settings")}
            </nav>
          )}
          {hasFolder && (
            <div className="sidebar-pill">
              <StoragePill storage={storage} />
            </div>
          )}
          <div className="sidebar-bottom">
            {hasFolder ? (
              <StorageCard storage={storage} isAdmin={me.isAdmin} />
            ) : (
              <div className="drive-card">
                <div className="drive-icon">
                  <HardDrive size={20} />
                  <span className="status-dot" />
                </div>
                <strong>Almost ready</strong>
                <p>
                  {me.isAdmin
                    ? "Choose where files are stored."
                    : "Waiting for an admin to finish setup."}
                </p>
              </div>
            )}
          </div>
        </aside>
        <main>
          <div className="main-content">
            {view === "account" ? (
              <>
                <div className="page-heading">
                  <h1>My account</h1>
                </div>
                <AccountPanel me={me} dropboxExpanded={dropboxFocus} />
              </>
            ) : view === "settings" && me.isAdmin ? (
              <>
                <div className="page-heading">
                  <h1>Settings</h1>
                </div>
                <div className="settings-page">
                  <SettingsGroup title="Storage">
                    <LocationSetting onSaved={() => setRevision((value) => value + 1)} />
                    {/* The storage meter shows the limit, so it is read again once one is saved. A
                        new location can be a different drive, so the row starts again for it. */}
                    {hasFolder && (
                      <HostStorage
                        key={library!.rootPath!}
                        onSaved={() => setRevision((value) => value + 1)}
                      />
                    )}
                  </SettingsGroup>
                  <SettingsGroup title="Access">
                    <RemoteAccessSetting />
                    <HostDropbox expanded={dropboxFocus} />
                  </SettingsGroup>
                  <HostPower />
                  {/* Backups set up from here appear in My files without a reload. */}
                  <HostBackup onChanged={() => setRevision((value) => value + 1)} />
                </div>
              </>
            ) : view === "users" && me.isAdmin ? (
              <UsersPanel me={me} />
            ) : hasFolder && view === "sync" ? (
              <SyncPanel
                me={me}
                // "Not you?": the wrong account is signed in to add a computer, so the computer's
                // question waits for the right one.
                onSignOut={() => void signOut(true)}
                approving={approving}
                onApprovalClosed={() => setApproving(null)}
              />
            ) : hasFolder && view === "backups" ? (
              <Backups
                // What comes back takes room the storage meter should show.
                onChanged={() => setRevision((value) => value + 1)}
                open={(folder) => {
                  setView("files");
                  navigate(folder);
                }}
              />
            ) : hasFolder && view === "bin" ? (
              <Bin
                // Letting go of something frees room the storage meter should show.
                onChanged={() => setRevision((value) => value + 1)}
                open={(folder) => {
                  setView("files");
                  navigate(folder);
                }}
              />
            ) : hasFolder ? (
              <FileBrowser
                rootPath={library!.rootPath!}
                path={path}
                revision={revision}
                navigate={navigate}
                onAdd={() => setDialog("add")}
                // Dropped onto the list: into the folder it shows, or the folder dropped on.
                onDropFiles={(folder, picked) => uploads.start(folder, picked)}
                // The storage meter, too: copying fills the drive and deleting frees it.
                onChanged={() => setRevision((value) => value + 1)}
                status={
                  <>
                    {/* Uploads a page went away from before they were in, for the same files added again to pick up. */}
                    {uploads.unfinished.map((upload) => (
                      <UnfinishedStatus
                        key={upload.id}
                        upload={upload}
                        onPlace={async () => {
                          await uploads.place(upload);
                          // Put in place after an import had the folder: the notes still need bringing in.
                          if (upload.destination === EVERNOTE_FOLDER) void bringEvernoteHome();
                        }}
                        onThrowAway={() => uploads.throwAway(upload.id)}
                      />
                    ))}
                    {/* Each upload from this device, in the order they go: on its way, waiting its turn, or how it went. */}
                    {uploads.uploads.map((upload) => (
                      <UploadStatus
                        key={upload.key}
                        upload={upload}
                        onStop={() => uploads.stop(upload.key)}
                        onDismiss={() => uploads.dismiss(upload.key)}
                        onOpen={navigate}
                        onDecide={(merge) => uploads.decide(upload.key, merge)}
                      />
                    ))}
                    {evernoteProblem && (
                      <section className="import-progress import-outcome failed" role="alert">
                        <div className="import-progress-head">
                          <strong>
                            <TriangleAlert size={16} />
                            {evernoteProblem}
                          </strong>
                          <button
                            className="icon-button"
                            onClick={() => setEvernoteProblem(null)}
                            aria-label="Dismiss"
                            title="Dismiss"
                          >
                            <X size={16} />
                          </button>
                        </div>
                      </section>
                    )}
                    {imports.job && (
                      <ImportStatus
                        job={imports.job}
                        onStop={() => void imports.stop()}
                        onDismiss={imports.dismiss}
                        onOpen={navigate}
                      />
                    )}
                  </>
                }
              />
            ) : (
              <div className="empty-state" role="status">
                <HardDrive size={35} />
                <h1>Almost ready</h1>
                <p>An admin still needs to choose where files are stored.</p>
                <p className="muted">Your files will be here once they do.</p>
              </div>
            )}
          </div>
        </main>
      </div>
      <dialog
        ref={setDialogElement}
        className="settings-dialog add-dialog"
        onCancel={closeDialog}
        onClose={closeDialog}
        aria-labelledby="dialog-title"
      >
        <div className="dialog-header">
          <h2 id="dialog-title">Add files</h2>
          <button className="icon-button" aria-label="Close" onClick={closeDialog}>
            <X size={20} />
          </button>
        </div>
        {dialog === "add" && hasFolder && (
          <AddFiles
            importing={imports.running}
            uploading={uploads.running}
            isAdmin={me.isAdmin}
            // Files from this device go into whichever folder is open behind the dialog.
            folder={path}
            storage={storage}
            arrivedFromDropbox={arriving}
            onStarted={(job) => {
              imports.start(job);
              closeDialog();
            }}
            onUpload={async (picked) => {
              await uploads.start(path, picked);
              closeDialog();
            }}
            // Into the folder Evernote's export files are kept in, wherever My files is open, and
            // their notes brought in as soon as they've arrived.
            onUploadEvernote={async (picked) => {
              const { path: folder } = await api<{ path: string }>("/imports/evernote/folder", { method: "POST" });
              await uploads.start(folder, picked, () => void bringEvernoteHome());
              closeDialog();
            }}
            // An admin sets Dropbox up once for everybody; anyone else can set up their own,
            // which needs nothing from anybody.
            onSetUpDropbox={() => {
              closeDialog();
              setView(me.isAdmin ? "settings" : "account");
              setDropboxFocus(true);
            }}
          />
        )}
      </dialog>
    </div>
  );
}
