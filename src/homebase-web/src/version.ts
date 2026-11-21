// Stamped in when the interface is built (see vite.config.ts): the same version and build the Mac
// app shows, so "which Uncloud is this?" has one answer wherever somebody looks.
declare const __UNCLOUD_VERSION__: string | undefined;
declare const __UNCLOUD_BUILD__: string | undefined;

const version = typeof __UNCLOUD_VERSION__ === "string" ? __UNCLOUD_VERSION__ : "";
const build = typeof __UNCLOUD_BUILD__ === "string" ? __UNCLOUD_BUILD__ : "";

/** "Version 0.492 (a553cc1)", or as much of it as the build knew; empty when it knew neither. */
export const versionLabel = version
  ? `Version ${version}${build ? ` (${build})` : ""}`
  : build
    ? `Build ${build}`
    : "";
