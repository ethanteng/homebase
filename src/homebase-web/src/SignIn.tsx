import { useState } from "react";
import type { FormEvent } from "react";
import { ArrowRight, HardDrive, LoaderCircle, ShieldCheck } from "lucide-react";
import { api } from "./api";
import type { Session, User } from "./api";

interface Props {
  session: Session;
  onSignedIn: (user: User) => void;
}

/**
 * The way in. On a host with no accounts this makes the first one, which is the administrator
 * because somebody has to choose where everyone's files live; after that it signs people in.
 */
export default function SignIn({ session, onSignedIn }: Props) {
  const setup = session.setupNeeded;
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
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
      setError(
        failure instanceof Error
          ? failure.message
          : "Uncloud couldn’t sign you in.",
      );
      setBusy(false);
    }
  }

  return (
    <section className="setup-card">
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
        <span className="eyebrow">
          {setup ? "A NEW UNCLOUD" : "WELCOME BACK"}
        </span>
        <h2>
          {setup ? (
            <>
              Make the first account.
              <br />
              It’s yours to look after.
            </>
          ) : (
            <>
              Your files are
              <br />
              right where you left them.
            </>
          )}
        </h2>
        <p>
          {setup
            ? "This account keeps the keys: it chooses where everyone’s files live and who else can have an account here."
            : "Everyone on this Uncloud has a folder of their own. Only you can see yours."}
        </p>
        <div className="setup-promise">
          <ShieldCheck size={17} />
          <span>Your own folder. Nobody else’s to open.</span>
        </div>
      </div>
      <form className="setup-form" onSubmit={submit}>
        <span className="step-label">
          {setup ? "LET’S GET STARTED" : "SIGN IN"}
        </span>
        <h3>{setup ? "Who are you?" : "Welcome back"}</h3>
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
        />
        {setup && (
          <>
            <label htmlFor="display-name">Your name</label>
            <input
              id="display-name"
              className="path-input"
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              placeholder={username || "Optional"}
              autoComplete="name"
              disabled={busy}
            />
          </>
        )}
        <label htmlFor="password">Password</label>
        <input
          id="password"
          className="path-input"
          type="password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          autoComplete={setup ? "new-password" : "current-password"}
          required
          disabled={busy}
          aria-describedby={setup ? "password-help" : undefined}
        />
        {setup && (
          <p id="password-help" className="field-help">
            At least 10 characters. Length is what makes a password hard to
            guess, so a few ordinary words beat a short tangle of symbols.
          </p>
        )}
        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        <button
          className="button primary"
          disabled={busy || !username.trim() || !password}
        >
          {busy ? (
            <LoaderCircle size={17} className="spin" />
          ) : (
            <ArrowRight size={17} />
          )}
          {busy
            ? setup
              ? "Making your account…"
              : "Signing you in…"
            : setup
              ? "Create this Uncloud"
              : "Sign in"}
        </button>
      </form>
    </section>
  );
}
