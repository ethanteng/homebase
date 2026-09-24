import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// The app's own return path is the thing these cover: what happens when a browser comes back from
// somewhere else — Dropbox, so far — carrying the outcome of a sign-in in its address. Every bug
// found there has been in React's lifecycle rather than in any function worth calling directly:
// an effect that did not run again when the element it wanted appeared, a browser call whose
// answer was thrown away, a value that outlived the dialog it belonged to. None of them would be
// caught by testing a function, so these render the app.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
    include: ["src/**/*.test.tsx"],
  },
});
