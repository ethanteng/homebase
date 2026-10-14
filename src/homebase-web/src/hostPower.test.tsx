import { afterEach, describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import HostPower from "./HostPower";
import type { HostPower as Report } from "./api";

/**
 * What could stop the host staying on, as Settings says it to whoever looks after Uncloud. What the
 * server reads on the Mac is stood in for; what the card makes of it is not.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function answer(report: Report | null, status = 200) {
  vi.stubGlobal("fetch", () => Promise.resolve(json(report ?? { detail: "No." }, status)));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function show() {
  let view!: ReturnType<typeof render>;
  await act(async () => {
    view = render(<HostPower />);
  });
  return view;
}

describe("keeping this host on", () => {
  it("says each thing that could stop it, as the host says it", async () => {
    answer({
      checked: true,
      findings: [
        {
          code: "sleeps",
          title: "Goes to sleep after 10 minutes",
          message: "This Mac goes to sleep after 10 minutes with nobody at it.",
          inMenu: true,
        },
        {
          code: "laptop",
          title: "Sleeps when its lid is closed",
          message: "This Mac is a laptop: closing its lid puts it to sleep.",
          inMenu: false,
        },
      ],
    });
    await show();

    expect(screen.getByRole("heading", { name: "Keeping this host on" })).toBeTruthy();
    const items = screen.getAllByRole("listitem");
    expect(items.map((item) => item.textContent)).toEqual([
      "This Mac goes to sleep after 10 minutes with nobody at it.",
      "This Mac is a laptop: closing its lid puts it to sleep.",
    ]);
  });

  it("says so when nothing it can see would stop it", async () => {
    answer({ checked: true, findings: [] });
    await show();

    expect(screen.getByText(/Nothing Uncloud can see should stop this Mac/)).toBeTruthy();
    expect(screen.queryAllByRole("listitem")).toHaveLength(0);
  });

  it("isn't there on a host that isn't a Mac, or when it can't be asked", async () => {
    answer({ checked: false, findings: [] });
    const notMac = await show();
    expect(notMac.container.innerHTML).toBe("");
    notMac.unmount();

    answer(null, 500);
    const failed = await show();
    expect(failed.container.innerHTML).toBe("");
  });
});
