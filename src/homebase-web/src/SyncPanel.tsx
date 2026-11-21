import { useCallback, useEffect, useState } from "react";
import {
  Check,
  ChevronRight,
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
import type { DirectoryListing, PairingCode, SyncFolder, SyncStatus, User } from "./api";
import PairApproval, { SignedInAs } from "./PairApproval";

function describe(folder: SyncFolder) {
  if (folder.error) return folder.error;
  // Held back by Syncthing until there's room, which is worth saying before anything else: from
  // the computer it looks as though they've been sent.
  const waiting = folder.waitingForRoom ?? 0;
  if (waiting > 0)
    return `${waiting} file${waiting === 1 ? " is" : "s are"} waiting for room on this Uncloud`;
  const state =
    folder.state === "idle"
      ? "Up to date"
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

/// `approving` is a computer that asked to be added, when this page was opened from the QR code
/// the Uncloud app shows on it. `onSignOut` is for whoever finds they are adding a computer to the
/// wrong account's files, and keeps that computer's question for the account they sign in as next.
export default function SyncPanel({
  me,
  onSignOut,
  approving = null,
  onApprovalClosed,
}: {
  me: User;
  onSignOut: () => void;
  approving?: string | null;
  onApprovalClosed?: () => void;
}) {
  const [status, setStatus] = useState<SyncStatus | null>(null);
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
        `Syncing ${folderPath || "all of your files"} with your computers. On a computer that uses Syncthing, accept it there.`,
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
      setNotice(`Keeping ${label} in ${path}.`);
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
      {approving && (
        <PairApproval
          id={approving}
          me={me}
          onApproved={setStatus}
          onClose={() => onApprovalClosed?.()}
          onSignOut={onSignOut}
        />
      )}
      <div className="page-heading">
        <div>
          <h1>Computers</h1>
          <p>These keep a folder in sync with your files here.</p>
        </div>
        {status && (
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
              <ol className="steps">
                <li>
                  <span className="step-number" aria-hidden="true">1</span>
                  <div className="step-body">
                    <strong>Open the Uncloud app on the computer</strong>
                    <span className="muted">Choose Connect to an Uncloud. At home, it finds this one.</span>
                  </div>
                </li>
                <li>
                  <span className="step-number" aria-hidden="true">2</span>
                  <div className="step-body">
                    <strong>Scan the code it shows with your phone</strong>
                    <span className="muted">Then tap Add this computer.</span>
                    {!pairing ? (
                      <button
                        className="link-button step-alternative"
                        onClick={() => void getCode()}
                        disabled={busy === "code" || !status.available}
                      >
                        <KeyRound size={15} />
                        {busy === "code" ? "Getting a code…" : "No phone? Get a pairing code instead"}
                      </button>
                    ) : (
                      <div className="pairing">
                        <span className="muted">No phone? Enter this code in the app instead:</span>
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
                          This code adds a computer to {me.displayName}’s files.{" "}
                          <a href={pairingLink(pairing.code)}>Open it in the Uncloud app</a>, or enter{" "}
                          <code>{window.location.origin}</code> and the code there.
                        </p>
                        <SignedInAs me={me} onSignOut={onSignOut} />
                        {["127.0.0.1", "localhost", "[::1]"].includes(window.location.hostname) && (
                          <p className="field-help warning-text">
                            This address only works on the Uncloud computer itself. Open Uncloud from
                            the computer you’re adding instead.
                          </p>
                        )}
                      </div>
                    )}
                  </div>
                </li>
              </ol>
              <details className="advanced-section">
                <summary>
                  <ChevronRight size={16} aria-hidden="true" />
                  Windows or Linux? Use Syncthing instead
                </summary>
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

          {status.offers.length > 0 && (
            <section className="import-section">
              <h2>Folders your computers offered</h2>
              <p className="field-help">Nothing arrives until you keep one.</p>
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
                      {busy === offer.folderId ? "Keeping…" : "Keep it here"}
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
                        {describe(folder)} · with {folder.deviceIds.length}{" "}
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
