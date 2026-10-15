// The service's Postgres, added from Vercel's marketplace, which puts its address in DATABASE_URL
// (some providers say POSTGRES_URL). One connection per function instance is plenty: nothing here is
// busy. pg is loaded when it's first needed, so nothing that never touches the database loads it.

let pool;

export function databaseUrl(env = process.env) {
  return env.DATABASE_URL || env.POSTGRES_URL || null;
}

export async function query(text, values) {
  pool ??= import("pg").then(({ default: pg }) => new pg.Pool({ connectionString: databaseUrl(), max: 1 }));
  return (await pool).query(text, values);
}
