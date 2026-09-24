import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { Check, CloudDownload, KeyRound, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { MyDropboxApp, User } from "./api";
import DropboxAppSteps, { AppKeyReassurance } from "./DropboxAppForm";

interface Props {
  me: User;
  /** Open straight onto the Dropbox steps, when somebody came here to set Dropbox up. */
  dropboxExpanded?: boolean;
}

/** The one thing everybody can change about their own account.  */
export default function AccountPanel({ me, dropboxExpanded = false }: Props) {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [dropbox, setDropbox] = useState<MyDropboxApp | null>(null);
  const [appKey, setAppKey] = useState("");
  const [savingKey, setSavingKey] = useState(false);
  const [keyError, setKeyError] = useState("");
  const [keyNotice, setKeyNotice] = useState("");

  const loadDropbox = useCallback(async () => {
    try {
      const mine = await api<MyDropboxApp>("/account/dropbox");
      setDropbox(mine);
      setAppKey(mine.appKey ?? "");
    } catch {
      // Changing a password is the point of this screen and works without this.
    }
  }, []);

  useEffect(() => {
    void loadDropbox();
  }, [loadDropbox]);

  async function saveAppKey() {
    setSavingKey(true);
    setKeyError("");
    setKeyNotice("");
    try {
      const result = await api<{
        configured: boolean;
        source: string;
        disconnected: boolean;
      }>("/account/dropbox", {
        method: "PUT",
        body: JSON.stringify({ appKey }),
      });
      const signedOut = result.disconnected
        ? " You were signed out of Dropbox, because you’d connected through the old setup — connect again from Add files."
        : "";
      setKeyNotice(
        appKey.trim() === ""
          ? result.configured
            ? `Removed. You’ll connect Dropbox the way everyone else here does.${signedOut}`
            : `Removed. Dropbox can’t be connected until you add a key again.${signedOut}`
          : `Saved. You can connect your Dropbox from Add files.${signedOut}`,
      );
      await loadDropbox();
    } catch (failure) {
      setKeyError(
        failure instanceof Error ? failure.message : "Couldn’t save that app key.",
      );
    } finally {
      setSavingKey(false);
    }
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await api("/account/password", {
        method: "POST",
        body: JSON.stringify({ currentPassword: current, newPassword: next }),
      });
      setCurrent("");
      setNext("");
      setNotice(
        "Password changed. Every other browser signed in as you has been signed out.",
      );
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t change your password.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="setup-form compact" onSubmit={submit}>
      <p className="muted">
        Signed in as <strong>{me.displayName}</strong> (@{me.username})
        {me.isAdmin ? " — you look after this Uncloud." : "."}
      </p>
      <label htmlFor="current-password">Current password</label>
      <input
        id="current-password"
        className="path-input"
        type="password"
        value={current}
        onChange={(event) => setCurrent(event.target.value)}
        autoComplete="current-password"
        required
        disabled={busy}
      />
      <label htmlFor="next-password">New password</label>
      <input
        id="next-password"
        className="path-input"
        type="password"
        value={next}
        onChange={(event) => setNext(event.target.value)}
        autoComplete="new-password"
        required
        disabled={busy}
        aria-describedby="new-password-help"
      />
      <p id="new-password-help" className="field-help">
        At least 10 characters. Changing it signs you out everywhere except
        here.
      </p>
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      {notice && <p className="library-note">{notice}</p>}
      <button className="button primary" disabled={busy || !current || !next}>
        {busy ? (
          <LoaderCircle size={16} className="spin" />
        ) : (
          <KeyRound size={16} />
        )}
        Change my password
      </button>

      {dropbox && (
        <section className="import-section account-dropbox" id="dropbox-setup">
          <div className="import-section-head">
            <h3>
              <CloudDownload size={16} />
              Dropbox
            </h3>
          </div>
          <p className="field-help">
            {dropbox.source === "Own"
              ? "You connect Dropbox through a setup of your own. Nobody else here can see or change it."
              : dropbox.source === "None"
                ? "To connect your Dropbox, it needs setting up once on Dropbox’s website. Nobody has done that on this Uncloud yet, so you can do it yourself — it takes about five minutes."
                : "Dropbox is ready to connect from Add files. There’s nothing you need to do here."}
          </p>
          {dropbox.source === "None" ? (
            <DropboxSetup
              dropbox={dropbox}
              appKey={appKey}
              setAppKey={setAppKey}
              saving={savingKey}
              error={keyError}
              notice={keyNotice}
              onSave={() => void saveAppKey()}
            />
          ) : (
            <>
              {keyNotice && <p className="library-note">{keyNotice}</p>}
              <details className="advanced" open={dropboxExpanded || dropbox.source === "Own"}>
                <summary>
                  {dropbox.source === "Own" ? "Change your Dropbox setup" : "Use your own Dropbox setup instead"}
                </summary>
                <DropboxSetup
                  dropbox={dropbox}
                  appKey={appKey}
                  setAppKey={setAppKey}
                  saving={savingKey}
                  error={keyError}
                  notice=""
                  onSave={() => void saveAppKey()}
                />
              </details>
            </>
          )}
        </section>
      )}
    </form>
  );
}

function DropboxSetup({
  dropbox,
  appKey,
  setAppKey,
  saving,
  error,
  notice,
  onSave,
}: {
  dropbox: MyDropboxApp;
  appKey: string;
  setAppKey: (value: string) => void;
  saving: boolean;
  error: string;
  notice: string;
  onSave: () => void;
}) {
  return (
    <>
      <DropboxAppSteps redirectUri={dropbox.redirectUri} scopes={dropbox.scopes} idPrefix="my-dropbox" />
      <label htmlFor="my-dropbox-app-key">Your app key</label>
      <input
        id="my-dropbox-app-key"
        className="path-input"
        value={appKey}
        onChange={(event) => setAppKey(event.target.value)}
        placeholder={dropbox.hostProvides ? "Leave empty to use this Uncloud’s setup" : "Paste the app key here"}
        autoComplete="off"
        spellCheck={false}
        disabled={saving}
        aria-describedby="my-dropbox-help"
      />
      <p id="my-dropbox-help" className="field-help">
        <AppKeyReassurance />
      </p>
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      {notice && <p className="library-note">{notice}</p>}
      <button
        type="button"
        className="button primary"
        onClick={onSave}
        disabled={saving || appKey.trim() === (dropbox.appKey ?? "")}
      >
        {saving ? <LoaderCircle size={16} className="spin" /> : <Check size={16} />}
        {appKey.trim() === "" && dropbox.appKey ? "Remove my app key" : "Save my app key"}
      </button>
    </>
  );
}
