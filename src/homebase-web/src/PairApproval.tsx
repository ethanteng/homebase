import { useEffect, useState } from "react";
import { Check, Laptop, LoaderCircle, X } from "lucide-react";
import { api } from "./api";
import type { PairingAsk, User } from "./api";

function message(problem: unknown) {
  return problem instanceof Error ? problem.message : "That didn’t work.";
}

/// Whose files a computer is about to join. A phone or browser stays signed in to whichever
/// account last used it, which needn't be the account of whoever is adding a computer now: said
/// where they decide, so that adding it to somebody else's files is something they'd notice.
export function SignedInAs({
  me,
  onSignOut,
  disabled = false,
}: {
  me: User;
  onSignOut: () => void;
  disabled?: boolean;
}) {
  return (
    <p className="field-help signed-in-as">
      You’re signed in as {me.displayName} (@{me.username}). Not you?{" "}
      <button className="link-button" onClick={onSignOut} disabled={disabled}>
        Sign out
      </button>
    </p>
  );
}

type State = "loading" | "asking" | "adding" | "declining" | "added" | "declined" | "gone";

/// A computer asking to be added: opened in that computer's own browser from the Uncloud app's
/// "Sign in to connect", or on a phone that scanned its QR code. The page is only this question,
/// so nothing else competes with it, and nothing is added until the person it belongs to,
/// signed in, says yes. Saying no tells the computer, which stops waiting. Opened again after
/// answering, it says what was answered rather than that it's gone.
export default function PairApproval({
  id,
  me,
  onDone,
  onSignOut,
}: {
  id: string;
  me: User;
  /// Leaves the question for the rest of Uncloud.
  onDone: () => void;
  onSignOut: () => void;
}) {
  const [ask, setAsk] = useState<PairingAsk | null>(null);
  const [state, setState] = useState<State>("loading");
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    api<PairingAsk>(`/sync/pairing-requests/${encodeURIComponent(id)}`, {
      signal: controller.signal,
    })
      .then((found) => {
        setAsk(found);
        setState(found.state === "added" ? "added" : found.state === "declined" ? "declined" : "asking");
      })
      .catch((problem: unknown) => {
        if (controller.signal.aborted) return;
        setError(message(problem));
        setState("gone");
      });
    return () => controller.abort();
  }, [id]);

  async function answer(yes: boolean) {
    setState(yes ? "adding" : "declining");
    setError("");
    try {
      await api(`/sync/pairing-requests/${encodeURIComponent(id)}/${yes ? "approve" : "decline"}`, {
        method: "POST",
      });
      setState(yes ? "added" : "declined");
    } catch (problem: unknown) {
      // Left asking, so a moment without a connection can be tried again.
      setError(message(problem));
      setState("asking");
    }
  }

  const busy = state === "adding" || state === "declining";
  const name = ask?.name ?? "This computer";

  return (
    <div className="wizard pair-page">
      <section className="wizard-card pair-approval" aria-live="polite">
        {state === "loading" && (
          <p className="field-help pair-loading" role="status">
            <LoaderCircle className="spin" size={16} />
            Finding the computer that asked…
          </p>
        )}

        {state === "gone" && (
          <>
            <div className="wizard-title">
              <h1>This request has ended</h1>
              <p>{error}</p>
            </div>
            <div className="pair-actions">
              <button className="button" onClick={onDone}>
                Open Uncloud
              </button>
            </div>
          </>
        )}

        {(state === "asking" || busy) && ask && (
          <>
            <span className="pair-icon" aria-hidden="true">
              <Laptop size={30} strokeWidth={1.6} />
            </span>
            <div className="wizard-title">
              <h1>
                Add {ask.name} to {me.displayName}’s files?
              </h1>
              <SignedInAs me={me} onSignOut={onSignOut} disabled={busy} />
            </div>
            <p className="field-help">
              It will keep your files in sync in an Uncloud folder there. Only add a computer
              you’re setting up right now.
            </p>
            {error && (
              <p className="error-message" role="alert">
                {error}
              </p>
            )}
            <div className="pair-actions">
              <button
                className="button primary"
                onClick={() => void answer(true)}
                disabled={busy}
              >
                {state === "adding" ? "Adding…" : "Add this computer"}
              </button>
              <button className="button" onClick={() => void answer(false)} disabled={busy}>
                {state === "declining" ? "Saying no…" : "Don’t add"}
              </button>
            </div>
          </>
        )}

        {state === "added" && (
          <>
            <span className="pair-icon done" aria-hidden="true">
              <Check size={30} strokeWidth={2} />
            </span>
            <div className="wizard-title">
              <h1>
                {name} is added to {me.displayName}’s files
              </h1>
              <p>It starts syncing in a moment. You can close this tab.</p>
            </div>
            <div className="pair-actions">
              <button className="button" onClick={onDone}>
                Open Uncloud
              </button>
            </div>
          </>
        )}

        {state === "declined" && (
          <>
            <span className="pair-icon" aria-hidden="true">
              <X size={30} strokeWidth={1.8} />
            </span>
            <div className="wizard-title">
              <h1>{name} wasn’t added</h1>
              <p>It’s been told, and nothing changed. You can close this tab.</p>
            </div>
            <div className="pair-actions">
              <button className="button" onClick={onDone}>
                Open Uncloud
              </button>
            </div>
          </>
        )}
      </section>
    </div>
  );
}
