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

describe("how much of the drive Uncloud may use", () => {
  it("starts on all of it, and says what the drive and everybody's files come to", async () => {
    render(<HostStorage onSaved={vi.fn()} />);

    expect(await screen.findByText(/The drive has 400 GB free, and everyone’s files here take up 100 GB/)).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /All of it/ })).toBeChecked();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("counts a share from what's free plus what's already here, and saves it", async () => {
    const onSaved = vi.fn();
    render(<HostStorage onSaved={onSaved} />);
    const share = await screen.findByLabelText("Percent of the room Uncloud could have");

    fireEvent.focus(share);
    fireEvent.change(share, { target: { value: "50" } });

    expect(screen.getByText(/Uncloud can hold up to 250 GB: room for 150 GB more/)).toBeInTheDocument();
    expect(screen.getByText(/A share is of what’s free plus what everyone’s files already take \(500 GB\)/)).toBeInTheDocument();
    await click(screen.getByRole("button", { name: "Save" }));

    expect(sent).toEqual([{ percent: 50 }]);
    expect(await screen.findByText("Saved. Everyone here now shares 250 GB.")).toBeInTheDocument();
    expect(onSaved).toHaveBeenCalledTimes(1);
    // Saved is what's set now, so there's nothing left to save.
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("sends a set amount in bytes, and warns when it's less than everybody already has", async () => {
    render(<HostStorage onSaved={vi.fn()} />);
    const amount = await screen.findByLabelText("Amount");

    fireEvent.focus(amount);
    fireEvent.change(amount, { target: { value: "60" } });
    expect(screen.getByText(/less than the 100 GB everyone’s files already take. Nothing is deleted/)).toBeInTheDocument();

    fireEvent.change(amount, { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Unit"), { target: { value: "TB" } });
    expect(screen.getByText("The drive has less than that free, so Uncloud can still fill it.")).toBeInTheDocument();

    fireEvent.change(amount, { target: { value: "0.2" } });
    await click(screen.getByRole("button", { name: "Save" }));
    expect(sent).toEqual([{ bytes: Math.round(0.2 * 1024 ** 4) }]);
  });

  it("gives back an amount exactly as it was typed, with nothing left to save", async () => {
    render(<HostStorage onSaved={vi.fn()} />);
    const amount = await screen.findByLabelText("Amount");

    fireEvent.focus(amount);
    fireEvent.change(amount, { target: { value: "1.234" } });
    await click(screen.getByRole("button", { name: "Save" }));

    expect(sent).toEqual([{ bytes: Math.round(1.234 * GB) }]);
    await screen.findByText(/^Saved\./);
    expect(screen.getByLabelText("Amount")).toHaveValue(1.234);
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("doesn't offer to save an amount set elsewhere just because the form rounds it", async () => {
    storage = { ...storage, allotment: { unit: "Bytes", amount: 200 * GB + 1 }, capped: true, totalBytes: 200 * GB + 1 };
    render(<HostStorage onSaved={vi.fn()} />);

    expect(await screen.findByLabelText("Amount")).toHaveValue(200);
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Amount"), { target: { value: "201" } });
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
  });

  it("shows what was set before, and takes it away again", async () => {
    storage = { ...storage, allotment: { unit: "Bytes", amount: 200 * GB }, capped: true, totalBytes: 200 * GB };
    render(<HostStorage onSaved={vi.fn()} />);

    expect(await screen.findByRole("radio", { name: /A set amount/ })).toBeChecked();
    expect(screen.getByLabelText("Amount")).toHaveValue(200);
    expect(screen.getByLabelText("Unit")).toHaveValue("GB");

    await click(screen.getByRole("radio", { name: /All of it/ }));
    await click(screen.getByRole("button", { name: "Save" }));
    expect(sent).toEqual([{}]);
    expect(await screen.findByText("Saved. Everyone’s files can fill the drive again.")).toBeInTheDocument();
  });

  it("won't send a share that can't be meant", async () => {
    render(<HostStorage onSaved={vi.fn()} />);
    const share = await screen.findByLabelText("Percent of the room Uncloud could have");

    fireEvent.focus(share);
    fireEvent.change(share, { target: { value: "150" } });

    expect(screen.getByText("A share is a whole number from 1 to 100.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });
});
