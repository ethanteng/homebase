import { SignedOutError } from "./api";

/**
 * Bringing Evernote notes in after an export has uploaded. One import at a time: a later export
 * is remembered and brought in when the one already going finishes, because that one may have
 * listed the folder before the new file was there.
 */
export interface FollowUp {
  /** This page is in the middle of an attempt, or waiting out an import it didn't start. */
  going: boolean;
  /** Another export landed during that and still needs a turn of its own. */
  again: boolean;
}

/** The server's own words for "an import is already running", which is a wait, not a failure. */
export function importAlreadyRunning(message: string): boolean {
  return message.includes("already bringing files home") || message.includes("already importing");
}

/** How many times to find an import already running before saying so, rather than waiting forever. */
const BUSY_LIMIT = 8;

/**
 * Asks for the notes to be brought in. A call made while one is already going is remembered and
 * returns null; that call's own attempt runs it when it finishes. Returns an error message when
 * a try fails for a reason other than an import already running.
 */
export async function bringNotesWhenFree(
  gate: { current: FollowUp },
  attempt: () => Promise<void>,
  untilIdle: () => Promise<void>,
): Promise<string | null> {
  if (gate.current.going) {
    gate.current = { going: true, again: true };
    return null;
  }
  gate.current = { going: true, again: false };
  let busy = 0;
  try {
    for (;;) {
      let failed: string | null = null;
      try {
        await attempt();
        busy = 0;
      } catch (problem: unknown) {
        if (problem instanceof SignedOutError) throw problem;
        const message = problem instanceof Error ? problem.message : "Uncloud didn’t answer.";
        if (importAlreadyRunning(message)) {
          // The one already running can't see an export that landed after it looked.
          if (++busy >= BUSY_LIMIT) {
            gate.current = { going: false, again: false };
            return message;
          }
          gate.current = { going: true, again: true };
          await untilIdle();
        } else {
          busy = 0;
          failed = message;
        }
      }
      // A failure of the try already going isn't a reason to drop an export that arrived during it.
      if (failed !== null && !gate.current.again) {
        gate.current = { going: false, again: false };
        return failed;
      }
      if (gate.current.again) {
        gate.current = { going: true, again: false };
        continue;
      }
      gate.current = { going: false, again: false };
      return null;
    }
  } catch (problem) {
    gate.current = { going: false, again: false };
    throw problem;
  }
}
