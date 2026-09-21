import { useState } from "react";
import type { FormEvent } from "react";
import {
  ArrowRight,
  FolderOpen,
  HardDrive,
  LoaderCircle,
  ShieldCheck,
} from "lucide-react";
import { api } from "./api";
import type { LibraryState } from "./api";

interface Props {
  library: LibraryState;
  onSaved: (library: LibraryState) => void;
  compact?: boolean;
}

export default function FolderSetup({
  library,
  onSaved,
  compact = false,
}: Props) {
  const [path, setPath] = useState(library.rootPath ?? "");
  const [busy, setBusy] = useState<"picker" | "saving" | null>(null);
  const [error, setError] = useState("");

  async function pickFolder() {
    setBusy("picker");
    setError("");
    try {
      const result = await api<{ path: string | null }>("/folder-picker", {
        method: "POST",
      });
      if (result.path) setPath(result.path);
    } catch (error) {
      setError(
        error instanceof Error
          ? error.message
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
      const result = await api<LibraryState>("/library", {
        method: "PUT",
        body: JSON.stringify({ path }),
      });
      onSaved({ ...result, canPickFolder: library.canPickFolder });
    } catch (error) {
      setError(
        error instanceof Error ? error.message : "Couldn’t use this folder.",
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
          <span className="eyebrow">A SPACE OF YOUR OWN</span>
          <h2>
            Start with a folder.
            <br />
            Make yourself at home.
          </h2>
          <p>
            Choose a folder on your Mac or an attached drive. Your files stay
            right where they are.
          </p>
          <div className="setup-promise">
            <ShieldCheck size={17} />
            <span>On your computer. Under your control.</span>
          </div>
        </div>
      )}
      <form className="setup-form" onSubmit={save}>
        {!compact && (
          <>
            <span className="step-label">LET’S GET SETTLED</span>
            <h3>Where will your files live?</h3>
          </>
        )}
        <p className="muted">
          Use an existing folder, or create a new one in Finder first. Uncloud
          remembers your choice.
        </p>
        {library.canPickFolder && (
          <button
            type="button"
            className="button secondary picker-button"
            onClick={pickFolder}
            disabled={busy !== null}
          >
            {busy === "picker" ? (
              <LoaderCircle className="spin" size={18} />
            ) : (
              <FolderOpen size={18} />
            )}
            {busy === "picker"
              ? "Check the folder chooser…"
              : "Choose in Finder"}
          </button>
        )}
        <label htmlFor="root-path">
          {library.canPickFolder ? "Or enter a folder path" : "Folder path"}
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
          Your files won’t be moved or changed. A hidden .homebase folder will
          hold the local index.
        </p>
        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        <button
          className="button primary"
          disabled={busy !== null || !path.trim()}
        >
          {busy === "saving" ? (
            <LoaderCircle size={17} className="spin" />
          ) : (
            <ArrowRight size={17} />
          )}
          {busy === "saving" ? "Opening your folder…" : "Use this folder"}
        </button>
        {compact && (
          <p className="field-help">
            Switching folders keeps the files and local index in each folder
            intact.
          </p>
        )}
      </form>
    </section>
  );
}
