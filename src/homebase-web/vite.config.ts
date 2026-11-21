import { execSync } from "node:child_process";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The version shown under the logo. Packaging passes it in, so it matches the Mac app's own;
// anywhere else (a Linux host's run.sh, the dev server) it comes from git the same way.
function git(args: string) {
  try {
    return execSync(`git ${args}`, { stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
  } catch {
    return "";
  }
}
const commits = git("rev-list --count HEAD");
const version = process.env.UNCLOUD_VERSION || (commits ? `0.${commits}` : "");
const build = process.env.UNCLOUD_BUILD || git("rev-parse --short HEAD");

export default defineConfig({
  plugins: [react()],
  define: {
    __UNCLOUD_VERSION__: JSON.stringify(version),
    __UNCLOUD_BUILD__: JSON.stringify(build),
  },
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: { "/api": { target: "http://127.0.0.1:5210", changeOrigin: false } },
  },
  build: { outDir: "../Homebase.Server/wwwroot", emptyOutDir: true },
});
