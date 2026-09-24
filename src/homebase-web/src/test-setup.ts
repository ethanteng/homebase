import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

// jsdom has no dialog element to speak of: showModal and close are not implemented, and the app
// leans on both. Standing them in for the real thing keeps `open` meaning what it means in a
// browser, which is the property these tests read.
if (!HTMLDialogElement.prototype.showModal)
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.open = true;
  };
if (!HTMLDialogElement.prototype.close)
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    this.open = false;
  };

afterEach(cleanup);
