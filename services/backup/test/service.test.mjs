// The service's own endpoints: what /api/health says and never says, and that only Vercel Cron gets
// through to the nightly job. No network and no database: the database is a stand-in where one's needed.

import { test } from "node:test";
import assert from "node:assert/strict";
import { checkHealth } from "../lib/health.mjs";
import { fromVercelCron } from "../lib/cron.mjs";
import { GET as health } from "../api/health.js";
import { GET as nightly } from "../api/cron/nightly.js";

test("with nothing set up, health says so and isn't ready", async () => {
  const said = await checkHealth({}, null);
  assert.deepEqual(said, {
    service: "uncloud-backup",
    ready: false,
    configured: { resellerToken: false, database: false, cronSecret: false },
    database: "not set up",
  });
});

test("set up and answering, health is ready, and says whether things are there but never what they are", async () => {
  const env = { IDRIVE_RESELLER_TOKEN: "token-that-opens-everything", CRON_SECRET: "cron-secret-value" };
  const said = await checkHealth(env, async () => [{ "?column?": 1 }]);
  assert.equal(said.ready, true);
  assert.equal(said.database, "answers");
  const text = JSON.stringify(said);
  assert.ok(!text.includes("token-that-opens-everything"));
  assert.ok(!text.includes("cron-secret-value"));
});

test("a database that doesn't answer isn't ready, and what it said doesn't leave the service", async () => {
  const original = console.error;
  console.error = () => {};
  try {
    const said = await checkHealth({ IDRIVE_RESELLER_TOKEN: "t", CRON_SECRET: "c" }, async () => {
      throw new Error("connect ECONNREFUSED postgres://user:hunter2@db.internal:5432");
    });
    assert.equal(said.ready, false);
    assert.equal(said.database, "doesn't answer");
    assert.ok(!JSON.stringify(said).includes("hunter2"));
  } finally {
    console.error = original;
  }
});

test("the health endpoint answers, uncached, without a database configured", async () => {
  const saved = { ...process.env };
  delete process.env.DATABASE_URL;
  delete process.env.POSTGRES_URL;
  try {
    const response = await health();
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal((await response.json()).database, "not set up");
  } finally {
    process.env = saved;
  }
});

const asking = (authorization) =>
  new Request("https://backup.example/api/cron/nightly", authorization ? { headers: { authorization } } : {});

test("only a request carrying the cron secret is Vercel Cron's", () => {
  assert.equal(fromVercelCron(asking("Bearer right"), "right"), true);
  assert.equal(fromVercelCron(asking("Bearer wrong"), "right"), false);
  assert.equal(fromVercelCron(asking("right"), "right"), false);
  assert.equal(fromVercelCron(asking(), "right"), false);
});

test("with no cron secret set, nobody gets in, not even with an empty one", () => {
  assert.equal(fromVercelCron(asking("Bearer "), ""), false);
  assert.equal(fromVercelCron(asking("Bearer undefined"), undefined), false);
});

test("the nightly job turns away anybody but Vercel Cron", async () => {
  const saved = process.env.CRON_SECRET;
  process.env.CRON_SECRET = "right";
  try {
    assert.equal((await nightly(asking("Bearer wrong"))).status, 401);
    const response = await nightly(asking("Bearer right"));
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { ran: [] });
  } finally {
    if (saved === undefined) delete process.env.CRON_SECRET;
    else process.env.CRON_SECRET = saved;
  }
});
