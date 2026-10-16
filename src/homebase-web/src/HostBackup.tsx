import { useCallback, useEffect, useRef, useState } from "react";
import {
  Check,
  CircleCheck,
  CloudUpload,
  Copy,
  LoaderCircle,
  ShieldCheck,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { BackupAccount, BackupCheck, BackupJoined, BackupSnapshots, BackupStatus } from "./api";

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
 * The join under way, and the password it brought until whoever joined says they've kept it. Kept
 * here rather than in the panel, which goes whenever Settings is closed: the password is sent once,
 * so a panel closed mid-join, or before Done, would otherwise take it with it. Reopening Settings
 * picks both up again.
 */
const unsaved: { joining: Promise<BackupJoined> | null; password: string | null } = {
  joining: null,
  password: null,
};

/** Only for the tests, which start each one afresh. */
export function forgetUnsaved() {
  unsaved.joining = null;
  unsaved.password = null;
}

/** Asks before the page is left or reloaded while a password is still to be kept. */
function warnBeforeLeaving(event: BeforeUnloadEvent) {
  if (!unsaved.password) return;
  event.preventDefault();
  event.returnValue = "";
}

/**
 * Uncloud Backup: an encrypted copy of everyone's files here, and of this Uncloud itself, kept
 * offsite. Whoever looks after Uncloud joins with an invite, keeps the password the backup is
 * encrypted with, and from then on sees how the backups are going.
 */
export default function HostBackup() {
  const [account, setAccount] = useState<BackupAccount | null>(null);
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [check, setCheck] = useState<BackupCheck | null>(null);
  const [keptBackups, setKeptBackups] = useState<BackupSnapshots["snapshots"] | null>(null);
  const [invite, setInvite] = useState("");
  // The backup's password, from the moment of joining until whoever joined says they've kept it.
  const [password, setPassword] = useState<string | null>(unsaved.password);
  const [kept, setKept] = useState(false);
  const [copied, setCopied] = useState<"copied" | "select" | null>(null);
  const [busy, setBusy] = useState(unsaved.joining !== null);
  const [error, setError] = useState("");
  const code = useRef<HTMLElement>(null);

  const read = useCallback(async (signal?: AbortSignal) => {
    const [nextAccount, nextStatus] = await Promise.all([
      api<BackupAccount>("/host/backup/account", { signal }),
      api<BackupStatus>("/host/backup", { signal }),
    ]);
    setAccount(nextAccount);
    setStatus(nextStatus);
    // A host from before checks were made answers 404: the rest of the panel still stands.
    setCheck(await api<BackupCheck>("/host/backup/check", { signal }).catch(() => null));
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    read(controller.signal).catch(() => {
      // The rest of the settings are still worth showing without this.
    });
    return () => controller.abort();
  }, [read]);

  // A join started before Settings was last closed: its answer is shown here once it comes.
  useEffect(() => {
    if (unsaved.joining) void finishJoining(unsaved.joining);
    // Only on opening: a join started from here is finished by join() itself.
  }, []);

  useEffect(() => {
    window.addEventListener("beforeunload", warnBeforeLeaving);
    return () => window.removeEventListener("beforeunload", warnBeforeLeaving);
  }, []);

  // Which backups are kept, read again whenever a new one has been made.
  const configured = status?.configured === true;
  const lastMade = status?.lastSucceeded ?? null;
  useEffect(() => {
    if (!configured) return;
    const controller = new AbortController();
    api<BackupSnapshots>("/host/backup/snapshots", { signal: controller.signal })
      .then((listed) => setKeptBackups(listed.snapshots))
      .catch(() => {
        // Worth showing, not worth anything else failing for.
      });
    return () => controller.abort();
  }, [configured, lastMade]);

  const underWay =
    (account?.joined === true && account.storage !== "ready") ||
    status?.running === true ||
    check?.running === true;
  useEffect(() => {
    if (!underWay) return;
    const timer = window.setInterval(() => void read().catch(() => {}), UNDER_WAY_EVERY);
    return () => window.clearInterval(timer);
  }, [underWay, read]);

  if (!account || !status) return null;

  function join() {
    unsaved.joining = api<BackupJoined>("/host/backup/account", {
      method: "POST",
      body: JSON.stringify({ invite: invite.trim() }),
    });
    void finishJoining(unsaved.joining);
  }

  async function finishJoining(joining: Promise<BackupJoined>) {
    setBusy(true);
    setError("");
    try {
      const joined = await joining;
      unsaved.password = joined.password;
      setPassword(joined.password);
      setKept(false);
      setAccount(joined.account);
      setStatus(await api<BackupStatus>("/host/backup"));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t join Uncloud Backup.");
    } finally {
      if (unsaved.joining === joining) unsaved.joining = null;
      setBusy(false);
    }
  }

  function done() {
    unsaved.password = null;
    setPassword(null);
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

  async function checkNow() {
    setBusy(true);
    setError("");
    try {
      setCheck(await api<BackupCheck>("/host/backup/check", { method: "POST" }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t start a check.");
    } finally {
      setBusy(false);
    }
  }

  // Said to be copied only once it is. Where the browser won't let a page copy (over plain HTTP
  // from another computer, say), the password is selected instead, for copying by hand.
  async function copy() {
    if (!password) return;
    try {
      if (!navigator.clipboard) throw new Error("No clipboard here.");
      await navigator.clipboard.writeText(password);
      setCopied("copied");
      window.setTimeout(() => setCopied(null), 2000);
    } catch {
      if (code.current) window.getSelection()?.selectAllChildren(code.current);
      setCopied("select");
    }
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
          <code ref={code} aria-label="Your backup’s password">
            {password}
          </code>
          <button type="button" className="button secondary" onClick={() => void copy()}>
            {copied === "copied" ? <Check size={16} /> : <Copy size={16} />}
            {copied === "copied" ? "Copied" : "Copy"}
          </button>
        </div>
        {copied === "select" && (
          <p className="field-help" role="status">
            This browser didn’t let Uncloud copy it. It’s selected: copy it with ⌘C or Ctrl+C.
          </p>
        )}
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
          onClick={done}
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
            Joining Uncloud Backup…
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
          onClick={join}
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
            {keptBackups?.length === 1 && `1 backup kept, from ${when.format(new Date(keptBackups[0].time))}. `}
            {keptBackups && keptBackups.length > 1 &&
              `${keptBackups.length} backups kept, the oldest from ${when.format(new Date(keptBackups[0].time))}. `}
            Every night, and soon after this computer is back if it missed one. Encrypted here before it
            leaves.
          </p>
          {check && <LastCheck check={check} />}
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <div className="backup-actions">
            <button
              type="button"
              className="button secondary"
              disabled={busy || status.running}
              onClick={() => void backUpNow()}
            >
              {status.running ? <LoaderCircle size={16} className="spin" /> : <CloudUpload size={16} />}
              Back up now
            </button>
            {check && (
              <button
                type="button"
                className="button secondary"
                disabled={busy || check.running || !status.lastSucceeded}
                onClick={() => void checkNow()}
              >
                {check.running ? <LoaderCircle size={16} className="spin" /> : <ShieldCheck size={16} />}
                Verify the backup
              </button>
            )}
          </div>
        </>
      )}
    </section>
  );
}

const count = (n: number | null, one: string, many: string) =>
  `${(n ?? 0).toLocaleString()} ${n === 1 ? one : many}`;

/**
 * How the last check went: what was restored, from which backup, and whether it came back as it is
 * here. Files changed or gone since the backup aren't a fault, only nothing to compare with.
 */
function LastCheck({ check }: { check: BackupCheck }) {
  if (check.running) {
    return (
      <p className="backup-line" role="status">
        <LoaderCircle size={14} className="spin" />
        <span>
          Verifying the backup: restoring the newest one to a temporary folder, comparing it with
          what’s here, then deleting it. Nothing here is changed.
        </span>
      </p>
    );
  }
  if (!check.finished) return null;
  const finished = when.format(new Date(check.finished));
  if (check.passed === null) {
    return (
      <p className="backup-line">
        <span>{check.message}</span>
      </p>
    );
  }
  const from = check.snapshotTime ? ` from the backup of ${when.format(new Date(check.snapshotTime))}` : "";
  if (check.passed) {
    // Everybody's files, which is fewer than the backup's own count: that has this Uncloud's
    // database and key in it too.
    const what = check.sampled
      ? `${(check.files ?? 0).toLocaleString()} of everyone’s ${count(check.filesInBackup, "file", "files")} (${formatSize(check.bytes)}), at random`
      : check.files === 1
        ? `everyone’s 1 file (${formatSize(check.bytes)})`
        : `all ${(check.files ?? 0).toLocaleString()} of everyone’s files (${formatSize(check.bytes)})`;
    const since = [
      check.changedSince ? `${count(check.changedSince, "has", "have")} changed here since` : null,
      check.goneSince ? `${count(check.goneSince, "has", "have")} gone` : null,
    ].filter(Boolean);
    return (
      <p className="backup-line">
        <ShieldCheck size={14} className="backup-ok" />
        <span>
          Verified {finished}: restored {what}
          {from}, and {since.length ? "every one still here as it was" : "every one"} came back exactly
          as it is here{since.length ? ` (${since.join(" and ")})` : ""}.
          {check.hostKeyMatched ? " So did this Uncloud’s own key, which a rebuilt host needs." : ""}
        </span>
      </p>
    );
  }
  const wrong = [
    check.mismatched ? `${count(check.mismatched, "file", "files")} came back different from what’s here` : null,
    check.missing ? `${count(check.missing, "file", "files")} didn’t come back at all` : null,
  ].filter(Boolean);
  return (
    <p className="backup-line">
      <TriangleAlert size={14} className="backup-warn" />
      <span>
        Verified {finished}
        {from}:{" "}
        {wrong.length ? `${wrong.join(", and ")}. Uncloud’s log says which.` : ""}
        {check.message ? ` ${check.message}` : ""}
      </span>
    </p>
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
    // Backed up, but older backups weren't tidied away: worth knowing, since what isn't given back
    // goes on taking room.
    return (
      <p className="backup-line">
        {status.message ? (
          <TriangleAlert size={14} className="backup-warn" />
        ) : (
          <CircleCheck size={14} className="backup-ok" />
        )}
        <span>
          Last backed up {finished}.{details && ` ${details}.`}
          {status.message && ` ${status.message}`}
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
