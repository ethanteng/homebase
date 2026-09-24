import { HardDrive, TriangleAlert } from "lucide-react";
import { formatSize } from "./api";
import type { StorageReport } from "./api";

export type SpaceLevel = "ok" | "low" | "critical" | "unknown";

const GB = 1024 ** 3;

/**
 * How worried to be about the space left. Everybody here shares one drive, so it running out is
 * everybody's problem, and worth saying before it happens rather than when an import is refused.
 */
export function spaceLevel(storage: StorageReport | null): SpaceLevel {
  if (!storage || storage.freeBytes == null) return "unknown";
  const free = storage.freeBytes;
  const share = storage.totalBytes ? free / storage.totalBytes : 1;
  if (free < GB || share < 0.03) return "critical";
  if (free < 5 * GB || share < 0.1) return "low";
  return "ok";
}

/** The drive as a bar: what's yours, what everything else takes, and what's left. */
function Bar({ storage, className = "" }: { storage: StorageReport; className?: string }) {
  const total = storage.totalBytes ?? 0;
  if (total <= 0 || storage.freeBytes == null) return null;
  const mine = Math.min(storage.usedBytes, total);
  const others = Math.max(0, total - storage.freeBytes - mine);
  const percent = (bytes: number) => `${Math.max(0, Math.min(100, (bytes / total) * 100))}%`;
  const used = Math.round(((total - storage.freeBytes) / total) * 100);
  return (
    <div
      className={`storage-bar ${className}`}
      role="meter"
      aria-label="Space used on this Uncloud’s drive"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={used}
      aria-valuetext={`${used}% used, ${formatSize(storage.freeBytes)} free`}
    >
      {/* At least a sliver, so "yours" is visible even when it's a rounding error of the drive. */}
      <span className="storage-mine" style={{ width: mine > 0 ? `max(3px, ${percent(mine)})` : 0 }} />
      <span className="storage-others" style={{ width: percent(others) }} />
    </div>
  );
}

interface CardProps {
  storage: StorageReport | null;
  isAdmin: boolean;
}

/** The sidebar's account of the drive: how much is left, in the largest type there. */
export function StorageCard({ storage, isAdmin }: CardProps) {
  const level = spaceLevel(storage);
  return (
    <div className={`storage-card level-${level}`}>
      <span className="storage-title">
        <HardDrive size={15} />
        Space on this Uncloud
      </span>
      {storage?.freeBytes != null ? (
        <>
          <strong className="storage-free">
            {formatSize(storage.freeBytes)} <span>free</span>
          </strong>
          <Bar storage={storage} />
          <span className="storage-legend">
            <span>
              <i className="swatch mine" /> Yours: {formatSize(storage.usedBytes)}
            </span>
            {storage.totalBytes != null && <span>of {formatSize(storage.totalBytes)} in all</span>}
          </span>
          {level !== "ok" && (
            <p className="storage-warning">
              <TriangleAlert size={13} />
              {level === "critical" ? "Almost full. " : "Running low. "}
              {isAdmin
                ? "Free up space on the Uncloud computer’s drive before anyone adds more."
                : "Let whoever looks after this Uncloud know."}
            </p>
          )}
        </>
      ) : (
        <p className="storage-warning">
          <TriangleAlert size={13} />
          {storage
            ? "Uncloud can’t tell how much room is left on its drive."
            : "Checking how much room is left…"}
        </p>
      )}
    </div>
  );
}

/** The same, small enough for the top of every page, including on a phone. */
export function StoragePill({ storage }: { storage: StorageReport | null }) {
  if (storage?.freeBytes == null) return null;
  const level = spaceLevel(storage);
  return (
    <span
      className={`storage-pill level-${level}`}
      title={`${formatSize(storage.freeBytes)} free${storage.totalBytes != null ? ` of ${formatSize(storage.totalBytes)}` : ""} on this Uncloud’s drive · ${formatSize(storage.usedBytes)} is yours`}
    >
      {level === "ok" ? <HardDrive size={14} /> : <TriangleAlert size={14} />}
      <strong>{formatSize(storage.freeBytes)}</strong> free
      <Bar storage={storage} className="mini" />
    </span>
  );
}
