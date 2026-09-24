import { useCallback, useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Check,
  FolderOpen,
  FolderPlus,
  LoaderCircle,
  Plus,
  Trash2,
  TriangleAlert,
} from "lucide-react";
import { api } from "./api";
import type { DropboxAppSettings, HostPlaces } from "./api";
import DropboxAppSteps, { AppKeyReassurance } from "./DropboxAppForm";

interface Props {
  /** Called whenever what a person can bring files in from changes, so the panel keeps up. */
  onChanged: () => void;
}

/**
 * How this Uncloud brings files in, for whoever looks after it: which folders on this computer
 * everybody may copy from, and which Dropbox app the household's connections are made through.
 *
 * Adding a place shares it with every account here, which is why only an administrator sees this.
 */
export default function ImportSettings({ onChanged }: Props) {
  const [places, setPlaces] = useState<HostPlaces | null>(null);
  const [dropbox, setDropbox] = useState<DropboxAppSettings | null>(null);
  const [path, setPath] = useState("");
  const [name, setName] = useState("");
  const [appKey, setAppKey] = useState("");
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const loadPlaces = useCallback(async () => {
    try {
      setPlaces(await api<HostPlaces>("/host/places"));
    } catch (failure) {
      setError(
        failure instanceof Error
          ? failure.message
          : "Couldn’t read the list of places.",
      );
    }
  }, []);

  const loadDropbox = useCallback(async () => {
    try {
      const settings = await api<DropboxAppSettings>("/host/dropbox");
      setDropbox(settings);
      setAppKey(settings.appKey ?? "");
    } catch {
      // The places above are still worth showing without this.
    }
  }, []);

  useEffect(() => {
    void loadPlaces();
    void loadDropbox();
  }, [loadPlaces, loadDropbox]);

  async function run(label: string, action: () => Promise<void>) {
    setBusy(label);
    setError("");
    try {
      await action();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "That didn’t work.");
    } finally {
      setBusy("");
    }
  }

  const pickFolder = () =>
    run("picker", async () => {
      const result = await api<{ path: string | null }>("/folder-picker", {
        method: "POST",
      });
      if (result.path) setPath(result.path);
    });

  const add = (folder: string, label: string) =>
    run(`add:${folder}`, async () => {
      setNotice("");
      await api("/host/places", {
        method: "POST",
        body: JSON.stringify({ path: folder, name: label }),
      });
      setPath("");
      setName("");
      setNotice(`Everyone here can now bring files in from ${label}.`);
      await loadPlaces();
      onChanged();
    });

  const remove = (id: string, label: string) =>
    run(`remove:${id}`, async () => {
      setNotice("");
      await api(`/host/places/${encodeURIComponent(id)}`, { method: "DELETE" });
      setNotice(
        `${label} is no longer somewhere to bring files in from. Anything already brought home stays where it is.`,
      );
      await loadPlaces();
      onChanged();
    });

  function addTyped(event: FormEvent) {
    event.preventDefault();
    const folder = path.trim();
    if (!folder) return;
    const label = name.trim() || folder.split("/").filter(Boolean).pop() || folder;
    void add(folder, label);
  }

  const saveAppKey = () =>
    run("app-key", async () => {
      setNotice("");
      const result = await api<{ configured: boolean; disconnected: number }>(
        "/host/dropbox",
        { method: "PUT", body: JSON.stringify({ appKey }) },
      );
      setNotice(
        !result.configured
          ? dropbox?.relayProvides
            ? "Dropbox app key removed. Anyone here who wasn’t using their own app key goes back to Uncloud’s own app, so nobody is left unable to connect."
            : "Dropbox app key removed. Anyone here who wasn’t using their own app key will need to add one under My account before they can connect Dropbox."
          : result.disconnected > 0
            ? `Dropbox app key saved. ${result.disconnected} Dropbox connection${result.disconnected === 1 ? " that used this key was" : "s that used this key were"} signed out, because they were authorised through the old app — those people can connect again from Bring files in. Anyone using their own app key was left alone.`
            : "Dropbox app key saved. Everyone here can now connect their own Dropbox in one click.",
      );
      await loadDropbox();
      onChanged();
    });

  // Nothing here is a step anybody has to take when Uncloud brings its own Dropbox app and nobody
  // has set a key — so it folds away. A key already set, one coming from the environment, or no
  // app of Uncloud's own, and it is a real setting again rather than a preference.
  const hostKeyIsAdvanced =
    dropbox !== null &&
    dropbox.relayProvides &&
    !dropbox.appKey &&
    !dropbox.fromEnvironment;

  const dropboxApp = dropbox && (
    <>
      <p className="field-help">
        {dropbox.relayProvides
          ? "Uncloud brings its own Dropbox app, so everybody here can already connect their Dropbox in one click. Set a key here only if you’d rather the household’s Dropbox sign-ins went through an app you registered."
          : "Only needed to connect Dropbox accounts online, and only a convenience: set a key here and everybody on this Uncloud can connect their own Dropbox in one click."}{" "}
        Anybody who would rather not wait for you, or would rather use their own
        Dropbox app, sets one under <strong>My account</strong> instead — theirs
        wins over this one, and changing this never touches them.
      </p>

      {dropbox.fromEnvironment && (
        <p className="library-note" role="status">
          This Uncloud is using the app key from{" "}
          <code>Homebase__Dropbox__AppKey</code>. Saving a key here replaces it.
        </p>
      )}

      <DropboxAppSteps
        redirectUri={dropbox.redirectUri}
        scopes={dropbox.scopes}
        idPrefix="host-dropbox"
      />

      <label htmlFor="dropbox-app-key">App key</label>
      <input
        id="dropbox-app-key"
        className="path-input"
        value={appKey}
        onChange={(event) => setAppKey(event.target.value)}
        placeholder="the app key from that page"
        autoComplete="off"
        spellCheck={false}
        disabled={busy !== ""}
        aria-describedby="app-key-help"
      />
      <p id="app-key-help" className="field-help">
        <AppKeyReassurance /> Changing this key signs out the Dropbox connections
        that were made through it, because each was authorised through the old
        app — anybody using their own key is left alone.
      </p>
      <button
        className="button primary"
        onClick={() => void saveAppKey()}
        disabled={busy !== "" || appKey.trim() === (dropbox.appKey ?? "")}
      >
        {busy === "app-key" ? (
          <LoaderCircle size={16} className="spin" />
        ) : (
          <Check size={16} />
        )}
        {/* Emptying the box is how a key is taken away, so say so — but only when there is one
            to take away, or an untouched box reads as an offer to break something. */}
        {appKey.trim() === "" && dropbox.appKey
          ? "Remove the app key"
          : "Save the app key"}
      </button>
    </>
  );

  return (
    <div className="import-settings">
      {error && (
        <p className="error-message" role="alert">
          {error}
        </p>
      )}
      {notice && (
        <p className="library-note" role="status">
          {notice}
        </p>
      )}

      <section className="import-section">
        <div className="import-section-head">
          <h3>Folders on this computer</h3>
        </div>
        <p className="field-help">
          The simplest way in, and nothing to sign in to: if a Dropbox or Google
          Drive app already syncs a folder onto this computer, point Uncloud at
          that folder. Everyone with an account here can copy from what you add,
          so add folders you’re happy for them all to read.
        </p>

        {places?.places.length ? (
          <ul className="import-list">
            {places.places.map((place) => (
              <li key={place.id}>
                {place.available ? (
                  <FolderOpen size={19} strokeWidth={1.6} />
                ) : (
                  <TriangleAlert size={19} strokeWidth={1.6} />
                )}
                <div>
                  <strong>{place.name}</strong>
                  <span className="muted">
                    {place.path}
                    {place.available ? "" : " · not on this computer right now"}
                    {" · arrives in "}
                    <code>{place.destinationPrefix}</code>
                  </span>
                </div>
                <button
                  className="button"
                  onClick={() => void remove(place.id, place.name)}
                  disabled={busy !== ""}
                >
                  <Trash2 size={15} />
                  {busy === `remove:${place.id}` ? "Removing…" : "Remove"}
                </button>
              </li>
            ))}
          </ul>
        ) : (
          <p className="field-help">Nothing shared yet.</p>
        )}

        {places?.suggestions.length ? (
          <>
            <span className="step-label">FOUND ON THIS COMPUTER</span>
            <ul className="import-list suggestions">
              {places.suggestions.map((suggestion) => (
                <li key={suggestion.path}>
                  <FolderPlus size={19} strokeWidth={1.6} />
                  <div>
                    <strong>{suggestion.name}</strong>
                    <span className="muted">{suggestion.path}</span>
                  </div>
                  <button
                    className="button primary"
                    onClick={() => void add(suggestion.path, suggestion.name)}
                    disabled={busy !== ""}
                  >
                    <Plus size={15} />
                    {busy === `add:${suggestion.path}` ? "Adding…" : "Add"}
                  </button>
                </li>
              ))}
            </ul>
          </>
        ) : null}

        <form className="setup-form inline-form" onSubmit={addTyped}>
          <span className="step-label">OR ADD ANOTHER FOLDER</span>
          {places?.canPickFolder && (
            <button
              type="button"
              className="button secondary picker-button"
              onClick={() => void pickFolder()}
              disabled={busy !== ""}
            >
              {busy === "picker" ? (
                <LoaderCircle className="spin" size={18} />
              ) : (
                <FolderOpen size={18} />
              )}
              {busy === "picker"
                ? "Check the folder chooser on the host…"
                : "Choose in Finder"}
            </button>
          )}
          <label htmlFor="place-path">Folder path</label>
          <input
            id="place-path"
            className="path-input"
            value={path}
            onChange={(event) => setPath(event.target.value)}
            placeholder="/Users/you/Dropbox"
            autoComplete="off"
            spellCheck={false}
            disabled={busy !== ""}
          />
          <label htmlFor="place-name">What to call it</label>
          <input
            id="place-name"
            className="path-input"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Dropbox"
            autoComplete="off"
            disabled={busy !== ""}
            aria-describedby="place-name-help"
          />
          <p id="place-name-help" className="field-help">
            Files brought in from here land in <code>Files/</code> under this
            name, in each person’s own folder.
          </p>
          <button className="button primary" disabled={busy !== "" || !path.trim()}>
            <Plus size={16} />
            Add this folder
          </button>
        </form>
      </section>

      {/* Registering a Dropbox app is an advanced setting wherever Uncloud brings its own: the
          household can already connect Dropbox in one click, so this is a preference about whose
          app the sign-ins go through and not a step anybody has to take. It opens out in full the
          moment there is a key here to see, because a setting somebody has already used must not
          become one they cannot find. */}
      {dropbox &&
        (hostKeyIsAdvanced ? (
          <details className="import-section advanced-section">
            <summary>
              <span className="advanced-lead">Advanced</span>
              Use a Dropbox app you registered
            </summary>
            {dropboxApp}
          </details>
        ) : (
          <section className="import-section">
            <div className="import-section-head">
              <h3>Dropbox app for everyone here</h3>
            </div>
            {dropboxApp}
          </section>
        ))}
    </div>
  );
}
