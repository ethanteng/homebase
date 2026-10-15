import { migrate } from "./schema.mjs";

// The service's Postgres, added from Vercel's marketplace, which puts its address in DATABASE_URL
// (some providers say POSTGRES_URL). One connection per function instance is plenty: nothing here is
// busy. pg is loaded when it's first needed, so nothing that never touches the database loads it.
//
// pg waits forever by default, to connect and for an answer. A database that's gone quiet would then
// hold every request until Vercel stops the function, so both are bounded, well inside its time.
//
// Everything here works on a small database object, { query, exec, transaction }, so the same code
// runs on pg in production and on PGlite in the tests.

let shared;

export function databaseUrl(env = process.env) {
  return env.DATABASE_URL || env.POSTGRES_URL || null;
}

/** The service's database, or null where it has none: a preview deployment, say. */
export function database(env = process.env) {
  const url = databaseUrl(env);
  if (!url) return null;
  shared ??= postgres(url);
  return shared;
}

/** A database over pg. */
export function postgres(url) {
  const pool = import("pg").then(({ default: pg }) => new pg.Pool({
    connectionString: url,
    max: 1,
    connectionTimeoutMillis: 5_000,
    query_timeout: 15_000,
    statement_timeout: 15_000,
  }));
  return {
    async query(text, values) {
      return (await pool).query(text, values);
    },
    async exec(sql) {
      await (await pool).query(sql);
    },
    async transaction(work) {
      const client = await (await pool).connect();
      try {
        await client.query("begin");
        const result = await work({
          query: (text, values) => client.query(text, values),
          exec: async (sql) => { await client.query(sql); },
        });
        await client.query("commit");
        return result;
      } catch (error) {
        await client.query("rollback").catch(() => {});
        throw error;
      } finally {
        client.release();
      }
    },
  };
}

const brought = new WeakMap();

/**
 * The database, with its tables brought up to date: once per instance, however many requests ask
 * at once. A failure isn't remembered, so the next request tries again.
 */
export async function ready(db) {
  let migrating = brought.get(db);
  if (!migrating) {
    migrating = migrate(db);
    brought.set(db, migrating);
    migrating.catch(() => brought.delete(db));
  }
  await migrating;
  return db;
}
