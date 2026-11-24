import { useEffect, useState } from "react";
import { Check, Copy, ExternalLink, File, Folder, Link2, Link2Off, RefreshCw, TriangleAlert, X } from "lucide-react";
import { api } from "./api";
import type { ShareItem, ShareLink, ShareList } from "./api";
import { copyText, describeLink, ReachNote } from "./ShareDialog";

interface Props {
  isAdmin: boolean;
  /** Opens a folder in My files. */
  open: (folder: string) => void;
  onOpenSettings?: () => void;
}

const dateFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric",
  month: "short",
  day: "numeric",
});
const folderOf = (path: string) => (path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "");

/** Where one thing a link shares is, said the way My files would. */
function where(item: ShareItem) {
  if (!item.here) return "No longer there";
  const folder = folderOf(item.path);
  return folder ? `In ${folder}` : "In My files";
}

/**
 * Every link this account has made, in one place: what each shares, whether people can download
 * from it, its address to copy again, and a way to turn it off.
 */
export default function SharedLinks({ isAdmin, open, onOpenSettings }: Props) {
  const [list, setList] = useState<ShareList | null>(null);
  const [loadError, setLoadError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoadError("");
    api<ShareList>("/shares", { signal: controller.signal })
      .then((result) => {
        if (!controller.signal.aborted) setList(result);
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setLoadError(failure instanceof Error ? failure.message : "Couldn’t read your links.");
      });
    return () => controller.abort();
  }, [refresh]);

  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(null), 2500);
    return () => window.clearTimeout(timer);
  }, [copied]);

  async function change(link: ShareLink, work: () => Promise<unknown>, said: string) {
    setBusy(link.id);
    setError("");
    setNotice("");
    try {
      await work();
      setList(await api<ShareList>("/shares"));
      setNotice(said);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Uncloud couldn’t make that change.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Shared links</h1>
          <p>Anyone with one of these links can open what it shares, without an account.</p>
        </div>
      </div>
      {list && !list.reachable && list.links.length > 0 && (
        <ReachNote isAdmin={isAdmin} onOpenSettings={onOpenSettings} />
      )}
      {(notice || error) && (
        <div className={`edit-notice${error ? " failed" : ""}`} role={error ? "alert" : "status"}>
          {error ? <TriangleAlert size={16} /> : <Check size={16} />}
          <span>{error || notice}</span>
          <button
            className="icon-button"
            aria-label="Dismiss"
            onClick={() => {
              setError("");
              setNotice("");
            }}
          >
            <X size={16} />
          </button>
        </div>
      )}
      <section className="file-panel" aria-label="Shared links" aria-busy={!list && !loadError}>
        {loadError ? (
          <div className="empty-state" role="alert">
            <Link2 size={36} />
            <h2>Couldn’t read your links</h2>
            <p>{loadError}</p>
            <button className="button secondary" onClick={() => setRefresh((value) => value + 1)}>
              Try again
            </button>
          </div>
        ) : !list ? (
          <div className="file-loading" role="status">
            <RefreshCw className="spin" size={23} />
            <span>Opening…</span>
          </div>
        ) : list.links.length === 0 ? (
          <div className="empty-state">
            <Link2 size={38} />
            <h2>Nothing shared yet</h2>
            <p>
              To share, tick files or folders in My files and choose Share. Whoever you send the link to
              can open them without an account.
            </p>
          </div>
        ) : (
          <ul className="share-list">
            {list.links.map((link) => {
              const first = link.items[0];
              const gone = link.items.every((item) => !item.here);
              return (
                <li key={link.id} className="share-row">
                  <div className="share-what">
                    <span className={`file-icon${first?.isDirectory ? " directory" : ""}`}>
                      {first?.isDirectory ? (
                        <Folder size={21} strokeWidth={1.6} fill="currentColor" fillOpacity={0.14} />
                      ) : (
                        <File size={21} strokeWidth={1.6} />
                      )}
                    </span>
                    <span className="share-names">
                      {first?.here ? (
                        <button className="link-button" onClick={() => open(folderOf(first.path))}>
                          {describeLink(link)}
                        </button>
                      ) : (
                        <strong>{describeLink(link)}</strong>
                      )}
                      <span className={gone ? "share-gone" : "muted"}>
                        {link.items.length === 1
                          ? where(first)
                          : gone
                            ? "None of these are there any more"
                            : `${where(first)}${link.items.some((item) => !item.here) ? " · some are no longer there" : ""}`}
                        {" · "}
                        {link.canDownload ? "View and download" : "View only"} · Made{" "}
                        {dateFormat.format(new Date(link.createdAt))}
                      </span>
                    </span>
                  </div>
                  <div className="share-actions">
                    {link.url && (
                      <>
                        <button
                          className="row-action"
                          onClick={async () => {
                            if (await copyText(link.url!)) setCopied(link.id);
                            else window.prompt("Copy this link:", link.url!);
                          }}
                        >
                          {copied === link.id ? <Check size={15} /> : <Copy size={15} />}
                          {copied === link.id ? "Copied" : "Copy link"}
                        </button>
                        <a className="row-action" href={link.url} target="_blank" rel="noreferrer">
                          <ExternalLink size={15} />
                          Open
                        </a>
                      </>
                    )}
                    <select
                      className="path-input share-access-select"
                      aria-label={`Who can do what with the link to ${describeLink(link)}`}
                      value={link.canDownload ? "download" : "view"}
                      disabled={busy === link.id}
                      onChange={(event) => {
                        const allow = event.target.value === "download";
                        void change(
                          link,
                          () =>
                            api(`/shares/${encodeURIComponent(link.id)}`, {
                              method: "PATCH",
                              body: JSON.stringify({ canDownload: allow }),
                            }),
                          allow
                            ? `People with the link to ${describeLink(link)} can download now.`
                            : `People with the link to ${describeLink(link)} can only look now.`,
                        );
                      }}
                    >
                      <option value="view">View only</option>
                      <option value="download">View and download</option>
                    </select>
                    <button
                      className="row-action danger"
                      disabled={busy === link.id}
                      onClick={() =>
                        void change(
                          link,
                          () => api(`/shares/${encodeURIComponent(link.id)}`, { method: "DELETE" }),
                          `Stopped sharing ${describeLink(link)}. That link doesn’t work any more.`,
                        )
                      }
                    >
                      <Link2Off size={15} />
                      Stop sharing
                    </button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </>
  );
}
