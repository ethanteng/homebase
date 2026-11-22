import { describe, expect, it } from "vitest";
import { SignedOutError } from "./api";
import { bringNotesWhenFree, importEverything, MOST_ROUNDS, type FollowUp } from "./evernoteFollowUp";
import type { ImportJob, ImportStage } from "./api";

const gate = (): { current: FollowUp } => ({ current: { going: false, again: false } });

describe("bringing Evernote notes in after an export", () => {
  it("runs one import, and a second export asked for during it once that finishes", async () => {
    const state = gate();
    let release!: () => void;
    let calls = 0;
    const attempt = () => {
      calls++;
      if (calls === 1) return new Promise<void>((resolve) => {
        release = resolve;
      });
      return Promise.resolve();
    };
    const first = bringNotesWhenFree(state, attempt, async () => {});
    // The first attempt is waiting inside its import. Asking again only remembers.
    expect(calls).toBe(1);
    const second = bringNotesWhenFree(state, attempt, async () => {});
    expect(calls).toBe(1);
    release();
    expect(await second).toBeNull();
    expect(await first).toBeNull();
    expect(calls).toBe(2);
    expect(state.current).toEqual({ going: false, again: false });
  });

  it("tries again when an import is already running, instead of treating that as the notes failing", async () => {
    const state = gate();
    const seen: string[] = [];
    let idle = 0;
    const message = await bringNotesWhenFree(
      state,
      async () => {
        seen.push("attempt");
        if (seen.length === 1)
          throw new Error("Uncloud is already bringing files home. Let that finish, or stop it first.");
      },
      async () => {
        idle++;
      },
    );
    expect(message).toBeNull();
    expect(seen).toEqual(["attempt", "attempt"]);
    expect(idle).toBe(1);
  });

  it("says what went wrong when the import is refused for some other reason", async () => {
    const state = gate();
    let calls = 0;
    const message = await bringNotesWhenFree(
      state,
      async () => {
        calls++;
        throw new Error("This would bring 4 GB home and only 1 GB is free on your Uncloud drive.");
      },
      async () => {},
    );
    expect(calls).toBe(1);
    expect(message).toMatch(/only 1 GB is free/);
    expect(state.current.going).toBe(false);
  });

  it("stops waiting when an import is reported as already running and never is", async () => {
    const state = gate();
    let calls = 0;
    const message = await bringNotesWhenFree(
      state,
      async () => {
        calls++;
        throw new Error("Uncloud is already importing. Let that finish first.");
      },
      async () => {},
    );
    expect(calls).toBe(8);
    expect(message).toMatch(/already importing/);
    expect(state.current.going).toBe(false);
  });

  it("lets a signed-out session through, and doesn't stay busy afterwards", async () => {
    const state = gate();
    const run = bringNotesWhenFree(
      state,
      async () => {
        throw new SignedOutError("Sign in to Uncloud to continue.");
      },
      async () => {},
    );
    await expect(run).rejects.toBeInstanceOf(SignedOutError);
    expect(state.current).toEqual({ going: false, again: false });
  });
});

describe("bringing in everything an export holds", () => {
  const job = (id: string, stage: ImportStage = "Done", more = false) =>
    ({ id, stage, running: false, result: { more } }) as unknown as ImportJob;

  it("starts another while the one before stopped at the most one import brings", async () => {
    const started: string[] = [];
    const left = [true, true, false];
    await importEverything(
      async () => {
        const id = `job-${started.length + 1}`;
        started.push(id);
        return job(id);
      },
      async (begun) => job(begun.id, "Done", left[started.length - 1]),
      true,
    );
    expect(started).toEqual(["job-1", "job-2", "job-3"]);
  });

  it("stops at one when somebody stopped it, it failed, another took over, or it isn't asked to carry on", async () => {
    for (const [finished, carryOn] of [
      [job("job-1", "Stopped", true), true],
      [job("job-1", "Failed", true), true],
      [job("someone-else", "Done", true), true],
      [null, true],
      [job("job-1", "Done", true), false],
    ] as const) {
      let starts = 0;
      await importEverything(
        async () => {
          starts++;
          return job("job-1");
        },
        async () => finished,
        carryOn,
      );
      expect(starts).toBe(1);
    }
  });

  it("gives up eventually on an import that always says there's more", async () => {
    let starts = 0;
    await importEverything(
      async () => {
        starts++;
        return job("job");
      },
      async () => job("job", "Done", true),
      true,
    );
    expect(starts).toBe(MOST_ROUNDS);
  });
});
