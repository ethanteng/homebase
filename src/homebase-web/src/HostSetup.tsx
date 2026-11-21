import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { ArrowRight, Check, Copy, ExternalLink, FolderOpen, LoaderCircle, PackageOpen } from "lucide-react";
import { api } from "./api";
import type { ClaimResult, HostState, RemoteAccessState } from "./api";
import { SettingRow } from "./Settings";
import { WizardSteps } from "./Wizard";

interface Props {
  onSaved: () => void;
}

/**
 * Where this host keeps everybody's files, as one piece of state with everything that goes with
 * choosing it: the folder chooser on the host, saving, and anything loose at the top of the folder
 * from before there were accounts. The first-run step and the Settings row both ask it.
 */
function useLocation(onSaved: () => void) {
  const [host, setHost] = useState<HostState | null>(null);
  const [path, setPath] = useState("");
  const [unclaimed, setUnclaimed] = useState<string[]>([]);
  const [busy, setBusy] = useState<"picker" | "saving" | "claiming" | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    api<HostState>("/host", { signal: controller.signal })
      .then((state) => {
        setHost(state);
        setPath(state.rootPath ?? "");
      })
      .catch(() => {
        // The form still works without knowing what was chosen before.
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (!host?.rootPath) return;
    const controller = new AbortController();
    api<{ entries: string[] }>("/host/unclaimed", { signal: controller.signal })
      .then((result) => setUnclaimed(result.entries))
      .catch(() => setUnclaimed([]));
    return () => controller.abort();
  }, [host?.rootPath, notice]);

  async function pick() {
    setBusy("picker");
    setError("");
    try {
      const result = await api<{ path: string | null }>("/folder-picker", { method: "POST" });
      if (result.path) setPath(result.path);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "The folder chooser couldn’t open.");
    } finally {
      setBusy(null);
    }
  }

  async function save() {
    setBusy("saving");
    setError("");
    try {
      const result = await api<{ rootPath: string }>("/host", {
        method: "PUT",
        body: JSON.stringify({ path }),
      });
      setHost((current) => ({
        rootPath: result.rootPath,
        canPickFolder: current?.canPickFolder ?? false,
      }));
      onSaved();
      return true;
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t use this folder.");
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function claim() {
    setBusy("claiming");
    setError("");
    try {
      const result = await api<ClaimResult>("/host/unclaimed", { method: "POST" });
      const kept = result.skipped.length;
      setNotice(
        `Moved ${result.moved.length} into your files` +
          (kept ? `. ${kept} stayed put because you already have something with that name.` : "."),
      );
      onSaved();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t move those files.");
    } finally {
      setBusy(null);
    }
  }

  return { host, path, setPath, unclaimed, busy, error, notice, pick, save, claim };
}

type Location = ReturnType<typeof useLocation>;

/** Files left at the top of the folder from before accounts, and the way to take them. */
function UnclaimedFiles({ location }: { location: Location }) {
  const { unclaimed, busy, claim } = location;
  if (unclaimed.length === 0) return null;
  return (
    <div className="notice-card">
      <p>
        {unclaimed.length === 1 ? "1 item here" : `${unclaimed.length} items here`} don’t belong to
        anyone yet: <strong>{unclaimed.slice(0, 5).join(", ")}</strong>
        {unclaimed.length > 5 ? ", and more" : ""}.
      </p>
      <button
        type="button"
        className="button secondary"
        onClick={() => void claim()}
        disabled={busy !== null}
      >
        {busy === "claiming" ? <LoaderCircle size={16} className="spin" /> : <PackageOpen size={16} />}
        Move them into my files
      </button>
    </div>
  );
}

/** The chooser, the path and whatever is loose in the folder: the same in both places. */
function LocationFields({ location }: { location: Location }) {
  const { host, path, setPath, busy, error, notice, pick } = location;
  return (
    <>
      {host?.canPickFolder && (
        <button
          type="button"
          className="button secondary"
          onClick={() => void pick()}
          disabled={busy !== null}
        >
          {busy === "picker" ? <LoaderCircle className="spin" size={18} /> : <FolderOpen size={18} />}
          {busy === "picker" ? "Choose it on the Uncloud computer…" : "Choose folder…"}
        </button>
      )}
      <div className="field">
        <label htmlFor="root-path">{host?.canPickFolder ? "Or type its location" : "Folder"}</label>
        <input
          id="root-path"
          className="path-input"
          value={path}
          onChange={(event) => setPath(event.target.value)}
          placeholder="For example, /Users/you/Uncloud"
          autoComplete="off"
          spellCheck={false}
          required
          disabled={busy !== null}
          aria-describedby="folder-help"
        />
        <p id="folder-help" className="field-help">
          Each person gets their own private folder inside it.
        </p>
      </div>
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      {notice && <p className="library-note">{notice}</p>}
      <UnclaimedFiles location={location} />
    </>
  );
}

/** First-run, step 2: where everybody's files will be stored. */
export default function HostSetup({ onSaved }: Props) {
  const location = useLocation(onSaved);

  function submit(event: FormEvent) {
    event.preventDefault();
    void location.save();
  }

  return (
    <div className="wizard">
      <WizardSteps step={2} total={2} />
      <form className="wizard-card" onSubmit={submit}>
        <div className="wizard-title">
          <h1>Where should files be stored?</h1>
          <p>Pick a spot with plenty of space. You can change it later.</p>
        </div>
        <LocationFields location={location} />
        <div className="wizard-actions">
          <button
            className="button primary"
            disabled={location.busy !== null || !location.path.trim()}
          >
            {location.busy === "saving" && <LoaderCircle size={17} className="spin" />}
            {location.busy === "saving" ? "Setting up…" : "Continue"}
            {location.busy !== "saving" && <ArrowRight size={17} />}
          </button>
        </div>
      </form>
    </div>
  );
}

/** Settings: where files are stored, and changing it. */
export function LocationSetting({ onSaved }: Props) {
  const location = useLocation(onSaved);
  const [editing, setEditing] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (await location.save()) setEditing(false);
  }

  return (
    <SettingRow
      label="Location"
      value={location.host?.rootPath ?? "Not chosen"}
      action={
        <button
          type="button"
          className="button secondary"
          aria-expanded={editing}
          onClick={() => setEditing((open) => !open)}
        >
          {editing ? "Cancel" : "Change"}
        </button>
      }
    >
      {editing ? (
        <form className="setting-form" onSubmit={(event) => void submit(event)}>
          <LocationFields location={location} />
          <p className="field-help">Files already stored stay where they are until you move them.</p>
          <button
            className="button primary"
            disabled={location.busy !== null || !location.path.trim()}
          >
            {location.busy === "saving" ? <LoaderCircle size={16} className="spin" /> : <Check size={16} />}
            Save
          </button>
        </form>
      ) : (
        // Loose files can be taken without opening the path editor. That editor has no Save here,
        // so showing it made a typed location look chosen when it wasn't.
        (location.unclaimed.length > 0 || location.notice || location.error) && (
          <>
            {location.error && (
              <p className="error-message" role="alert">
                {location.error}
              </p>
            )}
            {location.notice && <p className="library-note">{location.notice}</p>}
            <UnclaimedFiles location={location} />
          </>
        )
      )}
    </SettingRow>
  );
}

/** Settings: whether people can sign in from outside the home network, and the address if so. */
export function RemoteAccessSetting() {
  const [remote, setRemote] = useState<RemoteAccessState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    const read = () =>
      api<RemoteAccessState>("/remote-access", { signal: controller.signal })
        .then(setRemote)
        .catch(() => {
          // An older host, or one that couldn't answer. The rest of Settings still works.
        });
    void read();
    // A tunnel that is still opening, or opening again, settles on its own; this is how the admin
    // sees it settle without reloading.
    const timer = window.setInterval(() => void read(), 4000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, []);

  async function turn(enabled: boolean) {
    setBusy(true);
    setError("");
    try {
      // The answer is where things got to, not where they end up: a tunnel waiting to be allowed
      // is still waiting, and the poll above carries it the rest of the way.
      setRemote(
        await api<RemoteAccessState>("/remote-access", {
          method: "PUT",
          body: JSON.stringify({ enabled }),
        }),
      );
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t change that just now.");
    } finally {
      setBusy(false);
    }
  }

  async function copy(url: string) {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access can be refused; the address is on screen to be read either way.
    }
  }

  if (!remote) return null;
  const on = remote.status !== "off";
  const copyButton = (url: string, label: string) => (
    <button type="button" className="button secondary" onClick={() => void copy(url)}>
      {copied ? <Check size={15} /> : <Copy size={15} />}
      {copied ? "Copied" : label}
    </button>
  );

  return (
    <SettingRow
      label={<span id="remote-access-label">Access from anywhere</span>}
      value={
        remote.status === "off"
          ? "Off · home Wi-Fi only"
          : remote.status === "on" && remote.url
            ? <a className="setting-link" href={remote.url} target="_blank" rel="noreferrer">{remote.url}</a>
            : remote.status === "needs_sign_in" || remote.status === "needs_funnel"
              ? "Almost on · one more step"
              : "Turning on…"
      }
      action={
        remote.canChange && (
          <button
            type="button"
            role="switch"
            className="switch"
            aria-checked={on}
            aria-labelledby="remote-access-label"
            onClick={() => void turn(!on)}
            disabled={busy}
          />
        )
      }
    >
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      {!remote.canChange && (
        <p className="field-help">
          Set when Uncloud started (<code>Homebase__RemoteAccess__Provider</code>).
        </p>
      )}
      {remote.status === "needs_sign_in" && remote.signInUrl && (
        <>
          <p>Approve this Uncloud in Tailscale. You only do this once.</p>
          <div className="setting-buttons">
            <a className="button primary" href={remote.signInUrl} target="_blank" rel="noreferrer">
              <ExternalLink size={15} />
              Approve in Tailscale
            </a>
            {copyButton(remote.signInUrl, "Copy link")}
          </div>
        </>
      )}
      {remote.status === "needs_funnel" && remote.signInUrl && (
        <>
          <p>Let Tailscale open it to the internet. You only do this once.</p>
          <div className="setting-buttons">
            <a className="button primary" href={remote.signInUrl} target="_blank" rel="noreferrer">
              <ExternalLink size={15} />
              Open in Tailscale
            </a>
            {copyButton(remote.signInUrl, "Copy link")}
          </div>
        </>
      )}
      {remote.status === "on" && remote.url && (
        <div className="setting-buttons">
          {copyButton(remote.url, "Copy address")}
          <span className="field-help">Same usernames and passwords as at home.</span>
        </div>
      )}
      {(remote.status === "opening" || remote.status === "reconnecting") && (
        <p className="field-help">
          <LoaderCircle size={15} className="spin" /> {remote.detail ?? "Turning on. Everyone at home can keep working."}
        </p>
      )}
    </SettingRow>
  );
}
