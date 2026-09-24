import { useCallback, useEffect, useState } from "react";
import { Check, Cloud, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { DropboxAppSettings } from "./api";
import DropboxAppSteps, { AppKeyReassurance } from "./DropboxAppForm";

interface Props {
  /** Open straight onto the steps, when somebody came here to set Dropbox up. */
  expanded?: boolean;
}

/**
 * The Dropbox app this Uncloud offers everybody. Only a convenience: with it set, everybody here
 * connects their own Dropbox in one click; without it, Uncloud's own app does that where it can,
 * and anybody can set up their own under My account. It shares nobody's files — each person still
 * signs in to their own Dropbox.
 */
export default function HostDropbox({ expanded = false }: Props) {
  const [dropbox, setDropbox] = useState<DropboxAppSettings | null>(null);
  const [appKey, setAppKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const load = useCallback(async () => {
    try {
      const settings = await api<DropboxAppSettings>("/host/dropbox");
      setDropbox(settings);
      setAppKey(settings.appKey ?? "");
    } catch {
      // The rest of the settings are still worth showing without this.
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function save() {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const result = await api<{ configured: boolean; disconnected: number }>("/host/dropbox", {
        method: "PUT",
        body: JSON.stringify({ appKey }),
      });
      setNotice(
        !result.configured
          ? dropbox?.relayProvides
            ? "Removed. People here go back to connecting Dropbox the way Uncloud does by itself."
            : "Removed. People here will need to set up Dropbox themselves under My account before they can connect it."
          : result.disconnected > 0
            ? `Saved. ${result.disconnected} ${result.disconnected === 1 ? "person was" : "people were"} signed out of Dropbox because they’d connected through the old setup — they can connect again from Add files.`
            : "Saved. Everyone here can now connect their own Dropbox from Add files.",
      );
      await load();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t save that.");
    } finally {
      setBusy(false);
    }
  }

  if (!dropbox) return null;

  // Nothing here is a step anybody has to take when Uncloud brings its own Dropbox app and nobody
  // has set a key, so it folds away. A key already set, or one from the environment, and it is a
  // real setting again: one somebody has used must not become one they can't find.
  const optional = dropbox.relayProvides && !dropbox.appKey && !dropbox.fromEnvironment;

  const form = (
    <>
      <DropboxAppSteps redirectUri={dropbox.redirectUri} scopes={dropbox.scopes} idPrefix="host-dropbox" />
      <label htmlFor="dropbox-app-key">App key</label>
      <input
        id="dropbox-app-key"
        className="path-input"
        value={appKey}
        onChange={(event) => setAppKey(event.target.value)}
        placeholder="Paste the app key here"
        autoComplete="off"
        spellCheck={false}
        disabled={busy}
        aria-describedby="app-key-help"
      />
      <p id="app-key-help" className="field-help">
        <AppKeyReassurance /> Changing it signs out anyone who connected through the old one; they
        can connect again from Add files.
      </p>
      {dropbox.fromEnvironment && (
        <p className="field-help">
          This Uncloud was started with an app key already. Saving one here replaces it.
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
        onClick={() => void save()}
        disabled={busy || appKey.trim() === (dropbox.appKey ?? "")}
      >
        {busy ? <LoaderCircle size={16} className="spin" /> : <Check size={16} />}
        {appKey.trim() === "" && dropbox.appKey ? "Remove it" : "Save"}
      </button>
    </>
  );

  return (
    <section className="import-section account-dropbox" id="dropbox-setup">
      <div className="import-section-head">
        <h3>
          <Cloud size={16} />
          Dropbox for everyone here
        </h3>
      </div>
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {optional ? (
        <>
          <p className="field-help">
            Nothing to set up: everyone here can already connect their own Dropbox from Add files,
            and nobody can see anybody else’s. It works on the computer Uncloud runs on; to connect
            from other computers too, register a Dropbox app below.
          </p>
          <details className="advanced-section" open={expanded}>
            <summary>
              <span className="advanced-lead">Advanced</span>
              Use a Dropbox app you registered
            </summary>
            {form}
          </details>
        </>
      ) : dropbox.configured ? (
        <>
          <p className="field-help">
            Set up. Everyone here can connect their own Dropbox from Add files, and nobody can see
            anybody else’s.
          </p>
          <details className="own-app-details" open={expanded}>
            <summary>Change how Dropbox is set up</summary>
            {form}
          </details>
        </>
      ) : (
        <>
          <p className="field-help">
            Do this once, on Dropbox’s website, and everyone here can connect their own Dropbox with
            one click. It takes about five minutes. Nobody’s Dropbox is shared with anybody else.
          </p>
          {form}
        </>
      )}
    </section>
  );
}
