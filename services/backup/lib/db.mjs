// The service's Postgres, added from Vercel's marketplace, which puts its address in DATABASE_URL
// (some providers say POSTGRES_URL). One connection per function instance is plenty: nothing here is
// busy. pg is loaded when it's first needed, so nothing that never touches the database loads it.
//
// pg waits forever by default, to connect and for an answer. A database that's gone quiet would then
// hold every request until Vercel stops the function, so both are bounded, well inside its time.

let pool;

export function databaseUrl(env = process.env) {
  return env.DATABASE_URL || env.POSTGRES_URL || null;
}

export async function query(text, values) {
  pool ??= import("pg").then(({ default: pg }) => new pg.Pool({
    connectionString: databaseUrl(),
    max: 1,
    connectionTimeoutMillis: 5_000,
    query_timeout: 15_000,
    statement_timeout: 15_000,
  }));
  return (await pool).query(text, values);
}
