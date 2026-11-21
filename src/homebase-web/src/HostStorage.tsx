import { useEffect, useState } from "react";
import { Check, LoaderCircle } from "lucide-react";
import { api, formatSize } from "./api";
import type { HostStorage as Storage } from "./api";
import { SettingRow } from "./Settings";

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
  const [editing, setEditing] = useState(false);

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
          ? "Saved. No limit."
          : result.capped && result.totalBytes != null
            ? `Saved. The limit is ${formatSize(result.totalBytes)}.`
            : "Saved. The drive has less free than that, so the drive is the limit.",
      );
      setEditing(false);
      onSaved();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Couldn’t save that.");
    } finally {
      setBusy(false);
    }
  }

  const summary = !current
    ? "No limit"
    : current.unit === "Percent"
      ? `${current.amount}% of the drive`
      : `Up to ${formatSize(current.amount)}`;

  return (
    <SettingRow
      label="Storage limit"
      value={
        <>
          {summary}
          <small className="setting-sub">
            {formatSize(held)} used
            {storage.driveFreeBytes != null && ` · ${formatSize(storage.driveFreeBytes)} free on the drive`}
          </small>
        </>
      }
      action={
        <button
          type="button"
          className="button secondary"
          aria-expanded={editing}
          onClick={() => {
            setEditing((open) => !open);
            setNotice("");
            setError("");
          }}
        >
          {editing ? "Cancel" : "Change"}
        </button>
      }
    >
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {editing && (
        <>
          <fieldset className="allotment-choices" disabled={busy}>
            <legend className="sr-only">Storage limit</legend>
            <label className="allotment-choice">
              <input
                type="radio"
                name="allotment"
                checked={choice === "all"}
                onChange={() => setChoice("all")}
              />
              No limit
            </label>
            <label className="allotment-choice">
              <input
                type="radio"
                name="allotment"
                checked={choice === "share"}
                onChange={() => setChoice("share")}
              />
              Up to
              <input
                className="path-input"
                type="number"
                inputMode="numeric"
                min={1}
                max={100}
                step={1}
                value={percent}
                aria-label="Percent of the drive"
                onFocus={() => setChoice("share")}
                onChange={(event) => setPercent(event.target.value)}
              />
              % of the drive
            </label>
            <label className="allotment-choice">
              <input
                type="radio"
                name="allotment"
                checked={choice === "amount"}
                onChange={() => setChoice("amount")}
              />
              Up to
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
              ? "Uncloud can use all the free space on the drive."
              : !request
                ? choice === "share"
                  ? "Enter a whole number from 1 to 100."
                  : "Enter an amount."
                : limit == null || possible == null
                  ? null
                  : limit >= possible
                    ? "That’s more than the drive can give, so there’s no real limit."
                    : limit <= held
                      ? `That’s less than the ${formatSize(held)} already stored. Nothing is deleted, but nobody can add more.`
                      : `Room for ${formatSize(limit - held)} more, ${formatSize(limit)} in all.`}
          </p>
          <p className="field-help">When it’s full, computers wait to sync until there’s room.</p>
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
        </>
      )}
    </SettingRow>
  );
}
