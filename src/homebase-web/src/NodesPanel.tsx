import { useCallback, useEffect, useState } from "react";
import {
  Check,
  Copy,
  Inbox,
  FolderSync,
  Laptop,
  LoaderCircle,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { NodeStatus } from "./api";

export default function NodesPanel() {
  const [status, setStatus] = useState<NodeStatus | null>(null);
  const [deviceId, setDeviceId] = useState("");
  const [deviceName, setDeviceName] = useState("");
  const [folderPath, setFolderPath] = useState("");
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [copied, setCopied] = useState(false);

  const load = useCallback(async () => {
    try {
      setStatus(await api<NodeStatus>("/nodes"));
    } catch (problem: unknown) {
      setError(
        problem instanceof Error ? problem.message : "Couldn’t reach Uncloud.",
      );
    }
  }, []);

  useEffect(() => {
    void load();
    // Pairing and connection state change on the other computer's schedule, not ours.
    const timer = window.setInterval(() => void load(), 10000);
    return () => window.clearInterval(timer);
  }, [load]);

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

  const pair = () =>
    run("pair", async () => {
      await api<NodeStatus>("/nodes", {
        method: "POST",
        body: JSON.stringify({ deviceId, name: deviceName || null }),
      });
      setNotice(
        "Added. Now add this Uncloud’s ID on the other computer — both sides have to agree.",
      );
      setDeviceId("");
      setDeviceName("");
      await load();
    });

  const accept = (folderId: string, label: string) =>
    run(folderId, async () => {
      await api("/nodes/folders/accept", {
        method: "POST",
        body: JSON.stringify({ folderId, path: null }),
      });
      setNotice(`Keeping ${label} in step with the other computer.`);
      await load();
    });

  const share = () =>
    run("share", async () => {
      await api("/nodes/folders", {
        method: "POST",
        body: JSON.stringify({ path: folderPath, deviceIds: [] }),
      });
      setNotice(`Sharing ${folderPath}.`);
      setFolderPath("");
      await load();
    });

  const copyId = () => {
    if (!status?.deviceId) return;
    void navigator.clipboard?.writeText(status.deviceId).then(() => {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    });
  };

  if (!status && !error)
    return (
      <div className="file-loading" role="status">
        <LoaderCircle className="spin" size={24} />
        Looking for other computers…
      </div>
    );

  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">YOUR OWN COMPUTERS</span>
          <h1>
            Nodes<span className="heading-dot">.</span>
          </h1>
          <p>
            Connect another computer you own. A folder you share is kept the
            same on both, and a change made on either shows up on the other.
          </p>
        </div>
      </div>

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

      {status && !status.available ? (
        <div className="notice-card">
          <h2>Syncthing isn’t running</h2>
          <p className="field-help">
            {status.detail ??
              "Uncloud couldn’t start Syncthing on this computer."}{" "}
            Uncloud uses Syncthing to talk to your other computers. Install it
            (<code>brew install syncthing</code> on a Mac), then restart
            Uncloud. If it lives somewhere unusual, set{" "}
            <code>Homebase__Syncthing__Path</code>.
          </p>
        </div>
      ) : (
        status && (
          <>
            <section className="import-section">
              <h2>This computer</h2>
              <div className="device-id">
                <code>{status.deviceId}</code>
                <button
                  className="button"
                  onClick={copyId}
                  aria-label="Copy this computer’s ID"
                >
                  {copied ? <Check size={15} /> : <Copy size={15} />}
                  {copied ? "Copied" : "Copy"}
                </button>
              </div>
              <p className="field-help">
                Give this ID to your other computer, and paste that computer’s
                ID below. Both sides have to add each other.
              </p>
            </section>

            <section className="import-section">
              <h2>Add a computer</h2>
              <div className="setup-form">
                <input
                  className="path-input"
                  value={deviceId}
                  onChange={(event) => setDeviceId(event.target.value)}
                  placeholder="AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH"
                  aria-label="The other computer’s ID"
                  spellCheck={false}
                />
                <input
                  className="path-input"
                  value={deviceName}
                  onChange={(event) => setDeviceName(event.target.value)}
                  placeholder="What to call it (optional)"
                  aria-label="A name for the other computer"
                />
                <button
                  className="button primary"
                  onClick={() => void pair()}
                  disabled={busy === "pair" || deviceId.trim().length === 0}
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
                            ? `connected${device.address ? ` (${device.address})` : ""}`
                            : "not connected right now"}
                        </span>
                      </div>
                      <span
                        className={`sync-dot${device.connected ? " on" : ""}`}
                        aria-hidden="true"
                      />
                    </li>
                  ))}
                </ul>
              )}
            </section>

            {status.offers.length > 0 && (
              <section className="import-section">
                <h2>Offered to you</h2>
                <p className="field-help">
                  Another computer wants to share these. Nothing arrives until
                  you take one up.
                </p>
                <ul className="import-list">
                  {status.offers.map((offer) => (
                    <li key={`${offer.id}-${offer.offeredBy}`}>
                      <Inbox size={19} strokeWidth={1.6} />
                      <div>
                        <strong>{offer.label}</strong>
                        <span className="muted">
                          from {offer.offeredByName}
                        </span>
                      </div>
                      <button
                        className="button primary"
                        onClick={() => void accept(offer.id, offer.label)}
                        disabled={busy === offer.id}
                      >
                        {busy === offer.id ? "Accepting…" : "Keep this here"}
                      </button>
                    </li>
                  ))}
                </ul>
              </section>
            )}

            <section className="import-section">
              <h2>Shared folders</h2>
              <div className="setup-form">
                <input
                  className="path-input"
                  value={folderPath}
                  onChange={(event) => setFolderPath(event.target.value)}
                  placeholder="A folder inside your Uncloud folder, such as Files/Dropbox"
                  aria-label="Folder to share"
                  spellCheck={false}
                />
                <button
                  className="button primary"
                  onClick={() => void share()}
                  disabled={
                    busy === "share" ||
                    folderPath.trim().length === 0 ||
                    status.devices.length === 0
                  }
                >
                  <FolderSync size={15} />
                  {busy === "share" ? "Sharing…" : "Share folder"}
                </button>
              </div>
              <p className="field-help">
                Share a folder inside your Uncloud folder, not the whole thing —
                Uncloud keeps its own index in there, and copying that between
                computers would break it. The other computer has to take the
                folder up before anything moves.
              </p>
              {status.folders.length === 0 ? (
                <p className="field-help">Nothing shared yet.</p>
              ) : (
                <ul className="import-list">
                  {status.folders.map((folder) => (
                    <li key={folder.id}>
                      <FolderSync size={19} strokeWidth={1.6} />
                      <div>
                        <strong>{folder.label}</strong>
                        <span className="muted">
                          {folder.state ?? "starting"} · {folder.files} file
                          {folder.files === 1 ? "" : "s"} ·{" "}
                          {formatSize(folder.bytes)} · shared with{" "}
                          {folder.deviceIds.length - 1} computer
                          {folder.deviceIds.length - 1 === 1 ? "" : "s"}
                        </span>
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </>
        )
      )}
      {error && !status && (
        <div className="empty-state connection-error" role="alert">
          <TriangleAlert size={32} />
          <h1>Couldn’t load nodes</h1>
          <p>{error}</p>
        </div>
      )}
    </>
  );
}
