import { afterEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import HostBackup from "./HostBackup";
import type { BackupAccount, BackupStatus } from "./api";

/**
 * Uncloud Backup in Settings: joining with an invite, the password shown once and only put away
 * once it's kept, storage being set up, and how the backups are going. The host's answers are
 * stood in for; what the panel makes of them is not.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const PASSWORD = "K7QMX-3XPD9-TWAHF-2RBCD-EFGHJ-KLMNP";

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
        return json({ password: PASSWORD, account: ready }, 201);
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
    expect(screen.getByLabelText("Your backup’s password").textContent).toBe(PASSWORD);
    expect(screen.getByText(/only time Uncloud shows it/)).toBeTruthy();
    const done = screen.getByRole("button", { name: "Done" }) as HTMLButtonElement;
    expect(done.disabled).toBe(true);

    fireEvent.click(screen.getByLabelText("I’ve kept it somewhere safe"));
    expect(done.disabled).toBe(false);
    fireEvent.click(done);

    expect(screen.queryByText(PASSWORD)).toBeNull();
    expect(screen.getByText(/Not backed up yet/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Back up now" })).toBeTruthy();
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

  it("isn't there when the host can't be asked", async () => {
    host({});
    const view = await show();

    expect(view.container.innerHTML).toBe("");
  });
});
