# Uncloud Backup

An encrypted copy of a household's Uncloud kept offsite, in IDrive e2, set up and paid for through
Uncloud. Nobody signs in to iDrive, and nobody but the household can read the copy.

This document is the design; the phases at the end say how much is built. What it says about iDrive comes from
`tools/idrive-spike/`, which ran every step below against IDrive e2 for real (Oregon-2, October
2026), and the figures quoted are from those runs. What is still open is listed at the end.

## Decisions

| | Decided |
| --- | --- |
| Storage | IDrive e2, through the reseller API. One e2 account per household. |
| Backup engine | restic, carried with the app like Syncthing, run by the host. |
| Encryption | restic's, on the host. The password never leaves the household. |
| Protecting history | Object lock in **compliance** mode on every household's bucket, plus a rule that expires old versions. |
| Who holds what | The reseller token and a full-access key live only in the backup service. The host holds a key for its one bucket, and no more. |
| Over quota, unpaid | The account turns read-only: backups stop, restores still work. |
| Cancelling | `remove_user`, after a delay, with an alert. |
| Where the service runs | A Vercel project of its own, separate from the landing page's. |

## The shape of it

```
Uncloud host ──(its own credential)──▶ backup service ──(reseller token)──▶ iDrive reseller API
     │                                  (Vercel, Postgres)                   accounts, keys, quota
     │
     └── restic ──(S3, one bucket, encrypted on the host)──▶ the household's e2 bucket
```

Three parties, three kinds of key:

- **The reseller token** makes and removes e2 accounts, gives them storage and hands out keys. It
  can destroy any household's backup at once, object lock or not (below). It exists in one place:
  the backup service's environment.
- **The service's key** for a household is a full-access e2 key for that account. The service
  uses it to make the bucket, set and check its protection, and roll it back. It's made when it's
  needed and needn't be kept.
- **The host's key** reads and writes one bucket. It can't reach any other bucket, make a bucket,
  delete its own, or get past object lock.

The host never sees the reseller token or the service's key, and the service never sees the
backup's password.

## One account per household

Each household is its own e2 account, made with `create_user` under an address Uncloud controls,
with a random password nobody learns. Quota, usage, read-only and removal then belong to the
household rather than to a bucket in a shared account, and a mistake or a break-in touching one
account touches nobody else's.

Setting one up, in the order the service does it:

1. `create_user`: well under a second.
2. `enable_user_region`, which gives the account storage and returns its endpoint. **6.7 to
   44.4 seconds** across the runs, usually 7 to 16.
3. A full-access key (`create_access_key`, permissions 2). Usable after 0.2 to 5.4 seconds.
4. The bucket, made over S3 with object lock enabled, then versioning on, the default lock set,
   and the expiry rule set (below). The reseller API has no call for buckets.
5. The host's key: permissions 2, `buckets` set to that one bucket, `disable_delete_bucket`,
   `disable_delete_version`, and no `bypass_governance_retention`. Usable after 0.1 to 0.2 seconds.

The account is ready a few seconds to a minute after it's asked for. The service runs it as a job
that remembers each step done, because the reseller API's calls aren't transactional: a setup
interrupted after step 2 picks up at step 3.

Remembering isn't enough on its own. A step can succeed at iDrive and the function die, or lose
the answer, before the step is recorded, so the next attempt finds the work half done. So each
step first looks at what's already there, and only acts on what's missing:

- **The user**: its address is derived from the household, so the job asks `/users` for it
  first, and `email_already_in_use` on `create_user` means it's already made.
- **Storage**: `list_user_regions` first; `region_already_enabled` means it's done.
- **Keys** are named for what they're for (`service-<household>`, `host-<host>`). A key's secret
  is shown once, when it's made, so a key whose answer was lost is no use to anyone: the job
  lists the account's keys, removes any with the name it's about to use, then makes it afresh.
  Nothing is left behind that nobody holds the secret to.
- **The bucket**: if it's already there and the account's own, it's done. Versioning, the lock
  and the expiry rule are settings, so setting them again changes nothing.

## Protecting history

A backup is only worth having if the computer it backs up can't destroy it. A host can be stolen,
broken into or encrypted by ransomware, and its key goes with it. So the bucket keeps every
version of every object, and the host's key must not be able to remove them.

### What doesn't work

- **`disable_delete_version`** on the host's key. It doesn't stop the key destroying versions: a
  key made with it destroyed all 20 it tried to. It does keep the key off the bucket's lifecycle
  rules, though: every spike key with it was refused replacing or deleting the expiry rule, and a
  key made without it deleted the rule. So the host's key has it, for that.
- **Object lock in governance mode.** The host's key can't delete a locked version, with or
  without asking to bypass the lock. But asking to bypass it while moving the version's lock date
  is allowed, though the key wasn't given `bypass_governance_retention`. The spike moved a lock
  from a day away to eight seconds away, waited, and destroyed the version. A compromised host
  could do that to the whole backup.

### What does

**Object lock in compliance mode.** Every version is locked from the moment it's written until
its lock date, and in compliance mode nobody can bring that date forward, bypass or not. In the
spike, the host's key couldn't move a lock, couldn't destroy a version either way, and couldn't
suspend versioning or touch the expiry rule. After it tried to wipe the bucket, the service lifted
the delete markers it left and every file restored byte for byte.

What compliance costs: nothing can delete a version before its lock ends, the service included.
Something backed up by mistake stays, and is billed, for the lock period. Cancelling a household
is unaffected, because `remove_user` removes an account's data whatever its locks; it did so in
9.3 seconds under compliance.

### The lock period

How long each version stays locked is meant to be the window for noticing a compromised host: one
compromised for longer than that can wait for the oldest good versions to expire and then wipe
what's left. That holds only once nothing the backup depends on is ever left unlocked (below). It's
also how long every replaced or deleted version stays billed. **Not yet
chosen**: it waits for whether iDrive bills old versions, which the spike is measuring. A starting
point for the discussion is 30 days.

### When a lock runs out

A lock protects a version until its date, and no longer. After that, the host's key can destroy
the version, since its key can delete versions (`disable_delete_version` doesn't stop it). restic
writes a pack once and every later snapshot goes on using it, so most of a backup is packs older
than the lock period. A host compromised today could destroy all of those today, without waiting:
the lock period bounds how much of the backup is safe, not how long a compromise can go unnoticed.

Not yet tested against iDrive, though it's how object lock works on S3, and **not yet solved**. The
ways out:

- **A key that can't destroy versions**, only hide them behind delete markers, which is all restic
  needs, and what `disable_delete_version` promised. A question for iDrive.
- **Locks kept rolling.** The service pushes out the lock on every version a snapshot still uses
  before it runs out, so nothing the backup depends on is ever unlocked. That's one request per
  object every few weeks, tens of thousands for a large backup, so a job of its own rather than a
  corner of the nightly check.

### The expiry rule

A lifecycle rule on every bucket removes a version a set number of days after it's replaced or
deleted, once its lock allows, along with delete markers with nothing behind them. iDrive accepts
the rule and reads it back, but drops the part that would clear unfinished uploads. Whether iDrive
actually acts on the rule is being measured. If it doesn't, the service deletes expired versions
itself, which needs no password and no bypass.

### What the service watches

The host's key can change the bucket's **default** lock period. That only governs what's written
next, not any version already written, but a compromised host could quietly stop locking new
backups. So every night the service reads back each bucket's object lock (compliance, the chosen
period), versioning and expiry rule, puts right anything that has changed, and raises an alert,
since a change means a host's key has been used against its own bucket.

### Locks pushed further out

A lock can always be pushed later, and in compliance mode nobody can bring it back. So a
compromised host can lock things for far longer than the chosen period: by raising the default
and then writing, or by pushing out the lock date of versions already there. Resetting the default
doesn't undo either, and whatever is locked stays billed until its date.

- **It's bounded by the quota.** A host can't lock more than the account can hold, and an account
  that fills up turns read-only.
- **It's watched for.** The nightly check also reads the lock date of every version written since
  the night before, and samples older ones. A date beyond its write time plus the chosen period
  means somebody pushed it out, and raises the same alert as a changed default.
- **The way out is a new account.** Since nothing shortens a compliance lock, a poisoned account
  is replaced rather than repaired: the service makes a fresh one, the host backs up into it, and
  once the household has a good backup there, `remove_user` takes the old account with everything
  locked in it. Any history only the old account held goes with it, so this is a decision for the
  household, made with them, not something the service does by itself.

Whether a key can be denied changing locks at all, which would close this off, is a question for
iDrive.

### Rolling back

After a compromise, the service makes each object's last version from before it current again:
it lifts the delete markers placed since, and copies back any object overwritten since. That needs
the service's key, not the backup's password, and leaves restic seeing every snapshot from before
the compromise; anything the attacker wrote is extra snapshots to ignore. The spike did the
simple form of this, lifting markers after a wipe, and restored every file.

## Encryption and the recovery kit

restic encrypts everything on the host, names included, before it leaves. iDrive and Uncloud see
only sizes and times. The password is kept on the host, sealed with `host.key` like connector
tokens.

A backup exists for the day the host is gone, so the password can't live only there. At setup the
administrator gets a **recovery kit**: the password, and a recovery token the service accepts in
place of the lost host's credential. With it, a new host gets a fresh key (the old one is revoked)
and can open the backup. In the beta the kit alone is enough; once there are Uncloud accounts,
rebuilding a host will also need the account's sign-in.

While the host is alive it can issue a new kit and retire the old (`restic key add`, `key
remove`), so a lost kit is a nuisance rather than a loss, as long as it's noticed in time. Whether
to offer an opt-in recovery copy held by Uncloud is undecided.

## Over quota, unpaid, and cancelled

- **Quota** is the account's e2 quota with enforcement on (`edit_user`). Once iDrive counts usage
  past it, the account turns read-only. In the spike that took somewhere between ten minutes and
  8.5 hours, and a host can upload past the quota in the meantime, so the service leaves headroom.
- **Read-only** refuses new writes with `AccessDenied` (403) and still serves reads, so a
  household over quota, or whose payment has failed, can still restore. That makes read-only, not
  disabling the account, the first response to both.
- **Cancelling** ends with `disable_user` then `remove_user`, which removes the account and its
  data at once: 9.3 to 16.5 seconds in the spike, locked data included. Because it ignores object
  lock, it is the one thing that can destroy a backup, and it's the reseller token that can do it.
  So the service only removes an account after a delay, and alerts on every removal. The token
  goes nowhere but the service.

## Usage and billing

`/users` (`storage_used`), `usage_stats` (by day) and `bucket_stats` all agree with what the bucket
holds once they catch up, which took up to 8.5 hours. Right after a backup they report nothing, and
`bucket_stats` says a new bucket doesn't exist, as iDrive documents. `bucket_stats` counts old
versions among its files. Whether `storage_used` counts their bytes is being measured.

iDrive bills the reseller, not the household. The smallest reseller plan was 50 TB at $222.75 a
month (or $2,227.50 a year, the first year half off), with use beyond it at $0.005 per GB a month.
That's paid whatever the number of households. If iDrive's payment from the reseller fails, the
reseller API answers `payment_failed`, so one lapsed card would touch every household.

## The host

restic is carried with the app and run by the server as a child process, as Syncthing is. Its
pinned binary comes from `scripts/fetch-restic.sh`.

- **Nightly backups** of everyone's folders, plus a consistent copy of the control database
  (SQLite's online backup, not a copy of the live file) and `host.key`. Not `.homebase/index.db`,
  which is rebuilt, or `.homebase/uploads/`.
- **Pruning, lightly and seldom.** Under object lock every object a prune rewrites leaves its old
  copy behind, locked and billed. restic's default of tolerating 5% unused space keeps rewrites
  rare. Forcing them (`--max-unused 0`) turned 181 MB of a 235 MB backup into old versions.
- **Time limits on restic.** Reading an object behind a delete marker gets a 405 from iDrive where
  S3 says 404; restic doesn't recognise it and retries for about fifteen minutes. The host gives
  restic a limit and, when it can't open the repository, asks the bucket directly what's there.
- **Restores without a lock** (`--no-lock`), since taking one is a write and a read-only account
  refuses it.
- **A 403 on upload is asked about, not assumed.** A full account answers 403, but so do a
  revoked or wrongly scoped key, a disabled account and a failed signature. So the host asks the
  service, which reads the account's state from iDrive: only when the account is over quota and
  read-only does Settings say storage is full. Otherwise it reports the failed backup as it is, a
  key that no longer works included, which the host replaces through the service.

Upload speed from a home connection varied between 10 and 146 Mbit/s across the runs. A first
backup of a household's library is a matter of hours to days, and Settings shows its progress.

## The service

A Vercel project of its own, **uncloud-backup**, deploying from `services/backup/`, so the reseller
token is in no other project's environment and no deployment of the landing page can reach it.

- **Postgres**, from Vercel's marketplace, for households, hosts, the state of each setup job,
  and invite codes.
- **Functions** for: redeeming an invite and registering a host; handing a host its endpoint,
  bucket and key; replacing a host's key; and the rollback. Each setup step finishes within a
  function's time, the longest seen being 44 seconds, and a job resumes where it stopped.
- **Vercel Cron** for the nightly work: checking every bucket's protection, reading usage, and
  carrying out delayed removals.
- **The reseller client** is `tools/idrive-spike/reseller.mjs`, written to become this.

In the beta, households join with an invite code. The operator makes one, which is kept only as a
hash and works once; a host uses it to make its household and itself, and gets a credential of its
own, kept only as a hash too, which it sends with everything after. Billing, and Uncloud accounts,
come later.

A household's storage is set up when its host first asks for it (`POST /api/host/storage`). Each
request takes the setup as far as a function's time allows, from wherever it stopped, and answers
202 until it's done; then the host gets its key. A lease on the household keeps two requests from
setting it up at once. The account is `household-<id>@uncloud.life`, with a password nobody keeps
and, in the beta, a quota of 1,000 GB. The bucket is `uncloud-<household>`. The service's key is
made for each go and removed when it's done, so the service holds no key to anybody's bucket between
requests. The operator sees every household's setup, and the last thing that went wrong with it, at
`GET /api/operator/households`.

## iDrive's quirks

| Where S3 would | iDrive | What we do |
| --- | --- | --- |
| Honour `disable_delete_version` | Lets the key destroy versions anyway, but keeps it off lifecycle rules | Object lock, and the option kept for the rules |
| Refuse moving a governance lock without the bypass permission | Allows it when bypass is asked for | Compliance mode |
| Refuse deleting a locked version with `AccessDenied` (403) | `InvalidRequest` (400), "Object is WORM protected" | Judge by listing the bucket, not by the code |
| Answer a read behind a delete marker with 404 | 405 | Time limits on restic |
| Keep a lifecycle rule as given | Drops `AbortIncompleteMultipartUpload` | Nothing yet; clear unfinished uploads ourselves if they build up |
| Count usage at once | Up to 8.5 hours later | Headroom on quota; never bill from a fresh reading |
| Let a key be denied changing locks | No way found | Nightly check; a new account if one's poisoned |

Requests that need a checksum (lock settings, retention, versioning, lifecycle) are sent with
`Content-MD5`, which iDrive accepts.

## Still open

- **Are old versions billed, and does the expiry rule run?** Measured on two kept test accounts
  through 17 October. Together they decide the lock period and whether the service needs its own
  job to delete expired versions.
- **Versions whose lock has run out** can be destroyed by a host's key (above). Which way out, and
  testing it on a kept account once the measurements are done.
- **For iDrive:**
  - Is `disable_delete_version` meant to do anything? Can a key be allowed delete markers but not
    destroying versions?
  - Should a key without `bypass_governance_retention` be able to move a governance lock when it
    asks to bypass it? It looks like a bug.
  - Can a key be denied changing the bucket's default lock, or pushing out a version's lock?
  - Is the 405 for an object behind a delete marker intended?
  - Is `AbortIncompleteMultipartUpload` supported in lifecycle rules?
  - What does `is_managed: false` mean for users made with `create_user`?
  - Is there a limit on users per reseller account, or on request rates?
  - Is a plan's storage shared across its users?
- **Pricing**, and how long a cancelled household's data is kept before removal.
- **An opt-in recovery copy held by Uncloud**, or none.

## Phases

1. **Phase 0, the spike.** Done but for the measurements above.
2. **Phase 1, a backup that runs.** On the host: restic carried, the engine above, a Backup panel
   in Settings. In the service: invites, setting up accounts, the nightly check, the rollback and
   delayed removal. Tested with a few households before anything is sold. Built so far: restic carried
   and the engine; the service with its invites, and setting up accounts and handing out hosts'
   keys.
3. **Phase 2, getting it back.** Restoring files from My files under the Bin's rules, and
   rebuilding a host from its recovery kit.
4. **Phase 3, selling it.** Uncloud accounts, billing, read-only on failed payments, and the
   pricing page.
5. **Phase 4, hardening.** Regular kit checks, a kit per administrator, a region per household,
   automatic restore tests, and the opt-in recovery copy if there is one.
