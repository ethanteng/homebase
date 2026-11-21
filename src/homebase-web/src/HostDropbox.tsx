import { useCallback, useEffect, useState } from "react";
import { Check, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { DropboxAppSettings } from "./api";
import DropboxAppSteps, { AppKeyReassurance } from "./DropboxAppForm";
import { SettingRow } from "./Settings";

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
  const [open, setOpen] = useState(expanded);

  // Somebody sent here to set Dropbox up lands on the steps, even if they were here already.
  useEffect(() => {
    if (expanded) setOpen(true);
  }, [expanded]);

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
            ? "Removed. Uncloud’s built-in Dropbox connection is back."
            : "Removed. People can set up Dropbox themselves in My account."
          : result.disconnected > 0
            ? `Saved. ${result.disconnected} ${result.disconnected === 1 ? "person was" : "people were"} signed out of Dropbox and can reconnect from Add files.`
            : "Saved. Everyone can connect their Dropbox from Add files.",
      );
      setOpen(false);
      await load();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t save that.");
    } finally {
      setBusy(false);
    }
  }

  if (!dropbox) return null;

  // Uncloud brings its own Dropbox app, which works from the Uncloud computer itself. Somebody's
  // own app is only needed to connect from anywhere else, so that's what setting one up is for.
  const builtIn = dropbox.relayProvides && !dropbox.appKey && !dropbox.fromEnvironment;

  return (
    <SettingRow
      id="dropbox-setup"
      label="Dropbox"
      value={
        builtIn ? (
          <>
            Works on the Uncloud computer
            <small className="setting-sub">Add a Dropbox app to connect from any device</small>
          </>
        ) : dropbox.configured ? (
          <span className="ok">On for everyone</span>
        ) : (
          "Not set up"
        )
      }
      action={
        <button
          type="button"
          className="button secondary"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
        >
          {open ? "Cancel" : builtIn || !dropbox.configured ? "Set up" : "Change"}
        </button>
      }
    >
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {open && (
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
            <AppKeyReassurance /> Changing it signs out people who connected with the old key.
          </p>
          {dropbox.fromEnvironment && (
            <p className="field-help">A key was set when Uncloud started. Saving one here replaces it.</p>
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
      )}
    </SettingRow>
  );
}
