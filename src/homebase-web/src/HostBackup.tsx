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
    "under Backing up offsite, choose “Use a recovery kit” and type both in.",
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

  const heading = (
    <div className="import-section-head">
      <h3>
        <CloudUpload size={16} />
        Backing up offsite
      </h3>
    </div>
  );

  if (kit) {
    const copyButton = (what: "password" | "recovery", text: string) => (
      <button type="button" className="button secondary" onClick={() => void copy(what, text)}>
        {copied?.what === what && copied.how === "copied" ? <Check size={16} /> : <Copy size={16} />}
        {copied?.what === what && copied.how === "copied" ? "Copied" : "Copy"}
      </button>
    );
    return (
      <section className="import-section host-backup">
        {heading}
        <p className="field-help">
          <strong>This is your recovery kit.</strong> Everything is encrypted with the password on this
          computer before it leaves, so nobody else can read your backup: not iDrive, not Uncloud. It
          also means nobody can open the backup without the kit if this computer is lost or broken.
          With it, another computer running Uncloud can take this one’s place and get everything back.
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
            The recovery token couldn’t be made just now. Once storage is set up, make a recovery kit
            from here: it has this password in it too.
          </p>
        )}
        {copied?.how === "select" && (
          <p className="field-help" role="status">
            This browser didn’t let Uncloud copy it. It’s selected: copy it with ⌘C or Ctrl+C.
          </p>
        )}
        <button type="button" className="button secondary" onClick={() => download(kit)}>
          <Download size={16} />
          Download the kit
        </button>
        <p className="field-help warning-text">
          Keep it somewhere safe away from this computer, like a password manager, or printed and put
          away. This is the only time Uncloud shows it.
        </p>
        <label className="backup-kept">
          <input type="checkbox" checked={kept} onChange={(event) => setKept(event.target.checked)} />
          I’ve kept it somewhere safe
        </label>
        <button type="button" className="button primary" disabled={!kept} onClick={done}>
          <Check size={16} />
          Done
        </button>
      </section>
    );
  }

  if (!account.joined && !status.configured && recovering) {
    return (
      <section className="import-section host-backup">
        {heading}
        <p className="field-help">
          Taking the place of an Uncloud that’s been lost? Type in its household’s recovery kit. This
          Uncloud then opens that backup, and the lost one’s key stops working.
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
            Opening the household’s backup…
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
            Join with an invite instead
          </button>
        </div>
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
          {rebuild && <Rebuilding rebuild={rebuild} slow={slow} />}
          {account.paused && !rebuilding && (
            <p className="backup-line">
              <Pause size={14} className="backup-warn" />
              <span>
                This Uncloud has opened its household’s backup and isn’t adding to it yet. Rebuild it from
                the backup to bring back everyone’s accounts and files as they were. Or get back only what
                you need from Backups, then resume backing up.
              </span>
            </p>
          )}
          <LastBackup status={status} />
          <p className="field-help">
            {keptBackups?.length === 1 && `1 backup kept, from ${when.format(new Date(keptBackups[0].time))}. `}
            {keptBackups && keptBackups.length > 1 &&
              `${keptBackups.length} backups kept, the oldest from ${when.format(new Date(keptBackups[0].time))}. `}
            Every night, and soon after this computer is back if it missed one. Encrypted here before it
            leaves.
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
                  No recovery kit yet. Without one, nobody can get this backup back if this computer is
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
    </section>
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
          ? "Everything from the backup is ready, and Uncloud hasn’t started again by itself. Quit Uncloud and open it again: it finishes the rebuild as it starts."
          : "Everything from the backup is ready. Uncloud is starting again as the Uncloud in the backup: sign in with your account from before once it has."
        : rebuild.stage === "preparing"
          ? `Rebuilding this Uncloud${from}: everything is back, and the accounts are being got ready.`
          : rebuild.bytes !== null
            ? `Rebuilding this Uncloud${from}: ${formatSize(rebuild.bytesRestored ?? 0)} of ${formatSize(rebuild.bytes)} back so far. This can take a while; leave this computer on.`
            : `Rebuilding this Uncloud${from}: reading what’s in it.`;
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
          Computers that synced with the lost Uncloud need to be paired again.
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
