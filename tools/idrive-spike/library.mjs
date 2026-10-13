// A made-up household library to back up, and a way to tell whether what came back is the same.

import { createHash, randomBytes, randomInt } from "node:crypto";
import {
  appendFileSync,
  closeSync,
  createReadStream,
  mkdirSync,
  openSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
  writeSync,
} from "node:fs";
import { join, relative, sep } from "node:path";

export const MB = 1024 * 1024;

const WORDS = ["household", "invoice", "holiday", "receipt", "school", "garden", "recipe", "insurance",
  "photo", "letter", "tax", "warranty", "notes", "plan", "budget", "travel"];

/** Text that compresses, like most documents. */
function prose(bytes) {
  let text = "";
  while (text.length < bytes) text += `${WORDS[randomInt(WORDS.length)]} ${randomInt(10000)} `;
  return text.slice(0, bytes);
}

/** Bytes that don't compress, like photos and video, written a megabyte at a time. */
function writeRandom(path, bytes) {
  const fd = openSync(path, "w");
  try {
    for (let left = bytes; left > 0;) {
      const chunk = randomBytes(Math.min(left, MB));
      writeSync(fd, chunk);
      left -= chunk.length;
    }
  } finally {
    closeSync(fd);
  }
}

/**
 * About `sizeMb` of library: documents that compress, photos and a video that don't, names that
 * trip things up, a hidden file, an empty file and an empty folder.
 */
export function makeLibrary(root, sizeMb) {
  const budget = Math.max(1, sizeMb) * MB;
  let bytes = 0;
  let files = 0;
  for (const folder of ["documents", "photos", "video", "names", "empty folder"]) {
    mkdirSync(join(root, folder), { recursive: true });
  }

  const documents = Math.min(300, Math.max(20, Math.floor(budget / (40 * 1024))));
  for (let i = 0; i < documents; i++) {
    const size = randomInt(1024, 20 * 1024);
    writeFileSync(join(root, "documents", `note ${String(i).padStart(3, "0")}.txt`), prose(size));
    bytes += size;
    files++;
  }

  for (let i = 0; bytes < budget / 2 && i < 24; i++) {
    const size = Math.min(randomInt(MB, 4 * MB), budget - bytes);
    writeRandom(join(root, "photos", `IMG_${1000 + i}.jpg`), size);
    bytes += size;
    files++;
  }

  const names = ["café.txt", "日本語のメモ.txt", "two  spaces.txt", "party 🎉.txt", ".hidden",
    `${"long ".repeat(40)}name.txt`];
  for (const name of names) {
    writeFileSync(join(root, "names", name), prose(512));
    bytes += 512;
    files++;
  }
  writeFileSync(join(root, "names", "empty.txt"), "");
  files++;

  const rest = budget - bytes;
  if (rest > 0) {
    writeRandom(join(root, "video", "holiday.mov"), rest);
    bytes += rest;
    files++;
  }
  return { files, bytes };
}

/** A second day's changes: some documents deleted, some edited, some new, a photo gone. */
export function changeLibrary(root) {
  let removed = 0;
  let edited = 0;
  let added = 0;
  readdirSync(join(root, "documents")).sort().forEach((name, i) => {
    const path = join(root, "documents", name);
    if (i % 7 === 0) {
      rmSync(path);
      removed++;
    } else if (i % 11 === 0) {
      appendFileSync(path, prose(2048));
      edited++;
    }
  });
  mkdirSync(join(root, "documents", "new"), { recursive: true });
  for (let i = 0; i < 25; i++) {
    writeFileSync(join(root, "documents", "new", `added ${i}.txt`), prose(randomInt(1024, 8192)));
    added++;
  }
  const photos = readdirSync(join(root, "photos")).sort();
  if (photos.length > 0) {
    rmSync(join(root, "photos", photos[0]));
    removed++;
  }
  return { removed, edited, added };
}

/** `sizeMb` of incompressible files, to push an account over its quota. */
export function makeFiller(root, sizeMb) {
  mkdirSync(root, { recursive: true });
  for (let i = 0, left = sizeMb * MB; left > 0; i++) {
    const size = Math.min(left, 100 * MB);
    writeRandom(join(root, `filler ${i}.bin`), size);
    left -= size;
  }
}

/** Every file and folder under `root`, by path relative to it, with each file's size and SHA-256. */
export async function fingerprint(root) {
  const entries = new Map();
  async function walk(folder) {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      const path = join(folder, entry.name);
      // Compared in one Unicode form, so a name stored decomposed still matches.
      const key = relative(root, path).split(sep).join("/").normalize("NFC");
      if (entry.isDirectory()) {
        entries.set(key, { type: "folder" });
        await walk(path);
      } else if (entry.isFile()) {
        const { size, mtimeMs } = statSync(path);
        entries.set(key, { type: "file", size, modified: Math.floor(mtimeMs / 1000), sha256: await sha256(path) });
      } else {
        entries.set(key, { type: "other" });
      }
    }
  }
  await walk(root);
  return entries;
}

function sha256(path) {
  return new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    createReadStream(path)
      .on("error", reject)
      .on("data", (chunk) => hash.update(chunk))
      .on("end", () => resolve(hash.digest("hex")));
  });
}

/**
 * What differs between two fingerprints. Modification times are counted separately: restic
 * restores them, but they aren't content.
 */
export function compare(expected, actual) {
  const missing = [];
  const extra = [];
  const different = [];
  let modified = 0;
  for (const [key, want] of expected) {
    const got = actual.get(key);
    if (!got) missing.push(key);
    else if (want.type !== got.type || want.size !== got.size || want.sha256 !== got.sha256) different.push(key);
    else if (want.type === "file" && want.modified !== got.modified) modified++;
  }
  for (const key of actual.keys()) if (!expected.has(key)) extra.push(key);
  const files = [...expected.values()].filter((entry) => entry.type === "file").length;
  return { same: !missing.length && !extra.length && !different.length, missing, extra, different, modified, files };
}

export function describe(difference) {
  if (difference.same) {
    return `Identical: all ${difference.files} files byte for byte, ${difference.modified
      ? `though ${difference.modified} came back with a different modification time`
      : "modification times included"}.`;
  }
  const some = (label, keys) =>
    keys.length ? `${keys.length} ${label} (${keys.slice(0, 3).join(", ")}${keys.length > 3 ? ", …" : ""})` : null;
  const parts = [some("missing", difference.missing), some("extra", difference.extra), some("different", difference.different)];
  return `NOT identical: ${parts.filter(Boolean).join(", ")}.`;
}
