import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  ArrowRight,
  FolderOpen,
  HardDrive,
  LoaderCircle,
  PackageOpen,
  ShieldCheck,
} from "lucide-react";
import { api } from "./api";
import type { ClaimResult, HostState } from "./api";

interface Props {
  onSaved: () => void;
  compact?: boolean;
}

/**
 * Where this host keeps everybody's files. Only an administrator sees this: everyone's folder
 * is made underneath the one chosen here, and everyone draws on the same drive.
 */
export default function HostSetup({ onSaved, compact = false }: Props) {
  const [host, setHost] = useState<HostState | null>(null);
  const [path, setPath] = useState("");
  const [unclaimed, setUnclaimed] = useState<string[]>([]);
  const [busy, setBusy] = useState<"picker" | "saving" | "claiming" | null>(
    null,
  );
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
        // The setup form still works without knowing what was chosen before.
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

  async function pickFolder() {
    setBusy("picker");
    setError("");
    try {
      const result = await api<{ path: string | null }>("/folder-picker", {
        method: "POST",
      });
      if (result.path) setPath(result.path);
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "The folder chooser couldn’t open.",
      );
    } finally {
      setBusy(null);
    }
  }

  async function save(event: FormEvent) {
    event.preventDefault();
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
    } catch (failure) {
      setError(
        failure instanceof Error ? failure.message : "Couldn’t use this folder.",
      );
    } finally {
      setBusy(null);
    }
  }

  async function claim() {
    setBusy("claiming");
    setError("");
    try {
      const result = await api<ClaimResult>("/host/unclaimed", {
        method: "POST",
      });
      const kept = result.skipped.length;
      setNotice(
        `Moved ${result.moved.length} into your folder` +
          (kept
            ? `. ${kept} stayed put — you already have something by that name.`
            : "."),
      );
      onSaved();
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t move those files.",
      );
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className={`setup-card ${compact ? "compact" : ""}`}>
      {!compact && (
        <div className="setup-intro">
          <div className="folder-scene" aria-hidden="true">
            <div className="scene-orbit" />
            <div className="paper paper-back" />
            <div className="paper paper-front">
              <span />
              <span />
              <span />
            </div>
            <div className="scene-folder">
              <span>
                <HardDrive size={27} strokeWidth={1.4} />
              </span>
            </div>
            <div className="scene-badge">
              <ShieldCheck size={19} />
            </div>
          </div>
          <span className="eyebrow">ONE FOLDER, EVERYBODY’S FILES</span>
          <h2>
            Start with a folder.
            <br />
            Make everyone at home.
          </h2>
          <p>
            Choose a folder on this computer or an attached drive. Everyone with
            an account here gets their own folder underneath it, and only they
            can see inside it.
          </p>
          <div className="setup-promise">
            <ShieldCheck size={17} />
            <span>On hardware you own. Under your control.</span>
          </div>
        </div>
      )}
      <form className="setup-form" onSubmit={save}>
        {!compact && (
          <>
            <span className="step-label">LET’S GET SETTLED</span>
            <h3>Where will everyone’s files live?</h3>
          </>
        )}
        <p className="muted">
          Use an existing folder, or make a new one first. Uncloud remembers
          this for the whole host.
        </p>
        {host?.canPickFolder && (
          <button
            type="button"
            className="button secondary picker-button"
            onClick={() => void pickFolder()}
            disabled={busy !== null}
          >
            {busy === "picker" ? (
              <LoaderCircle className="spin" size={18} />
            ) : (
              <FolderOpen size={18} />
            )}
            {busy === "picker"
              ? "Check the folder chooser on the host…"
              : "Choose in Finder"}
          </button>
        )}
        <label htmlFor="root-path">
          {host?.canPickFolder ? "Or enter a folder path" : "Folder path"}
        </label>
        <input
          id="root-path"
          className="path-input"
          value={path}
          onChange={(event) => setPath(event.target.value)}
          placeholder="/Users/you/Uncloud"
          autoComplete="off"
          spellCheck={false}
          required
          disabled={busy !== null}
          aria-describedby="folder-help"
        />
        <p id="folder-help" className="field-help">
          Each account gets a folder under <code>users/</code>, named by an id
          rather than a username, so renaming an account never moves a file.
        </p>
        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        {notice && <p className="library-note">{notice}</p>}
        <button
          className="button primary"
          disabled={busy !== null || !path.trim()}
        >
          {busy === "saving" ? (
            <LoaderCircle size={17} className="spin" />
          ) : (
            <ArrowRight size={17} />
          )}
          {busy === "saving" ? "Opening the folder…" : "Use this folder"}
        </button>
        {unclaimed.length > 0 && (
          <div className="notice-card">
            <p>
              {unclaimed.length === 1
                ? "One thing is sitting loose at the top of this folder"
                : `${unclaimed.length} things are sitting loose at the top of this folder`}{" "}
              and belongs to no account — most likely from before this Uncloud
              had accounts: <strong>{unclaimed.slice(0, 5).join(", ")}</strong>
              {unclaimed.length > 5 ? ", and more" : ""}.
            </p>
            <button
              type="button"
              className="button secondary"
              onClick={() => void claim()}
              disabled={busy !== null}
            >
              {busy === "claiming" ? (
                <LoaderCircle size={16} className="spin" />
              ) : (
                <PackageOpen size={16} />
              )}
              Move them into my folder
            </button>
            <p className="field-help">
              Each one is renamed into place, so nothing is copied and nothing
              you already have is overwritten.
            </p>
          </div>
        )}
        {compact && (
          <p className="field-help">
            Moving the host’s folder moves where everybody’s files are looked
            for. The files themselves stay where they are.
          </p>
        )}
      </form>
    </section>
  );
}
