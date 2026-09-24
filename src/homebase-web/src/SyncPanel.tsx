import { useCallback, useEffect, useState } from "react";
import {
  Check,
  Copy,
  FolderSync,
  Inbox,
  KeyRound,
  Laptop,
  LoaderCircle,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { PairingCode, SyncFolder, SyncStatus } from "./api";

function describe(folder: SyncFolder) {
  if (folder.error) return folder.error;
  const state =
    folder.state === "idle"
      ? "up to date"
      : folder.state === "syncing" || folder.state === "sync-preparing"
        ? "syncing"
        : folder.state === "scanning" || folder.state === "scan-waiting"
          ? "checking for changes"
          : (folder.state ?? "starting");
  return `${state} · ${folder.files} file${folder.files === 1 ? "" : "s"} · ${formatSize(folder.bytes)}`;
}

export default function SyncPanel() {
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
        `Offered ${folderPath.trim() || "all of your files"} to your computers. Accept it in Syncthing there, and choose where it should go.`,
      );
      setFolderPath("");
      await load();
    });

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
      <div className="page-heading">
        <div>
          <span className="eyebrow">YOUR OWN COMPUTERS</span>
          <h1>
            My computers<span className="heading-dot">.</span>
          </h1>
          <p>
            Keep your files in a folder on your own computer as well. Add,
            change or delete something there and the same happens here, and
            the other way round, even if you were offline when you did it.
          </p>
        </div>
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
          <h2>Syncthing isn’t running on this Uncloud</h2>
          <p className="field-help">
            {status.detail ?? "Uncloud couldn’t start Syncthing."} Uncloud uses
            Syncthing to talk to your computers, and normally brings its own.
            Whoever looks after this Uncloud needs to sort that out and restart
            it. Nothing below is syncing until then.
          </p>
        </div>
      )}

      {status && (
        <>
          {status.hostDeviceId && (
            <section className="import-section">
              <h2>This Uncloud</h2>
              <div className="device-id">
                <code>{status.hostDeviceId}</code>
                <button
                  className="button"
                  onClick={copyId}
                  aria-label="Copy this Uncloud’s ID"
                >
                  {copied ? <Check size={15} /> : <Copy size={15} />}
                  {copied ? "Copied" : "Copy"}
                </button>
              </div>
              <p className="field-help">
                Install{" "}
                <a href="https://syncthing.net/downloads/" target="_blank" rel="noreferrer">
                  Syncthing
                </a>{" "}
                on your computer, add this ID there as a remote device, then
                paste that computer’s own ID below.
              </p>
            </section>
          )}

          <section className="import-section">
            <h2>Your computers</h2>
            <div className="sync-form">
              <button
                className="button"
                onClick={() => void getCode()}
                disabled={busy === "code" || !status.available}
              >
                <KeyRound size={15} />
                {pairing ? "New pairing code" : "Get a pairing code"}
              </button>
              {pairing && (
                <span className="pairing-code">
                  <code>{pairing.code}</code>
                  <span className="muted">
                    works once, until{" "}
                    {new Date(pairing.expiresAt).toLocaleTimeString([], {
                      hour: "numeric",
                      minute: "2-digit",
                    })}
                  </span>
                </span>
              )}
            </div>
            <p className="field-help">
              Using the Uncloud app on your computer? Type this code into it and
              it pairs itself. Otherwise, add your computer by its Syncthing ID:
            </p>
            <div className="sync-form">
              <input
                className="path-input"
                value={deviceId}
                onChange={(event) => setDeviceId(event.target.value)}
                placeholder="Your computer’s ID, from Syncthing there"
                aria-label="Your computer’s ID"
                spellCheck={false}
              />
              <input
                className="path-input"
                value={deviceName}
                onChange={(event) => setDeviceName(event.target.value)}
                placeholder="What to call it (optional)"
                aria-label="A name for your computer"
              />
              <button
                className="button primary"
                onClick={() => void pair()}
                disabled={
                  busy === "pair" ||
                  deviceId.trim().length === 0 ||
                  !status.available
                }
              >
                <Laptop size={15} />
                {busy === "pair" ? "Adding…" : "Add computer"}
              </button>
            </div>
            {status.devices.length > 0 && (
              <ul className="import-list">
                {status.devices.map((device) => (
                  <li key={device.deviceId}>
                    <Laptop size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{device.name}</strong>
                      <span className="muted">
                        {device.deviceId.slice(0, 7)}… ·{" "}
                        {device.connected
                          ? "connected"
                          : "not connected right now"}
                      </span>
                    </div>
                    <span
                      className={`sync-dot${device.connected ? " on" : ""}`}
                      aria-hidden="true"
                    />
                    <button
                      className="button"
                      onClick={() => void unpair(device.deviceId, device.name)}
                      disabled={busy === device.deviceId || !status.available}
                    >
                      Remove
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {status.offers.length > 0 && (
            <section className="import-section">
              <h2>Offered by your computers</h2>
              <p className="field-help">
                Folders your computers want to keep here too. Nothing arrives
                until you accept one.
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
                      {busy === offer.folderId ? "Accepting…" : "Keep it here"}
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section className="import-section">
            <h2>Synced folders</h2>
            <div className="sync-form">
              <input
                className="path-input"
                value={folderPath}
                onChange={(event) => setFolderPath(event.target.value)}
                placeholder="A folder such as Documents — or leave empty for all of your files"
                aria-label="Folder to sync"
                spellCheck={false}
              />
              <button
                className="button primary"
                onClick={() => void share()}
                disabled={
                  busy === "share" ||
                  status.devices.length === 0 ||
                  !status.available
                }
              >
                <FolderSync size={15} />
                {busy === "share" ? "Offering…" : "Sync folder"}
              </button>
            </div>
            <p className="field-help">
              A file deleted or replaced on one of your computers is deleted or
              replaced here too. Uncloud keeps the old copy for 30 days, in a
              hidden <code>.stversions</code> folder beside it, so a mistake on
              a laptop can be undone. That isn’t a backup of this Uncloud.
            </p>
            {status.folders.length === 0 ? (
              <p className="field-help">Nothing is syncing yet.</p>
            ) : (
              <ul className="import-list">
                {status.folders.map((folder) => (
                  <li key={folder.id}>
                    <FolderSync size={19} strokeWidth={1.6} />
                    <div>
                      <strong>{folder.path || "All of your files"}</strong>
                      <span className={folder.error ? "error-message" : "muted"}>
                        {describe(folder)} · with {folder.deviceIds.length}{" "}
                        computer{folder.deviceIds.length === 1 ? "" : "s"}
                      </span>
                    </div>
                    <button
                      className="button"
                      onClick={() => void stop(folder)}
                      disabled={busy === folder.id || !status.available}
                    >
                      Stop syncing
                    </button>
                  </li>
                ))}
              </ul>
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
