// restic, the backup engine a host would run, pointed at one repository with one key.

import { spawn } from "node:child_process";

/**
 * restic for one repository and key. Any RESTIC_* or AWS_* settings in the shell are dropped, so
 * nothing from the environment changes which repository or key is used.
 */
export function createRestic({ binary = "restic", repository, password, accessKey, secretKey, region, signal }) {
  const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(RESTIC_|AWS_)/.test(name)));
  Object.assign(env, {
    RESTIC_REPOSITORY: repository,
    RESTIC_PASSWORD: password,
    AWS_ACCESS_KEY_ID: accessKey,
    AWS_SECRET_ACCESS_KEY: secretKey,
  });
  // No cache, so every command reads what is really in the bucket, which is the point here.
  const options = ["--no-cache", "-o", "s3.bucket-lookup=path", ...(region ? ["-o", `s3.region=${region}`] : [])];

  const restic = {
    // Aborting `signal` stops restic wherever it is.
    run: (args, settings) => run(binary, options, args, env, { ...settings, signal }),

    /** Backs `path` up and returns restic's summary, plus how long it took. */
    async backup(path) {
      const result = await restic.run(["backup", "--json", "--host", "uncloud-spike", path]);
      const summary = result.stdout.split("\n").map(json).find((message) => message?.message_type === "summary");
      if (!summary) throw new Error(`restic backup gave no summary: ${tail(result.stderr)}`);
      return { ...summary, uploaded: summary.data_added_packed ?? summary.data_added, seconds: result.seconds };
    },
  };
  return restic;
}

export async function resticVersion(binary = "restic") {
  const { stdout } = await run(binary, [], ["version"], process.env);
  return stdout.trim();
}

/**
 * Runs restic. With `timeout` (ms), restic is stopped once it's run that long, and the result says
 * `timedOut`: restic retries errors it doesn't recognise for many minutes before giving up.
 */
function run(binary, options, args, env, { allowFailure = false, signal, timeout } = {}) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const child = spawn(binary, [...options, ...args], { env, signal, timeout, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => (stdout += chunk));
    child.stderr.on("data", (chunk) => (stderr += chunk));
    child.on("error", (error) =>
      reject(error.code === "ENOENT" ? new Error(`There's no ${binary} to run. Install it with: brew install restic`) : error));
    child.on("close", (code) => {
      const elapsed = Date.now() - start;
      const timedOut = timeout !== undefined && elapsed >= timeout && !signal?.aborted;
      const result = { code, stdout, stderr, seconds: elapsed / 1000, timedOut };
      if (code === 0 || allowFailure) resolve(result);
      else reject(new Error(`restic ${args.find((arg) => !arg.startsWith("-"))} exited ${code}: ${tail(stderr)}`));
    });
  });
}

function json(line) {
  try {
    return JSON.parse(line);
  } catch {
    return undefined;
  }
}

/** The last few lines restic said, on one line. */
export function tail(text, lines = 3) {
  return text.split("\n").map((line) => line.trim()).filter(Boolean).slice(-lines).join(" / ") || "(nothing)";
}
