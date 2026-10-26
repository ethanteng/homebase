import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import ImportStatus from "./ImportStatus";
import type { ImportJob } from "./api";

/**
 * One import brings at most so many files. When it stops there, its total is the first of them,
 * and neither the bar nor the result may pass that off as everything in the folder.
 */

const JOB: ImportJob = {
  id: "job-1",
  sourceId: "dropbox",
  remotePaths: ["/"],
  label: "Dropbox",
  stage: "Bringing",
  totalFiles: 20000,
  completedFiles: 8,
  bytes: 1_700_000_000,
  currentFile: "/photos/one.jpg",
  more: false,
  result: null,
  error: null,
  startedAt: new Date().toISOString(),
  finishedAt: null,
  running: true,
};

const show = (job: ImportJob) =>
  render(<ImportStatus job={job} onStop={() => {}} onDismiss={() => {}} onOpen={() => {}} />);

const finished = (more: boolean): ImportJob => ({
  ...JOB,
  stage: "Done",
  completedFiles: 20000,
  currentFile: null,
  more,
  result: {
    imported: [{ localPath: "Dropbox/photos/one.jpg", remotePath: "/photos/one.jpg", size: 10 }],
    skipped: [],
    bytes: 34_000_000_000,
    alreadyHere: 0,
    more,
    importedCount: 20000,
    skippedCount: 0,
  },
  finishedAt: new Date().toISOString(),
  running: false,
});

describe("an import stopped at the most one brings", () => {
  it("counts its files as the first of them, not all of them", () => {
    show({ ...JOB, more: true });

    expect(screen.getByText(/8 of the first 20,000 files/)).toBeTruthy();
    expect(screen.getByRole("progressbar").getAttribute("aria-valuetext")).toBe("8 of the first 20,000 files");
  });

  it("counts a whole folder as all of it", () => {
    show({ ...JOB, totalFiles: 1234, completedFiles: 8 });

    expect(screen.getByText(/8 of 1,234 files/)).toBeTruthy();
    expect(screen.queryByText(/the first/)).toBeNull();
  });

  it("asks for the rest when it finishes", () => {
    show(finished(true));

    expect(screen.getByText(/Added 20,000 files from Dropbox/)).toBeTruthy();
    expect(screen.getByText(/add Dropbox again for the rest/)).toBeTruthy();
  });

  it("doesn't ask for more once everything has come", () => {
    show(finished(false));

    expect(screen.getByText(/Added 20,000 files from Dropbox/)).toBeTruthy();
    expect(screen.queryByText(/again for the rest/)).toBeNull();
  });
});
