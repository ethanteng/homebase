// The service's own endpoints: what /api/health says and never says, and that only Vercel Cron gets
// through to the nightly job. No network and no database: the database is a stand-in where one's needed.

import { test } from "node:test";
import assert from "node:assert/strict";
import { checkHealth } from "../lib/health.mjs";
import { freshDatabase } from "./database.mjs";
import { fromVercelCron } from "../lib/cron.mjs";
import { GET as health } from "../api/health.js";
import { GET as nightly } from "../api/cron/nightly.js";

test("with nothing set up, health says so and isn't ready", async () => {
  const said = await checkHealth({}, null);
  assert.deepEqual(said, {
    service: "uncloud-backup",
    ready: false,
    configured: { resellerToken: false, operatorToken: false, cronSecret: false, database: false },
    database: "not set up",
    tables: null,
  });
});

const everything = { IDRIVE_RESELLER_TOKEN: "token-that-opens-everything", OPERATOR_TOKEN: "operator-value", CRON_SECRET: "cron-secret-value" };

test("set up, answering and with its tables up to date, health is ready, and never says what anything is", async () => {
  const db = await freshDatabase();
  try {
    const said = await checkHealth(everything, db);
    assert.equal(said.ready, true);
    assert.equal(said.database, "answers");
    assert.equal(said.tables, "current");
    const text = JSON.stringify(said);
    for (const secret of Object.values(everything)) assert.ok(!text.includes(secret));
  } finally {
    await db.close();
  }
});

const quietly = async (run) => {
  const original = console.error;
  console.error = () => {};
  try { return await run(); } finally { console.error = original; }
};

test("a database that doesn't answer isn't ready, and what it said doesn't leave the service", async () => {
  const said = await quietly(() => checkHealth(everything, {
    query: async () => { throw new Error("connect ECONNREFUSED postgres://user:hunter2@db.internal:5432"); },
  }));
  assert.equal(said.ready, false);
  assert.equal(said.database, "doesn't answer");
  assert.equal(said.tables, null);
  assert.ok(!JSON.stringify(said).includes("hunter2"));
});

test("a database that has gone quiet is reported as not answering, not waited on", async () => {
  const started = Date.now();
  const said = await quietly(() => checkHealth({}, { query: () => new Promise(() => {}) }, { deadline: 50 }));
  assert.equal(said.database, "doesn't answer");
  assert.ok(Date.now() - started < 2_000);
});

test("a database that answers but whose tables can't be brought up to date isn't ready", async () => {
  const said = await quietly(() => checkHealth(everything, {
    query: async () => ({ rows: [] }),
    transaction: async () => { throw new Error("permission denied for schema public"); },
  }));
  assert.equal(said.database, "answers");
  assert.equal(said.tables, "couldn't be brought up to date");
  assert.equal(said.ready, false);
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
