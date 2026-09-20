import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

export default defineConfig({
  root: fileURLToPath(new URL("../../landing", import.meta.url)),
  base: "./",
  publicDir: false,
  server: { host: "127.0.0.1", port: 5174, strictPort: true },
  preview: { host: "127.0.0.1", port: 5174, strictPort: true },
  build: {
    outDir: fileURLToPath(new URL("../../artifacts/landing", import.meta.url)),
    emptyOutDir: true,
  },
});
