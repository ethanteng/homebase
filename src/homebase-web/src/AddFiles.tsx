import { useCallback, useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import {
  ArrowLeft,
  ArrowUpRight,
  ChevronRight,
  Cloud,
  File,
  Folder,
  FolderOpen,
  HardDrive,
  Laptop,
  LoaderCircle,
  Lock,
  NotebookPen,
  Plus,
  Usb,
  Users,
} from "lucide-react";
import { api, formatSize } from "./api";
import { spaceLevel } from "./StorageMeter";
import type {
  ImportAccount,
  ImportJob,
  ImportPlace,
  ImportSources,
  PlaceKind,
  SourceEntry,
  StorageReport,
  SuggestedPlace,
} from "./api";

/**
 * Where people's files are waiting that Uncloud can't reach yet. Shown so the shape of what's
 * coming is clear; each becomes an online account like Dropbox when it arrives.
 */
const COMING_SOON = [
  { id: "googledrive", name: "Google Drive", icon: Cloud },
  { id: "evernote", name: "Evernote", icon: NotebookPen },
];

const LAST_SOURCE = "uncloud.add-files.source";

/** Somewhere the dialog can be looking. */
type Where =
  | { at: "start" }
  | { at: "computer" }
  | { at: "shared" }
  | { at: "account"; id: string }
  | { at: "place"; id: string };

/** One step into a folder, by the name a person knows it by and the path the source knows. */
interface Step {
  name: string;
  path: string;
}

interface Props {
  /** Whether an import is already going, since only one runs at a time. */
  importing: boolean;
  isAdmin: boolean;
  /** Room left on the drive, said up front so nobody picks something that can't fit. */
  storage: StorageReport | null;
  onStarted: (job: ImportJob) => void;
  /** Dropbox needs an app set up before anybody can connect; this opens wherever that happens. */
  onSetUpDropbox: () => void;
}

function PlaceIcon({ kind, size = 19 }: { kind: PlaceKind; size?: number }) {
  if (kind === "drive") return <Usb size={size} strokeWidth={1.6} />;
  if (kind === "cloud") return <Cloud size={size} strokeWidth={1.6} />;
  return <Folder size={size} strokeWidth={1.6} fill="currentColor" fillOpacity={0.14} />;
}

function readLastSource(): string | null {
  try {
    return window.localStorage.getItem(LAST_SOURCE);
  } catch {
    return null;
  }
}

function rememberSource(id: string) {
  try {
    window.localStorage.setItem(LAST_SOURCE, id);
  } catch {
    // Starting from the beginning next time is fine.
  }
}

/**
 * The one way files get into somebody's space. Every source — the computer Uncloud runs on, an
 * online account, a folder somebody shared — is browsed the same way and added the same way; the
 * only difference is that an online account has to be signed in to first.
 */
export default function AddFiles({ importing, isAdmin, storage, onStarted, onSetUpDropbox }: Props) {
  const [sources, setSources] = useState<ImportSources | null>(null);
  const [where, setWhere] = useState<Where>({ at: "start" });
  const [trail, setTrail] = useState<Step[]>([]);
  const [entries, setEntries] = useState<SourceEntry[] | null>(null);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [typing, setTyping] = useState(false);
  const [typedPath, setTypedPath] = useState("");

  const load = useCallback(async () => {
    try {
      const latest = await api<ImportSources>("/imports/sources");
      setSources(latest);
      return latest;
    } catch (problem: unknown) {
      setError(problem instanceof Error ? problem.message : "Couldn’t reach Uncloud.");
      return null;
    }
  }, []);

  useEffect(() => {
    // Coming back from signing in to Dropbox means Dropbox is what they were doing.
    const outcome = new URLSearchParams(window.location.search).get("dropbox");
    if (outcome) {
      setNotice(
        outcome === "connected"
          ? "Dropbox is connected. Choose what to add."
          : outcome === "denied"
            ? "Dropbox sign-in was cancelled."
            : "Dropbox sign-in didn’t finish. Try again.",
      );
      window.history.replaceState(null, "", window.location.pathname + window.location.hash);
    }
    void load().then((latest) => {
      if (!latest) return;
      if (outcome) return setWhere({ at: "account", id: "dropbox" });
      // Picking up where they left off saves a click for the common case of one favourite place.
      const last = readLastSource();
      if (last === "computer" && latest.canAddFolders) setWhere({ at: "computer" });
      else if (last && latest.accounts.some((account) => account.id === last))
        setWhere({ at: "account", id: last });
    });
  }, [load]);

  const place = sources?.places.find((candidate) => candidate.id === (where.at === "place" ? where.id : ""));
  const account = sources?.accounts.find((candidate) => candidate.id === (where.at === "account" ? where.id : ""));
  const shared = sources?.places.filter((candidate) => !candidate.mine) ?? [];
  const mine = sources?.places.filter((candidate) => candidate.mine) ?? [];
  // The id the server knows this source by, when it is somewhere with files to list.
  const sourceId = where.at === "place" ? where.id : where.at === "account" && account?.connected ? where.id : null;

  // Listing whatever folder is being looked at, whenever that changes.
  const folder = trail.at(-1)?.path ?? "";
  useEffect(() => {
    if (!sourceId) return;
    if (where.at === "place" && place && !place.available) return;
    const controller = new AbortController();
    setEntries(null);
    setError("");
    api<SourceEntry[]>(
      `/imports/sources/${encodeURIComponent(sourceId)}/files?${new URLSearchParams({ path: folder })}`,
      { signal: controller.signal },
    )
      .then((listed) => {
        if (!controller.signal.aborted) setEntries(listed);
      })
      .catch((problem: unknown) => {
        if (!controller.signal.aborted)
          setError(problem instanceof Error ? problem.message : "Couldn’t open this folder.");
      });
    return () => controller.abort();
  }, [sourceId, folder, where.at, place]);

  function go(next: Where) {
    setWhere(next);
    setTrail([]);
    setEntries(null);
    setError("");
    setNotice("");
    setTyping(false);
    if (next.at === "computer") rememberSource("computer");
    if (next.at === "account") rememberSource(next.id);
  }

  async function run(label: string, action: () => Promise<void>) {
    setBusy(label);
    setError("");
    try {
      await action();
    } catch (problem: unknown) {
      setError(problem instanceof Error ? problem.message : "That didn’t work.");
    } finally {
      setBusy("");
    }
  }

  const add = (source: string, path: string, label: string) =>
    run(`add:${source}:${path}`, async () => {
      const { job } = await api<{ job: ImportJob }>("/imports", {
        method: "POST",
        body: JSON.stringify({ source, remotePath: path, label }),
      });
      onStarted(job);
    });

  /** A folder on this computer becomes this account's own the first time it is used. */
  async function own(path: string): Promise<ImportPlace> {
    const added = await api<ImportPlace>("/host/places", {
      method: "POST",
      body: JSON.stringify({ path }),
    });
    await load();
    return added;
  }

  const openSuggestion = (suggestion: SuggestedPlace) =>
    run(`open:${suggestion.path}`, async () => {
      const added = await own(suggestion.path);
      go({ at: "place", id: added.id });
    });

  const addSuggestion = (suggestion: SuggestedPlace) =>
    run(`add:${suggestion.path}`, async () => {
      const added = await own(suggestion.path);
      const { job } = await api<{ job: ImportJob }>("/imports", {
        method: "POST",
        body: JSON.stringify({ source: added.id, remotePath: "/", label: added.name }),
      });
      onStarted(job);
    });

  const chooseAnother = () =>
    run("picker", async () => {
      const { path } = await api<{ path: string | null }>("/folder-picker", { method: "POST" });
      if (!path) return;
      const added = await own(path);
      go({ at: "place", id: added.id });
    });

  function addTyped(event: FormEvent) {
    event.preventDefault();
    const path = typedPath.trim();
    if (!path) return;
    void run("typed", async () => {
      const added = await own(path);
      setTypedPath("");
      go({ at: "place", id: added.id });
    });
  }

  const share = (target: ImportPlace, next: boolean) =>
    run("share", async () => {
      await api(`/host/places/${encodeURIComponent(target.id)}`, {
        method: "PATCH",
        body: JSON.stringify({ shared: next }),
      });
      setNotice(
        next
          ? `Everyone on this Uncloud can now add files from ${target.name} to their own space.`
          : `${target.name} is private again. Only you can add files from it.`,
      );
      await load();
    });

  const forget = (target: ImportPlace) =>
    run("forget", async () => {
      await api(`/host/places/${encodeURIComponent(target.id)}`, { method: "DELETE" });
      await load();
      go({ at: "computer" });
      setNotice(`${target.name} was taken off this list. Nothing on this computer or in your files changed.`);
    });

  const connect = (target: ImportAccount) =>
    run("connect", async () => {
      const { authorizeUrl } = await api<{ authorizeUrl: string }>(
        `/providers/${encodeURIComponent(target.id)}/connect`,
        { method: "POST" },
      );
      window.location.href = authorizeUrl;
    });

  const disconnect = (target: ImportAccount) =>
    run("disconnect", async () => {
      await api(`/providers/${encodeURIComponent(target.id)}/disconnect`, { method: "POST" });
      setNotice(`Uncloud is signed out of ${target.name}. Files you already added stay in your files.`);
      await load();
    });

  if (!sources)
    return error ? (
      <p className="error-message" role="alert">
        {error}
      </p>
    ) : (
      <div className="file-loading" role="status">
        <LoaderCircle className="spin" size={22} />
        One moment…
      </div>
    );

  const disabled = importing || busy !== "";
  const waitNote = importing && (
    <p className="library-note add-wait" role="status">
      Some files are still being added. You can look around, and add more once they’re in.
    </p>
  );
  const level = spaceLevel(storage);
  const room = storage?.freeBytes != null && (
    <p className={`add-room level-${level}`}>
      <HardDrive size={14} />
      <span>
        <strong>{formatSize(storage.freeBytes)} free</strong> on this Uncloud
        {level === "critical"
          ? " — almost full, so only small things will fit."
          : level === "low"
            ? " — running low."
            : "."}{" "}
        Uncloud checks that what you choose fits before it copies anything.
      </span>
    </p>
  );
  const messages = (
    <>
      {room}
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      {waitNote}
    </>
  );

  // The first question: where are the files?
  if (where.at === "start") {
    const connected = (candidate: ImportAccount) =>
      candidate.connected
        ? `Signed in as ${candidate.accountName ?? "you"}`
        : candidate.configured && candidate.connectableHere
          ? "Sign in to connect"
          : isAdmin
            ? "Needs a few minutes to set up"
            : "Not set up yet";
    return (
      <div className="add-files">
        <h3 className="add-question">Where are your files?</h3>
        {messages}
        <div className="source-grid">
          {sources.canAddFolders && (
            <SourceCard
              icon={<Laptop size={22} strokeWidth={1.5} />}
              name="This computer"
              detail="Folders and drives on the computer Uncloud runs on"
              onClick={() => go({ at: "computer" })}
            />
          )}
          {sources.accounts.map((candidate) => (
            <SourceCard
              key={candidate.id}
              icon={<Cloud size={22} strokeWidth={1.5} />}
              name={candidate.name}
              detail={connected(candidate)}
              onClick={() => go({ at: "account", id: candidate.id })}
            />
          ))}
          {shared.length > 0 && (
            <SourceCard
              icon={<Users size={22} strokeWidth={1.5} />}
              name="Shared with you"
              detail={`${shared.length} folder${shared.length === 1 ? "" : "s"} from people here`}
              onClick={() => go({ at: "shared" })}
            />
          )}
          {COMING_SOON.map((soon) => (
            <SourceCard
              key={soon.id}
              icon={<soon.icon size={22} strokeWidth={1.5} />}
              name={soon.name}
              detail="Coming soon"
              soon
            />
          ))}
        </div>
        <p className="field-help add-promise">
          <Lock size={13} /> Uncloud copies what you choose into your files, where only you can see
          it. The originals stay exactly where they are.
        </p>
      </div>
    );
  }

  // Where the dialog is, from the top, as something to click back along.
  const root =
    where.at === "computer" || (where.at === "place" && place?.mine)
      ? { name: "This computer", to: { at: "computer" } as Where }
      : where.at === "shared" || where.at === "place"
        ? { name: "Shared with you", to: { at: "shared" } as Where }
        : { name: account?.name ?? "Online", to: where };
  const crumbs: { name: string; onClick: (() => void) | null }[] = [
    { name: root.name, onClick: () => go(root.to) },
  ];
  if (where.at === "place" && place)
    crumbs.push({ name: place.name, onClick: () => setTrail([]) });
  trail.forEach((step, index) =>
    crumbs.push({ name: step.name, onClick: () => setTrail((current) => current.slice(0, index + 1)) }),
  );
  crumbs[crumbs.length - 1].onClick = null;

  const header = (
    <div className="add-nav">
      <button className="link-button add-back" onClick={() => go({ at: "start" })}>
        <ArrowLeft size={15} />
        All places
      </button>
      <nav aria-label="Where you are" className="breadcrumbs add-crumbs">
        {crumbs.map((crumb, index) => (
          <span key={index}>
            {index > 0 && <ChevronRight size={14} />}
            {crumb.onClick ? (
              <button onClick={crumb.onClick}>{crumb.name}</button>
            ) : (
              <button aria-current="location" disabled>
                {crumb.name}
              </button>
            )}
          </span>
        ))}
      </nav>
    </div>
  );

  // This computer, from the top: the folders on it, whether used before or not, all alike.
  if (where.at === "computer") {
    const rows = [
      ...mine.map((candidate) => ({ key: candidate.id, place: candidate, suggestion: null })),
      ...sources.suggestions.map((candidate) => ({ key: candidate.path, place: null, suggestion: candidate })),
    ];
    return (
      <div className="add-files">
        {header}
        {messages}
        <ul className="import-list browse-list">
          {rows.map(({ key, place: known, suggestion }) => {
            const name = known?.name ?? suggestion!.name;
            const kind = known?.kind ?? suggestion!.kind;
            const unavailable = known != null && !known.available;
            const open = () =>
              known ? go({ at: "place", id: known.id }) : void openSuggestion(suggestion!);
            return (
              <li key={key} className={unavailable ? "unavailable" : ""}>
                <button className="browse-name" onClick={open} disabled={unavailable || busy !== ""}>
                  <PlaceIcon kind={kind} />
                  <span>
                    <strong>{name}</strong>
                    <span className="muted">
                      {unavailable
                        ? "Not plugged in right now"
                        : known?.shared
                          ? "Shared with everyone here"
                          : kind === "drive"
                            ? "Drive"
                            : kind === "cloud"
                              ? // Not to be mistaken for the online account of the same name.
                                `Kept in step by the ${name} app`
                              : "Folder"}
                    </span>
                  </span>
                </button>
                <AddButton
                  busy={busy === `add:${known?.id ?? ""}:/` || busy === `add:${suggestion?.path ?? ""}`}
                  disabled={disabled || unavailable}
                  onClick={() =>
                    known ? void add(known.id, "/", known.name) : void addSuggestion(suggestion!)
                  }
                />
              </li>
            );
          })}
        </ul>
        {rows.length === 0 && (
          <p className="field-help">Uncloud didn’t find any of the usual folders on this computer.</p>
        )}
        <div className="add-another">
          {sources.canPickFolder && !typing ? (
            <button className="button secondary" onClick={() => void chooseAnother()} disabled={busy !== ""}>
              {busy === "picker" ? <LoaderCircle className="spin" size={16} /> : <FolderOpen size={16} />}
              {busy === "picker" ? "Look for the window on the Uncloud computer…" : "Choose another folder…"}
            </button>
          ) : typing ? (
            <form className="add-typed" onSubmit={addTyped}>
              <label htmlFor="typed-folder">Where is the folder?</label>
              <input
                id="typed-folder"
                className="path-input"
                value={typedPath}
                onChange={(event) => setTypedPath(event.target.value)}
                placeholder="/Users/you/Old photos"
                autoComplete="off"
                spellCheck={false}
                disabled={busy !== ""}
              />
              <button className="button secondary" disabled={busy !== "" || !typedPath.trim()}>
                Open it
              </button>
            </form>
          ) : (
            <button className="link-button" onClick={() => setTyping(true)}>
              Another folder…
            </button>
          )}
          {sources.canPickFolder && !typing && (
            <button className="link-button" onClick={() => setTyping(true)}>
              Not at that computer? Type where the folder is
            </button>
          )}
        </div>
        <p className="field-help add-promise">
          <Lock size={13} /> Only you can add from the folders here, unless you choose to share one.
        </p>
      </div>
    );
  }

  // Folders other people chose to share with everyone here.
  if (where.at === "shared")
    return (
      <div className="add-files">
        {header}
        {messages}
        <ul className="import-list browse-list">
          {shared.map((candidate) => (
            <li key={candidate.id} className={candidate.available ? "" : "unavailable"}>
              <button
                className="browse-name"
                onClick={() => go({ at: "place", id: candidate.id })}
                disabled={!candidate.available}
              >
                <PlaceIcon kind={candidate.kind} />
                <span>
                  <strong>{candidate.name}</strong>
                  <span className="muted">
                    {candidate.available
                      ? `Shared by ${candidate.sharedBy ?? "someone here"}`
                      : "Not plugged in right now"}
                  </span>
                </span>
              </button>
              <AddButton
                busy={busy === `add:${candidate.id}:/`}
                disabled={disabled || !candidate.available}
                onClick={() => void add(candidate.id, "/", candidate.name)}
              />
            </li>
          ))}
        </ul>
        {shared.length === 0 && <p className="field-help">Nothing is shared with you right now.</p>}
      </div>
    );

  // An online account that isn't ready to browse yet.
  if (where.at === "account" && account && !account.connected)
    return (
      <div className="add-files">
        {header}
        {messages}
        {account.configured && account.connectableHere ? (
          <div className="notice-card">
            <h2>Connect your {account.name}</h2>
            <p className="field-help">
              You’ll sign in on {account.name}’s own page. Uncloud can only look at and copy your
              files — it can never change or delete anything in your {account.name}.
            </p>
            <button className="button primary" onClick={() => void connect(account)} disabled={busy !== ""}>
              {busy === "connect" ? `Opening ${account.name}…` : `Connect ${account.name}`}
              <ArrowUpRight size={15} />
            </button>
          </div>
        ) : account.configured ? (
          // Uncloud's own app is there, but it can only finish a sign-in on the computer Uncloud
          // runs on, and this browser isn't there.
          <div className="notice-card">
            <h2>Connect {account.name} from the Uncloud computer</h2>
            <p className="field-help">
              {isAdmin
                ? `Right now ${account.name} can only be connected from the computer Uncloud runs on. Do it there, or set ${account.name} up once for everyone — about five minutes on ${account.name}’s website — and it connects from anywhere.`
                : `Right now ${account.name} can only be connected from the computer Uncloud runs on. Do it there, or set it up yourself — about five minutes on ${account.name}’s website — and it connects from anywhere.`}
            </p>
            <button className="button primary" onClick={onSetUpDropbox}>
              {isAdmin ? `Set up ${account.name} for everyone` : "Set it up myself"}
              <ArrowUpRight size={15} />
            </button>
          </div>
        ) : (
          <div className="notice-card">
            <h2>{account.name} needs setting up first</h2>
            <p className="field-help">
              {isAdmin
                ? `It’s a one-time step on ${account.name}’s website, and takes about five minutes. After that, everyone here can connect their own ${account.name} with one click.`
                : `Whoever looks after this Uncloud can set it up once for everybody. Or you can set it up yourself — it takes about five minutes on ${account.name}’s website.`}
            </p>
            <button className="button primary" onClick={onSetUpDropbox}>
              {isAdmin ? `Set up ${account.name}` : "Set it up myself"}
              <ArrowUpRight size={15} />
            </button>
          </div>
        )}
      </div>
    );

  // Inside something with files in it: an online account, or a folder on this computer.
  const source = where.at === "place" ? place : account;
  if (!source)
    return (
      <div className="add-files">
        {header}
        <div className="notice-card">
          <h2>That isn’t here any more</h2>
          <p className="field-help">Go back and choose somewhere else.</p>
        </div>
      </div>
    );
  const sourceName = source.name;
  const current = trail.at(-1);
  // A folder can be added whole from inside it. An online account's very top can't be, which is
  // also not a thing anybody wants in one go.
  const whole =
    where.at === "place"
      ? { path: current?.path ?? "/", name: current?.name ?? sourceName }
      : current
        ? { path: current.path, name: current.name }
        : null;

  return (
    <div className="add-files">
      {header}
      {messages}
      {where.at === "place" && place && !place.available ? (
        <div className="notice-card">
          <h2>{place.name} isn’t here right now</h2>
          <p className="field-help">If it’s on a drive, plug the drive back in and try again.</p>
        </div>
      ) : (
        <>
          <div className="add-toolbar">
            <span className="muted add-where">
              {where.at === "account" && account
                ? `Signed in to ${account.name} as ${account.accountName ?? "you"}`
                : place && !place.mine
                  ? `Shared by ${place.sharedBy ?? "someone here"}`
                  : place?.shared
                    ? "Everyone here can add from this folder"
                    : "Only you can add from this folder"}
            </span>
            {whole && (
              <button
                className="button primary"
                onClick={() => void add(sourceId!, whole.path, whole.name)}
                disabled={disabled}
              >
                {busy === `add:${sourceId}:${whole.path}` ? (
                  <LoaderCircle className="spin" size={15} />
                ) : (
                  <Plus size={15} />
                )}
                Add all of {whole.name}
              </button>
            )}
          </div>
          {entries === null && !error ? (
            <div className="file-loading" role="status">
              <LoaderCircle className="spin" size={22} />
              Opening {current?.name ?? sourceName}…
            </div>
          ) : entries?.length === 0 ? (
            <p className="field-help">This folder is empty.</p>
          ) : entries ? (
            <ul className="import-list browse-list">
              {entries.map((entry) => (
                <li key={entry.id}>
                  <button
                    className="browse-name"
                    onClick={() =>
                      entry.isFolder && setTrail((steps) => [...steps, { name: entry.name, path: entry.path }])
                    }
                    disabled={!entry.isFolder}
                  >
                    {entry.isFolder ? (
                      <Folder size={19} strokeWidth={1.6} fill="currentColor" fillOpacity={0.14} />
                    ) : (
                      <File size={19} strokeWidth={1.6} />
                    )}
                    <span>
                      <strong>{entry.name}</strong>
                      <span className="muted">{entry.isFolder ? "Folder" : formatSize(entry.size)}</span>
                    </span>
                    {entry.isFolder && <ChevronRight size={16} className="browse-chevron" />}
                  </button>
                  <AddButton
                    busy={busy === `add:${sourceId}:${entry.path}`}
                    disabled={disabled}
                    onClick={() => void add(sourceId!, entry.path, entry.name)}
                  />
                </li>
              ))}
            </ul>
          ) : null}
          <p className="field-help add-promise">
            <HardDrive size={13} /> Copies go into My files, in a folder called{" "}
            <strong>{source.destination}</strong>. Nothing in {sourceName} is moved or changed,
            and adding something twice only brings what’s new.
          </p>
          {where.at === "account" && account && (
            <button className="link-button" onClick={() => void disconnect(account)} disabled={busy !== ""}>
              Sign Uncloud out of {account.name}
            </button>
          )}
          {place?.mine && (
            <div className="place-controls">
              <button className="link-button" onClick={() => void share(place, !place.shared)} disabled={busy !== ""}>
                {place.shared ? <Lock size={14} /> : <Users size={14} />}
                {place.shared ? "Make private again" : "Share this folder with everyone here"}
              </button>
              <button className="link-button" onClick={() => void forget(place)} disabled={busy !== ""}>
                Take off this list
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function SourceCard({
  icon,
  name,
  detail,
  onClick,
  soon = false,
}: {
  icon: ReactNode;
  name: string;
  detail: string;
  onClick?: () => void;
  soon?: boolean;
}) {
  return (
    <button className={`source-card${soon ? " soon" : ""}`} onClick={onClick} disabled={soon}>
      <span className="source-card-icon">{icon}</span>
      <strong>{name}</strong>
      <span className="muted">{detail}</span>
    </button>
  );
}

function AddButton({ busy, disabled, onClick }: { busy: boolean; disabled: boolean; onClick: () => void }) {
  return (
    <button className="button secondary add-button" onClick={onClick} disabled={disabled}>
      {busy ? <LoaderCircle className="spin" size={15} /> : <Plus size={15} />}
      Add
    </button>
  );
}

