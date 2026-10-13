import { useEffect, useState } from "react";
import { Check, HardDrive, LoaderCircle } from "lucide-react";
import { api, formatSize } from "./api";
import type { HostStorage as Storage } from "./api";

type Choice = "all" | "share" | "amount";
type Unit = "GB" | "TB";

// The same units the storage meter shows, so 200 GB set here reads as 200 GB everywhere else.
const SIZES: Record<Unit, number> = { GB: 1024 ** 3, TB: 1024 ** 4 };

interface Props {
  onSaved: () => void;
}

/** What the form asks for, as the request would carry it, or null when it can't be meant. */
function wanted(
  choice: Choice,
  percent: string,
  amount: string,
  unit: Unit,
): { percent?: number; bytes?: number } | null {
  if (choice === "all") return {};
  if (choice === "share") {
    const share = Number(percent);
    return Number.isInteger(share) && share >= 1 && share <= 100 ? { percent: share } : null;
  }
  const size = Number(amount);
  return Number.isFinite(size) && size > 0 ? { bytes: Math.round(size * SIZES[unit]) } : null;
}

/**
 * An amount in whichever unit reads more naturally, with places enough to give back what was typed —
 * 1.234 GB reads as 1.234, not 1.23 — and never as 0.
 */
function inUnits(bytes: number): { amount: string; unit: Unit } {
  const unit: Unit = bytes >= SIZES.TB ? "TB" : "GB";
  const size = bytes / SIZES[unit];
  return { amount: String(Number(size.toFixed(6)) || Number(size.toPrecision(2))), unit };
}

/**
 * How much of the drive Uncloud may fill. Everybody's files share it, so this is the host's to
 * decide: all of the drive, a set amount, or a share of what it could have. Nothing is ever deleted
 * to fit — less than everybody already has only stops more arriving.
 */
export default function HostStorage({ onSaved }: Props) {
  const [storage, setStorage] = useState<Storage | null>(null);
  const [choice, setChoice] = useState<Choice>("all");
  const [percent, setPercent] = useState("50");
  const [amount, setAmount] = useState("");
  const [unit, setUnit] = useState<Unit>("GB");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  function show(next: Storage) {
    setStorage(next);
    const allotment = next.allotment;
    if (!allotment) setChoice("all");
    else if (allotment.unit === "Percent") {
      setChoice("share");
      setPercent(String(allotment.amount));
    } else {
      setChoice("amount");
      const shown = inUnits(allotment.amount);
      setAmount(shown.amount);
      setUnit(shown.unit);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    api<Storage>("/host/storage", { signal: controller.signal })
      .then(show)
      .catch(() => {
        // The rest of the settings are still worth showing without this.
      });
    return () => controller.abort();
  }, []);

  if (!storage) return null;

  const request = wanted(choice, percent, amount, unit);
  const current = storage.allotment;
  // An amount set some other way may not be one the form can show exactly. Left as shown, it is
  // still the one that's set, and saving it would only nudge it.
  const shown = current?.unit === "Bytes" ? inUnits(current.amount) : null;
  const unchanged =
    request !== null &&
    (current === null
      ? request.percent === undefined && request.bytes === undefined
      : current.unit === "Percent"
        ? request.percent === current.amount
        : request.bytes === current.amount ||
          (choice === "amount" && unit === shown!.unit && Number(amount) === Number(shown!.amount)));

  const held = storage.uncloudBytes;
  // Everything Uncloud could have: what's free, and what it already holds.
  const possible = storage.driveFreeBytes == null ? null : held + storage.driveFreeBytes;
  const limit =
    possible == null || !request
      ? null
      : request.percent !== undefined
        ? Math.floor((possible * request.percent) / 100)
        : (request.bytes ?? null);

  async function save() {
    if (!request) return;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const result = await api<Storage>("/host/storage", {
        method: "PUT",
        body: JSON.stringify(request),
      });
      show(result);
      setNotice(
        !result.allotment
          ? "Saved. Everyone’s files can fill the drive again."
          : result.capped && result.totalBytes != null
            ? `Saved. Everyone here now shares ${formatSize(result.totalBytes)}.`
            : "Saved. The drive has less than that free, so it’s still the drive that sets the limit.",
      );
      onSaved();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t save that.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="import-section host-storage">
      <div className="import-section-head">
        <h3>
          <HardDrive size={16} />
          How much of the drive Uncloud may use
        </h3>
      </div>
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      <p className="field-help">
        {storage.driveFreeBytes != null
          ? `The drive has ${formatSize(storage.driveFreeBytes)} free, and everyone’s files here take up ${formatSize(held)}.`
          : `Everyone’s files here take up ${formatSize(held)}. Uncloud can’t tell how much room the drive has left.`}
      </p>
      <fieldset className="allotment-choices" disabled={busy}>
        <legend className="sr-only">How much of the drive Uncloud may use</legend>
        <label className="allotment-choice">
          <input
            type="radio"
            name="allotment"
            checked={choice === "all"}
            onChange={() => setChoice("all")}
          />
          All of it
        </label>
        <label className="allotment-choice">
          <input
            type="radio"
            name="allotment"
            checked={choice === "share"}
            onChange={() => setChoice("share")}
          />
          A share
          <input
            className="path-input"
            type="number"
            inputMode="numeric"
            min={1}
            max={100}
            step={1}
            value={percent}
            aria-label="Percent of the room Uncloud could have"
            onFocus={() => setChoice("share")}
            onChange={(event) => setPercent(event.target.value)}
          />
          %
        </label>
        <label className="allotment-choice">
          <input
            type="radio"
            name="allotment"
            checked={choice === "amount"}
            onChange={() => setChoice("amount")}
          />
          A set amount
          <input
            className="path-input"
            type="number"
            inputMode="decimal"
            min={0}
            step="any"
            value={amount}
            placeholder="200"
            aria-label="Amount"
            onFocus={() => setChoice("amount")}
            onChange={(event) => setAmount(event.target.value)}
          />
          <select
            className="path-input"
            value={unit}
            aria-label="Unit"
            onFocus={() => setChoice("amount")}
            onChange={(event) => setUnit(event.target.value as Unit)}
          >
            <option value="GB">GB</option>
            <option value="TB">TB</option>
          </select>
        </label>
      </fieldset>
      <p className="field-help" role="status">
        {choice === "all"
          ? "Everyone shares whatever the drive has free."
          : !request
            ? choice === "share"
              ? "A share is a whole number from 1 to 100."
              : "Enter how much Uncloud may use."
            : limit == null || possible == null
              ? null
              : limit >= possible
                ? "The drive has less than that free, so Uncloud can still fill it."
                : limit <= held
                  ? `That’s less than the ${formatSize(held)} everyone’s files already take. Nothing is deleted, but nobody can add more until there’s room under it.`
                  : `Uncloud can hold up to ${formatSize(limit)}: room for ${formatSize(limit - held)} more. The rest of the drive is left for this computer.`}
        {choice === "share" && request && possible != null && (
          <>
            {" "}
            A share is of what’s free plus what everyone’s files already take ({formatSize(possible)}),
            so it doesn’t shrink as they grow.
          </>
        )}
      </p>
      <p className="field-help">
        Files people’s computers sync stop here too: once it’s full, they wait on the computer
        until there’s room.
      </p>
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      <button
        type="button"
        className="button primary"
        onClick={() => void save()}
        disabled={busy || !request || unchanged}
      >
        {busy ? <LoaderCircle size={16} className="spin" /> : <Check size={16} />}
        Save
      </button>
    </section>
  );
}
