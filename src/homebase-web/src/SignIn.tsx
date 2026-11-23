import { useState } from "react";
import type { FormEvent } from "react";
import { ArrowRight, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { Session, User } from "./api";
import { WizardSteps } from "./Wizard";

interface Props {
  session: Session;
  onSignedIn: (user: User) => void;
  /// Why they're signing in, when they came for one thing: a computer waiting to be added.
  purpose?: { title: string; lead: string };
}

/**
 * The way in. On a host with no accounts this makes the first one, which is the admin because
 * somebody has to choose where everyone's files live; after that it signs people in.
 */
export default function SignIn({ session, onSignedIn, purpose }: Props) {
  const setup = session.setupNeeded;
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const result = await api<{ user: User }>(setup ? "/setup" : "/session", {
        method: "POST",
        body: JSON.stringify(
          setup
            ? { username, displayName: displayName || username, password }
            : { username, password },
        ),
      });
      onSignedIn(result.user);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t sign you in.");
      setBusy(false);
    }
  }

  return (
    <div className="wizard">
      {setup && <WizardSteps step={1} total={2} />}
      <form className="wizard-card" onSubmit={submit}>
        <div className="wizard-title">
          <h1>{setup ? "Create your account" : (purpose?.title ?? "Sign in")}</h1>
          <p>
            {setup
              ? "You’ll be the admin, so you can add family members later."
              : (purpose?.lead ?? "Welcome back to Uncloud.")}
          </p>
        </div>
        {setup && (
          <div className="field">
            <label htmlFor="display-name">Your name</label>
            <input
              id="display-name"
              className="path-input"
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              autoComplete="name"
              disabled={busy}
            />
          </div>
        )}
        <div className="field">
          <label htmlFor="username">Username</label>
          <input
            id="username"
            className="path-input"
            value={username}
            onChange={(event) => setUsername(event.target.value)}
            autoComplete="username"
            autoCapitalize="none"
            spellCheck={false}
            required
            disabled={busy}
            aria-describedby={setup ? "username-help" : undefined}
          />
          {setup && (
            <p id="username-help" className="field-help">
              Used to sign in
            </p>
          )}
        </div>
        <div className="field">
          <label htmlFor="password">Password</label>
          <div className="field-row">
            <input
              id="password"
              className="path-input"
              type={showPassword ? "text" : "password"}
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoComplete={setup ? "new-password" : "current-password"}
              required
              disabled={busy}
              aria-describedby={setup ? "password-help" : undefined}
            />
            <button
              type="button"
              className="button secondary"
              onClick={() => setShowPassword((value) => !value)}
              aria-pressed={showPassword}
            >
              {showPassword ? "Hide" : "Show"}
            </button>
          </div>
          {setup && (
            <p id="password-help" className="field-help">
              At least 10 characters. A few words work well.
            </p>
          )}
        </div>
        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        <div className="wizard-actions">
          <button className="button primary" disabled={busy || !username.trim() || !password}>
            {busy && <LoaderCircle size={17} className="spin" />}
            {busy
              ? setup
                ? "Creating your account…"
                : "Signing in…"
              : setup
                ? "Continue"
                : "Sign in"}
            {!busy && <ArrowRight size={17} />}
          </button>
        </div>
      </form>
    </div>
  );
}
