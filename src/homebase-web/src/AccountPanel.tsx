import { useState } from "react";
import type { FormEvent } from "react";
import { KeyRound, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { User } from "./api";

interface Props {
  me: User;
}

/** The one thing everybody can change about their own account.  */
export default function AccountPanel({ me }: Props) {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

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
    </form>
  );
}
