import { useEffect, useState } from "react";
import { api } from "./api";
import type { FolderSize } from "./api";

/** A folder's size once measured, null if it couldn't be, and absent while it still is being. */
export type FolderSizes = Record<string, FolderSize | null>;

/**
 * The sizes of the folders on screen, measured without anybody asking. A folder's size means going
 * through everything under it, so the listing never waits for them: each fills in as it arrives,
 * a few folders at a time so a large one doesn't hold up the rest, and leaving the folder stops
 * whatever is still being measured.
 */
export function useFolderSizes(
  folders: string[],
  address: (folder: string) => string,
  together = 4,
): FolderSizes {
  const [sizes, setSizes] = useState<FolderSizes>({});
  // What is asked for, and where, is the whole of what the measuring depends on.
  const wanted = JSON.stringify(folders.map((folder) => [folder, address(folder)]));

  useEffect(() => {
    const queue = JSON.parse(wanted) as [string, string][];
    setSizes({});
    if (queue.length === 0) return;
    const controller = new AbortController();
    const measureNext = async (): Promise<void> => {
      const next = queue.shift();
      if (!next || controller.signal.aborted) return;
      const [folder, url] = next;
      let size: FolderSize | null = null;
      try {
        size = await api<FolderSize>(url, { signal: controller.signal });
      } catch {
        if (controller.signal.aborted) return;
      }
      setSizes((current) => ({ ...current, [folder]: size }));
      return measureNext();
    };
    for (let lane = 0; lane < together; lane++) void measureNext();
    return () => controller.abort();
  }, [wanted, together]);

  return sizes;
}
