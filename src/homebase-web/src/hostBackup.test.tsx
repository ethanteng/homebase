import { afterEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import HostBackup, { forgetUnsaved } from "./HostBackup";
import type { BackupAccount, BackupCheck, BackupStatus } from "./api";

/**
 * Uncloud Backup in Settings: joining with an invite, the password shown once and only put away
 * once it's kept, storage being set up, and how the backups are going. The host's answers are
 * stood in for; what the panel makes of them is not.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

// What a host makes for the backup: six groups of five. Made up here, so nothing real is in it.
const SHOWN_ONCE = ["K7QMX", "3XPD9", "TWAHF", "2RBCD", "EFGHJ", "KLMNP"].join("-");

const notJoined: BackupAccount = {
  joined: false, service: null, household: null, host: null, hostName: null, joinedAt: null,
  storage: null, step: null, bucket: null,
};
const ready: BackupAccount = {
  joined: true, service: "https://backup.uncloud.life/", household: "h1", host: "host1", hostName: "Mac mini",
  joinedAt: "2026-10-15T12:00:00Z", storage: "ready", step: null, bucket: "uncloud-h1",
};
const never: BackupStatus = {
  configured: false, running: false, lastStarted: null, lastFinished: null, lastOutcome: null, message: null,
  lastSucceeded: null, snapshot: null, filesNew: null, filesChanged: null, bytesAdded: null, totalFiles: null,
  totalBytes: null, lastPruned: null,
};

type Route = (body: unknown) => Response;

/** Answers each request by its method and path; what was asked is kept to look at after. */
function host(routes: Record<string, Route>) {
  const asked: { route: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", (url: string, init?: RequestInit) => {
    const route = `${init?.method ?? "GET"} ${url}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    asked.push({ route, body });
    const answer = routes[route];
    return Promise.resolve(answer ? answer(body) : json({ detail: "No." }, 404));
  });
  return asked;
}

afterEach(() => {
  forgetUnsaved();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

async function show() {
  let view!: ReturnType<typeof render>;
  await act(async () => {
    view = render(<HostBackup />);
  });
  return view;
}

describe("backing up offsite", () => {
  it("joins with an invite and shows the password once, until it's been kept", async () => {
    let joined = false;
    const asked = host({
      "GET /api/host/backup/account": () => json(joined ? ready : notJoined),
      "GET /api/host/backup": () => json(joined ? { ...never, configured: true } : never),
      "POST /api/host/backup/account": () => {
        joined = true;
        return json({ password: SHOWN_ONCE, account: ready }, 201);
      },
    });
    await show();

    const join = screen.getByRole("button", { name: "Join" }) as HTMLButtonElement;
    expect(join.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Invite code"), { target: { value: "  K7QM-3XPD-9TWA-HF2R " } });
    await act(async () => {
      fireEvent.click(join);
    });

    expect(asked.find((request) => request.route === "POST /api/host/backup/account")?.body).toEqual({
      invite: "K7QM-3XPD-9TWA-HF2R",
    });
    expect(screen.getByLabelText("Your backup’s password").textContent).toBe(SHOWN_ONCE);
    expect(screen.getByText(/only time Uncloud shows it/)).toBeTruthy();
    const done = screen.getByRole("button", { name: "Done" }) as HTMLButtonElement;
    expect(done.disabled).toBe(true);

    fireEvent.click(screen.getByLabelText("I’ve kept it somewhere safe"));
    expect(done.disabled).toBe(false);
    fireEvent.click(done);

    expect(screen.queryByText(SHOWN_ONCE)).toBeNull();
    expect(screen.getByText(/Not backed up yet/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Back up now" })).toBeTruthy();
  });

  it("keeps the password through Settings being closed mid-join, and asks before the page is left", async () => {
    let joined = false;
    let answer!: (response: Response) => void;
    host({
      "GET /api/host/backup/account": () => json(joined ? { ...ready, storage: "setting_up", step: "bucket" } : notJoined),
      "GET /api/host/backup": () => json(never),
    });
    const fetch = globalThis.fetch;
    vi.stubGlobal("fetch", (url: string, init?: RequestInit) =>
      init?.method === "POST"
        ? new Promise<Response>((resolve) => (answer = resolve))
        : fetch(url, init));
    const first = await show();
    fireEvent.change(screen.getByLabelText("Invite code"), { target: { value: "K7QM-3XPD-9TWA-HF2R" } });
    fireEvent.click(screen.getByRole("button", { name: "Join" }));

    // Settings closed before the answer came.
    first.unmount();
    joined = true;
    await act(async () => {
      answer(json({ password: SHOWN_ONCE, account: { ...ready, storage: "setting_up", step: "bucket" } }, 201));
    });

    const second = await show();
    expect(screen.getByLabelText("Your backup’s password").textContent).toBe(SHOWN_ONCE);
    const leaving = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(leaving);
    expect(leaving.defaultPrevented).toBe(true);

    // And closed again before Done: still there.
    second.unmount();
    await show();
    fireEvent.click(screen.getByLabelText("I’ve kept it somewhere safe"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByText(SHOWN_ONCE)).toBeNull();
    const later = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(later);
    expect(later.defaultPrevented).toBe(false);
  });

  it("says it copied the password only when it did, and otherwise selects it to copy by hand", async () => {
    host({
      "GET /api/host/backup/account": () => json(notJoined),
      "GET /api/host/backup": () => json(never),
      "POST /api/host/backup/account": () => json({ password: SHOWN_ONCE, account: ready }, 201),
    });
    await show();
    fireEvent.change(screen.getByLabelText("Invite code"), { target: { value: "K7QM-3XPD-9TWA-HF2R" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Join" }));
    });

    // Plain HTTP from another computer: no clipboard for a page to write to.
    vi.stubGlobal("navigator", { ...navigator, clipboard: undefined });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    });
    expect(screen.queryByRole("button", { name: "Copied" })).toBeNull();
    expect(screen.getByText(/didn’t let Uncloud copy it/)).toBeTruthy();
    expect(window.getSelection()?.toString()).toBe(SHOWN_ONCE);

    const written: string[] = [];
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: async (text: string) => void written.push(text) } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    });
    expect(written).toEqual([SHOWN_ONCE]);
    expect(screen.getByRole("button", { name: "Copied" })).toBeTruthy();
  });

  it("says why an invite wasn't taken, in the host's words, and stays ready to try another", async () => {
    host({
      "GET /api/host/backup/account": () => json(notJoined),
      "GET /api/host/backup": () => json(never),
      "POST /api/host/backup/account": () =>
        json({ detail: "That invite code isn't one Uncloud Backup knows, or it has been used, or it has expired." }, 400),
    });
    await show();

    fireEvent.change(screen.getByLabelText("Invite code"), { target: { value: "AAAA-AAAA-AAAA-AAAA" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Join" }));
    });

    expect(screen.getByRole("alert").textContent).toMatch(/isn't one Uncloud Backup knows/);
    expect(screen.getByLabelText("Invite code")).toBeTruthy();
    expect(screen.queryByText(/password/)).toBeNull();
  });

  it("follows the household's storage being set up until it's ready", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let account: BackupAccount = { ...ready, storage: "setting_up", step: "bucket", bucket: null };
    host({
      "GET /api/host/backup/account": () => json(account),
      "GET /api/host/backup": () => json({ ...never, configured: account.storage === "ready" }),
    });
    await show();

    expect(screen.getByRole("status").textContent).toMatch(/Setting up this household’s storage: making somewhere to keep the backup/);
    expect(screen.queryByRole("button", { name: "Back up now" })).toBeNull();

    account = ready;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });

    expect(screen.getByText(/Not backed up yet/)).toBeTruthy();
  });

  it("says when setting the storage up keeps failing, without asking anything of anybody", async () => {
    host({
      "GET /api/host/backup/account": () => json({ ...ready, storage: "failing", step: "storage", bucket: null }),
      "GET /api/host/backup": () => json(never),
    });
    await show();

    expect(screen.getByText(/having trouble setting up this household’s storage \(giving it storage\)/)).toBeTruthy();
  });

  it("says how the last backup went, and starts one when asked", async () => {
    const asked = host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () =>
        json({
          ...never, configured: true, lastOutcome: "Succeeded", lastStarted: "2026-10-15T09:00:00Z",
          lastFinished: "2026-10-15T09:30:00Z", lastSucceeded: "2026-10-15T09:30:00Z", totalFiles: 1234,
          totalBytes: 5 * 1024 ** 3, bytesAdded: 3 * 1024 ** 2,
        }),
      "POST /api/host/backup": () => json({ ...never, configured: true, running: true }, 202),
    });
    await show();

    expect(screen.getByText(/Last backed up .*1,234 files, 5 GB · 3 MB new\./)).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Back up now" }));
    });

    expect(asked.some((request) => request.route === "POST /api/host/backup")).toBe(true);
    expect(screen.getByRole("status").textContent).toMatch(/Backing up now/);
    expect((screen.getByRole("button", { name: "Back up now" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("says when a backup worked but older ones couldn't be tidied away", async () => {
    host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () =>
        json({
          ...never, configured: true, lastOutcome: "Succeeded", lastFinished: "2026-10-15T09:30:00Z",
          lastSucceeded: "2026-10-15T09:30:00Z",
          message: "Older backups weren't tidied away: Uncloud couldn't tidy older backups: repository is locked",
        }),
    });
    await show();

    expect(screen.getByText(/Last backed up .*Older backups weren't tidied away/)).toBeTruthy();
  });

  it("says a backup that didn't work didn't, with why, and when the last good one was", async () => {
    host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () =>
        json({
          ...never, configured: true, lastOutcome: "Refused", lastFinished: "2026-10-15T09:30:00Z",
          lastSucceeded: "2026-10-14T09:30:00Z",
          message: "The backup storage refused to let Uncloud back up. It may be full, or this Uncloud's key may no longer work.",
        }),
    });
    await show();

    expect(screen.getByText(/didn’t work\. The backup storage refused .* The last one that worked was/)).toBeTruthy();
  });

  const backedUp: BackupStatus = {
    ...never, configured: true, lastOutcome: "Succeeded", lastFinished: "2026-10-15T23:14:00Z",
    lastSucceeded: "2026-10-15T23:14:00Z", totalFiles: 364, totalBytes: 571_000_000, bytesAdded: 505_000_000,
  };
  const unchecked: BackupCheck = {
    running: false, started: null, finished: null, passed: null, message: null, snapshot: null, snapshotTime: null,
    filesInBackup: null, bytesInBackup: null, files: null, bytes: null, sampled: false, matched: null,
    changedSince: null, goneSince: null, mismatched: null, missing: null, hostKeyMatched: null,
  };
  const checked: BackupCheck = {
    ...unchecked, started: "2026-10-16T09:00:00Z", finished: "2026-10-16T09:02:00Z", passed: true,
    snapshot: "4fa2b9c1", snapshotTime: "2026-10-15T23:14:00Z", filesInBackup: 364, bytesInBackup: 571_000_000,
    files: 364, bytes: 571_000_000, matched: 364, changedSince: 0, goneSince: 0, mismatched: 0, missing: 0,
    hostKeyMatched: true,
  };

  it("says how the last check went, and which backups are kept", async () => {
    host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () => json(backedUp),
      "GET /api/host/backup/check": () => json(checked),
      "GET /api/host/backup/snapshots": () =>
        json({ snapshots: [{ id: "a1", time: "2026-10-14T23:14:00Z" }, { id: "4fa2b9c1", time: "2026-10-15T23:14:00Z" }] }),
    });
    await show();

    expect(screen.getByText(/Checked .*: restored all 364 of everyone’s files \(544\.5 MB\) from the backup of .*, and every one came back exactly as it is here\. So did this Uncloud’s own key/)).toBeTruthy();
    expect(screen.getByText(/2 backups kept, the oldest from/)).toBeTruthy();
  });

  it("says a sample was checked, and that files changed or gone since aren't a fault", async () => {
    host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () => json(backedUp),
      "GET /api/host/backup/check": () =>
        json({ ...checked, sampled: true, filesInBackup: 50_000, files: 2_000, matched: 1_996, changedSince: 3, goneSince: 1 }),
    });
    await show();

    expect(screen.getByText(/restored 2,000 of everyone’s 50,000 files .*, at random from the backup .*every one still here as it was came back exactly as it is here \(3 have changed here since and 1 has gone\)/)).toBeTruthy();
  });

  it("says when files came back wrong, without saying which", async () => {
    host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () => json(backedUp),
      "GET /api/host/backup/check": () => json({ ...checked, passed: false, matched: 362, mismatched: 1, missing: 1 }),
    });
    await show();

    expect(screen.getByText(/1 file came back different from what’s here, and 1 file didn’t come back at all\. Uncloud’s log says which\./)).toBeTruthy();
  });

  it("checks the backup when asked", async () => {
    const asked = host({
      "GET /api/host/backup/account": () => json(ready),
      "GET /api/host/backup": () => json(backedUp),
      "GET /api/host/backup/check": () => json(unchecked),
      "POST /api/host/backup/check": () => json({ ...unchecked, running: true }, 202),
    });
    await show();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Check the backup" }));
    });

    expect(asked.some((request) => request.route === "POST /api/host/backup/check")).toBe(true);
    expect(screen.getByText(/Checking the backup: restoring it somewhere of its own/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Check the backup" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("isn't there when the host can't be asked", async () => {
    host({});
    const view = await show();

    expect(view.container.innerHTML).toBe("");
  });
});
