// Invites, households and hosts, against a real Postgres (PGlite): the tables, codes kept only as
// hashes, one use per invite, a host's credential, and the endpoints around them.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { freshDatabase } from "./database.mjs";
import { migrate, migrations } from "../lib/schema.mjs";
import { ready } from "../lib/db.mjs";
import { ServiceError } from "../lib/http.mjs";
import { createInvite, listInvites, newInviteCode, normalise, redeemInvite } from "../lib/invites.mjs";
import { authenticateHost } from "../lib/hosts.mjs";
import { createInviteRoute, currentHostRoute, listInvitesRoute, registerHostRoute } from "../lib/routes.mjs";

const NOW = new Date("2026-10-15T12:00:00Z");
const OPERATOR = "operator-token-for-tests";

async function withDatabase(run) {
  const db = await freshDatabase();
  try {
    await run(await ready(db));
  } finally {
    await db.close();
  }
}

const asking = (path, { method = "GET", token, body } = {}) => new Request(`https://backup.example${path}`, {
  method,
  headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), ...(body ? { "content-type": "application/json" } : {}) },
  body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
});

const refused = (code) => (error) => error instanceof ServiceError && error.code === code;

test("the tables are made once, however many times an instance asks", async () => {
  const db = await freshDatabase();
  try {
    await migrate(db);
    await migrate(db);
    const { rows } = await db.query("select name from schema_migrations");
    assert.deepEqual(rows.map((row) => row.name), migrations.map((migration) => migration.name));
    const { rows: tables } = await db.query(
      "select table_name from information_schema.tables where table_schema = 'public'");
    assert.deepEqual(tables.map((row) => row.table_name).sort(), ["hosts", "households", "invites", "schema_migrations"]);
  } finally {
    await db.close();
  }
});

test("an invite code is easy to read out and type, in any case and with or without its dashes", () => {
  const code = newInviteCode();
  assert.match(code, /^[2-9A-HJ-NP-Z]{4}(-[2-9A-HJ-NP-Z]{4}){3}$/);
  assert.notEqual(newInviteCode(), code);
  assert.equal(normalise(code.toLowerCase().replaceAll("-", " ")), code.replaceAll("-", ""));
  assert.equal(normalise("K7QM-3XPD-9TWA-HF2"), null);
  assert.equal(normalise("K7QM-3XPD-9TWA-HF2O"), null, "O isn't in the alphabet, so it's no code");
  assert.equal(normalise(42), null);
});

test("an invite's code is given once and kept only as a hash", async () => {
  await withDatabase(async (db) => {
    const invite = await createInvite(db, { note: "the Smiths", now: NOW });

    assert.equal(invite.note, "the Smiths");
    assert.equal(invite.expiresAt, "2026-11-14T12:00:00.000Z");
    const { rows: [stored] } = await db.query("select * from invites");
    const everything = JSON.stringify(stored);
    assert.ok(!everything.includes(invite.code));
    assert.ok(!everything.includes(invite.code.replaceAll("-", "")));
    assert.deepEqual((await listInvites(db, NOW)).map(({ id, state, note }) => ({ id, state, note })),
      [{ id: invite.id, state: "open", note: "the Smiths" }]);
  });
});

test("using an invite makes a household and a host, with a credential only the host gets", async () => {
  await withDatabase(async (db) => {
    const { code } = await createInvite(db, { now: NOW });

    const joined = await redeemInvite(db, { code, name: "  The Mac mini  ", now: NOW });

    assert.equal(joined.host.name, "The Mac mini");
    assert.match(joined.credential, /^[A-Za-z0-9_-]{43}$/);
    const { rows: [host] } = await db.query("select * from hosts");
    assert.equal(host.household_id, joined.household.id);
    assert.equal(host.credential_hash, createHash("sha256").update(joined.credential).digest("hex"));
    assert.ok(!JSON.stringify(host).includes(joined.credential));
    const [invite] = await listInvites(db, NOW);
    assert.equal(invite.state, "used");
    assert.equal(invite.household, joined.household.id);
  });
});

test("an invite works once, and unknown, used and expired codes all get the same answer", async () => {
  await withDatabase(async (db) => {
    const { code } = await createInvite(db, { days: 1, now: NOW });
    const { code: lapsing } = await createInvite(db, { days: 1, now: NOW });
    await redeemInvite(db, { code, name: "first", now: NOW });

    const tries = [
      redeemInvite(db, { code, name: "second", now: NOW }),
      redeemInvite(db, { code: lapsing, name: "late", now: new Date(NOW.getTime() + 2 * 86_400_000) }),
      redeemInvite(db, { code: newInviteCode(), name: "guess", now: NOW }),
      redeemInvite(db, { code: "not a code", name: "typo", now: NOW }),
    ];
    const answers = await Promise.allSettled(tries);
    assert.ok(answers.every((answer) => answer.status === "rejected" && refused("invite_invalid")(answer.reason)));
    assert.equal(new Set(answers.map((answer) => answer.reason.message)).size, 1);
    const { rows } = await db.query("select count(*)::int as hosts from hosts");
    assert.equal(rows[0].hosts, 1);
    assert.equal((await listInvites(db, new Date(NOW.getTime() + 2 * 86_400_000))).find((i) => i.state !== "used").state, "expired");
  });
});

test("if anything fails while an invite is being used, it's left unused and nothing half-made stays", async () => {
  await withDatabase(async (db) => {
    const { code } = await createInvite(db, { now: NOW });
    // The same database, but making the host fails partway through.
    const failing = {
      ...db,
      transaction: (work) => db.transaction((tx) => work({
        ...tx,
        query: (text, values) => text.startsWith("insert into hosts")
          ? Promise.reject(new Error("the disk is full")) : tx.query(text, values),
      })),
    };

    await assert.rejects(redeemInvite(failing, { code, name: "host", now: NOW }), /the disk is full/);

    assert.equal((await listInvites(db, NOW))[0].state, "open");
    const { rows } = await db.query("select (select count(*) from households)::int as households, (select count(*) from hosts)::int as hosts");
    assert.deepEqual(rows[0], { households: 0, hosts: 0 });
    // And it still works afterwards.
    assert.ok((await redeemInvite(db, { code, name: "host", now: NOW })).credential);
  });
});

test("a host is known by its credential until it's revoked, and each time is noted", async () => {
  await withDatabase(async (db) => {
    const { code } = await createInvite(db, { now: NOW });
    const joined = await redeemInvite(db, { code, name: "host", now: NOW });
    const later = new Date(NOW.getTime() + 3_600_000);

    const host = await authenticateHost(db, asking("/api/host", { token: joined.credential }), later);

    assert.deepEqual(host, { id: joined.host.id, household: joined.household.id, name: "host" });
    const { rows: [seen] } = await db.query("select last_seen_at from hosts");
    assert.equal(new Date(seen.last_seen_at).toISOString(), later.toISOString());
    assert.equal(await authenticateHost(db, asking("/api/host", { token: "somebody-elses" }), later), null);
    assert.equal(await authenticateHost(db, asking("/api/host"), later), null);
    await db.query("update hosts set revoked_at = $1", [later]);
    assert.equal(await authenticateHost(db, asking("/api/host", { token: joined.credential }), later), null);
  });
});

test("only the operator makes or sees invites, and with no operator token set, nobody does", async () => {
  await withDatabase(async (db) => {
    const deps = { db, env: { OPERATOR_TOKEN: OPERATOR }, now: NOW };

    await assert.rejects(createInviteRoute(asking("/api/operator/invites", { method: "POST", body: {} }), deps), refused("operator_only"));
    await assert.rejects(listInvitesRoute(asking("/api/operator/invites", { token: "guess" }), deps), refused("operator_only"));
    await assert.rejects(listInvitesRoute(asking("/api/operator/invites", { token: "" }), { ...deps, env: {} }), refused("operator_only"));

    const made = await createInviteRoute(
      asking("/api/operator/invites", { method: "POST", token: OPERATOR, body: { note: "the Joneses", days: 7 } }), deps);
    assert.equal(made.status, 201);
    const invite = await made.json();
    assert.match(invite.code, /^[2-9A-HJ-NP-Z]{4}(-[2-9A-HJ-NP-Z]{4}){3}$/);
    assert.equal(invite.expiresAt, "2026-10-22T12:00:00.000Z");

    const listed = await (await listInvitesRoute(asking("/api/operator/invites", { token: OPERATOR }), deps)).json();
    assert.equal(listed.invites.length, 1);
    assert.equal(listed.invites[0].state, "open");
    assert.ok(!JSON.stringify(listed).includes(invite.code));
  });
});

test("a host joins with a code, then proves which it is with its credential", async () => {
  await withDatabase(async (db) => {
    const deps = { db, env: {}, now: NOW };
    const { code } = await createInvite(db, { now: NOW });

    const joined = await registerHostRoute(asking("/api/hosts", { method: "POST", body: { invite: code, name: "Mac mini" } }), deps);
    assert.equal(joined.status, 201);
    assert.equal(joined.headers.get("cache-control"), "no-store");
    const { credential, host, household } = await joined.json();

    const asked = await currentHostRoute(asking("/api/host", { token: credential }), deps);
    assert.equal(asked.status, 200);
    assert.deepEqual(await asked.json(), { host: { id: host.id, name: "Mac mini" }, household: { id: household.id } });
    await assert.rejects(currentHostRoute(asking("/api/host", { token: "wrong" }), deps), refused("not_a_host"));
  });
});

test("a request that isn't a JSON object, or comes where there's no database, is turned away", async () => {
  await withDatabase(async (db) => {
    await assert.rejects(registerHostRoute(asking("/api/hosts", { method: "POST", body: "not json" }), { db, env: {}, now: NOW }),
      refused("bad_request"));
    await assert.rejects(registerHostRoute(asking("/api/hosts", { method: "POST", body: "[1, 2]" }), { db, env: {}, now: NOW }),
      refused("bad_request"));
    await assert.rejects(registerHostRoute(asking("/api/hosts", { method: "POST", body: { invite: "x" } }), { db: null, env: {}, now: NOW }),
      refused("no_database"));
  });
});
