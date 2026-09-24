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
  status:
    | "off"
    | "opening"
    | "needs_sign_in"
    | "needs_funnel"
    | "on"
    | "reconnecting";
  detail: string | null;
  // Where somebody has to go, once, for whichever of the two yeses is being waited on: signing
  // in says this host is theirs, and allowing Funnel says the tailnet may be reached from outside.
  signInUrl: string | null;
  // Where Dropbox is told to return the browser, which the tunnel usually decides.
  publicUrl: string;
  // False when a provider was named before launch, which this panel reports rather than changes.
  canChange: boolean;
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

/// What a folder on this computer looks like to a person, which decides its icon.
export type PlaceKind = "folder" | "cloud" | "drive";

/// A folder on the host's computer somebody brings files in from. It is its owner's alone unless
/// they have shared it with everyone here.
export interface ImportPlace {
  id: string;
  name: string;
  // Only its owner is told where it sits on the host's disk.
  path: string | null;
  kind: PlaceKind;
  available: boolean;
  mine: boolean;
  shared: boolean;
  // Who shared it, when it isn't this account's own.
  sharedBy: string | null;
  // The folder at the top of My files its files arrive in, such as Documents.
  destination: string;
}

/// A folder on this computer worth offering, which becomes this account's own once used.
export interface SuggestedPlace {
  name: string;
  path: string;
  kind: PlaceKind;
}

/// An online account somebody can connect and bring files in from. Dropbox is the first.
export interface ImportAccount {
  id: string;
  name: string;
  configured: boolean;
  connected: boolean;
  accountName: string | null;
  // False when signing in can't finish from this browser, and a setup of one's own is needed.
  connectableHere: boolean;
  destination: string;
}

export interface ImportSources {
  accounts: ImportAccount[];
  places: ImportPlace[];
  suggestions: SuggestedPlace[];
  // Only somebody who looks after this Uncloud can read the computer it runs on.
  canAddFolders: boolean;
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
  // Whether Uncloud's own app could finish a sign-in for this browser. False when Uncloud is being
  // used from another computer, where only a key of one's own works.
  relayReachable: boolean;
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
