import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import HostStorage from "./HostStorage";
import type { HostStorage as Storage } from "./api";

/**
 * How much of the drive Uncloud may use, as whoever looks after it sets it. What the server answers
 * is stood in for; what the form sends, and what it says each choice comes to, is not.
 */

const GB = 1024 ** 3;

let storage: Storage;
let sent: unknown[] = [];

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

beforeEach(() => {
  // 400 GB free and 100 GB of everybody's files: half of what Uncloud could have is 250 GB.
  storage = {
    allotment: null,
    driveFreeBytes: 400 * GB,
    driveTotalBytes: 1000 * GB,
    uncloudBytes: 100 * GB,
    freeBytes: 400 * GB,
    totalBytes: 1000 * GB,
    capped: false,
  };
  sent = [];
  vi.stubGlobal("fetch", (_input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === "PUT") {
      const body = JSON.parse(String(init.body));
      sent.push(body);
      if (body.percent === 0) return Promise.resolve(json({ detail: "Not that." }, 400));
      const allotment =
        body.percent !== undefined
          ? { unit: "Percent", amount: body.percent }
          : body.bytes !== undefined
            ? { unit: "Bytes", amount: body.bytes }
            : null;
      storage = { ...storage, allotment: allotment as Storage["allotment"], capped: allotment !== null, totalBytes: 250 * GB };
    }
    return Promise.resolve(json(storage));
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function click(element: HTMLElement) {
  await act(async () => {
    element.click();
  });
}

/** The limit's row, opened to change it, as somebody would. */
async function change() {
  await click(await screen.findByRole("button", { name: "Change" }));
}

describe("the storage limit", () => {
  it("starts with no limit, and says what the drive and everybody's files come to", async () => {
    render(<HostStorage onSaved={vi.fn()} />);

    expect(await screen.findByText("No limit")).toBeInTheDocument();
    expect(screen.getByText(/100 GB used · 400 GB free on the drive/)).toBeInTheDocument();
    await change();
    expect(screen.getByRole("radio", { name: /No limit/ })).toBeChecked();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("counts a share from what's free plus what's already here, and saves it", async () => {
    const onSaved = vi.fn();
    render(<HostStorage onSaved={onSaved} />);
    await change();
    const share = screen.getByLabelText("Percent of the drive");

    fireEvent.focus(share);
    fireEvent.change(share, { target: { value: "50" } });

    expect(screen.getByText("Room for 150 GB more, 250 GB in all.")).toBeInTheDocument();
    await click(screen.getByRole("button", { name: "Save" }));

    expect(sent).toEqual([{ percent: 50 }]);
    expect(await screen.findByText("Saved. The limit is 250 GB.")).toBeInTheDocument();
    expect(onSaved).toHaveBeenCalledTimes(1);
    // Saved is what's set now, shown in the row, and the choices fold away.
    expect(screen.getByText("50% of the drive")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
  });

  it("sends a set amount in bytes, and warns when it's less than everybody already has", async () => {
    render(<HostStorage onSaved={vi.fn()} />);
    await change();
    const amount = screen.getByLabelText("Amount");

    fireEvent.focus(amount);
    fireEvent.change(amount, { target: { value: "60" } });
    expect(screen.getByText(/less than the 100 GB already stored. Nothing is deleted/)).toBeInTheDocument();

    fireEvent.change(amount, { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Unit"), { target: { value: "TB" } });
    expect(screen.getByText("That’s more than the drive can give, so there’s no real limit.")).toBeInTheDocument();

    fireEvent.change(amount, { target: { value: "0.2" } });
    await click(screen.getByRole("button", { name: "Save" }));
    expect(sent).toEqual([{ bytes: Math.round(0.2 * 1024 ** 4) }]);
  });

  it("gives back an amount exactly as it was typed, with nothing left to save", async () => {
    render(<HostStorage onSaved={vi.fn()} />);
    await change();
    const amount = screen.getByLabelText("Amount");

    fireEvent.focus(amount);
    fireEvent.change(amount, { target: { value: "1.234" } });
    await click(screen.getByRole("button", { name: "Save" }));

    expect(sent).toEqual([{ bytes: Math.round(1.234 * GB) }]);
    await screen.findByText(/^Saved\./);
    await change();
    expect(screen.getByLabelText("Amount")).toHaveValue(1.234);
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("doesn't offer to save an amount set elsewhere just because the form rounds it", async () => {
    storage = { ...storage, allotment: { unit: "Bytes", amount: 200 * GB + 1 }, capped: true, totalBytes: 200 * GB + 1 };
    render(<HostStorage onSaved={vi.fn()} />);
    await change();

    expect(screen.getByLabelText("Amount")).toHaveValue(200);
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Amount"), { target: { value: "201" } });
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
  });

  it("shows what was set before, and takes it away again", async () => {
    storage = { ...storage, allotment: { unit: "Bytes", amount: 200 * GB }, capped: true, totalBytes: 200 * GB };
    render(<HostStorage onSaved={vi.fn()} />);

    expect(await screen.findByText("Up to 200 GB")).toBeInTheDocument();
    await change();
    expect(screen.getByRole("radio", { name: /Up to.*GB/ })).toBeChecked();
    expect(screen.getByLabelText("Amount")).toHaveValue(200);
    expect(screen.getByLabelText("Unit")).toHaveValue("GB");

    await click(screen.getByRole("radio", { name: /No limit/ }));
    await click(screen.getByRole("button", { name: "Save" }));
    expect(sent).toEqual([{}]);
    expect(await screen.findByText("Saved. No limit.")).toBeInTheDocument();
  });

  it("won't send a share that can't be meant", async () => {
    render(<HostStorage onSaved={vi.fn()} />);
    await change();
    const share = screen.getByLabelText("Percent of the drive");

    fireEvent.focus(share);
    fireEvent.change(share, { target: { value: "150" } });

    expect(screen.getByText("Enter a whole number from 1 to 100.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });
});
