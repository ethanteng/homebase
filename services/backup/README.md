# Uncloud Backup's service

The service half of Uncloud Backup ([the design](../../docs/backup.md)). It sets up each
household's storage in IDrive e2 through the reseller API, hands each host the key for its one
bucket, and every night checks each bucket is still protected. It's the only thing that holds the
reseller token.

It's the Vercel project **uncloud-backup**, separate from the landing page's, so the token is in no
other project's environment. It deploys from this folder of this repository: a push that changes
nothing here doesn't deploy it.

So far: its tables, invites, and hosts joining with one. Setting up each household's storage and
the nightly checks come next.

## What's here

- `api/health.js`: whether the service is set up. It says which of the reseller token, the operator
  token, the cron secret and the database are in place, whether the database answers, and whether
  its tables are up to date. It never says what any of them are.
- `api/operator/invites.js`: the operator's invites. `POST` with `{ "note": "the Smiths", "days":
  30 }` makes one and answers with its code, which isn't kept anywhere, only its hash; `GET` lists
  every invite and what became of it. Both need `Authorization: Bearer <OPERATOR_TOKEN>`, and with
  no operator token set nobody gets in.
- `api/hosts.js`: a host joining. `POST { "invite": "K7QM-3XPD-9TWA-HF2R", "name": "Mac mini" }`
  uses the invite once, makes the household and the host, and answers with the host's credential,
  shown this once. The host keeps it and sends it as a bearer token from then on.
- `api/host.js`: `GET` with that credential says which host and household it is, so a host can tell
  its credential still works.
- `api/cron/nightly.js`: Vercel Cron's nightly call, at 10:00 UTC (`vercel.json`). It only lets
  Vercel in, by the cron secret.
- `lib/reseller.mjs`: the IDrive e2 reseller API client, from the spike (`tools/idrive-spike/`),
  which still uses it.
- `lib/schema.mjs`: the tables, as migrations. They're applied the first time a function instance
  touches the database, under a lock, so two instances never both apply one, and a deployment never
  runs against tables older than it expects. A migration that has shipped is never edited.
- `lib/db.mjs`: Postgres, through `pg`.

## Setting it up

The project exists and is connected to this repository. Three things it needs are yours to give it,
in the Vercel dashboard under the **uncloud-backup** project:

1. **A database.** Under **Storage**, add Postgres from the marketplace. Neon is the usual choice,
   and its free plan is plenty to start. Connect it to this project; it puts the address in
   `DATABASE_URL` for every environment.
2. **The reseller token.** Under **Settings → Environment Variables**, add `IDRIVE_RESELLER_TOKEN`,
   marked **Sensitive**, for Production. It's the same token the spike reads from its `.env`. Keep it
   out of Preview, so no branch can use it.
3. **The cron secret.** Add `CRON_SECRET`, marked **Sensitive**, set to a long random value, for
   example from `openssl rand -hex 32`. Vercel sends it with each cron call, and the nightly job turns
   away anything without it.
4. **The operator token.** Add `OPERATOR_TOKEN` the same way, with a value of its own. It's what lets
   you make and list invites, and it's never sent to a host.

Then redeploy, and `/api/health` on the production address should say `"ready": true`.

## Tests

```sh
npm --prefix services/backup test
```

They need no network and no database server: their Postgres is [PGlite](https://pglite.dev),
Postgres itself built to run inside Node, so the SQL runs for real. `scripts/check.sh` runs them too.
