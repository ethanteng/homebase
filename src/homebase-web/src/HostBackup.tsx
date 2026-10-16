import { useCallback, useEffect, useState } from "react";
import {
  Check,
  CircleCheck,
  CloudUpload,
  Copy,
  LoaderCircle,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { BackupAccount, BackupJoined, BackupStatus } from "./api";

/** How often it looks again while something is under way: storage being set up, or a backup running. */
const UNDER_WAY_EVERY = 5000;

/** The parts of setting a household up, as the service names them, in words. */
const STEPS: Record<string, string> = {
  account: "making its account",
  storage: "giving it storage",
  bucket: "making somewhere to keep the backup",
  "host key": "making this Uncloud’s key",
};

const when = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

/**
 * Uncloud Backup: an encrypted copy of everyone's files here, and of this Uncloud itself, kept
 * offsite. Whoever looks after Uncloud joins with an invite, keeps the password the backup is
 * encrypted with, and from then on sees how the backups are going.
 */
export default function HostBackup() {
  const [account, setAccount] = useState<BackupAccount | null>(null);
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [invite, setInvite] = useState("");
  // The backup's password, from the moment of joining until whoever joined says they've kept it.
  // It's sent once and kept nowhere here, so closing Settings before then loses it from view.
  const [password, setPassword] = useState<string | null>(null);
  const [kept, setKept] = useState(false);
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const read = useCallback(async (signal?: AbortSignal) => {
    const [nextAccount, nextStatus] = await Promise.all([
      api<BackupAccount>("/host/backup/account", { signal }),
      api<BackupStatus>("/host/backup", { signal }),
    ]);
    setAccount(nextAccount);
    setStatus(nextStatus);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    read(controller.signal).catch(() => {
      // The rest of the settings are still worth showing without this.
    });
    return () => controller.abort();
  }, [read]);

  const underWay =
    (account?.joined === true && account.storage !== "ready") || status?.running === true;
  useEffect(() => {
    if (!underWay) return;
    const timer = window.setInterval(() => void read().catch(() => {}), UNDER_WAY_EVERY);
    return () => window.clearInterval(timer);
  }, [underWay, read]);

  if (!account || !status) return null;

  async function join() {
    setBusy(true);
    setError("");
    try {
      const joined = await api<BackupJoined>("/host/backup/account", {
        method: "POST",
        body: JSON.stringify({ invite: invite.trim() }),
      });
      setPassword(joined.password);
      setKept(false);
      setAccount(joined.account);
      setStatus(await api<BackupStatus>("/host/backup"));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t join Uncloud Backup.");
    } finally {
      setBusy(false);
    }
  }

  async function backUpNow() {
    setBusy(true);
    setError("");
    try {
      setStatus(await api<BackupStatus>("/host/backup", { method: "POST" }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t start a backup.");
    } finally {
      setBusy(false);
    }
  }

  async function copy() {
    if (!password) return;
    await navigator.clipboard?.writeText(password);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 2000);
  }

  const heading = (
    <div className="import-section-head">
      <h3>
        <CloudUpload size={16} />
        Backing up offsite
      </h3>
    </div>
  );

  if (password) {
    return (
      <section className="import-section host-backup">
        {heading}
        <p className="field-help">
          <strong>This is your backup’s password.</strong> Everything is encrypted with it on this
          computer before it leaves, so nobody else can read your backup: not iDrive, not Uncloud.
          It also means nobody can open the backup without it if this computer is lost or broken.
        </p>
        <div className="pairing-code backup-password">
          <code aria-label="Your backup’s password">{password}</code>
          <button type="button" className="button secondary" onClick={() => void copy()}>
            {copied ? <Check size={16} /> : <Copy size={16} />}
            {copied ? "Copied" : "Copy"}
          </button>
        </div>
        <p className="field-help warning-text">
          Keep it somewhere safe away from this computer, like a password manager, or printed and put
          away. This is the only time Uncloud shows it.
        </p>
        <label className="backup-kept">
          <input type="checkbox" checked={kept} onChange={(event) => setKept(event.target.checked)} />
          I’ve kept it somewhere safe
        </label>
        <button
          type="button"
          className="button primary"
          disabled={!kept}
          onClick={() => setPassword(null)}
        >
          <Check size={16} />
          Done
        </button>
      </section>
    );
  }

  if (!account.joined && !status.configured) {
    return (
      <section className="import-section host-backup">
        {heading}
        <p className="field-help">
          Uncloud Backup keeps an encrypted copy of everyone’s files here, and of this Uncloud
          itself, offsite every night. It’s in a trial for now, so joining takes an invite code.
        </p>
        <label htmlFor="backup-invite">Invite code</label>
        <input
          id="backup-invite"
          className="path-input"
          value={invite}
          onChange={(event) => setInvite(event.target.value)}
          placeholder="K7QM-3XPD-9TWA-HF2R"
          autoComplete="off"
          spellCheck={false}
          disabled={busy}
        />
        {busy && (
          <p className="field-help" role="status">
            Joining, and setting up this household’s storage. That can take up to a minute.
          </p>
        )}
        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        <button
          type="button"
          className="button primary"
          disabled={busy || !invite.trim()}
          onClick={() => void join()}
        >
          {busy ? <LoaderCircle size={16} className="spin" /> : <Check size={16} />}
          Join
        </button>
      </section>
    );
  }

  return (
    <section className="import-section host-backup">
      {heading}
      {account.joined && account.storage !== "ready" ? (
        account.storage === "failing" ? (
          <p className="backup-line">
            <TriangleAlert size={14} className="backup-warn" />
            <span>
              Uncloud Backup is having trouble setting up this household’s storage
              {account.step && STEPS[account.step] ? ` (${STEPS[account.step]})` : ""}. It keeps
              trying, and there’s nothing to do here.
            </span>
          </p>
        ) : (
          <p className="backup-line" role="status">
            <LoaderCircle size={14} className="spin" />
            <span>
              Setting up this household’s storage
              {account.step && STEPS[account.step] ? `: ${STEPS[account.step]}` : ""}. It usually
              takes under a minute.
            </span>
          </p>
        )
      ) : (
        <>
          <LastBackup status={status} />
          <p className="field-help">
            Every night, and soon after this computer is back if it missed one. Encrypted here before
            it leaves.
          </p>
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <button
            type="button"
            className="button secondary"
            disabled={busy || status.running}
            onClick={() => void backUpNow()}
          >
            {status.running ? <LoaderCircle size={16} className="spin" /> : <CloudUpload size={16} />}
            Back up now
          </button>
        </>
      )}
    </section>
  );
}

/** How the last backup went, and the last one that worked if that wasn't it. */
function LastBackup({ status }: { status: BackupStatus }) {
  if (status.running) {
    return (
      <p className="backup-line" role="status">
        <LoaderCircle size={14} className="spin" />
        <span>
          Backing up now. A first backup can take hours; if it’s stopped, the next one carries on
          from where it got to.
        </span>
      </p>
    );
  }
  if (!status.lastOutcome) {
    return (
      <p className="backup-line">
        <span>Not backed up yet. The first backup starts on its own within a few minutes.</span>
      </p>
    );
  }
  const finished = status.lastFinished ? when.format(new Date(status.lastFinished)) : null;
  const whole =
    status.totalFiles != null && status.totalBytes != null
      ? `${status.totalFiles.toLocaleString()} ${status.totalFiles === 1 ? "file" : "files"}, ${formatSize(status.totalBytes)}`
      : null;
  const added = status.bytesAdded != null ? `${formatSize(status.bytesAdded)} new` : null;
  const details = [whole, added].filter(Boolean).join(" · ");

  if (status.lastOutcome === "Succeeded") {
    return (
      <p className="backup-line">
        <CircleCheck size={14} className="backup-ok" />
        <span>
          Last backed up {finished}.{details && ` ${details}.`}
        </span>
      </p>
    );
  }
  const lastGood =
    status.lastSucceeded && status.lastOutcome !== "Partial"
      ? ` The last one that worked was ${when.format(new Date(status.lastSucceeded))}.`
      : "";
  return (
    <p className="backup-line">
      <TriangleAlert size={14} className="backup-warn" />
      <span>
        {status.lastOutcome === "Partial"
          ? `Last backed up ${finished}, but not everything: ${status.message ?? ""}`
          : `The last backup, ${finished}, didn’t work. ${status.message ?? ""}${lastGood}`}
      </span>
    </p>
  );
}
