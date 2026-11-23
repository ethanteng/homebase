import { useCallback, useEffect, useState } from "react";
import {
  Check,
  ChevronRight,
  CircleCheck,
  Copy,
  FolderSync,
  History,
  Inbox,
  KeyRound,
  Laptop,
  LoaderCircle,
  Plus,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type {
  DirectoryListing,
  PairingCode,
  SyncDevice,
  SyncFolder,
  SyncProgress,
  SyncReach,
  SyncStatus,
  User,
} from "./api";
import { SignedInAs } from "./PairApproval";

/// What your computers still have to get of a folder, or null once every one of them has it all.
/// This Uncloud having everything says nothing about whether they do: a big folder takes a
/// computer far longer than it took here.
function behind(folder: SyncFolder, devices: SyncDevice[]): string | null {
  // From an Uncloud that doesn't say, nothing is claimed either way.
  if (!folder.computers) return null;
  const known = folder.computers;
  // A computer Uncloud couldn't ask about hasn't been heard from, which isn't the same as done.
  const computers = folder.deviceIds.map(
    (deviceId): SyncProgress =>
      known.find((computer) => computer.deviceId === deviceId) ?? {
        deviceId,
        state: "unheard",
        completion: 0,
        needBytes: 0,
        needItems: 0,
      },
  );
  // Syncthing says "unknown" of a computer that's away, and still knows how far it had got: one
  // asleep with everything is up to date as far as anybody can tell.
  const waiting = computers.filter(
    (computer) =>
      !(computer.state === "valid" || computer.state === "unknown") ||
      computer.needItems > 0 ||
      computer.needBytes > 0,
  );
  if (waiting.length === 0) return null;
  if (waiting.length > 1) {
    const toGo = waiting.reduce((sum, computer) => sum + computer.needBytes, 0);
    return toGo > 0
      ? `Sending to ${waiting.length} computers, ${formatSize(toGo)} to go`
      : `Waiting on ${waiting.length} computers`;
  }

  const [computer] = waiting;
  const device = devices.find((entry) => entry.deviceId === computer.deviceId);
  const who = device?.name ?? "your computer";
  if (computer.state === "notSharing") return `Waiting for ${who} to accept it`;
  if (computer.state === "paused") return `Paused on ${who}`;
  if (computer.state !== "valid" && computer.state !== "unknown") return `Waiting to hear from ${who}`;
  // Never 100% while something is left, however little.
  const done = Math.min(99, Math.floor(computer.completion));
  const toGo = computer.needBytes > 0 ? `, ${formatSize(computer.needBytes)} to go` : "";
  return device?.connected
    ? `Sending to ${who}: ${done}% there${toGo}`
    : `${who} is ${done}% there${toGo} — it catches up when it’s back online`;
}

function describe(folder: SyncFolder, devices: SyncDevice[]) {
  if (folder.error) return folder.error;
  // Held back by Syncthing until there's room, which is worth saying before anything else: from
  // the computer it looks as though they've been sent.
  const waiting = folder.waitingForRoom ?? 0;
  if (waiting > 0)
    return `${waiting} file${waiting === 1 ? " is" : "s are"} waiting for room on this Uncloud`;
  const state =
    folder.state === "idle"
      ? (behind(folder, devices) ?? "Up to date")
      : folder.state === "syncing" || folder.state === "sync-preparing"
        ? "Syncing"
        : folder.state === "scanning" || folder.state === "scan-waiting"
          ? "Checking for changes"
          : folder.state
            ? folder.state.charAt(0).toUpperCase() + folder.state.slice(1)
            : "Starting";
  const files = `${folder.files.toLocaleString()} file${folder.files === 1 ? "" : "s"}`;
  return `${state} · ${files} · ${formatSize(folder.bytes)}`;
}

/// The address the app pairs with is the one this page was opened at: if a browser on that
/// computer can reach Uncloud here, so can the app.
function pairingLink(code: string) {
  return `uncloud://pair?address=${encodeURIComponent(window.location.origin)}&code=${encodeURIComponent(code)}`;
}

/// The Mac app, as the README's Get the app gives it: always the latest build.
const MAC_APP = {
  appleSilicon: "https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-arm64.dmg",
  intel: "https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-x64.dmg",
};

/// This page open on the Uncloud computer itself, at an address no other computer can use.
const onHost = () => ["127.0.0.1", "localhost", "[::1]"].includes(window.location.hostname);

/// A phone or tablet, where the Mac app isn't something to open.
const onTouch = () => window.matchMedia?.("(pointer: coarse)").matches ?? false;

/// `onOpenSettings` is for an admin, who can turn on Access from anywhere when other computers
/// can't reach this Uncloud yet; anybody else is told who can.
export default function SyncPanel({
  me,
  onSignOut,
  onOpenSettings,
}: {
  me: User;
  onSignOut: () => void;
  onOpenSettings?: () => void;
}) {
  const [status, setStatus] = useState<SyncStatus | null>(null);
  // Whether other computers can reach this Uncloud, which adding one depends on.
  const [reach, setReach] = useState<SyncReach | null>(null);
  const [deviceId, setDeviceId] = useState("");
  const [deviceName, setDeviceName] = useState("");
  const [folderPath, setFolderPath] = useState("");
  const [offerPaths, setOfferPaths] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [copied, setCopied] = useState(false);
  const [pairing, setPairing] = useState<PairingCode | null>(null);
  const [adding, setAdding] = useState(false);
  // Choosing another folder to sync: the folders at the top of My files, read when asked for.
  const [choosing, setChoosing] = useState(false);
  const [choices, setChoices] = useState<string[] | null>(null);

  const load = useCallback(async () => {
    try {
      setStatus(await api<SyncStatus>("/sync"));
      // Turned on in Settings while this is open, it says so on the next look.
      api<SyncReach>("/sync/reach").then(setReach, () => setReach(null));
    } catch (problem: unknown) {
      setError(
        problem instanceof Error ? problem.message : "Couldn’t reach Uncloud.",
      );
    }
  }, []);

  useEffect(() => {
    void load();
    // Connections and progress change on the other computer's schedule, not ours.
    const timer = window.setInterval(() => void load(), 10000);
    return () => window.clearInterval(timer);
  }, [load]);

  async function run(label: string, action: () => Promise<void>) {
    setBusy(label);
    setError("");
    setNotice("");
    try {
      await action();
    } catch (problem: unknown) {
      setError(problem instanceof Error ? problem.message : "That didn’t work.");
    } finally {
      setBusy("");
    }
  }

  const pair = () =>
    run("pair", async () => {
      setStatus(
        await api<SyncStatus>("/sync/devices", {
          method: "POST",
          body: JSON.stringify({ deviceId, name: deviceName || null }),
        }),
      );
      setNotice(
        "Added. If you haven’t yet, add this Uncloud’s ID in Syncthing on that computer too — both sides have to agree.",
      );
      setDeviceId("");
      setDeviceName("");
    });

  const getCode = () =>
    run("code", async () => {
      setPairing(
        await api<PairingCode>("/sync/pairing-codes", { method: "POST" }),
      );
    });

  // On the computer being added, with the app: a code it never has to see, handed to the app.
  const openApp = () =>
    run("open", async () => {
      const code = await api<PairingCode>("/sync/pairing-codes", { method: "POST" });
      window.location.href = pairingLink(code.code);
    });

  const unpair = (id: string, name: string) =>
    run(id, async () => {
      if (
        !window.confirm(
          `Stop syncing with ${name}? Nothing is deleted on either side.`,
        )
      )
        return;
      setStatus(
        await api<SyncStatus>(`/sync/devices/${encodeURIComponent(id)}`, {
          method: "DELETE",
        }),
      );
    });

  const share = () =>
    run("share", async () => {
      await api("/sync/folders", {
        method: "POST",
        body: JSON.stringify({ path: folderPath, deviceIds: null }),
      });
      setNotice(
        `Syncing ${folderPath || "all of your files"} with your computers. Macs with the Uncloud app pick it up by themselves; in Syncthing, accept it there.`,
      );
      setFolderPath("");
      setChoosing(false);
      await load();
    });

  // A folder can't sync inside one that already does, or around one, so the choices are the
  // folders not syncing yet, and all of your files only while nothing else syncs.
  const syncedPaths = new Set(status?.folders.map((folder) => folder.path) ?? []);
  const allOffered = (status?.folders.length ?? 0) === 0;
  const available = (choices ?? []).filter((path) => !syncedPaths.has(path));

  const chooseFolder = () => {
    setChoosing(true);
    setChoices(null);
    setFolderPath("");
    api<DirectoryListing>(`/files?${new URLSearchParams({ path: "" })}`)
      .then((listing) => {
        const folders = listing.entries.filter((entry) => entry.isDirectory).map((entry) => entry.path);
        setChoices(folders);
        if (!allOffered) setFolderPath(folders.find((path) => !syncedPaths.has(path)) ?? "");
      })
      .catch(() => setChoices([]));
  };

  const stop = (folder: SyncFolder) =>
    run(folder.id, async () => {
      if (
        !window.confirm(
          `Stop syncing ${folder.path || "all of your files"}? The files stay here and on your computers.`,
        )
      )
        return;
      setStatus(
        await api<SyncStatus>(`/sync/folders/${encodeURIComponent(folder.id)}`, {
          method: "DELETE",
        }),
      );
    });

  const accept = (folderId: string, label: string) =>
    run(folderId, async () => {
      const path = offerPaths[folderId]?.trim() || label;
      await api("/sync/folders/accept", {
        method: "POST",
        body: JSON.stringify({ folderId, path }),
      });
      setNotice(`Syncing ${label} in ${path}.`);
      await load();
    });

  const copyId = () => {
    if (!status?.hostDeviceId) return;
    void navigator.clipboard?.writeText(status.hostDeviceId).then(() => {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    });
  };

  if (!status && !error)
    return (
      <div className="file-loading" role="status">
        <LoaderCircle className="spin" size={24} />
        Looking for your computers…
      </div>
    );

  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Computers</h1>
          <p>Computers with the Uncloud app keep a folder in sync with your files here.</p>
        </div>
        {/* With none yet, adding one is already the whole page. */}
        {status && status.devices.length > 0 && (
          <div className="heading-actions">
            <button
              className="button primary"
              aria-expanded={adding}
              onClick={() => setAdding((open) => !open)}
            >
              <Plus size={17} />
              Add a computer
            </button>
          </div>
        )}
      </div>

      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {error && status && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}

      {status && !status.available && (
        <div className="notice-card">
          <h2>Syncing is off</h2>
          <p className="field-help">
            {status.detail ?? "Uncloud couldn’t start syncing."} An admin needs to restart Uncloud.
          </p>
        </div>
      )}

      {status && (
        <>
          {(adding || status.devices.length === 0) && (
            <section className="import-section add-computer" aria-labelledby="add-computer-title">
              <h2 id="add-computer-title">Add a computer</h2>
              {reach &&
                (reach.address ? (
                  <p className="reach-note ok">
                    <CircleCheck size={17} aria-hidden="true" />
                    <span>
                      Computers can reach this Uncloud at <strong>{new URL(reach.address).host}</strong>
                    </span>
                  </p>
                ) : (
                  <div className="reach-note warn" role="note">
                    <TriangleAlert size={17} aria-hidden="true" />
                    <div>
                      <strong>Other computers can’t reach this Uncloud yet</strong>
                      <span>
                        Computers need Access from anywhere turned on, even at home.{" "}
                        {onOpenSettings ? (
                          <button className="link-button" onClick={onOpenSettings}>
                            Turn it on in Settings
                          </button>
                        ) : (
                          "Ask whoever looks after Uncloud to turn it on in Settings."
                        )}
                      </span>
                    </div>
                  </div>
                ))}
              <ol className="steps">
                <li>
                  <span className="step-number" aria-hidden="true">1</span>
                  <div className="step-body">
                    <strong>Get the Uncloud app on the computer you’re adding</strong>
                    <span className="muted">
                      For Mac: <a href={MAC_APP.appleSilicon}>Apple silicon</a> ·{" "}
                      <a href={MAC_APP.intel}>Intel</a>
                    </span>
                  </div>
                </li>
                <li>
                  <span className="step-number" aria-hidden="true">2</span>
                  <div className="step-body">
                    <strong>Open it and choose Connect to your Uncloud</strong>
                    <span className="muted">It finds this Uncloud and asks you to sign in to add it.</span>
                  </div>
                </li>
              </ol>
              {!onHost() && !onTouch() && (
                <div className="open-app">
                  <button
                    className="button secondary"
                    onClick={() => void openApp()}
                    disabled={busy === "open" || !status.available}
                  >
                    <Laptop size={16} />
                    {busy === "open" ? "Opening…" : "Open in the Uncloud app"}
                  </button>
                  <span className="muted">On the computer you’re adding, with the app installed.</span>
                  {/* It hands the app a code for whoever is signed in here, like Get a pairing code. */}
                  <SignedInAs me={me} onSignOut={onSignOut} />
                </div>
              )}
              <details className="advanced-section">
                <summary>
                  <ChevronRight size={16} aria-hidden="true" />
                  Other ways to connect
                </summary>
                <div className="other-ways">
                  <div className="other-way">
                    <h3>With a pairing code</h3>
                    {!pairing ? (
                      <>
                        <p className="field-help">
                          For when the app can’t open a browser to sign in. Type the code in the
                          app, under Other ways to connect. It adds the computer to {me.displayName}’s
                          files.
                        </p>
                        <SignedInAs me={me} onSignOut={onSignOut} />
                        <button
                          className="button secondary"
                          onClick={() => void getCode()}
                          disabled={busy === "code" || !status.available}
                        >
                          <KeyRound size={15} />
                          {busy === "code" ? "Getting a code…" : "Get a pairing code"}
                        </button>
                      </>
                    ) : (
                      <div className="pairing">
                        <span className="pairing-code">
                          <code>{pairing.code}</code>
                          <span className="muted">
                            Works once, until{" "}
                            {new Date(pairing.expiresAt).toLocaleTimeString([], {
                              hour: "numeric",
                              minute: "2-digit",
                            })}
                          </span>
                          <button
                            className="link-button"
                            onClick={() => void getCode()}
                            disabled={busy === "code" || !status.available}
                          >
                            Get a new code
                          </button>
                        </span>
                        {/* A code is made for whoever is signed in, and adds the computer to their files. */}
                        <p className="field-help">
                          This code adds a computer to {me.displayName}’s files. In the app, choose
                          Other ways to connect, then Type a pairing code, and enter{" "}
                          <code>{reach?.address ?? window.location.origin}</code> and the code.
                        </p>
                        <SignedInAs me={me} onSignOut={onSignOut} />
                        {!reach?.address && onHost() && (
                          <p className="field-help warning-text">
                            This address only works on the Uncloud computer itself. Turn on Access
                            from anywhere first.
                          </p>
                        )}
                      </div>
                    )}
                  </div>
                  <div className="other-way">
                    <h3>Windows or Linux, with Syncthing</h3>
                    {status.hostDeviceId && (
                      <>
                        <p className="field-help">
                          Install{" "}
                          <a href="https://syncthing.net/downloads/" target="_blank" rel="noreferrer">
                            Syncthing
                          </a>{" "}
                          on the computer and add this Uncloud as a remote device:
                        </p>
                        <div className="device-id">
                          <code>{status.hostDeviceId}</code>
                          <button
                            className="button secondary"
                            onClick={copyId}
                            aria-label="Copy this Uncloud’s ID"
                          >
                            {copied ? <Check size={15} /> : <Copy size={15} />}
                            {copied ? "Copied" : "Copy"}
                          </button>
                        </div>
                        <p className="field-help">Then paste that computer’s ID here:</p>
                      </>
                    )}
                    <div className="sync-form">
                      <input
                        className="path-input"
                        value={deviceId}
                        onChange={(event) => setDeviceId(event.target.value)}
                        placeholder="Computer ID from Syncthing"
                        aria-label="Your computer’s ID"
                        spellCheck={false}
                      />
                      <input
                        className="path-input"
                        value={deviceName}
                        onChange={(event) => setDeviceName(event.target.value)}
                        placeholder="Name (optional)"
                        aria-label="A name for your computer"
                      />
                      <button
                        className="button primary"
                        onClick={() => void pair()}
                        disabled={busy === "pair" || deviceId.trim().length === 0 || !status.available}
                      >
                        <Laptop size={15} />
                        {busy === "pair" ? "Adding…" : "Add computer"}
                      </button>
                    </div>
                  </div>
                </div>
              </details>
            </section>
          )}

          {status.devices.length > 0 && (
            <section className="import-section" aria-label="Your computers">
              <ul className="import-list">
                {status.devices.map((device) => (
                  <li key={device.deviceId}>
                    <Laptop size={22} strokeWidth={1.6} />
                    <div>
                      <strong>{device.name}</strong>
                      <span className="muted">{device.connected ? "Connected" : "Not connected right now"}</span>
                    </div>
                    <span className={`sync-dot${device.connected ? " on" : ""}`} aria-hidden="true" />
                    <button
                      className="button secondary"
                      onClick={() => void unpair(device.deviceId, device.name)}
                      disabled={busy === device.deviceId || !status.available}
                    >
                      Remove
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section className="import-section" aria-labelledby="in-sync-title">
            <div className="import-section-head">
              <div>
                <h2 id="in-sync-title">What stays in sync</h2>
                <p className="field-help section-lead">
                  {status.devices.length === 0
                    ? "Add a computer first, then choose what it keeps in sync."
                    : status.folders.length === 0
                      ? "Nothing is syncing yet."
                      : status.devices.length === 1
                        ? "Kept the same here and on your computer."
                        : "Kept the same here and on all of your computers."}
                </p>
              </div>
              {/* Nothing can be added beside all of your files: everything is already in it. */}
              {status.available && status.devices.length > 0 && !syncedPaths.has("") && !choosing && (
                <button className="button secondary" onClick={chooseFolder}>
                  <Plus size={17} />
                  {allOffered ? "Choose what to sync" : "Sync another folder"}
                </button>
              )}
            </div>
            {status.folders.length > 0 && (
              <ul className="import-list">
                {status.folders.map((folder) => (
                  <li key={folder.id}>
                    <FolderSync size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{folder.path || "All of your files"}</strong>
                      <span className={folder.error || folder.waitingForRoom ? "error-message" : "muted"}>
                        {describe(folder, status.devices)} · with {folder.deviceIds.length}{" "}
                        computer{folder.deviceIds.length === 1 ? "" : "s"}
                      </span>
                    </div>
                    <button
                      className="button secondary"
                      onClick={() => void stop(folder)}
                      disabled={busy === folder.id || !status.available}
                    >
                      Stop syncing
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {choosing && (
              <form
                className="sync-choice"
                onSubmit={(event) => {
                  event.preventDefault();
                  void share();
                }}
              >
                <label htmlFor="sync-choice">Folder to sync</label>
                {choices === null ? (
                  <span className="muted">
                    <LoaderCircle size={15} className="spin" /> Looking at your folders…
                  </span>
                ) : !allOffered && available.length === 0 ? (
                  <span className="muted">Every folder is already syncing.</span>
                ) : (
                  <div className="sync-form">
                    <select
                      id="sync-choice"
                      className="path-input"
                      value={folderPath}
                      onChange={(event) => setFolderPath(event.target.value)}
                    >
                      {allOffered && <option value="">All of your files</option>}
                      {available.map((path) => (
                        <option key={path} value={path}>
                          {path}
                        </option>
                      ))}
                    </select>
                    <button className="button primary" disabled={busy === "share"}>
                      <FolderSync size={15} />
                      {busy === "share" ? "Starting…" : "Start syncing"}
                    </button>
                    <button
                      type="button"
                      className="button secondary"
                      onClick={() => setChoosing(false)}
                    >
                      Cancel
                    </button>
                  </div>
                )}
              </form>
            )}
            {status.folders.length > 0 && (
              <p className="field-help sync-undo">
                <History size={16} />
                <span>
                  Deleted or changed something on a computer by mistake? The old version is kept for
                  30 days in the hidden <code>.stversions</code> folder.
                </span>
              </p>
            )}
          </section>
          {/* Folders somebody shared from Syncthing on a Windows or Linux computer. Never the
              Uncloud app's: it only syncs what's chosen here, and the host leaves out a stopped
              folder a computer goes on offering back. */}
          {status.offers.length > 0 && (
            <section className="import-section" aria-labelledby="shared-title">
              <h2 id="shared-title">Shared from Syncthing</h2>
              <p className="field-help">
                A computer shared these folders in Syncthing. They don’t sync with your files here
                until you choose where to keep them.
              </p>
              <ul className="import-list">
                {status.offers.map((offer) => (
                  <li key={`${offer.folderId}-${offer.deviceId}`}>
                    <Inbox size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{offer.label}</strong>
                      <span className="muted">from {offer.deviceName}</span>
                    </div>
                    <input
                      className="path-input"
                      value={offerPaths[offer.folderId] ?? ""}
                      onChange={(event) =>
                        setOfferPaths((paths) => ({
                          ...paths,
                          [offer.folderId]: event.target.value,
                        }))
                      }
                      placeholder={offer.label}
                      aria-label={`Where to keep ${offer.label}`}
                      spellCheck={false}
                    />
                    <button
                      className="button primary"
                      onClick={() => void accept(offer.folderId, offer.label)}
                      disabled={busy === offer.folderId}
                    >
                      {busy === offer.folderId ? "Starting…" : "Start syncing"}
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}
      {error && !status && (
        <div className="empty-state connection-error" role="alert">
          <TriangleAlert size={32} />
          <h1>Couldn’t load your computers</h1>
          <p>{error}</p>
        </div>
      )}
    </>
  );
}
