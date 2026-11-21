import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArchiveRestore,
  Check,
  CircleCheck,
  CloudUpload,
  Copy,
  Download,
  KeyRound,
  LoaderCircle,
  Pause,
  ShieldCheck,
  TriangleAlert,
} from "lucide-react";
import { api, formatSize, SignedOutError } from "./api";
import { SettingsGroup } from "./Settings";
import type {
  BackupAccount,
  BackupCheck,
  BackupJoined,
  BackupKit,
  BackupRebuild,
  BackupSnapshots,
  BackupStatus,
} from "./api";

/** How often it looks again while something is under way: storage being set up, or a backup running. */
const UNDER_WAY_EVERY = 5000;
/** How often it looks again while this Uncloud is being rebuilt, and how long it waits for one starting again. */
const REBUILD_EVERY = 2000;
const SLOW_RESTART = 90_000;

/** Starts the page again, at signing in once this Uncloud no longer knows the session. */
export const page = { startOver: () => window.location.reload() };

/** The parts of setting a household up, as the service names them, in words. */
const STEPS: Record<string, string> = {
  account: "making its account",
  storage: "giving it storage",
  bucket: "making somewhere to keep the backup",
  "host key": "making this Uncloud’s key",
};

const when = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

/**
 * A recovery kit being shown: always the backup's password, and the recovery token unless joining
 * couldn't get one yet.
 */
interface ShownKit {
  password: string;
  recovery: string | null;
  household: string | null;
  madeAt: string | null;
}

/**
 * The join under way, and the kit it brought until whoever joined says they've kept it. Kept here
 * rather than in the panel, which goes whenever Settings is closed: a kit is sent once, so a panel
 * closed mid-join, or before Done, would otherwise take it with it. Reopening Settings picks both
 * up again.
 */
const unsaved: { joining: Promise<BackupJoined> | null; kit: ShownKit | null } = {
  joining: null,
  kit: null,
};

/** Only for the tests, which start each one afresh. */
export function forgetUnsaved() {
  unsaved.joining = null;
  unsaved.kit = null;
}

/** Asks before the page is left or reloaded while a kit is still to be kept. */
function warnBeforeLeaving(event: BeforeUnloadEvent) {
  if (!unsaved.kit) return;
  event.preventDefault();
  event.returnValue = "";
}

const kitOf = (kit: BackupKit): ShownKit => ({ ...kit });

/** The kit as a file to keep: what's in it, and what to do with it on the day it's needed. */
function kitText(kit: ShownKit) {
  return [
    "Uncloud Backup recovery kit",
    kit.madeAt ? `Made ${when.format(new Date(kit.madeAt))}${kit.household ? ` for household ${kit.household}` : ""}` : "",
    "",
    `Backup password: ${kit.password}`,
    kit.recovery ? `Recovery token:  ${kit.recovery}` : "Recovery token:  (not made yet: make a recovery kit in Settings)",
    "",
    "Keep this somewhere safe, away from the computer Uncloud runs on.",
    "If that computer is lost, set Uncloud up on another, then in Settings,",
    "under Offsite backup, choose “Use a recovery kit” and type both in.",
    "Making a new kit replaces this one, and this one stops working.",
    "",
  ].join("\n");
}

/**
 * Uncloud Backup: an encrypted copy of everyone's files here, and of this Uncloud itself, kept
 * offsite. Whoever looks after Uncloud joins with an invite, keeps the password the backup is
 * encrypted with, and from then on sees how the backups are going.
 */
interface Props {
  /** When this Uncloud comes to have backups, so My files can offer them. */
  onChanged?: () => void;
}

export default function HostBackup({ onChanged }: Props = {}) {
  const [account, setAccount] = useState<BackupAccount | null>(null);
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [check, setCheck] = useState<BackupCheck | null>(null);
  const [keptBackups, setKeptBackups] = useState<BackupSnapshots["snapshots"] | null>(null);
  const [invite, setInvite] = useState("");
  // The recovery kit, from the moment it's made until whoever made it says they've kept it.
  const [kit, setKit] = useState<ShownKit | null>(unsaved.kit);
  const [kept, setKept] = useState(false);
  const [copied, setCopied] = useState<{ what: "password" | "recovery"; how: "copied" | "select" } | null>(null);
  const [busy, setBusy] = useState(unsaved.joining !== null);
  const [error, setError] = useState("");
  // Taking a lost Uncloud's place with its kit, instead of joining with an invite.
  const [recovering, setRecovering] = useState(false);
  const [recovery, setRecovery] = useState("");
  const [kitPassword, setKitPassword] = useState("");
  // Rebuilding this Uncloud from the backup, and whether it has been gone a while starting again.
  const [rebuild, setRebuild] = useState<BackupRebuild | null>(null);
  const [slow, setSlow] = useState(false);
  // A look that started before Rebuild was clicked must not paint over the rebuild it started.
  const rebuildSeen = useRef(0);
  const passwordCode = useRef<HTMLElement>(null);
  const recoveryCode = useRef<HTMLElement>(null);

  const read = useCallback(async (signal?: AbortSignal) => {
    const [nextAccount, nextStatus] = await Promise.all([
      api<BackupAccount>("/host/backup/account", { signal }),
      api<BackupStatus>("/host/backup", { signal }),
    ]);
    setAccount(nextAccount);
    setStatus(nextStatus);
    // A host from before checks, or rebuilds, were made answers 404: the rest of the panel still stands.
    setCheck(await api<BackupCheck>("/host/backup/check", { signal }).catch(() => null));
    const seen = rebuildSeen.current;
    const nextRebuild = await api<BackupRebuild>("/host/backup/rebuild", { signal }).catch(() => null);
    if (seen === rebuildSeen.current) setRebuild(nextRebuild);
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

  // Said once, when backups go from not set up to set up while this is open: not on every look,
  // and not again for each new callback a parent hands down.
  const changed = useRef(onChanged);
  changed.current = onChanged;
  const known = status ? status.configured : null;
  const wasConfigured = useRef<boolean | null>(null);
  useEffect(() => {
    if (known === null) return;
    if (wasConfigured.current === false && known) changed.current?.();
    wasConfigured.current = known;
  }, [known]);

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

  // Followed until Uncloud starts again as the host in the backup. Once it has said it's about to,
  // it goes quiet while it stops and starts; when it answers again it doesn't know this session,
  // which was the old host's, and the page starts again too, at signing in.
  const rebuilding = rebuild?.running === true;
  const stage = useRef<BackupRebuild["stage"]>(null);
  stage.current = rebuild?.stage ?? null;
  useEffect(() => {
    if (!rebuilding) return;
    let gone: number | null = null;
    const timer = window.setInterval(() => {
      api<BackupRebuild>("/host/backup/rebuild")
        .then((now) => {
          gone = null;
          setSlow(false);
          setRebuild(now);
          // Stopped short: the rest of the panel is as it was, and worth reading again.
          if (!now.running) void read().catch(() => {});
        })
        .catch((failure: unknown) => {
          if (failure instanceof SignedOutError) {
            page.startOver();
            return;
          }
          // Out of reach before then says nothing about the rebuild, which goes on regardless:
          // what it last said stands.
          if (stage.current !== "restarting") return;
          gone ??= Date.now();
          if (Date.now() - gone > SLOW_RESTART) setSlow(true);
        });
    }, REBUILD_EVERY);
    return () => window.clearInterval(timer);
  }, [rebuilding, read]);

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
      const shown = joined.kit ? kitOf(joined.kit) : { password: joined.password, recovery: null, household: null, madeAt: null };
      unsaved.kit = shown;
      setKit(shown);
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
    unsaved.kit = null;
    setKit(null);
  }

  async function makeKit() {
    if (
      account?.recoveryKitMadeAt &&
      !window.confirm(
        `Make a new recovery kit? The one made ${when.format(new Date(account.recoveryKitMadeAt))} stops working.`,
      )
    )
      return;
    setBusy(true);
    setError("");
    try {
      const made = await api<{ kit: BackupKit; account: BackupAccount }>("/host/backup/kit", { method: "POST" });
      unsaved.kit = kitOf(made.kit);
      setKit(unsaved.kit);
      setKept(false);
      setAccount(made.account);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t make a recovery kit.");
    } finally {
      setBusy(false);
    }
  }

  async function recover() {
    setBusy(true);
    setError("");
    try {
      const recovered = await api<{ account: BackupAccount }>("/host/backup/recover", {
        method: "POST",
        body: JSON.stringify({ recovery: recovery.trim(), password: kitPassword.trim() }),
      });
      setAccount(recovered.account);
      setKitPassword("");
      setRecovery("");
      setStatus(await api<BackupStatus>("/host/backup"));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t open the household’s backup.");
    } finally {
      setBusy(false);
    }
  }

  async function rebuildNow() {
    const newest = keptBackups?.at(-1);
    if (!newest) return;
    if (
      !window.confirm(
        `Rebuild this Uncloud from the backup of ${when.format(new Date(newest.time))}?\n\n` +
          "Everyone’s accounts and files come back as they were then, in place of what’s here now. " +
          "The account you’re signed in with goes: sign in again with your account from before. " +
          "Computers that synced with the lost Uncloud need to be paired again.",
      )
    )
      return;
    setBusy(true);
    setError("");
    setSlow(false);
    const seen = ++rebuildSeen.current;
    try {
      const started = await api<BackupRebuild>("/host/backup/rebuild", {
        method: "POST",
        body: JSON.stringify({ snapshot: newest.id, confirm: true }),
      });
      if (seen === rebuildSeen.current) setRebuild(started);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t start rebuilding this Uncloud.");
    } finally {
      setBusy(false);
    }
  }

  async function resume() {
    setBusy(true);
    setError("");
    try {
      setAccount(await api<BackupAccount>("/host/backup/resume", { method: "POST" }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t resume backing up.");
    } finally {
      setBusy(false);
    }
  }

  function download(shown: ShownKit) {
    const link = document.createElement("a");
    link.href = URL.createObjectURL(new Blob([kitText(shown)], { type: "text/plain" }));
    link.download = "Uncloud recovery kit.txt";
    link.click();
    URL.revokeObjectURL(link.href);
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
  // from another computer, say), what was asked for is selected instead, for copying by hand.
  async function copy(what: "password" | "recovery", text: string) {
    try {
      if (!navigator.clipboard) throw new Error("No clipboard here.");
      await navigator.clipboard.writeText(text);
      setCopied({ what, how: "copied" });
      window.setTimeout(() => setCopied(null), 2000);
    } catch {
      const element = (what === "password" ? passwordCode : recoveryCode).current;
      if (element) window.getSelection()?.selectAllChildren(element);
      setCopied({ what, how: "select" });
    }
  }

  if (kit) {
    const copyButton = (what: "password" | "recovery", text: string) => (
      <button type="button" className="button secondary" onClick={() => void copy(what, text)}>
        {copied?.what === what && copied.how === "copied" ? <Check size={16} /> : <Copy size={16} />}
        {copied?.what === what && copied.how === "copied" ? "Copied" : "Copy"}
      </button>
    );
    return (
      <SettingsGroup title="Offsite backup">
        <div className="backup-body">
        <p className="field-help">
          <strong>Save your recovery kit.</strong> Your backup is encrypted on this computer, so nobody
          else can read it. If this computer is lost, the kit is the only way to get everything back.
        </p>
        <span className="backup-kit-label">Backup password</span>
        <div className="pairing-code backup-password">
          <code ref={passwordCode} aria-label="Your backup’s password">
            {kit.password}
          </code>
          {copyButton("password", kit.password)}
        </div>
        {kit.recovery ? (
          <>
            <span className="backup-kit-label">Recovery token</span>
            <div className="pairing-code backup-password">
              <code ref={recoveryCode} aria-label="Your recovery token">
                {kit.recovery}
              </code>
              {copyButton("recovery", kit.recovery)}
            </div>
          </>
        ) : (
          <p className="field-help">
            The recovery token couldn’t be made just now. Make a recovery kit here once storage is
            ready.
          </p>
        )}
        {copied?.how === "select" && (
          <p className="field-help" role="status">
            Copying was blocked, so it’s selected instead. Press ⌘C or Ctrl+C.
          </p>
        )}
        <button type="button" className="button secondary" onClick={() => download(kit)}>
          <Download size={16} />
          Download the kit
        </button>
        <p className="field-help warning-text">
          Keep it somewhere safe, away from this computer. This is the only time it’s shown.
        </p>
        <label className="backup-kept">
          <input type="checkbox" checked={kept} onChange={(event) => setKept(event.target.checked)} />
          I’ve kept it somewhere safe
        </label>
        <button type="button" className="button primary" disabled={!kept} onClick={done}>
          <Check size={16} />
          Done
        </button>
        </div>
      </SettingsGroup>
    );
  }

  if (!account.joined && !status.configured && recovering) {
    return (
      <SettingsGroup title="Offsite backup">
        <div className="backup-body">
        <p className="field-help">
          Replacing a lost Uncloud? Enter its recovery kit. The lost one stops working.
        </p>
        <label htmlFor="backup-recovery">Recovery token</label>
        <input
          id="backup-recovery"
          className="path-input"
          value={recovery}
          onChange={(event) => setRecovery(event.target.value)}
          placeholder="K7QMX-3XPD9-TWAHF-2RBCD-EFGHJ-KLMNP"
          autoComplete="off"
          spellCheck={false}
          disabled={busy}
        />
        <label htmlFor="backup-kit-password">Backup password</label>
        <input
          id="backup-kit-password"
          className="path-input"
          value={kitPassword}
          onChange={(event) => setKitPassword(event.target.value)}
          autoComplete="off"
          spellCheck={false}
          disabled={busy}
        />
        {busy && (
          <p className="field-help" role="status">
            Opening the backup…
          </p>
        )}
        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        <div className="backup-actions">
          <button
            type="button"
            className="button primary"
            disabled={busy || !recovery.trim() || !kitPassword.trim()}
            onClick={() => void recover()}
          >
            {busy ? <LoaderCircle size={16} className="spin" /> : <KeyRound size={16} />}
            Open the backup
          </button>
          <button
            type="button"
            className="button secondary"
            disabled={busy}
            onClick={() => {
              setRecovering(false);
              setError("");
            }}
          >
            Use an invite code instead
          </button>
        </div>
        </div>
      </SettingsGroup>
    );
  }

  if (!account.joined && !status.configured) {
    return (
      <SettingsGroup title="Offsite backup">
        <div className="backup-body">
        <p className="field-help">
          Keeps an encrypted copy of everyone’s files offsite, every night. During the trial, joining
          needs an invite code.
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
            Joining…
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
        <p className="field-help">
          Replacing an Uncloud that’s been lost?{" "}
          <button
            type="button"
            className="link-button"
            disabled={busy}
            onClick={() => {
              setRecovering(true);
              setError("");
            }}
          >
            Use a recovery kit
          </button>
        </p>
        </div>
      </SettingsGroup>
    );
  }

  return (
    <SettingsGroup title="Offsite backup">
      <div className="backup-body">
      {account.joined && account.storage !== "ready" ? (
        account.storage === "failing" ? (
          <p className="backup-line">
            <TriangleAlert size={14} className="backup-warn" />
            <span>
              Backup storage is having trouble getting ready
              {account.step && STEPS[account.step] ? ` (${STEPS[account.step]})` : ""}. It keeps
              trying; there’s nothing you need to do.
            </span>
          </p>
        ) : (
          <p className="backup-line" role="status">
            <LoaderCircle size={14} className="spin" />
            <span>
              Getting backup storage ready
              {account.step && STEPS[account.step] ? `: ${STEPS[account.step]}` : ""}. Usually under a
              minute.
            </span>
          </p>
        )
      ) : (
        <>
          {rebuild && <Rebuilding rebuild={rebuild} slow={slow} />}
          {account.paused && !rebuilding && (
            <p className="backup-line">
              <Pause size={14} className="backup-warn" />
              <span>
                This Uncloud opened an existing backup and isn’t adding to it yet. Rebuild everything
                from it, or restore what you need from Backups and then resume.
              </span>
            </p>
          )}
          <LastBackup status={status} />
          <p className="field-help">
            {keptBackups?.length === 1 && `1 backup kept, from ${when.format(new Date(keptBackups[0].time))}. `}
            {keptBackups && keptBackups.length > 1 &&
              `${keptBackups.length} backups kept, the oldest from ${when.format(new Date(keptBackups[0].time))}. `}
            Backs up every night, encrypted before it leaves.
          </p>
          {check && <LastCheck check={check} />}
          {account.joined &&
            (account.recoveryKitMadeAt ? (
              <p className="backup-line">
                <KeyRound size={14} />
                <span>Recovery kit made {when.format(new Date(account.recoveryKitMadeAt))}.</span>
              </p>
            ) : (
              <p className="backup-line">
                <TriangleAlert size={14} className="backup-warn" />
                <span>
                  No recovery kit yet. Without one, the backup can’t be recovered if this computer is
                  lost.
                </span>
              </p>
            ))}
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <div className="backup-actions">
            {account.paused && (keptBackups?.length ?? 0) > 0 && (
              <button
                type="button"
                className="button primary"
                disabled={busy || rebuilding}
                onClick={() => void rebuildNow()}
              >
                {rebuilding ? <LoaderCircle size={16} className="spin" /> : <ArchiveRestore size={16} />}
                Rebuild from the backup
              </button>
            )}
            {account.paused && (
              <button
                type="button"
                className="button secondary"
                disabled={busy || rebuilding}
                onClick={() => void resume()}
              >
                <CloudUpload size={16} />
                Resume backing up
              </button>
            )}
            <button
              type="button"
              className="button secondary"
              disabled={busy || rebuilding || status.running || account.paused}
              onClick={() => void backUpNow()}
            >
              {status.running ? <LoaderCircle size={16} className="spin" /> : <CloudUpload size={16} />}
              Back up now
            </button>
            {check && (
              <button
                type="button"
                className="button secondary"
                disabled={busy || rebuilding || check.running || !status.lastSucceeded}
                onClick={() => void checkNow()}
              >
                {check.running ? <LoaderCircle size={16} className="spin" /> : <ShieldCheck size={16} />}
                Verify the backup
              </button>
            )}
            {account.joined && (
              <button type="button" className="button secondary" disabled={busy || rebuilding} onClick={() => void makeKit()}>
                <KeyRound size={16} />
                {account.recoveryKitMadeAt ? "Make a new recovery kit" : "Make a recovery kit"}
              </button>
            )}
          </div>
        </>
      )}
      </div>
    </SettingsGroup>
  );
}

/**
 * This Uncloud being rebuilt from the backup, how far it has got, or why it stopped short; and on an
 * Uncloud that was rebuilt, from which backup, and what that leaves to do.
 */
function Rebuilding({ rebuild, slow }: { rebuild: BackupRebuild; slow: boolean }) {
  const from = rebuild.snapshotTime ? ` from the backup of ${when.format(new Date(rebuild.snapshotTime))}` : " from the backup";
  if (rebuild.running) {
    const text =
      rebuild.stage === "restarting"
        ? slow
          ? "Everything is ready, but Uncloud didn’t restart. Quit Uncloud and open it again to finish."
          : "Everything is ready. Uncloud is restarting; then sign in with your old account."
        : rebuild.stage === "preparing"
          ? `Rebuilding${from}: getting the accounts ready.`
          : rebuild.bytes !== null
            ? `Rebuilding${from}: ${formatSize(rebuild.bytesRestored ?? 0)} of ${formatSize(rebuild.bytes)} restored. This can take a while, so leave this computer on.`
            : `Rebuilding${from}: reading the backup.`;
    return (
      <p className="backup-line" role="status">
        {slow ? <TriangleAlert size={14} className="backup-warn" /> : <LoaderCircle size={14} className="spin" />}
        <span>{text}</span>
      </p>
    );
  }
  if (rebuild.message)
    return (
      <p className="backup-line" role="alert">
        <TriangleAlert size={14} className="backup-warn" />
        <span>The rebuild didn’t finish. {rebuild.message}</span>
      </p>
    );
  if (rebuild.rebuiltAt && rebuild.rebuiltFrom)
    return (
      <p className="backup-line">
        <ArchiveRestore size={14} />
        <span>
          Rebuilt {when.format(new Date(rebuild.rebuiltAt))} from the backup of {when.format(new Date(rebuild.rebuiltFrom))}.
          Computers that synced with the old Uncloud need to be connected again.
        </span>
      </p>
    );
  return null;
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
          Verifying: restoring the latest backup to a temporary folder and comparing it. Nothing here
          changes.
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
          {from}, and {since.length ? "every one still here" : "every one"} matched
          {since.length ? ` (${since.join(" and ")})` : ""}.
          {check.hostKeyMatched ? " This Uncloud’s key matched too." : ""}
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
          Backing up now. The first one can take hours, and picks up where it left off if
          interrupted.
        </span>
      </p>
    );
  }
  if (!status.lastOutcome) {
    return (
      <p className="backup-line">
        <span>Not backed up yet. The first backup starts within a few minutes.</span>
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
