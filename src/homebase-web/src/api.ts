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
/// What a folder holds, however deep: its bytes and how many files.
export interface FolderSize {
  bytes: number;
  files: number;
}

export interface DirectoryListing {
  path: string;
  entries: LibraryEntry[];
  skippedCount: number;
  indexedAt: string;
}

/// Something deleted from My files, which can be put back until it expires.
export interface BinEntry {
  id: string;
  name: string;
  // Where it was, which is where putting it back returns it.
  path: string;
  isDirectory: boolean;
  size: number;
  deletedAt: string;
  expiresAt: string;
}
export interface BinListing {
  entries: BinEntry[];
  bytes: number;
}

/// What can be done in My files. Making a new folder acts on no entries, only on where it goes.
export type EditAction = "copy" | "move" | "rename" | "delete" | "folder";
/// Where one copied or moved item ended up: a different name when its own was taken there.
export interface EditedEntry {
  from: string;
  to: string;
}
/// What of an upload is already where it's going, each file by its path in the upload. A file not
/// named in any of these is new there.
export interface UploadComparison {
  /// Already there, the same size and last changed at the same time: no need to send it.
  same: string[];
  /// Already there, but this one is different: sending it replaces that one, which goes to the bin.
  changed: string[];
  /// Already there, and changed there since this one was: sending it would undo that.
  newer: string[];
  /// Folders at the top of the upload that are already there, for the new files to join.
  folders: string[];
}

export interface StorageReport {
  // The volume is shared by everyone on this host; the usage is the signed-in account's own.
  // With an allotment set, free and total are Uncloud's share of the volume rather than all of it.
  freeBytes: number | null;
  totalBytes: number | null;
  usedBytes: number;
  // The allotment, rather than the drive, is what is running out.
  capped?: boolean;
}

/// How much of the drive Uncloud may fill. A percent is of what's free plus what Uncloud holds.
export interface Allotment {
  unit: "Bytes" | "Percent";
  amount: number;
}

export interface HostStorage {
  // Null when Uncloud may fill the whole drive.
  allotment: Allotment | null;
  driveFreeBytes: number | null;
  driveTotalBytes: number | null;
  // Everybody's files together, which is what an allotment is measured against.
  uncloudBytes: number;
  // What everybody is shown and every write is checked against.
  freeBytes: number | null;
  totalBytes: number | null;
  capped: boolean;
}

/// Something that could stop the host staying on for everybody, as the host Mac's own settings say.
export interface PowerFinding {
  code: string;
  title: string;
  message: string;
  // Whether Keep This Mac Awake, in the Uncloud menu on the host, puts it right.
  inMenu: boolean;
}

export interface HostPower {
  // False wherever there's nothing to read: a host that isn't a Mac.
  checked: boolean;
  findings: PowerFinding[];
}

/// How this host stands with Uncloud Backup. Never any of its secrets.
export interface BackupAccount {
  joined: boolean;
  service: string | null;
  household: string | null;
  host: string | null;
  hostName: string | null;
  joinedAt: string | null;
  // Until the household's storage is ready, how setting it up stands.
  storage: "ready" | "setting_up" | "failing" | null;
  step: string | null;
  bucket: string | null;
  // When the household's recovery kit in use was made, or null if none has been.
  recoveryKitMadeAt: string | null;
  // A host that opened a lost one's backup doesn't add to it until it's resumed.
  paused: boolean;
}

/// What gets a household's backup back when its host is gone. Shown once each time one is made.
export interface BackupKit {
  password: string;
  recovery: string;
  household: string;
  madeAt: string;
}

/// Joining's answer: the password the backup is encrypted with, the one time it's ever sent, in a
/// kit with the recovery token if one could be made.
export interface BackupJoined {
  password: string;
  kit: BackupKit | null;
  account: BackupAccount;
}

export type BackupOutcome = "Succeeded" | "Partial" | "Refused" | "TimedOut" | "Failed";

/// How the host's backups stand: the last run, and the last one that worked.
export interface BackupStatus {
  configured: boolean;
  running: boolean;
  lastStarted: string | null;
  lastFinished: string | null;
  lastOutcome: BackupOutcome | null;
  message: string | null;
  lastSucceeded: string | null;
  snapshot: string | null;
  filesNew: number | null;
  filesChanged: number | null;
  bytesAdded: number | null;
  totalFiles: number | null;
  totalBytes: number | null;
  lastPruned: string | null;
}

/// How the last check of the backup went: restored somewhere of its own and compared with what's
/// here. Counts only, never names.
export interface BackupCheck {
  running: boolean;
  started: string | null;
  finished: string | null;
  // Null when there was nothing to check.
  passed: boolean | null;
  message: string | null;
  snapshot: string | null;
  snapshotTime: string | null;
  filesInBackup: number | null;
  bytesInBackup: number | null;
  files: number | null;
  bytes: number | null;
  sampled: boolean;
  matched: number | null;
  changedSince: number | null;
  goneSince: number | null;
  mismatched: number | null;
  missing: number | null;
  hostKeyMatched: boolean | null;
}

/// Rebuilding this host from one of its backups, and, on a host that was, which backup it came from.
export interface BackupRebuild {
  running: boolean;
  // restarting: everything is ready, and Uncloud is starting again as the host in the backup.
  stage: "restoring" | "preparing" | "restarting" | null;
  started: string | null;
  snapshot: string | null;
  snapshotTime: string | null;
  files: number | null;
  bytes: number | null;
  filesRestored: number | null;
  bytesRestored: number | null;
  // Why the last rebuild stopped short. Nothing on the host had changed.
  message: string | null;
  rebuiltAt: string | null;
  rebuiltFrom: string | null;
}

/// The backups kept, oldest first.
export interface BackupSnapshots {
  snapshots: { id: string; time: string }[];
}

/// This Uncloud's backups, newest first, for looking in for one's own files.
export interface BackupList {
  configured: boolean;
  backups: { id: string; time: string }[];
}

/// One folder of one's own files, as a backup holds it.
export interface BackupFolderListing {
  snapshot: string;
  time: string;
  path: string;
  entries: {
    name: string;
    path: string;
    isDirectory: boolean;
    size: number | null;
    modifiedAt: string;
  }[];
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
  // False when signing in can't finish by itself from this browser. Connecting still works: the
  // person copies a code from Dropbox instead of being handed back automatically.
  oneClickHere: boolean;
  destination: string;
}

export interface ImportSources {
  accounts: ImportAccount[];
  places: ImportPlace[];
  suggestions: SuggestedPlace[];
  // Only somebody who looks after this Uncloud can read the computer it runs on.
  canAddFolders: boolean;
  canPickFolder: boolean;
  // Whether this page is open on the computer Uncloud runs on, where "This device" already is it.
  atTheComputer?: boolean;
  // Why the folders on the computer Uncloud runs on can't be read just now — on a Mac where it runs
  // in an account of its own, because the Uncloud app isn't open there — or null when they can.
  computerUnavailable?: string | null;
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
  // Passed over because they were already here: counted rather than listed.
  alreadyHere: number;
  importedCount: number;
  skippedCount: number;
}

export type ImportStage = "Measuring" | "Bringing" | "Done" | "Stopped" | "Failed";

export interface ImportJob {
  id: string;
  // Which place it is coming from, so a reload shows the right one.
  sourceId: string;
  // What was chosen: one file or folder, or several beside each other.
  remotePaths: string[];
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
  // Files from your computers that can't arrive until this Uncloud has room.
  waitingForRoom?: number;
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
/// A computer asking to be added, as the person approving it sees it.
export interface PairingAsk {
  name: string;
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

/** Several names the way somebody would say them: "Art and Chime", "Art, Chime and 3 more". */
export function nameTogether(names: string[]): string {
  if (names.length <= 1) return names[0] ?? "";
  if (names.length <= 3) return `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
  return `${names.slice(0, 2).join(", ")} and ${names.length - 2} more`;
}

export function downloadUrl(path: string): string {
  return `/api/files/download?${new URLSearchParams({ path })}`;
}
