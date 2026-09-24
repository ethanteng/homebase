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

  // Uncloud's own app finishes a sign-in by handing it to Uncloud at its plain local address, and
  // there is more than one way that can fail to arrive: a browser on another computer, a proxy in
  // between, a host answering on its own secure address. Whoever this is true for has just been
  // turned away from Connect, and this is the screen they were sent to.
  const relayIsNoUseHere =
    dropbox !== null && dropbox.source === "Relay" && !dropbox.relayReachable;

  // Nothing is connecting this account to Dropbox yet, it is their own key doing it, or what would
  // have can't reach them: either way the app-key form is the thing they came here for, so it is
  // not tucked away.
  const settleForOwn =
    dropbox !== null &&
    (dropbox.source === "Own" || dropbox.source === "None" || relayIsNoUseHere);

  const form = dropbox && (
    <>
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
            : dropbox.relayProvides
              ? "leave empty to use Uncloud’s own app"
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
    </>
  );

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
              : relayIsNoUseHere
                ? "Dropbox can only be connected from the computer Uncloud runs on, the way things are set up now. To connect it from here, set it up yourself below — it takes about five minutes on Dropbox’s website, and needs nobody else."
                : dropbox.source === "None"
                  ? "To connect your Dropbox, it needs setting up once on Dropbox’s website. Nobody has done that on this Uncloud yet, so you can do it yourself — it takes about five minutes."
                  : "Dropbox is ready to connect from Add files. There’s nothing you need to do here."}
          </p>

          {settleForOwn ? (
            form
          ) : (
            // Somebody already has a Dropbox app working for this account, so the steps for making
            // one are an answer to a question they haven’t asked. Folded away rather than dropped:
            // wanting your own is a legitimate thing to want, and this is where it is.
            <details className="own-app-details" open={dropboxExpanded}>
              <summary>Use my own Dropbox setup instead</summary>
              <p className="field-help">
                {dropbox.source === "Relay"
                  ? "Worth doing if you’d rather your Dropbox sign-in didn’t go through an app somebody else registered, or to connect Dropbox from a computer other than the one Uncloud runs on."
                  : "Worth doing if you’d rather not depend on whoever looks after this Uncloud keeping their setup working."}
              </p>
              {form}
            </details>
          )}
        </section>
      )}
    </form>
  );
}

