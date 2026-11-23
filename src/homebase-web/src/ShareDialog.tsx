import { useEffect, useState } from "react";
import { Check, Copy, Globe, Link2, Link2Off, RefreshCw, X } from "lucide-react";
import { api, nameTogether } from "./api";
import type { LibraryEntry, ShareLink, ShareList } from "./api";

/** Puts a link on the clipboard, saying whether the browser let it. */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/** What a link shares, the way somebody would say it: “Photos”, or “Photos, notes.txt and 3 more”. */
export function describeLink(link: ShareLink) {
  const names = link.items.map((item) => item.name);
  return names.length === 1 ? `“${names[0]}”` : nameTogether(names.map((name) => `“${name}”`));
}

/** Whether a link shares exactly these, in whatever order. */
function sharesExactly(link: ShareLink, paths: string[]) {
  const mine = new Set(link.items.map((item) => item.path));
  return mine.size === paths.length && paths.every((path) => mine.has(path));
}

/** Look, or look and download: the one choice a link has. */
export function AccessChoice({
  canDownload,
  onChange,
  many,
  disabled,
  name,
}: {
  canDownload: boolean;
  onChange: (canDownload: boolean) => void;
  /** Whether it's more than one thing, for “it” or “them”. */
  many: boolean;
  disabled?: boolean;
  /** Tells this choice's buttons apart from another's on the same page. */
  name: string;
}) {
  const it = many ? "them" : "it";
  return (
    <fieldset className="role-choices access-choices" disabled={disabled}>
      <legend>People with the link can</legend>
      <label className={`role-choice${!canDownload ? " chosen" : ""}`}>
        <input type="radio" name={name} checked={!canDownload} onChange={() => onChange(false)} />
        <span>
          <strong>View only</strong>
          <span className="muted">Look at {it} in their browser, with no download buttons</span>
        </span>
      </label>
      <label className={`role-choice${canDownload ? " chosen" : ""}`}>
        <input type="radio" name={name} checked={canDownload} onChange={() => onChange(true)} />
        <span>
          <strong>View and download</strong>
          <span className="muted">Download {it} too, a folder as a zip</span>
        </span>
      </label>
    </fieldset>
  );
}

/** Said wherever a link is handed out while nobody away from this computer could open it. */
export function ReachNote({ isAdmin, onOpenSettings }: { isAdmin: boolean; onOpenSettings?: () => void }) {
  return (
    <div className="reach-note share-reach">
      <Globe size={18} />
      <p>
        Nobody else can open links yet, because <strong>Access from anywhere</strong> is off.{" "}
        {isAdmin ? (
          onOpenSettings ? (
            <button className="link-button" onClick={onOpenSettings}>
              Turn it on in Settings
            </button>
          ) : (
            "Turn it on in Settings."
          )
        ) : (
          "Ask whoever looks after this Uncloud to turn it on."
        )}
      </p>
    </div>
  );
}

/** A link's address, ready to copy. */
export function LinkField({ url, label }: { url: string; label: string }) {
  const [copied, setCopied] = useState(false);
  const [field, setField] = useState<HTMLInputElement | null>(null);
  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(false), 2500);
    return () => window.clearTimeout(timer);
  }, [copied]);
  return (
    <div className="share-link-row">
      <input
        ref={setField}
        className="path-input share-link-field"
        aria-label={label}
        value={url}
        readOnly
        onFocus={(event) => event.currentTarget.select()}
      />
      <button
        className="button primary"
        onClick={async () => {
          if (await copyText(url)) setCopied(true);
          // A browser that won't let a page near the clipboard still lets somebody copy it themselves.
          else field?.select();
        }}
      >
        {copied ? <Check size={16} /> : <Copy size={16} />}
        {copied ? "Copied" : "Copy link"}
      </button>
    </div>
  );
}

interface Props {
  /** What's being shared, or null while nothing is. */
  entries: LibraryEntry[] | null;
  isAdmin: boolean;
  onClose: () => void;
  /** After a link was made, changed or turned off, so whatever shows which files are shared reads again. */
  onChanged: () => void;
  /** Opens the list of every link. */
  onShowLinks: () => void;
  onOpenSettings?: () => void;
}

/**
 * Sharing files or folders by link: making one, copying it, choosing whether people can download
 * as well as look, and turning it off. Picking the same things again finds the same link.
 */
export default function ShareDialog({ entries, isAdmin, onClose, onChanged, onShowLinks, onOpenSettings }: Props) {
  const [element, setElement] = useState<HTMLDialogElement | null>(null);
  const [list, setList] = useState<ShareList | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [canDownload, setCanDownload] = useState(false);
  const [notice, setNotice] = useState("");

  useEffect(() => {
    if (entries) {
      if (!element?.open) element?.showModal();
    } else element?.close();
  }, [entries, element]);

  useEffect(() => {
    if (!entries) return;
    const controller = new AbortController();
    setList(null);
    setError("");
    setNotice("");
    setCanDownload(false);
    api<ShareList>("/shares", { signal: controller.signal })
      .then((result) => {
        if (!controller.signal.aborted) setList(result);
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setError(failure instanceof Error ? failure.message : "Couldn’t read your links.");
      });
    return () => controller.abort();
  }, [entries]);

  const paths = entries?.map((entry) => entry.path) ?? [];
  const many = paths.length > 1;
  const link = list?.links.find((candidate) => sharesExactly(candidate, paths)) ?? null;
  // Shared already, but together with other things or on their own, in another link.
  const elsewhere =
    list?.links.filter(
      (candidate) => candidate !== link && candidate.items.some((item) => paths.includes(item.path)),
    ) ?? [];

  async function change(work: () => Promise<ShareList>, said = "") {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      setList(await work());
      setNotice(said);
      onChanged();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Uncloud couldn’t make that change.");
    } finally {
      setBusy(false);
    }
  }
  const reread = () => api<ShareList>("/shares");

  const create = () =>
    change(async () => {
      await api<ShareLink>("/shares", { method: "POST", body: JSON.stringify({ paths, canDownload }) });
      return reread();
    });
  const setAccess = (id: string, allow: boolean) =>
    change(async () => {
      await api<ShareLink>(`/shares/${encodeURIComponent(id)}`, {
        method: "PATCH",
        body: JSON.stringify({ canDownload: allow }),
      });
      return reread();
    });
  const stop = (id: string) =>
    change(async () => {
      await api(`/shares/${encodeURIComponent(id)}`, { method: "DELETE" });
      return reread();
    }, "Stopped sharing. That link doesn’t work any more.");

  const what = entries?.length === 1 ? `“${entries[0].name}”` : `${entries?.length ?? 0} items`;

  return (
    <dialog
      ref={setElement}
      className="settings-dialog share-dialog"
      onClose={onClose}
      aria-labelledby="share-title"
    >
      {entries && (
        <>
          <div className="dialog-header">
            <h2 id="share-title">Share {what}</h2>
            <button className="icon-button" aria-label="Close" onClick={onClose}>
              <X size={20} />
            </button>
          </div>
          <div className="edit-body">
            {list === null && !error ? (
              <div className="folder-list-note" role="status">
                <RefreshCw className="spin" size={15} />
                Opening…
              </div>
            ) : link ? (
              <>
                <p>
                  Anyone with this link can {link.canDownload ? "look at and download" : "look at"}{" "}
                  {many ? "them" : "it"}, without an account.
                </p>
                {link.url ? (
                  <LinkField url={link.url} label="Link" />
                ) : (
                  <p className="field-help">
                    Uncloud can’t show this link again. Stop sharing it, then share again for a new one.
                  </p>
                )}
                <AccessChoice
                  name="share-access"
                  canDownload={link.canDownload}
                  onChange={(allow) => void setAccess(link.id, allow)}
                  many={many}
                  disabled={busy}
                />
                {!link.canDownload && (
                  <p className="field-help">
                    Anything people can see, they can still keep a copy of, with a screenshot say.
                  </p>
                )}
                <div>
                  <button className="row-action danger" onClick={() => void stop(link.id)} disabled={busy}>
                    <Link2Off size={15} />
                    Stop sharing
                  </button>
                </div>
              </>
            ) : (
              list && (
                <>
                  {notice ? (
                    <p className="share-notice" role="status">
                      <Check size={16} />
                      {notice}
                    </p>
                  ) : (
                    <p>
                      Make a link to {many ? "them" : "it"} that anyone can open, without an account. Only
                      people you send it to will know it.
                    </p>
                  )}
                  <AccessChoice
                    name="share-access"
                    canDownload={canDownload}
                    onChange={setCanDownload}
                    many={many}
                    disabled={busy}
                  />
                </>
              )
            )}
            {list && elsewhere.length > 0 && (
              <p className="field-help">
                {elsewhere.length === 1
                  ? `Also shared in another link, with ${describeLink(elsewhere[0])}.`
                  : `Also shared in ${elsewhere.length} other links.`}{" "}
                <button className="link-button" onClick={onShowLinks}>
                  See all shared links
                </button>
              </p>
            )}
            {list && !list.reachable && <ReachNote isAdmin={isAdmin} onOpenSettings={onOpenSettings} />}
          </div>
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <div className="edit-actions">
            {link || !list ? (
              <button className="button secondary" onClick={onClose}>
                Done
              </button>
            ) : (
              <>
                <button className="button secondary" onClick={onClose} disabled={busy}>
                  Cancel
                </button>
                <button className="button primary" onClick={() => void create()} disabled={busy}>
                  {busy ? <RefreshCw className="spin" size={15} /> : <Link2 size={16} />}
                  {busy ? "Making link…" : "Create link"}
                </button>
              </>
            )}
          </div>
        </>
      )}
    </dialog>
  );
}
