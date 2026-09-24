export interface LibraryState {
  rootPath: string | null;
  name: string | null;
  // Only an administrator is told where the host keeps everybody's folders.
  hostRoot: string | null;
  canPickFolder: boolean;
}

export interface User {
  id: string;
  username: string;
  displayName: string;
  isAdmin: boolean;
  createdAt: string;
  disabledAt: string | null;
}

export interface Session {
  setupNeeded: boolean;
  hostConfigured: boolean;
  user: User | null;
}

export interface ManagedUser extends User {
  usedBytes: number | null;
}

export interface HostState {
  rootPath: string | null;
  canPickFolder: boolean;
}

export interface RemoteAccessState {
  provider: string;
  // The name the tunnel is carrying right now, absent while there isn't one.
  hostname: string | null;
  url: string | null;
  status: "off" | "opening" | "needs_sign_in" | "on" | "reconnecting";
  detail: string | null;
  // Where somebody has to go, once, to allow this host onto the internet.
  signInUrl: string | null;
  // Where Dropbox is told to return the browser, which the tunnel usually decides.
  publicUrl: string;
}

export interface ClaimResult {
  moved: string[];
  skipped: { name: string; reason: string }[];
}
export interface LibraryEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number | null;
  modifiedAt: string;
}
export interface DirectoryListing {
  path: string;
  entries: LibraryEntry[];
  skippedCount: number;
  indexedAt: string;
}

export interface StorageReport {
  // The volume is shared by everyone on this host; the usage is the signed-in account's own.
  freeBytes: number | null;
  totalBytes: number | null;
  usedBytes: number;
}

export interface ImportEstimate {
  fileCount: number;
  bytes: number;
  newFileCount: number;
  newBytes: number;
  freeBytes: number | null;
  fits: boolean;
}

export interface DropboxStatus {
  configured: boolean;
  connected: boolean;
  accountName: string | null;
  // Only an administrator can add the host's Dropbox app key, so only they are shown how.
  canConfigure: boolean;
}

/// One file or folder as the place it came from describes it. The same shape whether it came from
/// a Dropbox account or a folder on the host's own disk.
export interface SourceEntry {
  id: string;
  name: string;
  path: string;
  displayPath: string;
  isFolder: boolean;
  size: number | null;
  rev: string | null;
  modified: string | null;
}

/// A folder on the host's computer that an administrator has shared with everyone here.
export interface ImportPlace {
  id: string;
  name: string;
  path: string;
  available: boolean;
}

export interface ImportSources {
  places: ImportPlace[];
  dropbox: {
    configured: boolean;
    connected: boolean;
    accountName: string | null;
  };
}

export interface ManagedPlace extends ImportPlace {
  addedAt: string;
  destinationPrefix: string;
}

export interface SuggestedPlace {
  name: string;
  path: string;
}

export interface HostPlaces {
  places: ManagedPlace[];
  suggestions: SuggestedPlace[];
  canPickFolder: boolean;
}

export interface DropboxAppSettings {
  appKey: string | null;
  configured: boolean;
  // A key supplied by Homebase__Dropbox__AppKey rather than typed in here.
  fromEnvironment: boolean;
  // Whether this build brings its own Dropbox app, which makes a key here optional.
  relayProvides: boolean;
  redirectUri: string;
  scopes: string[];
}

/// Where the app key an account connects through came from.
export type DropboxKeySource =
  | "None"
  | "Own"
  | "Host"
  | "Environment"
  // Uncloud’s own Dropbox app, which is there without anybody setting anything up.
  | "Relay";

export interface MyDropboxApp {
  // This account's own key, as opposed to whatever it falls back to.
  appKey: string | null;
  configured: boolean;
  source: DropboxKeySource;
  // What leaving the box empty falls back to. Kept apart because they read differently: an
  // administrator here chose the host's key, and nobody chose Uncloud's own.
  hostProvides: boolean;
  relayProvides: boolean;
  redirectUri: string;
  scopes: string[];
}

/// Dropbox is always reachable by this name; a folder on this computer is named by its id.
export const DROPBOX = "dropbox";
export interface ImportedFile {
  provider: string;
  remotePath: string;
  remoteRev: string;
  localPath: string;
  size: number;
  contentHash: string;
  importedAt: string;
}
export interface ImportedItem {
  localPath: string;
  remotePath: string;
  size: number;
}
export interface SkippedItem {
  remotePath: string;
  reason: string;
  // A file already home is an ordinary outcome, not a problem to raise.
  expected: boolean;
}
export interface ImportResult {
  imported: ImportedItem[];
  skipped: SkippedItem[];
  bytes: number;
  importedCount: number;
  skippedCount: number;
}

export type ImportStage = "Measuring" | "Bringing" | "Done" | "Stopped" | "Failed";

export interface ImportJob {
  id: string;
  // Which place it is coming from, so a reload shows the right one.
  sourceId: string;
  remotePath: string;
  label: string;
  stage: ImportStage;
  totalFiles: number;
  completedFiles: number;
  bytes: number;
  currentFile: string | null;
  result: ImportResult | null;
  error: string | null;
  startedAt: string;
  finishedAt: string | null;
  running: boolean;
}

export interface SyncDevice {
  deviceId: string;
  name: string;
  connected: boolean;
  address: string | null;
}
export interface SyncFolder {
  id: string;
  path: string;
  label: string;
  deviceIds: string[];
  state: string | null;
  error: string | null;
  files: number;
  bytes: number;
}
export interface SyncOffer {
  folderId: string;
  label: string;
  deviceId: string;
  deviceName: string;
}
export interface PairingCode {
  code: string;
  expiresAt: string;
}
export interface SyncStatus {
  available: boolean;
  detail: string | null;
  hostDeviceId: string | null;
  devices: SyncDevice[];
  folders: SyncFolder[];
  offers: SyncOffer[];
}

/// Thrown when a session has ended, so the app can ask for a sign-in rather than showing an
/// error nobody can act on.
export class SignedOutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SignedOutError";
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    // The session cookie is the credential; nothing here reads or writes it.
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      "X-Homebase-Request": "1",
      ...init?.headers,
    },
  });
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as {
      detail?: string;
    } | null;
    const detail =
      problem?.detail ??
      `Uncloud couldn’t complete this request (${response.status}).`;
    if (response.status === 401) throw new SignedOutError(detail);
    throw new Error(detail);
  }
  return response.json() as Promise<T>;
}

export function formatSize(size: number | null): string {
  if (size === null) return "—";
  if (size < 1024) return `${size} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = size / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value)} ${units[unit]}`;
}

export function downloadUrl(path: string): string {
  return `/api/files/download?${new URLSearchParams({ path })}`;
}
