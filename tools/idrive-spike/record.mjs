// What a run found, as it goes: a report a person reads, and every API call for when they want
// the detail. Neither ever holds anything that would let somebody in.

import { createWriteStream, writeFileSync } from "node:fs";
import { join } from "node:path";

const SECRET = /secret|password|token|authorization|invite_url|reset_link/i;

/** A copy with secrets removed and access key ids cut short. */
export function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(Object.entries(value).map(([key, inner]) => {
    if (SECRET.test(key)) return [key, "[removed]"];
    if (key === "access_key" && typeof inner === "string") return [key, `${inner.slice(0, 4)}…`];
    return [key, redact(inner)];
  }));
}

/** The shape of an answer without its values, for answers the docs only show as placeholders. */
export function shapeOf(value, depth = 0) {
  if (Array.isArray(value)) return value.length ? `[${shapeOf(value[0], depth + 1)}${value.length > 1 ? ", …" : ""}]` : "[]";
  if (value === null) return "null";
  if (typeof value !== "object") return typeof value;
  if (depth > 3) return "{…}";
  return `{ ${Object.entries(value).map(([key, inner]) => `${key}: ${shapeOf(inner, depth + 1)}`).join(", ")} }`;
}

const MARK = { pass: "✅", fail: "❌", note: "ℹ️", error: "⛔" };

export class Recorder {
  constructor(dir, title, file = "report.md") {
    this.dir = dir;
    this.title = title;
    this.file = file;
    this.facts = [];
    this.steps = [];
    this.calls = createWriteStream(join(dir, "calls.jsonl"), { flags: "a" });
  }

  fact(label, value) {
    const known = this.facts.find(([existing]) => existing === label);
    if (known) known[1] = value;
    else this.facts.push([label, value]);
  }

  call(entry) {
    this.calls.write(`${JSON.stringify(redact({ at: new Date().toISOString(), ...entry }))}\n`);
  }

  /**
   * One step. `action` returns what happened: a string, or `{ outcome, detail }` when the step has
   * a verdict of its own ("pass", "fail" or "note"). A throw is recorded and stops the run, unless
   * `carryOn` says the rest is still worth doing.
   */
  async step(name, action, { carryOn = false } = {}) {
    console.log(`\n▶ ${name}`);
    const start = Date.now();
    try {
      const result = await action();
      const { outcome = "pass", detail = "" } =
        result && typeof result === "object" ? result : { detail: result ?? "" };
      return this.#finish(name, outcome, detail, start);
    } catch (error) {
      this.#finish(name, "error", error?.message ?? String(error), start);
      if (!carryOn) {
        if (error && typeof error === "object") error.recorded = true;
        throw error;
      }
      return "error";
    }
  }

  #finish(name, outcome, detail, start) {
    const seconds = (Date.now() - start) / 1000;
    this.steps.push({ name, outcome, detail, seconds });
    console.log(`${MARK[outcome]} ${detail} (${seconds.toFixed(1)}s)`);
    this.write();
    return outcome;
  }

  get troubled() {
    return this.steps.some((step) => step.outcome === "fail" || step.outcome === "error");
  }

  write() {
    const cell = (text) => String(text).replaceAll("|", "\\|").replaceAll("\n", "<br>");
    const lines = [
      `# ${this.title}`,
      "",
      ...this.facts.map(([label, value]) => `- **${label}:** ${value}`),
      "",
      "| | Step | Took | What happened |",
      "|---|---|---|---|",
      ...this.steps.map((s) => `| ${MARK[s.outcome]} | ${cell(s.name)} | ${s.seconds.toFixed(1)}s | ${cell(s.detail)} |`),
      "",
      "Every reseller API call, with secrets removed, is in `calls.jsonl` beside this file.",
      "",
    ];
    writeFileSync(join(this.dir, this.file), lines.join("\n"));
  }

  close() {
    this.calls.end();
  }
}
