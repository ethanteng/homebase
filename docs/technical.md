# Uncloud technical reference

Everything about how Uncloud works, how to run it from source, and how to configure it. For what
Uncloud is and how to set it up and use it, start with the [README](../README.md).
[`multi-user.md`](multi-user.md) is the design for accounts and isolation.

- [What's built](#whats-built)
- [Running from source](#running-from-source)
- [Where the host keeps things](#where-the-host-keeps-things)
- [Reaching the host](#reaching-the-host): home network, reverse proxy, tunnels
- [Bringing files in](#bringing-files-in), [Your own computers](#your-own-computers), [Keeping a copy](#keeping-a-copy)
- [Development](#development)

## What's built

**Accounts and isolation**

- Username-and-password accounts with sessions. The first account is the administrator; an
  administrator can add people, give them a new password, make them administrators too, sign them
  out for good, or delete them, and the last administrator can't be removed.
- One host folder holds a folder per account under `users/<opaque id>/`. Which folder a request
  may touch comes from the signed-in session, never from the request, and every path goes through
  one policy that refuses traversal, hidden entries and symbolic links.
- Sign-in guesses are throttled; changing a password signs you out everywhere else. The API checks
  host and origin, requires a custom header on anything that changes state, and refuses everything
  but sign-in without a session.
- Accounts, sessions, host settings and sealed connector tokens live in a control database in the
  preference directory, outside the storage folder.

**Browsing**

- Nested folders, breadcrumbs, browser back/forward, filtering and sorting within a folder, file
  details and downloads. Files are always read live from disk; a per-account SQLite cache in
  `.homebase/index.db` holds metadata and is rebuildable.
- The sidebar shows how much you are using and how much is free on the drive.
- Every folder shows its size, in My files and when browsing a place to add files from, without
  being asked for. The listing arrives first and each folder's size fills in as it's measured, a
  few at a time (`GET /api/files/size?path=` and `GET /api/imports/sources/{id}/size?path=`, both
  returning `{ bytes, files }`). My files counts what browsing shows, leaving out hidden entries and
  links, walked afresh each time. A place counts what an import would bring: a folder on the host
  is walked, and a Dropbox folder is measured from one recursive `list_folder`, paged, rather than
  a request per folder. Sizes from a place are kept for five minutes per account, so stepping back
  out of a folder doesn't ask Dropbox again, and forgotten whenever Dropbox is connected or
  disconnected, since the next account may hold different folders under the same names.
  **Add files** measures the folder being looked at too, first, and shows it above the list with
  **Add all**; ticking rows turns that into a running total of what's ticked (adding up the rows'
  sizes as they arrive, hidden entries left out) with **Add selected**. Ticking everything reads,
  and adds, as the whole folder.
- Copying, moving and deleting files and folders (`POST /api/files/copy`, `/move`, `/delete`),
  through the same path policy as browsing. Nothing is overwritten: an item whose name is taken
  arrives as `name copy` or `name 2`. A copy checks the drive has room, is built under `.homebase/`
  and moved into place whole, and leaves hidden entries and links behind. A folder that syncs with
  a computer, or holds one that does, can't be moved, renamed or deleted, since Syncthing would
  stop that folder with its marker gone. Nor can the folder an import is still writing into, until
  it finishes.
- Renaming one file or folder where it is (`POST /api/files/rename` with `{ path, name }`). A name
  already in use there is refused rather than changed, since the name is the point; a change of
  capitals alone is allowed. Names can't be empty, start with a dot (they would be hidden), contain
  `/`, `\`, `:` or control characters, or run past 255 bytes.
- Sending files and folders from the browser, from whichever computer or phone somebody is using,
  into the folder they have open (`POST /api/uploads` with `{ destination, bytes }`, then
  `PUT /api/uploads/{id}?path=&modified=` once per file with the file as the body, then
  `POST /api/uploads/{id}/finish` with `{ destination }`; `DELETE /api/uploads/{id}` throws one
  away). The account's folder comes from the session like every other edit; the id is a random one
  only ever matched under that account's `.homebase/uploads/`, and the path inside it goes through
  the same path policy as browsing, so hidden names and anything reaching outside it are refused.
  Files are gathered there out of sight and moved into place together at the end, so a stopped
  upload adds nothing and leaves nothing; one nobody finishes is cleared away after a day. Starting
  checks the drive has room for all of it, and each file checks again. Nothing is overwritten: a
  name already taken arrives as `name 2`, a folder included. Each file keeps its modification
  time. The request size limit is lifted for these requests alone. Files and folders can be chosen
  in **Add files** or dropped onto it, or dropped straight onto the list in My files; a drop
  anywhere else is refused rather than letting the browser open the file in place of Uncloud. The
  page sends three files at a time with progress, retries one whose connection drops, skips one Uncloud refuses by name, and
  gives up on the whole upload when the drive runs out of room or the session ends.
- Deleting moves things into a per-account bin at `.homebase/bin/`: a folder per deletion, named
  by a random id, holding the item under its own name, beside an `<id>.json` record of where it
  came from, when, and how big it was. Being under `.homebase` keeps it out of browsing, copies and
  sync, and on the same drive, so deleting and putting back are renames. `GET /api/bin` lists it;
  `POST /api/bin/restore` puts things back by id, recreating missing parent folders and never
  overwriting; `/api/bin/delete` and `/api/bin/empty` delete for good. Anything older than 30 days
  goes the next time the account's bin is opened or added to, or Uncloud starts. The bin counts
  toward the account's usage until it lets go.

**Bringing files in** — two sources, one import engine

- **A folder on the host's computer**: a Dropbox, Google Drive, OneDrive or iCloud Drive folder a
  desktop app already syncs, an old drive, `Documents`. An administrator chooses which folders are
  offered; nobody signs in to anything.
- **A Dropbox account online**, read-only, over OAuth with PKCE. Each account connects its own
  Dropbox. The Dropbox app key can be set by the administrator for everybody, or by any person for
  themselves, entirely in the app.
- Imports run as background jobs with progress and **Stop**, check the drive has room before
  starting, never overwrite anything, bring only what's new on a repeat, and log provenance.

**Your own computers**

- Each person can keep a folder on their laptop or desktop in two-way sync with their Uncloud folder
  through [Syncthing](https://syncthing.net), which the host runs and supervises. Computers,
  folders and offers are owned per account. Deleted or replaced files are kept on the host for 30
  days. Syncing works from anywhere, not only at home.
- The Uncloud app for Mac pairs a computer with nothing to install, copy or type: it finds the host
  on the home network over Bonjour and shows a QR code, which its owner scans and approves on their
  phone. One-time pairing codes do the same without a phone ([The Uncloud app](#the-uncloud-app)).

**Reaching the host**

- Loopback-only by default. It can be opened to the home network (host allowlist required, HTTPS
  with your own certificate or behind a reverse proxy), or to the internet through a tunnel: the
  bundled one, built on Tailscale's `tsnet`, needs only a free Tailscale account and one click;
  an existing `tailscale` or `cloudflared` install also works.

**Also in this repository**

- A landing page for [www.uncloud.life](https://www.uncloud.life/) with an early-access signup
  through a LaunchList widget ([`landing/`](../landing/README.md)).
- Integration tests over real temporary files and SQLite, and front-end tests that render the app
  in jsdom, run by `./scripts/check.sh` in CI on macOS and Linux.

**Not built yet**

- Sharing a folder between accounts, per-account storage quotas, search across folders, and a
  restore button for synced files.
- Direct connectors for Google Drive, iCloud or Evernote (use their synced folders instead).
- A packaged, signed app, a background service that starts with the computer, and a desktop client.
  The folder chooser is macOS-only; elsewhere you type a path.
- Any backup. See [Keeping a copy](#keeping-a-copy).


## Running from source

The host needs to stay on and awake while people use it. Anything that runs .NET works; macOS is
the most exercised, and Linux runs in CI.

### Requirements

| | Needed for | macOS | Debian/Ubuntu |
| --- | --- | --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Always | `brew install --cask dotnet-sdk` | [Microsoft's packages](https://learn.microsoft.com/dotnet/core/install/linux) |
| [Node.js](https://nodejs.org/) 22.12+ | Always, to build the interface | `brew install node` | [NodeSource](https://github.com/nodesource/distributions) or `nvm` |
| [Go](https://go.dev/dl/) | The bundled tunnel, when running from source | `brew install go` | [go.dev/dl](https://go.dev/dl/) |

`scripts/dotnet.sh` finds an SDK matching the major version in `global.json` on `PATH`, in
`~/.dotnet`, or in `~/.cache/homebase/dotnet`, and skips an older `dotnet` rather than using it.
Only the first dependency restore needs the internet; Uncloud itself works offline.

You don't need to install or start Syncthing. `./scripts/run.sh` and `./scripts/publish-macos.sh`
put a copy beside Uncloud — a pinned release whose checksum is checked, see
[Your own computers](#your-own-computers) — and Uncloud runs it with its own configuration under
the preference directory and stops it on the way out. If that download fails, everything except
**My computers** still works.

### Start it

```sh
git clone https://github.com/ethanteng/homebase.git uncloud
cd uncloud
./scripts/run.sh
```

The run script installs the interface's dependencies when needed, builds it, and starts the server.
Leave the terminal open; Ctrl+C stops Uncloud. There is no background service yet, so if the host
restarts, run it again.

On a Mac you can instead build a self-contained executable, which needs neither the SDK nor Node
on the host; see [Desktop packaging](#desktop-packaging).

For remote access through the bundled tunnel, build the tunnel first (it needs Go). Turning it on
is done from **Settings** afterwards, not here:

```sh
./scripts/build-tunnel.sh
./scripts/run.sh
```

## Where the host keeps things

| What | Where |
| --- | --- |
| Everyone's files | The host folder you chose: `users/<id>/…` |
| Each account's metadata cache | `users/<id>/.homebase/index.db` (rebuildable) |
| Each account's bin, 30 days of what they deleted | `users/<id>/.homebase/bin/` (not rebuildable) |
| Accounts, sessions, settings, sealed tokens, `host.key` | macOS: `~/Library/Application Support/Homebase/` · Linux: `~/.local/share/Homebase/` |
| Syncthing's configuration and the tunnel's identity | `syncthing/` and `tunnel/` in that same preference directory |

Run one Uncloud per preference directory. Uncloud runs as one operating-system user and can read
every account's folder, so isolation between accounts is enforced by Uncloud, not by the operating
system: anyone with a shell on the host can read everything.


## Reaching the host

How Uncloud can be reached beyond the host itself. The [README](../README.md#6-let-everyone-in) walks through the recommended option.

### Reaching it from other computers

By default Uncloud binds `127.0.0.1`, regardless of `ASPNETCORE_URLS`. `Homebase__Bind` moves it; binding anywhere else **requires** `Homebase__AllowedHosts`, and the process refuses to start without it, because the host-header allowlist is what stops a name in somebody's DNS from turning a browser on the network into a way in.

```sh
Homebase__Bind=0.0.0.0 \
Homebase__AllowedHosts=uncloud.local,192.168.1.10 \
Homebase__Certificate__Path=/path/to/uncloud.pfx \
Homebase__PublicUrl=https://uncloud.local:5210 \
./scripts/run.sh
```

Without a certificate, passwords and files cross the network in the clear; Uncloud says so at startup rather than leaving you to notice. `Homebase__Certificate__Path` (with `Homebase__Certificate__Password`) serves HTTPS directly. `Homebase__PublicUrl` sets the address Dropbox returns to, which must match the redirect URI registered with your Dropbox app.

Behind a reverse proxy that terminates TLS, name it in `Homebase__TrustedProxies` (a comma-separated list of the addresses it connects from):

```sh
Homebase__Bind=127.0.0.1 \
Homebase__AllowedHosts=uncloud.local \
Homebase__TrustedProxies=127.0.0.1 \
Homebase__PublicUrl=https://uncloud.local \
./scripts/run.sh
```

Uncloud otherwise sees plain HTTP on a loopback address while the browser sent an `https://` origin, and refuses every sign-in as cross-site. Only the addresses listed here are believed, and only about the scheme and host — trusting those headers from anyone would let any client claim HTTPS or any hostname it liked. A forwarded host must still be one of `Homebase__AllowedHosts`.

A proxy named here is not believed about *who its client is*, because a client that could name itself could invent a new name per attempt and never meet the sign-in throttle. The cost is that everyone arriving through the proxy shares one throttle bucket, so put the rate limiting in the proxy if that matters. A tunnel Uncloud opened itself is the exception, below: it is not somebody else's proxy, so what it says about the client is Uncloud's own to trust.

### Reaching it from anywhere

Everything above puts Uncloud on the network the host sits on. To let everyone with an account
sign in from outside the house, Uncloud can open a **tunnel**: an outbound connection to a service
that already owns a name and a certificate. Nothing is forwarded at the router, no port is opened
to the internet, and no certificate has to be obtained here or renewed.

It is turned on in **Settings**, under *Reaching this host from anywhere*, and turned off
in the same place. Nothing is restarted either way: the tunnel opens or closes where it stands,
and everyone on the network carries on throughout. The choice is kept in `host_settings` beside
the storage root, so a host that was reachable comes back reachable.

Uncloud ships its own tunnel — a Tailscale node built from
[`tsnet`](https://tailscale.com/kb/1244/tsnet), the library rather than the daemon — so there is
nothing to install, no command line to meet, and no configuration file to find. The host stays
bound to `127.0.0.1`; the tunnel is the only way in from outside.

The first time it is turned on, the same panel shows one button: *Allow this host*. It goes to
Tailscale, where a free account says this machine is yours. Tailscale then wants a second yes,
about the tailnet rather than this host — Funnel allowed for it (Access controls → `nodeAttrs` →
`funnel`) and HTTPS certificates turned on (DNS) — and the panel asks for that the same way, as
*Open it to the internet*. The link behind it is the one Tailscale hands back for turning both on
at once, so neither is something anybody has to go and find. The public address appears by itself
once it is through. The node's identity is kept in `tunnel/` beside the accounts, so both are
asked once and not again.

The menu-bar app's **Reach From Anywhere** tick is the same switch, writing the same setting; it
restarts the server rather than opening the tunnel in place, because from outside there is no
signed-in way to ask it to do that. Neither one overrules the other.

The tunnel says why in words meant for a person, on a line Uncloud reads (`uncloud-tunnel:
trouble=…`) as well as to the log, so a failure it cannot get past — a tailnet whose owner isn't
the one sitting at the panel, say — is quoted in **Settings** rather than reported as a tunnel
that is generically trying again.

Anyone with an account signs in at that address exactly as they would at home, with the same
username and password; remote access is another door, not another set of keys.

### Bringing your own tunnel

If you already run Tailscale or Cloudflare on this host, Uncloud will drive those instead of its
own — `tailscale` and `cloudflared` are expected to be installed and signed in already.

```sh
Homebase__RemoteAccess__Provider=tailscale ./scripts/run.sh
```

Uncloud runs [`tailscale funnel`](https://tailscale.com/kb/1223/funnel), reads the address out of
what it prints, and answers to it.

[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/)
works the same way. Without a Cloudflare account it hands out a throwaway `trycloudflare.com`
address that changes every time the tunnel opens:

```sh
Homebase__RemoteAccess__Provider=cloudflare ./scripts/run.sh
```

A tunnel you have named and pointed at your own domain keeps its address, and Uncloud has to be
told it, because a named tunnel announces nothing on startup:

```sh
Homebase__RemoteAccess__Provider=cloudflare \
Homebase__RemoteAccess__Tunnel=household \
Homebase__RemoteAccess__Hostname=files.example.com \
./scripts/run.sh
```

| Setting | What it does |
| --- | --- |
| `Homebase__RemoteAccess__Provider` | `none`, `builtin`, `tailscale`, or `cloudflare`. Setting this at all decides the matter before launch, so **Settings** then reports remote access rather than changing it. Leave it unset to decide it there. |
| `Homebase__RemoteAccess__Hostname` | The address to answer to. Required for a named Cloudflare tunnel; the name to ask the tailnet for under `builtin`; otherwise read from what the tunnel announces. |
| `Homebase__RemoteAccess__Tunnel` | The name of a Cloudflare tunnel to run, instead of a throwaway one. |
| `Homebase__RemoteAccess__Command` | Where the tunnel program lives, if it isn't on the `PATH`. |
| `Homebase__RemoteAccess__Arguments` | The whole command line, replacing what Uncloud would have run. `{port}` is substituted. |
| `Homebase__RemoteAccess__TimeoutSeconds` | How long to wait for an address before giving up. 180 by default, because the bundled tunnel waits for its first certificate before announcing one. |

Under `builtin` there is nothing to install: `./scripts/build-tunnel.sh` builds the tunnel (it needs
[Go](https://go.dev/dl/)) and `./scripts/publish-macos.sh` puts it beside the published application.
Under `tailscale` or `cloudflare`, install and sign in to that program yourself. Either way Uncloud
runs it, and stops it on the way out, so a funnel never outlives the host it was opened for.

Four things follow from turning this on, and are worth knowing before you do:

- **The first account is still made at the host.** Until one exists there is nobody on this
  Uncloud to refuse anybody, so whoever asks first becomes its administrator. Setting up is
  therefore refused through the tunnel, and has to be done at the computer or from its own
  network. Everything else is reachable through the tunnel as usual.
- **The address is settled before the first request is served, and startup fails if it can't be.**
  Remote access was asked for, and a host that came up without it would be quietly unreachable for
  everyone not on the network. A tunnel that drops *later* is only an outage of reaching the host
  from outside: Uncloud opens another in the background while everyone at home carries on.
- **Loopback becomes a trusted proxy.** The tunnel client runs on this machine and reaches Uncloud
  over `127.0.0.1`, and it is where TLS ends, so the `https://` origin the browser sent has to be
  believed for sign-in to work at all. Unlike a reverse proxy you configured yourself, a tunnel
  Uncloud opened is also believed about the client's address, without which the sign-in throttle
  would count everybody arriving through it into one bucket and ten wrong guesses from anywhere
  would lock out the whole household. Anything else with a shell on the host could claim the same
  — which it could already, since it can read every account's files directly.
- **`Homebase__PublicUrl` follows the tunnel unless you set it.** That address must be registered
  as the redirect URI of your Dropbox app. A throwaway `trycloudflare.com` name changes on every
  restart, so Dropbox can't be connected through one; use Tailscale or a named tunnel for that.

Uncloud is not a sandbox and this does not change that — see the note above. Opening it to the
internet means the passwords on this host are the whole defence, so pick good ones.

### Which tunnel, and what it costs you

|  | `builtin` / `tailscale` | `cloudflare` |
| --- | --- | --- |
| Who can read your files in transit | Nobody. TLS ends on this machine; Tailscale's relays carry bytes they can't decrypt. | Cloudflare. TLS ends at their edge, so your files pass through their servers in the clear. |
| Account needed | A free Tailscale account, once. | None for a throwaway tunnel; one for a named tunnel. |
| Address | Stable, and yours. | Changes every restart unless the tunnel is named. |
| Bulk transfer | [Funnel is meant for low-traffic services](https://tailscale.com/kb/1223/funnel) and is bandwidth-limited. Browsing and small files are fine; pulling a 4 GB video over it will not be. | Better suited to it. |

Neither is a good way to move a whole library across the internet. Both are a good way to reach
one from a phone. If large transfers from outside matter to you, put Uncloud behind your own
reverse proxy on your own name, as above, and keep the tunnel for convenience.


### Upgrading a single-user library

The folder a previous version used becomes the host folder, and nothing in it is moved. Files sitting at its top level are listed under **Settings**, and **Move them into my folder** renames each one into the administrator's folder — nothing is copied, and anything whose name is already taken is reported rather than overwritten.

## Bringing files in

Uncloud copies files and folders onto storage you own. An import happens **once**: after a file is
here, this copy is the one that counts, and Uncloud never goes back for it. Nothing already on disk
is ever overwritten.

Everything comes in through one place: **Add files** on **My files**. It asks where the files are —
the device the browser is on, this computer, an online account such as Dropbox, or a folder somebody
shared. Files from the browser's own device are sent by the page rather than imported (see
[What's built](#whats-built)); every other source is browsed and added the same way, because underneath it is the same import: the same walk, the same
room check, the same never-overwrite rule, the same log. Google Drive and Evernote appear there as
*coming soon*; each will be an online account like Dropbox when it arrives.

What anybody adds is **theirs alone**. Sharing is a separate, deliberate step, not the default.

### A folder on this computer

The easy one, and the one to reach for first: nothing to sign up for, nothing to configure, and it
works for every service at once. If a desktop app already syncs a folder onto this machine —
Dropbox, Google Drive, OneDrive, iCloud Drive — add from that folder. An old external drive or a
`Documents` folder works exactly the same way.

**This computer** lists the folders Uncloud finds (`Desktop`, `Documents`, `Downloads`, `Pictures`,
`~/Dropbox`, `~/Google Drive`, `~/Library/CloudStorage/*`, and drives under `/Volumes`, leaving out
an app's installer left open — a drive holding nothing but an `.app` and a shortcut to
Applications) alongside any already used, all alike: open one and browse it, or add the whole
thing. **Choose another folder…** opens the folder chooser on the host, with a path box behind it
for anyone not sitting at the host. Files from a folder called *Documents* land in a `Documents/`
folder at the top of My files; a second folder with the same name is numbered rather than refused.

Only someone who looks after the host (an administrator) is offered this computer at all, because
Uncloud runs as one operating-system user and can read whatever that user can — the administrator's
own files included. Letting a member name a folder would be a way around the isolation between
accounts rather than a feature. A folder an administrator adds is **theirs alone**: nobody else, other
administrators included, sees it or can read from it, and asking for it by id answers *not found*
rather than confirming it exists. Its owner can **Share this folder with everyone here**, after which
every account sees it under **Shared with you** by name and by who shared it — never by where it sits
on the disk — and can add from it into their own space. **Make private again** takes it back and stops
anyone else's import already running from it; **Take off this list** stops every import from it.

Being able to read and share this computer's folders comes with looking after it and goes with it: an
administrator who is demoted, disabled or deleted stops being able to read their folders, and so does
everyone they shared one with, including any import already under way.

For the same reason as above, a folder can never be, contain, or sit inside the host's storage folder
or Uncloud's preference directory, and that is re-checked every time a folder is used rather than
only when it is added — moving the host's folder afterwards doesn't open a way in. Suggestions that
would break the rule, such as a drive that holds the host's folder, are left out. Paths inside a
folder go through the same policy the library uses: no traversal, no hidden entries, and symbolic
links are left out of listings rather than followed.

Taking a folder off the list, or unsharing it, touches nothing already brought home: those are
ordinary files in somebody's folder now.

A host from before folders had owners gives each existing one to its longest-standing administrator
and leaves it shared, since adding one then came with the warning that everyone could read it.

### A Dropbox account online

For Dropbox files that aren't synced to this computer. Each account connects its own Dropbox, and
the connection it authorises is the signing-in person's alone. Uncloud asks only for read-only
permissions, so it cannot change anything in anybody's Dropbox.

Connecting goes through a Dropbox *app*, and there are three places one can come from, first
match wins:

- **Your own**, under **My account**. Anybody signed in can set this up for themselves, and it wins
  over the others. Nobody's Dropbox waits on anybody else.
- **The host's**, under **Settings** (*Advanced*, while Uncloud's own is in use). An administrator
  who sets one here saves everyone else the trouble: connecting Dropbox is one click for every
  account, from any computer. It shares nobody's files: each person still signs in to their own
  Dropbox.
- **Uncloud's own**, which needs nobody to set anything up. It finishes a sign-in only on the
  computer Uncloud runs on; from anywhere else **Add files** says so and offers the setup above
  instead. See [the Dropbox relay](dropbox-relay.md).

Both screens show the same four steps, the exact redirect URI to register, and the three permissions
to tick. An app key is not a secret — Uncloud signs in with PKCE precisely because a program on
somebody's own computer cannot keep one, and there is no Dropbox app secret anywhere in Uncloud.
That is what makes it safe for a member to set their own rather than reserving it to an
administrator.

`Homebase__Dropbox__AppKey` still works as the host's key when nothing has been set in the app, so a
host started that way keeps working untouched.

```sh
# Still supported, no longer necessary.
Homebase__Dropbox__AppKey=your-app-key ./scripts/run.sh
```

Changing an app key signs out the connections that were made through it, because each was authorised
against the old app and its refresh token cannot be used against a new one — Uncloud clears them
rather than leaving connections that fail on a later refresh with nothing on screen to explain why.
Changing the host's key leaves anybody connecting through their own alone, which is the whole point
of having one.

### Either way

Files land at the top of My files, in a folder named after where they came from (`Dropbox/`,
`Documents/`), as ordinary files the normal browser shows. They share the top level with anything
else there, such as a folder synced from a laptop: a folder of the same name is added to, never
overwritten, and a file already in the way is reported as skipped. Importing a folder again
brings only what is new; anything already imported, hidden, or blocked by an existing file is listed
as skipped rather than silently passed over, and one unreadable file doesn't abandon the rest. A
folder Dropbox refuses to list is retried before Uncloud gives up on it, and giving up is reported
when the import finishes and written to the log rather than left to be noticed.

`POST /api/imports` takes `remotePaths`, several files and folders side by side (or `remotePath`,
one), and brings them as one import: one walk, one room check for all of them together, one result.
Among several, one that has gone since it was listed (or is refused by name) is skipped rather than
failing the rest; anything wider, like an expired Dropbox connection, fails the import as it would
for one. The top of a Dropbox account can be added whole — Dropbox won't describe its own root, so
Uncloud doesn't ask. Files already here are counted (`alreadyHere` in the result) rather than carried
through the import, and don't count towards the 20,000 files one import collects at most, so adding
the same thing again after reaching the limit carries on from where the last one stopped.

An import runs on its own rather than inside the request that started it, so **My files** shows how
far it has got — how many of how many files, and how much has arrived — and **Stop** ends it.
Whatever already arrived stays; only the rest is dropped. Leaving the page or reloading doesn't
cancel anything: the import carries on and My files picks it back up, then says what arrived with a
**Show them** link to the folder it landed in.

The space left on the host's drive is on every page: large in the sidebar, with a bar of what is
yours, what everything else takes and what is free, and as a pill in the top bar that stays on a
phone. It turns amber under 10% (or 5 GB) free and red under 3% (or 1 GB), and refreshes every minute,
every few seconds while files are arriving. **Add files** repeats it. Every import measures what it
would bring before downloading anything, and refuses outright when the files wouldn't fit — running
the drive out of room halfway through a folder is worse than not starting. Some room is always left
over, so the local index still has somewhere to write.

Dropbox sign-in uses the authorization-code flow with PKCE, so there is no client secret — an app
key is not one, which is why it is safe to set from a settings screen. The refresh token is sealed
with AES-256-GCM under the host key and stored against the account that connected it, never in the
library folder where it would travel alongside imported files; the account and provider are
authenticated alongside it, so a sealed token moved between accounts fails its tag check rather than
handing over somebody else's Dropbox. Because Dropbox returns the browser by a cross-site redirect
that may not carry a session, the PKCE state names the account instead — and the address the person
was on, so a sign-in started at `127.0.0.1` comes back there instead of landing on `localhost` and
reading as a sign-out. Only an address this host already answers to is accepted as a return address.

Each file is written beside its destination and moved into place, so an interrupted transfer can't
leave a half-written file, and the move never overwrites — a file that appears mid-transfer wins. The
revision (or, from a folder, the size and modification time) and a SHA-256 of each import are
recorded in `.homebase` as provenance: what came from where, and when. A folder on this computer is
copied, never moved: the originals stay exactly where they were.

## Your own computers

Each person can keep their files in a folder on their own laptop or desktop as well. Add, change or
delete a file there and the same happens on the host, and the other way round; a computer that was
offline catches up when it reconnects. Open **My computers** in the sidebar.

The sync protocol is [Syncthing](https://syncthing.net)'s, not Uncloud's: device identity,
discovery, NAT traversal, encryption and conflict handling are all its work. The host runs one
Syncthing, supervised by Uncloud with its own home directory under the preference directory and its
own loopback-only API port, started and stopped with the app. Nothing needs installing on the
host: `scripts/fetch-syncthing.sh`, which the run and publish scripts call, downloads a pinned
Syncthing release (macOS and Linux, Apple Silicon/arm64 and Intel/amd64), refuses it unless its
SHA-256 matches the one pinned in the script — taken from the release's checksum file after checking
Syncthing's release signature — and puts it beside the application with its MPL-2.0 licence. That
copy is used ahead of anything on the `PATH` and never upgrades itself; a new version is a change
to the script. `Homebase__Syncthing__Path` still points at a different binary if you'd rather.
With [the Uncloud app](#the-uncloud-app), people's own computers need nothing installed either;
without it, each person installs Syncthing and pairs by device ID:

1. On your computer, add the host's ID (shown in **My computers**) as a remote device.
2. In **My computers**, paste your computer's ID and **Add computer**.
3. Either **Sync folder** in Uncloud — a folder such as `Documents`, or leave it empty for all of
   your files — and accept the offer in Syncthing on your computer; or share a folder from your
   computer, and **Keep it here** when it appears under **Offered by your computers**.

Instead of copying device IDs, a computer can pair itself with a code. **Get a pairing code** in
**My computers** shows a ten-character code that works once, for ten minutes. A client on the
computer sends it to `POST /api/sync/pair` as `{ "code", "deviceId", "name" }`, without a session
and with the `X-Homebase-Request: 1` header, and gets back `{ "hostDeviceId", "accountName", "folders" }`. With `"syncEverything": true`, which
the Uncloud app sends, the computer is also brought in on every folder the account already syncs,
or the whole account folder if nothing syncs yet, and `folders` lists them for it to set up.
That computer is now paired with the account that asked for the code. Codes are stored only as
hashes, a new one replaces the last, wrong guesses are throttled per address, and a mistake that
isn't the code's — a malformed device ID, a computer another account already has — doesn't use it
up.

The Uncloud app pairs the other way round: the computer asks first, and the person it belongs to
approves. `POST /api/sync/pair/requests` with `{ "deviceId", "name", "syncEverything" }`, without a
session, answers `{ "id", "secret", "expiresAt" }`, and the app shows `<address>/?pair=<id>` as a QR
code. Opened by somebody signed in, that address asks **Add this computer?** at the top of **My
computers** (`GET /api/sync/pairing-requests/{id}`), and approving it
(`POST /api/sync/pairing-requests/{id}/approve`) pairs the computer with whoever approved, as
redeeming a code would. Meanwhile the app asks `POST /api/sync/pair/requests/{id}/answer` with
`{ "secret" }` every couple of seconds; that answers `{ "approved": false }` until somebody has, then
`{ "approved": true, "result" }` — the same result a code gets — once, after which the request is
gone. The id is on the computer's screen for anyone to read, and only ever leads to a page that
needs a session; the secret never leaves the computer. So a photo of the QR code can neither
approve the request into somebody else's account nor collect what was approved. Requests last ten
minutes and are kept in memory: a computer asking again replaces what it asked before, no more
than 32 wait at once and 8 from any one address, and a host that restarts forgets them, so the app
asks again and shows a new code. The page asks people to approve only a computer they're setting up
themselves, because a link somebody else sends would add *their* computer to your account.

On a Mac, the host announces itself on the local network over Bonjour, as `_uncloud._tcp` under the
computer's own name, with a TXT record carrying `url=` and the address other computers reach it at:
`Homebase__PublicUrl` if that was set, or the tunnel's. It is asked again every ten seconds, since a
tunnel allowed after startup brings an address the host didn't have before, and it carries no
`url` at all while there's nowhere another computer can reach it; the app then says to turn on
**Reach From Anywhere**. The address is all that's announced, and it is no key to anything —
everybody still signs in. `Homebase__Announce=false` stops it. A host on Linux doesn't announce
itself, so its computers type the address.

Syncthing has one configuration for the whole host and no idea of accounts, so Uncloud records
which account every computer and every folder belongs to and answers everything from that record.
Each person sees and manages only their own computers, folders and offers; a computer can be paired
with one account only; a folder can only be made inside your own space and only sent to your own
computers; and a pairing request is refused without saying which other account has that computer.
Disabling an account pauses its computers, and deleting one removes them — refused while Syncthing
can't be reached, so nothing carries on writing into the folder of an account that no longer
exists. Folders synced by the older administrator-only version are given to the account whose
folder they are in the next time Uncloud starts.

Uncloud's index is kept out: before Syncthing first sees a folder, Uncloud writes a `.stignore`
there that excludes `.homebase` (and `.DS_Store`), adding to any rules already in it. One synced
folder can't sit inside or around another. Moving the host's folder moves where every synced folder
points; if the new one doesn't hold the files, Syncthing stops that folder with an error rather than
treating the empty folder as everything having been deleted.

Folders are two-way (`sendreceive`). If a file changes on both sides while they're apart, Syncthing
keeps both and renames the loser with `.sync-conflict-` beside the winner, so you may have a
duplicate to tidy up. **A deletion on your laptop deletes the file here.** Every synced folder on the
host keeps what it deletes or replaces for 30 days under a hidden `.stversions` folder
(Syncthing's staggered versioning), which is how a mistake on a laptop is undone — there is no
restore button yet, so that means copying it back out of `.stversions`.

Syncthing connects through its global discovery and relays by default, so your computers keep
syncing when they're away from home, whether or not the host has a tunnel open for the web
interface. A pairing code can be redeemed, and a pairing request made, through that tunnel too;
guesses and requests are counted per client, the same as sign-in.

`Homebase__Syncthing__Path` runs a different Syncthing than the bundled one,
`Homebase__Syncthing__GuiPort` moves its local API off 8390, and
`Homebase__Syncthing__Enabled=false` switches the whole thing off.

## Keeping a copy

Syncing isn't a backup of the host, and nothing else copies your files anywhere. One host, one
drive: if that drive dies, everything that exists only there is gone. Set up a backup before you
put anything you care about here.

Anything that copies a directory works, because the library is ordinary files in ordinary folders —
nothing has to understand Uncloud to back it up. On macOS, include the host folder in Time Machine.
Otherwise [restic](https://restic.net) or [rclone](https://rclone.org) to a second drive or an
offsite target does the job:

```sh
restic -r /Volumes/Backup/uncloud backup ~/Uncloud
```

Prefer something that keeps **versions** rather than a live mirror. A mirror propagates a deletion
as faithfully as it propagates a new file, so it protects you against a failed drive and not at all
against the far more common way people lose things, which is deleting them and noticing later.

`.homebase/index.db` is a live SQLite database and a rebuildable cache, not a backup — it is fine
if it comes along, and fine if it doesn't. The control database that holds accounts and connector
tokens lives in the preference directory, so back that up separately if you want the accounts
themselves to survive, not just the files.

The web interface is reachable from outside the house only through a tunnel, described above.
Syncing with your own computers works from anywhere either way.

## Development

For live interface updates, use two terminals:

```sh
./scripts/dotnet.sh run --project src/Homebase.Server
# In another terminal:
npm --prefix src/homebase-web ci
npm --prefix src/homebase-web run dev
```

Open **http://127.0.0.1:5173**. Vite proxies `/api` to the backend; neither server listens on the network.

### Architecture

```text
src/Homebase.Core/       Filesystem boundaries, SQLite cache, the import engine
  Accounts/             Accounts, sessions, sealed connector tokens, per-user workspaces
  Providers/            Import sources (Dropbox, folders on the host), import jobs and log
  Sync/                 Syncthing client, per-account ownership, pairing, Bonjour
  Importing/            Contract for future importer adapters
src/Homebase.Server/     ASP.NET Core API, host binding, tunnel and Syncthing supervision, macOS picker
src/homebase-web/        React + TypeScript + Vite
tools/uncloud-tunnel/    The bundled tunnel: a Tailscale node built from tsnet (Go)
tests/Homebase.Tests/    Integration tests against real temporary files and SQLite
src/homebase-web/src/*.test.tsx   Front-end tests: the app rendered in jsdom, host answers stood in
landing/, api/           The uncloud.life landing page, and the Dropbox sign-in relay it hosts
```

[`docs/multi-user.md`](multi-user.md) is the design for accounts and isolation.

The filesystem is the source of truth. Browsing scans one directory and atomically replaces that directory’s cached entries. There is no recursive scan, file watcher, content index, or global search yet. Metadata for unvisited or removed nested folders may be stale until visited; the UI always reads disk. Filtering covers the current folder only. Keep v0 to reasonably sized individual directories; pagination is a later step.

Accounts, sessions, host settings and sealed provider tokens live in `homebase.db` in the preference directory ([where](#where-the-host-keeps-things)), beside a 32-byte `host.key`, outside the storage root and so outside the boundary they define. Each account's file metadata lives in its own `.homebase`. Stop Uncloud and remove a `.homebase` to reset that rebuildable cache; browse the folder again to recreate it. This cache is not a backup.

`Homebase__Dropbox__AppKey` supplies the host's Dropbox app key for a host that would rather not use
the settings screen; an account with its own key ignores it. `Homebase__Syncthing__*` configures the Syncthing process described under [Your own computers](#your-own-computers). `Homebase__ConfigDirectory` overrides the preference directory for isolated testing. `Homebase__Port` overrides port 5210 (also update Vite's proxy for development). Run a single Uncloud process per preference directory. Uncloud is not a sandbox: one process runs as one operating-system user and can read every account's folder, so isolation is enforced in Uncloud, not by the kernel, and anyone with a shell on the host can read everything.

### Future importers

`IImportSource` is what the import engine sees: metadata, a listing, and a stream. A Dropbox account
and a folder on this computer both implement it, so nothing in the engine, the panel, or the log
knows which is which. A new service is that interface plus whatever it takes to authenticate, and it
chooses the folder at the top of My files its imports land in. An implementation is responsible for refusing
any path that reaches outside the place it stands for. `IHomebaseImporter` remains an unimplemented
contract for adapters that want to write into the library directly, such as Evernote notes and
attachments under `Notes/Evernote/`; these folders aren’t created until something needs them.

### The Uncloud app

One Mac app, `Uncloud.app`, for both ends. It lives in the menu bar and, on first launch, asks what
this Mac is:

- **The host.** The app carries the Uncloud server, its Syncthing and its tunnel, runs them as a
  child process and stops them when it quits, and opens the browser for the first account. Its
  menu opens Uncloud, turns **Reach From Anywhere** (the bundled tunnel) on and off, and restarts
  the server if it stops. Accounts and settings are kept in the same preference directory as a
  host run from source, so moving between the two keeps everything.
- **Somebody's computer.** The app runs its own Syncthing (API on `127.0.0.1:8391`, state in
  `~/Library/Application Support/Uncloud`) and pairs it. It looks for the host over Bonjour
  (macOS asks once whether Uncloud may look around the local network), asks to be added, and
  shows the QR code until somebody approves it; away from home it asks for the address first, and a
  code from **My computers** still works. Either way it pairs with `syncEverything`, which adds
  every folder the account already syncs to this computer, or syncs the whole account folder if
  nothing syncs yet, and returns the folder ids, so the app sets them up under `~/Uncloud` directly
  and nothing waits to be accepted.
  The host is the only device this Syncthing knows, and folders it shares later are accepted into
  `~/Uncloud` automatically; nothing from any other device ever is. **Disconnect** stops syncing
  and leaves `~/Uncloud` as it is.

The link **open in the Uncloud app** is `uncloud://pair?address=…&code=…`, with the address being
the one the page was opened at: if a browser on that computer can reach Uncloud there, so can the
app. Opened on the host itself at `127.0.0.1`, the page says that address won't work elsewhere.

Build it with:

```sh
./scripts/package-macos-app.sh            # Apple Silicon
./scripts/package-macos-app.sh osx-x64    # Intel
```

On a Mac this produces `artifacts/Uncloud-osx-arm64.dmg`: the app beside a shortcut to
Applications, compressed with LZMA (`ULMO`): 59 MB for Apple silicon and 67 MB for Intel, against 76 and 81 MB
zipped. The
script mounts it and checks the app's signature as it is inside, since the managed `.dll` files
carry theirs in extended attributes that a careless copy drops. Signed ad hoc, as it is by default, it runs on
the Mac that built it and has to be allowed in System Settings on any other. For distribution, set
`UNCLOUD_SIGN_IDENTITY` to a "Developer ID Application" identity, and either `UNCLOUD_NOTARY_PROFILE` to a
`notarytool` keychain profile or `UNCLOUD_NOTARY_KEY`, `UNCLOUD_NOTARY_KEY_ID` and `UNCLOUD_NOTARY_ISSUER` to
an App Store Connect API key, to notarize and staple the disk image too. Notarized, the script also asks
Gatekeeper (`spctl`) whether it would open the app, and fails if not. CI builds the app for Apple silicon and
Intel on every run and keeps both as the run's **Artifacts**; see [Signing the app](#signing-the-app) for
which of those are signed. Every push to `main` that passes also
updates the `mac-latest` release with them (never deleting it, and without being cancelled part way), so
`https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-arm64.dmg` (and
`…-osx-x64.dmg`) always serve the latest build; the README links there. The zips the app was first
published as are removed from the release. Before the disk image is kept,
`scripts/smoke-macos-app.sh` opens the built app on the runner, once as a Mac nobody has set up and
once as the host, and fails the build if it doesn't stay open, logs an error, or its server doesn't
answer.

The app and the server it carries sit side by side in `Contents/MacOS` and share one copy of .NET
(around 190 MB unpacked instead of 275 with a copy each). The desktop project
references the ASP.NET Core framework for that reason alone, so both publish identical framework
files; the packaging script stops if any file the two share differs. Trimming would shrink each
further, but trimmed copies differ, so they couldn't share. The app logs to
`~/Library/Application Support/Uncloud/uncloud.log`, including anything that stops it.

### Signing the app

CI signs the app with the project's Developer ID and has Apple notarize it on every push to
`main`, and on any run started by hand from the **Actions** tab, which is how to try a change to
signing on a branch before it reaches `main`. Pull request builds stay signed ad hoc: they never
publish, and notarizing each push would only slow them down. A run that should sign and can't find
the secrets fails, rather than publishing an app macOS stops.

It needs five repository secrets, under **Settings → Secrets and variables → Actions**:

| Secret | What it is |
| --- | --- |
| `MACOS_CERTIFICATE_P12` | The Developer ID Application certificate and its private key, exported from Keychain Access as a `.p12`, base64-encoded: `base64 -i DeveloperID.p12 \| pbcopy`. |
| `MACOS_CERTIFICATE_PASSWORD` | The password the `.p12` was exported with. |
| `APPLE_NOTARY_KEY` | The whole text of the App Store Connect API key's `AuthKey_….p8` file. |
| `APPLE_NOTARY_KEY_ID` | That key's ID. |
| `APPLE_NOTARY_ISSUER_ID` | The issuer ID shown above the list of team keys. |

To make them, as the Apple Developer account holder:

1. **The certificate.** On a Mac, open Keychain Access and choose **Keychain Access → Certificate
   Assistant → Request a Certificate From a Certificate Authority**. Enter your e-mail, choose
   **Saved to disk**, and save the `.certSigningRequest`. At
   [developer.apple.com → Certificates](https://developer.apple.com/account/resources/certificates/list),
   click **+**, choose **Developer ID Application** and the **G2 Sub-CA**, and upload the request.
   Download the certificate and double-click it to add it to Keychain Access. (Xcode's **Settings →
   Accounts → Manage Certificates → + → Developer ID Application** does the same in one step.)
2. **The `.p12`.** In Keychain Access, under **My Certificates**, right-click **Developer ID
   Application: …** (it has a key under it) and choose **Export**. Save it as a `.p12` with a
   strong password. Keep a copy somewhere safe: Apple can't give the private key back, and anybody
   with it can sign apps as you.
3. **The API key.** In [App Store Connect](https://appstoreconnect.apple.com/access/integrations/api),
   open **Users and Access → Integrations → App Store Connect API** (click **Request Access** the
   first time), then **Team Keys → +**. Name it for notarizing and give it the **Developer** role.
   Download the `.p8` (Apple lets you download it once) and note its key ID and the issuer ID.

Notarizing fails until the account holder has accepted any new agreement waiting at
developer.apple.com, which Apple adds from time to time.

### Desktop packaging

To build only the server as a self-contained macOS executable, without the app:

```sh
./scripts/publish-macos.sh            # Apple Silicon
./scripts/publish-macos.sh osx-x64    # Intel
./artifacts/osx-arm64/Homebase.Server
```

Open http://127.0.0.1:5210. The bundled tunnel is built beside it, so turning on remote access in **Settings** needs nothing else. For the menu-bar app, see [The Uncloud app](#the-uncloud-app). No billing, AI, or photo management is included, and there are no per-account storage quotas: everyone draws on the same drive, so one account can fill it for everyone.

### Check

```sh
./scripts/check.sh
```

Builds/type-checks the frontend, runs its tests, and runs backend integration tests for accounts (first-run setup, sign-in refusals that say nothing about who exists, throttled guessing, password changes that sign out everywhere else, disabling and deleting accounts, and keeping the last administrator), isolation (separate folders, every path by which one account might name another's files, administrator-only endpoints, nothing readable without signing in, per-account provider connections and import queues, sealed tokens refused under another account, and shared free space with private usage), the upgrade path from a single-user library, host binding rules, remote access over a tunnel (the announced address becoming the one this host answers to and where Dropbox returns the browser, a configured public URL not being overruled, an address only administrators are shown, refusing to let the first account be claimed over the internet, counting a stranger's guessing against the stranger rather than the whole household, refusing to take a client's word for its own address through a proxy that isn't a tunnel, following a tunnel that reconnects under a new name without opening the door to one it never carried, and refusing to start when the tunnel program isn't there), persistence, indexing, file integrity/downloads, folder sizes (counting only what browsing or an import would see, however deep, measured from one recursive Dropbox listing, and remembered for a while), copying, moving, renaming and deleting (never overwriting, keeping both under a new name, a rename refused rather than given a name already in use, names that would hide or break a file refused, copies built out of sight and left without hidden entries or links, a folder refused as its own destination, the same path refusals as browsing, synced folders and the folders holding them left alone, and the folder an import is writing into left alone until it finishes), the bin (deleting into it, putting back where things were with missing folders made again and nothing overwritten, deleting for good one at a time or all at once, letting go after 30 days, and ids that are only ever matched), host folder switching, unavailable folders, symlinks, traversal, request boundaries, imports (single files, whole folders, skipping what's already here, refusing to overwrite anything it didn't write, carrying on past a file that fails or times out while still stopping when cancelled, retrying a listing Dropbox rate-limits, refusing a folder that wouldn't fit on the drive, and running an import as a job that reports its progress, refuses a second one and stops when asked), places on the host's computer (bringing a folder home, the refusals that keep a place from reaching the host's folder or the preference directory — including after the host's folder moves — only an administrator adding one, paths that try to walk out of one, links left unfollowed, and two places kept from sharing a folder in the library), in-app Dropbox setup (setting the host's app key and an account's own, an account's key winning over the host's and falling back to it when cleared, the environment variable behind both, signing out only the connections a changed key would have broken, the redirect address to register, and a sign-in returning to the address it started from), Uncloud's own Dropbox app (connecting with nothing set up, a key somebody here chose winning over it, the exchange presenting the app and address the sign-in began with rather than whatever is in force when it returns, the three ways the one-click hop cannot arrive — another computer, a proxy Uncloud cannot see past, a host on its own https — each falling back to a sign-in Dropbox shows a code for, and that code being good once, refused when nothing is waiting for it, and retypeable when mistyped), and syncing with your own computers (device-ID checks, one account per computer, each account seeing and using only its own computers, folders and offers, folders confined to the account's space and never nested, Uncloud's index ignored before Syncthing first scans, offered names held to the same rules, pausing a disabled account's computers and removing a deleted one's, adopting folders from the administrator-only version, following a moved host folder, pairing codes that are single-use, expiring, throttled and bound to the account that asked, and pairing requests that only a signed-in person can approve, only the asking computer can collect, and that run out, replace each other and are bounded per address and per host), and finding a host on the network over Bonjour. Tests use disposable fixtures and isolated settings, never your real library or accounts.

The front-end tests cover the return path: what the app does when a browser comes back from Dropbox carrying how the sign-in went. Every bug found there has been in React's lifecycle rather than in a function worth calling directly — an effect that did not run again when the element it wanted appeared, a browser call whose answer was discarded, a value that outlived the dialog it belonged to — so they render the app and arrive at it the way a browser does, standing in for what the host answers and nothing else.

GitHub Actions runs this same script on every pull request and push to `main`, on both macOS and Linux. The tests cover filesystem, indexing, and request-boundary behavior; the native macOS folder chooser isn't automatable and still needs a manual pass, as does completing a real Dropbox sign-in.

## Landing page

The standalone **Uncloud** messaging page for **uncloud.life** is in [`landing/`](../landing/README.md). Preview it with `npm --prefix src/homebase-web run dev:landing` at **http://127.0.0.1:5174**. Build it with `npm --prefix src/homebase-web run build:landing`; the static output goes to `artifacts/landing/` and contains no file-browser API. The application and its storage paths still use the internal name Homebase.

