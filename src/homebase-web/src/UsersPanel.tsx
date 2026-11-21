import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Check, Copy, LoaderCircle, RefreshCw, Trash2, UserPlus, X } from "lucide-react";
import { api, formatSize } from "./api";
import type { ManagedUser, User } from "./api";
import { WORDS } from "./words";

interface Props {
  me: User;
}

// A whole number from 0 up to (not including) `limit`, from the browser's own random numbers.
// Draws that would favour the low numbers are thrown away and drawn again, so every one is as
// likely as the next.
function randomBelow(limit: number) {
  const ceiling = Math.floor(0x1_0000_0000 / limit) * limit;
  const draw = new Uint32Array(1);
  do crypto.getRandomValues(draw);
  while (draw[0] >= ceiling);
  return draw[0] % limit;
}

/** Four everyday words and a number, like "maple-river-candle-harbor-427": about 50 bits. */
export function suggestPassword() {
  const words = Array.from({ length: 4 }, () => WORDS[randomBelow(WORDS.length)]);
  return `${words.join("-")}-${100 + randomBelow(900)}`;
}

/** Who has an account on this host. Only an admin ever sees this. */
export default function UsersPanel({ me }: Props) {
  const [users, setUsers] = useState<ManagedUser[] | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [isAdmin, setIsAdmin] = useState(false);
  const [copied, setCopied] = useState(false);
  // The username follows the name until somebody types one of their own.
  const [usernameTyped, setUsernameTyped] = useState(false);
  const sheet = useRef<HTMLDialogElement>(null);

  // The side panel is a dialog, so focus stays in it and Escape closes it.
  useEffect(() => {
    if (adding && !sheet.current?.open) sheet.current?.showModal();
    if (!adding && sheet.current?.open) sheet.current.close();
  }, [adding]);

  function startAdding() {
    setUsername("");
    setDisplayName("");
    setPassword(suggestPassword());
    setIsAdmin(false);
    setUsernameTyped(false);
    setError("");
    setNotice("");
    setAdding(true);
  }

  async function copyPassword() {
    try {
      await navigator.clipboard.writeText(password);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access can be refused; the password is on screen to read out either way.
    }
  }

  async function load() {
    try {
      setUsers(await api<ManagedUser[]>("/users"));
      setError("");
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t load people.",
      );
    }
  }

  useEffect(() => {
    void load();
  }, []);

  /** Runs one change against one account, then reloads so the list can't drift. */
  async function change(id: string, action: () => Promise<unknown>) {
    setBusy(id);
    setError("");
    setNotice("");
    try {
      await action();
      await load();
    } catch (failure) {
      setError(
        failure instanceof Error ? failure.message : "That change didn’t work.",
      );
    } finally {
      setBusy(null);
    }
  }

  async function add(event: FormEvent) {
    event.preventDefault();
    setBusy("new");
    setError("");
    setNotice("");
    try {
      await api<User>("/users", {
        method: "POST",
        body: JSON.stringify({
          username,
          displayName: displayName || username,
          password,
          isAdmin,
        }),
      });
      setUsername("");
      setDisplayName("");
      setPassword("");
      setIsAdmin(false);
      setAdding(false);
      setNotice(`${displayName || username} can sign in now as ${username}.`);
      await load();
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t add that person.",
      );
    } finally {
      setBusy(null);
    }
  }

  async function remove(user: ManagedUser) {
    if (
      !window.confirm(
        `Delete ${user.displayName}’s account? Their files stay on the drive.`,
      )
    )
      return;
    await change(user.id, async () => {
      const result = await api<{ filesRemainAt: string | null }>(
        `/users/${user.id}`,
        { method: "DELETE" },
      );
      setNotice(
        result.filesRemainAt
          ? `Account deleted. Their files are still at ${result.filesRemainAt}.`
          : "Account deleted. Their files weren’t touched.",
      );
    });
  }

  async function resetPassword(user: ManagedUser) {
    const next = window.prompt(
      `New password for ${user.displayName}. They’ll be signed out everywhere.`,
    );
    if (!next) return;
    await change(user.id, async () => {
      await api(`/users/${user.id}`, {
        method: "PATCH",
        body: JSON.stringify({ password: next }),
      });
      setNotice(`${user.displayName} has a new password.`);
    });
  }

  return (
    <>
      <div className="page-heading">
        <h1>People</h1>
        <div className="heading-actions">
          <button
            className="button secondary refresh-button"
            onClick={() => void load()}
            aria-label="Refresh"
            title="Refresh"
          >
            <RefreshCw size={16} />
          </button>
          <button className="button primary" onClick={startAdding}>
            <UserPlus size={17} />
            Add person
          </button>
        </div>
      </div>
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}
      {error && !adding && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      <section className="file-panel" aria-label="People">
        {users === null ? (
          <div className="file-loading" role="status">
            <LoaderCircle className="spin" size={22} />
            Loading…
          </div>
        ) : (
          <div className="table-scroll">
            <table className="people-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th className="size-cell">Storage</th>
                  <th>Role</th>
                  <th>
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {users.map((user) => (
                  <tr key={user.id} className={user.disabledAt ? "turned-off" : undefined}>
                    <td>
                      <div className="person-cell">
                        <span className="person-avatar" aria-hidden="true">
                          {(user.displayName || user.username).slice(0, 1).toUpperCase()}
                        </span>
                        <span>
                          <strong>
                            {user.displayName}
                            {user.id === me.id && <span className="you"> (you)</span>}
                          </strong>
                          <span className="muted">
                            @{user.username}
                            {user.disabledAt && " · can’t sign in"}
                          </span>
                        </span>
                      </div>
                    </td>
                    <td className="size-cell">
                      {user.usedBytes === null ? "—" : formatSize(user.usedBytes)}
                    </td>
                    <td>
                      <select
                        className="role-select"
                        aria-label={`Role for ${user.displayName}`}
                        value={user.isAdmin ? "admin" : "member"}
                        disabled={busy === user.id}
                        onChange={(event) =>
                          void change(user.id, () =>
                            api(`/users/${user.id}`, {
                              method: "PATCH",
                              body: JSON.stringify({ isAdmin: event.target.value === "admin" }),
                            }),
                          )
                        }
                      >
                        <option value="member">Member</option>
                        <option value="admin">Admin</option>
                      </select>
                    </td>
                    <td>
                      <div className="row-actions">
                        <button
                          className="row-action"
                          disabled={busy === user.id}
                          onClick={() => void resetPassword(user)}
                        >
                          New password
                        </button>
                        <button
                          className="row-action"
                          disabled={busy === user.id}
                          onClick={() =>
                            void change(user.id, () =>
                              api(`/users/${user.id}`, {
                                method: "PATCH",
                                body: JSON.stringify({ disabled: user.disabledAt === null }),
                              }),
                            )
                          }
                        >
                          {user.disabledAt ? "Allow sign-in" : "Block sign-in"}
                        </button>
                        <button
                          className="row-action danger"
                          disabled={busy === user.id}
                          onClick={() => void remove(user)}
                          aria-label={`Delete ${user.displayName}`}
                        >
                          <Trash2 size={15} />
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      <p className="field-help">Deleting an account never deletes their files.</p>

      <dialog
        ref={sheet}
        className="side-sheet"
        aria-labelledby="add-person-title"
        onClose={() => setAdding(false)}
      >
        <form className="side-sheet-body" onSubmit={add}>
          <div className="dialog-header">
            <h2 id="add-person-title">Add person</h2>
            <button
              type="button"
              className="icon-button"
              aria-label="Close"
              onClick={() => setAdding(false)}
            >
              <X size={20} />
            </button>
          </div>
          <div className="field">
            <label htmlFor="new-display-name">Name</label>
            <input
              id="new-display-name"
              className="path-input"
              value={displayName}
              onChange={(event) => {
                setDisplayName(event.target.value);
                if (!usernameTyped)
                  setUsername(event.target.value.trim().toLowerCase().replace(/[^a-z0-9]+/g, ""));
              }}
              autoComplete="off"
            />
          </div>
          <div className="field">
            <label htmlFor="new-username">Username</label>
            <input
              id="new-username"
              className="path-input"
              value={username}
              onChange={(event) => {
                setUsername(event.target.value);
                setUsernameTyped(true);
              }}
              autoCapitalize="none"
              spellCheck={false}
              autoComplete="off"
              required
              aria-describedby="new-username-help"
            />
            <p id="new-username-help" className="field-help">
              Used to sign in
            </p>
          </div>
          <div className="field">
            <label htmlFor="new-password">Password</label>
            <div className="field-row">
              <input
                id="new-password"
                className="path-input mono"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                autoComplete="off"
                spellCheck={false}
                required
                aria-describedby="new-password-help"
              />
              <button
                type="button"
                className="button secondary square"
                aria-label="Suggest another password"
                title="Suggest another"
                onClick={() => setPassword(suggestPassword())}
              >
                <RefreshCw size={18} />
              </button>
              <button
                type="button"
                className="button secondary square"
                aria-label="Copy password"
                title="Copy"
                onClick={() => void copyPassword()}
              >
                {copied ? <Check size={18} /> : <Copy size={18} />}
              </button>
            </div>
            <p id="new-password-help" className="field-help">
              At least 10 characters. Share it in person; they can change it later.
            </p>
          </div>
          <fieldset className="role-choices">
            <legend>Role</legend>
            <label className={`role-choice${!isAdmin ? " chosen" : ""}`}>
              <input type="radio" name="new-role" checked={!isAdmin} onChange={() => setIsAdmin(false)} />
              <span>
                <strong>Member</strong>
                <span className="muted">Sees only their own files</span>
              </span>
            </label>
            <label className={`role-choice${isAdmin ? " chosen" : ""}`}>
              <input type="radio" name="new-role" checked={isAdmin} onChange={() => setIsAdmin(true)} />
              <span>
                <strong>Admin</strong>
                <span className="muted">Can also add people and change settings</span>
              </span>
            </label>
          </fieldset>
          {error && (
            <p className="error-message" role="alert">
              {error}
            </p>
          )}
          <div className="side-sheet-actions">
            <button type="button" className="button secondary" onClick={() => setAdding(false)}>
              Cancel
            </button>
            <button className="button primary" disabled={busy === "new" || !username.trim() || !password}>
              {busy === "new" ? <LoaderCircle size={16} className="spin" /> : <UserPlus size={16} />}
              {displayName.trim() ? `Add ${displayName.trim()}` : "Add person"}
            </button>
          </div>
        </form>
      </dialog>
    </>
  );
}
