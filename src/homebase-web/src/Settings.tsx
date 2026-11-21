import type { ReactNode } from "react";
import { CircleCheck, Info, TriangleAlert } from "lucide-react";

/**
 * The pieces Settings is made of: groups of rows, each saying what a setting is, what it's set to
 * now, and offering one thing to do about it. Whatever changes a setting opens under its row, so
 * only the setting being changed has its options on screen.
 */
export function SettingsGroup({
  title,
  note,
  children,
}: {
  title: string;
  note?: ReactNode;
  children: ReactNode;
}) {
  const id = `group-${title.toLowerCase().replace(/[^a-z]+/g, "-")}`;
  return (
    <section className="settings-group" aria-labelledby={id}>
      <div className="settings-group-head">
        <h2 id={id}>{title}</h2>
        {note && <p>{note}</p>}
      </div>
      <div className="settings-card">{children}</div>
    </section>
  );
}

export function SettingRow({
  label,
  value,
  action,
  children,
  id,
}: {
  label: ReactNode;
  value?: ReactNode;
  action?: ReactNode;
  /** What opens under the row: a form to change it, or more about it. */
  children?: ReactNode;
  id?: string;
}) {
  return (
    <div className="setting-row" id={id}>
      <div className="setting-main">
        <span className="setting-label">{label}</span>
        {value !== undefined && <span className="setting-value">{value}</span>}
        {action && <span className="setting-action">{action}</span>}
      </div>
      {children && <div className="setting-detail">{children}</div>}
    </div>
  );
}

/** One line about how something stands: a problem, something worth knowing, or all well. */
export function StatusLine({
  tone,
  children,
  action,
}: {
  tone: "warn" | "info" | "ok";
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className={`status-line ${tone}`}>
      <span className="status-icon" aria-hidden="true">
        {tone === "warn" ? (
          <TriangleAlert size={17} />
        ) : tone === "ok" ? (
          <CircleCheck size={17} />
        ) : (
          <Info size={17} />
        )}
      </span>
      <div className="status-text">{children}</div>
      {action && <span className="setting-action">{action}</span>}
    </div>
  );
}
