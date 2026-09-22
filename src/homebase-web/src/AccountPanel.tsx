import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import { Check, CloudDownload, KeyRound, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { MyDropboxApp, User } from "./api";
import DropboxAppSteps, { AppKeyReassurance } from "./DropboxAppForm";

interface Props {
  me: User;
  /** Called when this account's Dropbox app changes, so the import panel keeps up. */
  onDropboxChanged: () => void;
}

/** The one thing everybody can change about their own account.  */
export default function AccountPanel({ me, onDropboxChanged }: Props) {
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
        ? " Your existing Dropbox connection was signed out, because it was authorised through the old app — connect again under Bring files in."
        : "";
      setKeyNotice(
        appKey.trim() === ""
          ? result.configured
            ? `Your own app key was removed. You’ll connect through this Uncloud’s app instead.${signedOut}`
            : `Your own app key was removed, and this Uncloud doesn’t offer one, so Dropbox can’t be connected until you add a key.${signedOut}`
          : `Saved. Your Dropbox connects through your own app now.${signedOut}`,
      );
      await loadDropbox();
      onDropboxChanged();
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
        <section className="import-section account-dropbox">
          <div className="import-section-head">
            <h3>
              <CloudDownload size={16} />
              Your Dropbox app
            </h3>
          </div>
          <p className="field-help">
            {dropbox.source === "Own"
              ? "You connect Dropbox through your own Dropbox app. Nobody else here can see or change it."
              : dropbox.source === "None"
                ? "Connecting Dropbox needs a Dropbox app. Nobody has set one up on this Uncloud, so make your own — it takes a couple of minutes and doesn’t need anyone else."
                : "You’re connecting through the Dropbox app this Uncloud offers everybody. That’s usually what you want. Set your own below if you’d rather not depend on it."}
          </p>

          <DropboxAppSteps
            redirectUri={dropbox.redirectUri}
            scopes={dropbox.scopes}
            idPrefix="my-dropbox"
          />

          <label htmlFor="my-dropbox-app-key">Your app key</label>
          <input
            id="my-dropbox-app-key"
            className="path-input"
            value={appKey}
            onChange={(event) => setAppKey(event.target.value)}
            placeholder={
              dropbox.hostProvides
                ? "leave empty to use this Uncloud’s app"
                : "the app key from that page"
            }
            autoComplete="off"
            spellCheck={false}
            disabled={savingKey}
            aria-describedby="my-dropbox-help"
          />
          <p id="my-dropbox-help" className="field-help">
            <AppKeyReassurance />
          </p>
          {keyError && (
            <p className="error-message" role="alert">
              {keyError}
            </p>
          )}
          {keyNotice && <p className="library-note">{keyNotice}</p>}
          <button
            type="button"
            className="button primary"
            onClick={() => void saveAppKey()}
            disabled={savingKey || appKey.trim() === (dropbox.appKey ?? "")}
          >
            {savingKey ? (
              <LoaderCircle size={16} className="spin" />
            ) : (
              <Check size={16} />
            )}
            {appKey.trim() === "" && dropbox.appKey
              ? "Remove my app key"
              : "Save my app key"}
          </button>
        </section>
      )}
    </form>
  );
}
