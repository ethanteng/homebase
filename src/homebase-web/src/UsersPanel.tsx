import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  LoaderCircle,
  RefreshCw,
  ShieldCheck,
  Trash2,
  UserPlus,
  Users,
} from "lucide-react";
import { api, formatSize } from "./api";
import type { ManagedUser, User } from "./api";

interface Props {
  me: User;
}

/** Who has an account on this host. Only an administrator ever sees this. */
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

  async function load() {
    try {
      setUsers(await api<ManagedUser[]>("/users"));
      setError("");
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t read the accounts on this Uncloud.",
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
      setNotice(`${username} has an account and a folder of their own.`);
      await load();
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t add that account.",
      );
    } finally {
      setBusy(null);
    }
  }

  async function remove(user: ManagedUser) {
    if (
      !window.confirm(
        `Delete ${user.displayName}’s account? Their files stay on the drive — ` +
          `only the account and its connections go.`,
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
          : "Account deleted. Their files were left where they were.",
      );
    });
  }

  async function resetPassword(user: ManagedUser) {
    const next = window.prompt(
      `A new password for ${user.displayName}. They’ll be signed out everywhere.`,
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
        <div>
          <span className="eyebrow">THIS UNCLOUD</span>
          <h1>
            Who lives here<span className="heading-dot">.</span>
          </h1>
          <p>
            Everyone gets a folder of their own and their own connected
            accounts. Nobody can see anybody else’s.
          </p>
        </div>
        <span className="welcome-icon">
          <Users size={24} strokeWidth={1.4} />
        </span>
      </div>

      <section className="import-section">
        <div className="import-section-head">
          <h2>Accounts</h2>
          <div className="empty-actions">
            <button className="refresh-button" onClick={() => void load()}>
              <RefreshCw size={15} />
              Refresh
            </button>
            <button
              className="button primary"
              onClick={() => setAdding((open) => !open)}
            >
              <UserPlus size={16} />
              {adding ? "Never mind" : "Add someone"}
            </button>
          </div>
        </div>

        {adding && (
          <form className="setup-form compact" onSubmit={add}>
            <label htmlFor="new-username">Username</label>
            <input
              id="new-username"
              className="path-input"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              autoCapitalize="none"
              spellCheck={false}
              required
            />
            <label htmlFor="new-display-name">Their name</label>
            <input
              id="new-display-name"
              className="path-input"
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              placeholder={username || "Optional"}
            />
            <label htmlFor="new-password">First password</label>
            <input
              id="new-password"
              className="path-input"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoComplete="new-password"
              required
            />
            <p className="field-help">
              At least 10 characters. Hand it over in person and let them change
              it — Uncloud has no way to e-mail anybody a reset.
            </p>
            <label className="private-label">
              <input
                type="checkbox"
                checked={isAdmin}
                onChange={(event) => setIsAdmin(event.target.checked)}
              />
              Let them look after this Uncloud too
            </label>
            <button className="button primary" disabled={busy === "new"}>
              {busy === "new" ? (
                <LoaderCircle size={16} className="spin" />
              ) : (
                <UserPlus size={16} />
              )}
              Add this account
            </button>
          </form>
        )}

        {error && (
          <p className="error-message" role="alert">
            {error}
          </p>
        )}
        {notice && <p className="library-note">{notice}</p>}

        {users === null ? (
          <div className="file-loading" role="status">
            <LoaderCircle className="spin" size={22} />
            Reading the accounts…
          </div>
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Account</th>
                  <th className="size-cell">Using</th>
                  <th className="action-cell">Looks after this Uncloud</th>
                  <th className="action-cell" />
                </tr>
              </thead>
              <tbody>
                {users.map((user) => (
                  <tr key={user.id}>
                    <td className="file-name">
                      <strong>{user.displayName}</strong>
                      <span className="muted"> @{user.username}</span>
                      {user.id === me.id && (
                        <span className="muted"> — that’s you</span>
                      )}
                      {user.disabledAt && (
                        <span className="muted"> — signed out for good</span>
                      )}
                    </td>
                    <td className="size-cell">
                      {user.usedBytes === null
                        ? "—"
                        : formatSize(user.usedBytes)}
                    </td>
                    <td className="action-cell">
                      <button
                        className="refresh-button"
                        disabled={busy === user.id}
                        onClick={() =>
                          void change(user.id, () =>
                            api(`/users/${user.id}`, {
                              method: "PATCH",
                              body: JSON.stringify({ isAdmin: !user.isAdmin }),
                            }),
                          )
                        }
                      >
                        <ShieldCheck size={15} />
                        {user.isAdmin ? "Yes" : "No"}
                      </button>
                    </td>
                    <td className="action-cell">
                      <button
                        className="refresh-button"
                        disabled={busy === user.id}
                        onClick={() => void resetPassword(user)}
                      >
                        New password
                      </button>
                      <button
                        className="refresh-button"
                        disabled={busy === user.id}
                        onClick={() =>
                          void change(user.id, () =>
                            api(`/users/${user.id}`, {
                              method: "PATCH",
                              body: JSON.stringify({
                                disabled: user.disabledAt === null,
                              }),
                            }),
                          )
                        }
                      >
                        {user.disabledAt ? "Let back in" : "Sign out for good"}
                      </button>
                      <button
                        className="refresh-button"
                        disabled={busy === user.id}
                        onClick={() => void remove(user)}
                      >
                        <Trash2 size={15} />
                        Delete
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="field-help">
          Deleting an account never deletes anybody’s files. Everyone here draws
          on the same drive, so one person filling it fills it for everyone.
        </p>
      </section>
    </>
  );
}
