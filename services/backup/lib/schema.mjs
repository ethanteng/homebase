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
  {
    name: "002_household_storage",
    sql: `
      -- Setting up a household's storage (setup.mjs). Each step leaves what it made here, so a
      -- setup that stops picks up where it was: the e2 account, then storage in a region, then the
      -- bucket, made and protected. Nothing secret: the service's own key is made for each go and
      -- removed after it.
      alter table households
        add column account_email text unique,
        add column region text,
        add column storage_dn text,
        add column bucket text unique,
        add column ready_at timestamptz,
        add column setup_tried_at timestamptz,
        add column setup_failures integer not null default 0,
        add column setup_problem text,
        -- Whoever is setting the household up holds it until then, so two never do at once.
        add column setup_lease_until timestamptz;

      -- The e2 key a host was last given. Only its id: the secret goes to the host and nowhere else.
      -- Whoever is replacing it holds the host until then, so two requests never both do.
      alter table hosts
        add column key_id text,
        add column key_issued_at timestamptz,
        add column key_lease_until timestamptz;
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
