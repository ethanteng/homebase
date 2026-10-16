import { useEffect, useState } from "react";
import { Check, Laptop, LoaderCircle } from "lucide-react";
import { api } from "./api";
import type { PairingAsk, SyncStatus, User } from "./api";

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

/// A computer asking to be added, arrived at by scanning the QR code the Uncloud app shows on it.
/// The code only ever leads here: nothing is added until the person it belongs to, signed in,
/// says yes.
export default function PairApproval({
  id,
  me,
  onApproved,
  onClose,
  onSignOut,
}: {
  id: string;
  me: User;
  onApproved: (status: SyncStatus) => void;
  onClose: () => void;
  onSignOut: () => void;
}) {
  const [ask, setAsk] = useState<PairingAsk | null>(null);
  const [state, setState] = useState<
    "loading" | "asking" | "adding" | "added" | "gone"
  >("loading");
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    api<PairingAsk>(`/sync/pairing-requests/${encodeURIComponent(id)}`, {
      signal: controller.signal,
    })
      .then((found) => {
        setAsk(found);
        setState("asking");
      })
      .catch((problem: unknown) => {
        if (controller.signal.aborted) return;
        setError(message(problem));
        setState("gone");
      });
    return () => controller.abort();
  }, [id]);

  async function approve() {
    setState("adding");
    setError("");
    try {
      onApproved(
        await api<SyncStatus>(
          `/sync/pairing-requests/${encodeURIComponent(id)}/approve`,
          { method: "POST" },
        ),
      );
      setState("added");
    } catch (problem: unknown) {
      // Left asking, so a moment without a connection can be tried again.
      setError(message(problem));
      setState("asking");
    }
  }

  return (
    <section className="notice-card pair-approval" aria-live="polite">
      {state === "loading" && (
        <p className="field-help pair-loading" role="status">
          <LoaderCircle className="spin" size={16} />
          Finding the computer that asked…
        </p>
      )}
      {state === "gone" && (
        <>
          <h2>Couldn’t find that computer</h2>
          <p className="field-help">{error}</p>
          <button className="button" onClick={onClose}>
            Close
          </button>
        </>
      )}
      {(state === "asking" || state === "adding") && ask && (
        <>
          <h2>
            <Laptop size={20} strokeWidth={1.7} />
            Add {ask.name} to {me.displayName}’s files?
          </h2>
          <SignedInAs
            me={me}
            onSignOut={onSignOut}
            disabled={state === "adding"}
          />
          <p className="field-help">
            It will keep all of your files in an Uncloud folder there, and
            anything you add, change or delete on either side changes on the
            other. Only add a computer you’re setting up yourself right now.
          </p>
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <div className="pair-actions">
            <button
              className="button primary"
              onClick={() => void approve()}
              disabled={state === "adding"}
            >
              {state === "adding" ? "Adding…" : "Add this computer"}
            </button>
            <button
              className="button"
              onClick={onClose}
              disabled={state === "adding"}
            >
              Not now
            </button>
          </div>
        </>
      )}
      {state === "added" && ask && (
        <>
          <h2>
            <Check size={20} strokeWidth={1.9} />
            {ask.name} is added to {me.displayName}’s files
          </h2>
          <p className="field-help">
            It’s setting itself up now, and your files will start arriving in
            its Uncloud folder in a moment. There’s nothing else to do here.
          </p>
          <button className="button" onClick={onClose}>
            Done
          </button>
        </>
      )}
    </section>
  );
}
