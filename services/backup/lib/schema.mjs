// The service's tables, as the migrations that make them, oldest first. They're brought up to date
// the first time a function instance touches the database (db.mjs), so a deployment never runs
// against tables older than it expects, and nothing depends on what a build can see.
//
// A migration that has shipped is never edited: a change is a new one at the end.

export const migrations = [
  {
    name: "001_households_invites_hosts",
    sql: `
      -- A household backing up through Uncloud. Its e2 account and bucket come with setting it up.
      create table households (
        id uuid primary key default gen_random_uuid(),
        created_at timestamptz not null default now()
      );

      -- A code the operator gives somebody so their household can join. Only its hash is kept:
      -- the code itself is shown once, when it's made. One use, and it expires.
      create table invites (
        id uuid primary key default gen_random_uuid(),
        code_hash text not null unique,
        note text,
        created_at timestamptz not null default now(),
        expires_at timestamptz not null,
        redeemed_at timestamptz,
        household_id uuid references households (id)
      );

      -- An Uncloud host backing up for a household. It proves which it is with a credential only it
      -- holds, of which only the hash is kept here.
      create table hosts (
        id uuid primary key default gen_random_uuid(),
        household_id uuid not null references households (id),
        name text not null,
        credential_hash text not null unique,
        created_at timestamptz not null default now(),
        last_seen_at timestamptz,
        revoked_at timestamptz
      );
      create index hosts_household on hosts (household_id);
    `,
  },
];

// Any number will do, as long as nothing else in the database takes the same advisory lock.
const MIGRATING = 74_2001;

/**
 * Applies whichever migrations the database hasn't had, in order, in one transaction. Instances
 * starting at once take turns on a lock, so a migration never runs twice; whoever waited finds
 * nothing left to do.
 */
export async function migrate(db) {
  await db.transaction(async (tx) => {
    await tx.query("select pg_advisory_xact_lock($1)", [MIGRATING]);
    await tx.exec(`
      create table if not exists schema_migrations (
        name text primary key,
        applied_at timestamptz not null default now()
      );
    `);
    const { rows } = await tx.query("select name from schema_migrations");
    const applied = new Set(rows.map((row) => row.name));
    for (const migration of migrations) {
      if (applied.has(migration.name)) continue;
      await tx.exec(migration.sql);
      await tx.query("insert into schema_migrations (name) values ($1)", [migration.name]);
    }
  });
}
