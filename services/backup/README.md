# Uncloud Backup's service

The service half of Uncloud Backup ([the design](../../docs/backup.md)). It sets up each
household's storage in IDrive e2 through the reseller API, hands each host the key for its one
bucket, and every night checks each bucket is still protected. It's the only thing that holds the
reseller token.

It's the Vercel project **uncloud-backup**, separate from the landing page's, so the token is in no
other project's environment. It deploys from this folder of this repository: a push that changes
nothing here doesn't deploy it.

So far: its tables, invites, hosts joining with one, and setting up each household's storage. The
nightly checks come next.

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
- `api/host/recovery.js`: `POST` with a host's credential makes its household a new recovery token,
  for its recovery kit, shown this once. Only its hash is kept, and it replaces any before it.
- `api/hosts/recover.js`: the first of recovery's two steps. `POST { "recovery": "…", "name":
  "New Mac" }` lets a new host in provisionally, with a credential of its own shown this once, and
  changes nothing about the household's hosts. A provisional host may get its storage, to try the
  kit's password on, and stops working after an hour unless it confirms.
- `api/host/recovery/confirm.js`, `api/host/recovery/abandon.js`: the second step. Confirming, once
  the password has opened the backup, revokes the household's other hosts and removes their e2 keys;
  withdrawing retires only the provisional host. The token goes on working until a new kit replaces
  it, which also retires a recovery already under way, so a recovery can always be tried again.
- `api/host/storage.js`: `POST` with that credential sets the household's storage up if it isn't
  yet, then answers with where the host backs up: the endpoint, the bucket, and a key of its own to
  that bucket alone, shown this once. Setting up takes up to a minute, over one request or a few:
  until it's done the answer is 202 with how far it has got, and the host asks again. Asking once
  it's done gives the host a new key, and the old one stops working.
- `api/operator/households.js`: `GET`, for the operator, lists every household, how far its setup
  has got, and the last thing that went wrong with it.
- `api/cron/nightly.js`: Vercel Cron's nightly call, at 10:00 UTC (`vercel.json`). It only lets
  Vercel in, by the cron secret.
- `lib/setup.mjs`: setting a household up: its e2 account, storage in a region, and a bucket with
  object lock, each step remembered once done and each looking at what's already at iDrive before
  making anything, so a setup cut short anywhere picks up without making anything twice. Then each
  host's key.
- `lib/protection.mjs`: how every bucket is protected, applied and read back: versioning, a lock in
  compliance mode for 30 days, and the rule that expires old versions 30 days after they're
  replaced.
- `lib/idrive.mjs`, `lib/s3.mjs`: iDrive as the service uses it, and the S3 client the spike proved
  against it.
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
Postgres itself built to run inside Node, so the SQL runs for real, and their iDrive is a pretend
one (`test/fake-idrive.mjs`) that answers as the spike saw the real one answer. `scripts/check.sh`
runs them too.

To try setting up against the real iDrive, with the spike's token:

```sh
cd services/backup
node --env-file=../../tools/idrive-spike/.env scripts/try-setup.mjs
```

It sets up one pretend household through the service's own code, checks what its host was given
can and can't do, and removes the account. It takes about a minute.
