export interface LibraryState {
  rootPath: string | null;
  name: string | null;
  canPickFolder: boolean;
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

export interface DropboxStatus {
  configured: boolean;
  connected: boolean;
  accountName: string | null;
}
export interface DropboxEntry {
  id: string;
  name: string;
  pathLower: string;
  pathDisplay: string;
  isFolder: boolean;
  size: number | null;
  rev: string | null;
  serverModified: string | null;
}
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
}
export interface ImportResult {
  imported: ImportedItem[];
  skipped: SkippedItem[];
  bytes: number;
  importedCount: number;
  skippedCount: number;
}

export interface NodeDevice {
  deviceId: string;
  name: string;
  connected: boolean;
  address: string | null;
}
export interface SharedFolder {
  id: string;
  label: string;
  localPath: string;
  deviceIds: string[];
  state: string | null;
  files: number;
  bytes: number;
}
export interface NodeStatus {
  available: boolean;
  detail: string | null;
  deviceId: string | null;
  devices: NodeDevice[];
  folders: SharedFolder[];
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
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
    throw new Error(
      problem?.detail ??
        `Homebase couldn’t complete this request (${response.status}).`,
    );
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
