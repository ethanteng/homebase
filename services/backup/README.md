# Uncloud Backup's service

The service half of Uncloud Backup ([the design](../../docs/backup.md)). It sets up each
household's storage in IDrive e2 through the reseller API, hands each host the key for its one
bucket, and every night checks each bucket is still protected. It's the only thing that holds the
reseller token.

It's the Vercel project **uncloud-backup**, separate from the landing page's, so the token is in no
other project's environment. It deploys from this folder of this repository: a push that changes
nothing here doesn't deploy it.

So far it's the setup: a health check and a nightly job that does nothing yet. Invites, setting up
households and the nightly checks come next.

## What's here

- `api/health.js`: whether the service is set up. It says which of the token, the database and the
  cron secret are in place, and whether the database answers. It never says what any of them are.
- `api/cron/nightly.js`: Vercel Cron's nightly call, at 10:00 UTC (`vercel.json`). It only lets
  Vercel in, by the cron secret.
- `lib/reseller.mjs`: the IDrive e2 reseller API client, from the spike (`tools/idrive-spike/`),
  which still uses it.
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

Then redeploy, and `/api/health` on the production address should say `"ready": true`.

## Tests

```sh
npm --prefix services/backup test
```

They need neither a network nor a database. `scripts/check.sh` runs them too.
