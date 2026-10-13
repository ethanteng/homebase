# iDrive e2 spike

Phase 0 of Uncloud Backup. Before anything is built on IDrive e2, this script puts one throwaway
household through the whole lifecycle, for real, and writes down what happened at each step.

It uses the reseller API the way Uncloud's backup service would, and restic the way a host would.
Nothing here is part of Uncloud itself. The reseller client in `reseller.mjs` is written so it can
become the service's client later.

## What a run does

1. **Picks a region**, the first active one unless `IDRIVE_REGION` names another.
2. **Creates a test user** with `create_user`. It is managed by you, has a random password nobody
   learns, and gets no emails.
3. **Gives it storage** in that region (`enable_user_region`), and reads back the endpoint.
4. **Makes a full-access key**, like the one the service would hold, and times how long until it
   works.
5. **Creates a bucket with versioning on**, plus a second bucket that the host's key shouldn't
   reach.
6. **Makes the host's key:** limited to the one bucket, and unable to delete versions or the
   bucket.
7. **Checks that key can't reach past its bucket:** it can't list or write the other bucket,
   create a bucket, or delete its own.
8. **Backs up a made-up library** with restic, using the host's key. The library is 200 MB by
   default and includes documents, photos, a video, awkward names, a hidden file, an empty file
   and an empty folder. The step reports upload speed.
9. **Changes the library and backs up again.**
10. **Prunes the first backup** with the host's key. In a versioned bucket, deletes leave delete
    markers behind, and this step checks restic still copes.
11. **Checks every byte, restores, and compares** with the library, byte for byte.
12. **Tries to wipe the bucket with the host's key**, deleting every object and every version.
13. **Rolls the bucket back** with the service's key, then checks and restores again.
14. **Compares usage three ways:** what `/users`, `usage_stats` and `bucket_stats` report, against
    what the bucket really holds, older versions included.
15. **Optionally, goes over quota** (with `--quota-test`). It stores about 1.3 GB, sets a 1 GB quota
    with enforcement on, and records whether new writes are refused and whether a restore still
    works.
16. **Revokes the host's key**, and times how long until it stops working.
17. **Removes the user** with its data still stored, which is what cancelling a subscription would
    do. If iDrive won't allow that, it empties the buckets first, and the report says which way it
    went.

A step that doesn't go as the design hopes is marked ❌ and the run carries on wherever later steps
still mean something. Each step is timed.

Only a deliberate refusal from the store counts as a key being stopped: `AccessDenied`, or a key
that is no longer valid. A dropped connection, throttling or a server error is retried. If it
keeps happening, the step fails rather than passing, because a probe that merely failed proves
nothing about what the key may do. The revocation timing likewise ends only at a real refusal.

## What it touches

One new user on your reseller account, named from `SPIKE_EMAIL`. That user stores about 200 MB for
a few minutes, or about 1.3 GB with `--quota-test`, and is removed at the end unless you pass
`--keep`. If a run without `--keep` stops partway, it removes the user anyway. That includes
pressing Ctrl-C, which stops restic and any waiting, starts no further step, and removes the user.
Pressing Ctrl-C a second time leaves at once. If the user can't be removed, the script prints the
command that removes it.

Keys and passwords live only in memory while the script runs. They are never printed, and never
written to the report, the state file or the raw-call log. Access key IDs are cut to their first
four characters wherever they're recorded. The test library and restores are
written under `runs/<id>/data` and deleted after a clean run. When a step fails they are kept, so
you can look at them; with `--quota-test` they come to about 3 GB.

## Before the first run

```sh
brew install restic
cd tools/idrive-spike
npm install
cp example.env .env   # then fill in the token and SPIKE_EMAIL
```

`.env` is ignored by git. Use an address at a domain you control for `SPIKE_EMAIL`, with `{id}`
in it, so every run makes a user nobody has used.

## Running it

```sh
node spike.mjs regions                  # read-only: checks the token works, lists regions
node spike.mjs run                      # the whole lifecycle, removed afterwards
node spike.mjs run --keep               # leave the user, to look again later
node spike.mjs run --quota-test         # also go over quota (implies --keep)
node spike.mjs stats <run id>           # hours later: has usage caught up? still writable?
node spike.mjs cleanup <run id>         # remove a kept run's user and everything it stored
```

Each run writes to `runs/<id>/`:

- `report.md`: what happened at each step.
- `calls.jsonl`: every reseller API call and answer, with secrets removed. Most answers' shapes
  aren't in iDrive's docs, so this is where they get learned.
- `state.json`: the user, region, endpoint and buckets, and nothing secret.

`stats` and `cleanup` add their own reports beside these.

iDrive says usage can take up to 24 hours to count, and bucket stats are cached for hours. So the
most useful run is `run --quota-test`, followed by `stats` a few hours later and again the next
day, then `cleanup`.

## What the answers decide

- **The host's key can't destroy versions (12), and the bucket can be rolled back (13).** This is
  the protection against a compromised host or ransomware. If it fails, the design needs object
  lock, or another way to keep history out of the host's reach.
- **Prune works with versioning on (10).** If it doesn't, restic and the protection above can't
  both be used as planned.
- **Older versions count toward usage (14).** If they do, deleted data keeps costing money until
  it expires, which affects pricing, and the service needs a rule for expiring old versions.
- **Read-only still allows restores (15).** This decides whether over-quota and failed payments
  can be handled by making the account read-only rather than disabling it.
- **How fast keys start and stop working (4, 6, 16).** This sets how long signing up takes, and
  how quickly "this host was stolen" takes effect.
- **Removing a user with its data in place (17).** This decides what cancellation looks like.

## Tests

```sh
npm test
```

These cover the parts that don't need iDrive: the reseller client's requests and errors, what
gets recorded, and the comparison every restore is judged by.
