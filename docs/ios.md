# Uncloud for iPhone

The design for an iPhone app that signs in to an Uncloud host and shows the person their files.
Nothing here is built yet. The first version is for viewing only; uploading and syncing come after,
and the end of this document says what they will need so that nothing built now is in their way.

- [Why an app](#why-an-app)
- [What the first version does](#what-the-first-version-does)
- [How the phone reaches the host](#how-the-phone-reaches-the-host)
- [Connecting](#connecting) and [signing in](#signing-in)
- [Browsing](#browsing) and [opening a file](#opening-a-file)
- [The code](#the-code)
- [Testing and shipping](#testing-and-shipping)
- [Server changes worth making](#server-changes-worth-making)
- [After viewing: uploading and syncing](#after-viewing-uploading-and-syncing)
- [Decisions to make](#decisions-to-make) and [milestones](#milestones)

## Why an app

Uncloud already works on a phone. Anybody can open the host's address in Safari, sign in, browse
and download. So the first version has to be clear about what it adds over that, because for
viewing alone it is not a lot:

- **Files open in place.** The web app downloads everything, deliberately (see
  [Opening a file](#opening-a-file)). The app shows photos, video, PDFs and documents right there,
  with Quick Look.
- **A file opened once opens again at once.** The app keeps what it has downloaded, so looking at
  the same thing twice doesn't cross the tunnel twice.
- **Files go where iOS keeps things.** Save to Photos, save to Files, AirDrop and *Open in…*, from
  the share sheet.

That is a modest gain on its own. The real reason for an app is what comes next: sending the camera
roll home in the background, and Uncloud showing up in the Files app. Neither can be done from a web
page. So the first version is built as the foundation for those (the API client, the sign-in, the
transfers) and kept small everywhere else.

It is a native app, not a web view around the existing pages. A web view gets none of the three
things above, and App Review turns down apps that are a website in a frame (guideline 4.2).

## What the first version does

**Does:** connect to one host, sign in, browse **My files**, sort and filter the folder being
looked at, open any file in Quick Look, share or save it, show how much room is left, sign out.

**Doesn't:** upload; copy, move, rename or delete; the bin; **Add files**, Dropbox or imports;
**People** and **Settings**; offline browsing; more than one host or account at a time; thumbnails;
streaming video. An administrator still uses the web for everything administrative, and the app
links there.

## How the phone reaches the host

The host listens on `127.0.0.1` only. Nothing on the home network can reach it directly, the phone
included. The one way in is the address **Reach From Anywhere** gives it, such as
`https://uncloud.tail1234.ts.net`, and the phone uses that address **at home as well as away**:

```text
  iPhone ──https──▶ Tailscale Funnel ──▶ uncloud-tunnel ──▶ Uncloud on 127.0.0.1:5210
                    (public internet)     (on the host; TLS ends here)
```

What follows from that:

- **Remote access has to be on.** Without it there is nothing for the phone to connect to, and the
  app says so in those words rather than as a generic connection failure.
- **The certificate is a real one** (Tailscale gets it from Let's Encrypt), so App Transport
  Security accepts it and the app needs no exceptions. A host somebody has put on their own network
  with a self-signed certificate (`Homebase__Certificate__Path`) won't work in the first version.
  Debug builds allow plain HTTP to loopback, for a simulator talking to a server run from source.
- **Funnel is bandwidth-limited** ([technical.md](technical.md#which-tunnel-and-what-it-costs-you)).
  Browsing and everyday files are fine; a 4 GB video is not. This is why the app asks before large
  downloads, keeps what it has downloaded, and why thumbnails are the first server change after the
  first version.
- **Tailscale on the phone makes it fast, for free.** `ListenFunnel` in `tsnet` serves the tailnet
  as well as the internet unless it is told `FunnelOnly`, and `uncloud-tunnel` doesn't tell it that.
  A phone running the Tailscale app on the same tailnet resolves the same name to the host's tailnet
  address and connects directly, with no Funnel in between. The app needs no changes for it. It is
  worth a line in the README for households willing to join everyone to the tailnet. Check it on a
  real phone before promising it.

## Connecting

The first screen asks for the address. Most people will paste it, since whoever looks after Uncloud
sends it to everyone ([README step 6](../README.md#6-let-everyone-in)).

1. **Read the address** the way the Mac app does (`PairingLink.ParseAddress`): add `https://` if it
   is missing, and keep only the scheme, host and port.
2. **Check it is an Uncloud.** `GET /api/health` should answer `{ "status": "ok" }`. Anything else is
   *"That address doesn't look like an Uncloud. Check it with whoever looks after yours."*
3. **Check it is ready.** `GET /api/session`. If `setupNeeded` is true, nobody has made the first
   account yet, and the tunnel refuses that anyway, so say it has to be set up on the host first.
4. Go on to sign in, showing the host's address so the person knows where they are signing in.

**Finding it on the home network.** A Mac host already announces itself over Bonjour as
`_uncloud._tcp` (`Bonjour.cs`), with its address in the TXT record as `url=…`, but only once it has
an address somewhere else can reach. A Linux host announces nothing, and its address is typed. A **Find it on this Wi-Fi** button browses for that with
`NWBrowser` and fills in the address. A host found *without* a `url` is a host whose remote access is
off, and the app can say exactly that: *"Found Ethan's Mac mini, but your phone can't reach it yet.
Ask whoever looks after it to turn on Reach From Anywhere."*

Browsing for Bonjour services makes iOS ask for permission to use the local network. That prompt
comes when the button is tapped, never at launch, so it arrives with a reason in front of it. The
app declares `NSBonjourServices = [_uncloud._tcp]` and an `NSLocalNetworkUsageDescription`.

## Signing in

Username and password, sent as `POST /api/session` with `{ username, password }`. The person sees
whatever the host says if it refuses, so a wrong password and too many attempts read exactly as they
do in the browser.

**No server changes are needed.** The rules every `/api` request passes through (`Program.cs`) were
written for browsers, and a native client passes them for the right reasons:

| Rule | Why the app passes |
| --- | --- |
| The `Host` must be one this Uncloud answers to | It is the tunnel's name. |
| An `Origin`, if sent, must be this host | `URLSession` sends none. |
| `Sec-Fetch-Site: cross-site` is refused | `URLSession` sends none. |
| Anything but `GET` must carry `X-Homebase-Request: 1` | The app puts it on every request. |
| A live `uncloud_session` cookie | The app sends the token it was given. |

The backend tests already talk to the server exactly this way (`TestHost.Anonymous()`): no `Origin`,
the header on every request, a cookie handed back.

**The token.** Sign-in answers with `Set-Cookie: uncloud_session=…`. The app reads that one value,
keeps it in the Keychain (`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`: never synced to
another device, and readable by a background transfer once the phone has been unlocked once), and
sends it as a `Cookie` header itself. `URLSession`'s own cookie storage is turned off, so there is
exactly one copy and it is in the Keychain.

**Staying signed in.** A session lasts 30 days from when it was last used, and the server moves that
forward itself while keeping the same token (`SessionStore.Resolve`). The app ignores the
`Set-Cookie` that comes with a renewal, since the value doesn't change. Somebody who doesn't open the
app for a month signs in again with their username filled in. Any `401` from any request means the
session has gone, and the app returns to sign-in, with the same message the server sent.

**Not a bearer token.** A `POST /api/tokens` and an `Authorization` header would be tidier to read,
but it would be a second way to authenticate in the one piece of middleware every request passes
through, and it buys nothing the cookie doesn't: the cookie *is* an opaque bearer token, stored only
as its SHA-256 on the host. If a later client can't set a cookie header, add it then.

**A lost phone.** Changing the password signs out every other session (`/api/account/password`),
which is the remedy today. Naming each session's device and revoking one from **My account** is
already Phase 2 of [multi-user.md](multi-user.md#phases), and a phone app makes it more worth doing.

Administrators sign in like anybody else. The app shows no administration, and has an **Open Uncloud
in Safari** link for it (and, until uploads ship, for **Add files**).

## Browsing

| Endpoint | Answers | Used for |
| --- | --- | --- |
| `GET /api/files?path=` | `{ path, entries: [{ name, path, isDirectory, size, modifiedAt }], skippedCount, indexedAt }` | A folder |
| `GET /api/storage` | `{ freeBytes, totalBytes, usedBytes, capped }` | Room left, on the account screen |
| `GET /api/session` | `{ setupNeeded, hostConfigured, user }` | Who is signed in, at launch |

- **One screen, pushed per folder,** in a `NavigationStack` whose path is the list of folder paths.
  Back, swipe back and long-press on the back button (to jump several levels) come with it.
- **Each folder's last listing is kept in memory** and shown at once on the way back, then
  refreshed. Pull to refresh asks again. Over Funnel a listing takes a moment, and showing nothing
  while it does makes the app feel broken.
- **Sort and filter on the phone.** The server sends folders first, then names in order. Name, date
  and size sorting, and a search field that filters the folder being looked at, are done locally.
  There is no search across folders: the server has none.
- **`skippedCount`** becomes a footer, *"2 items here can't be shown,"* the same reason the web
  gives: links and things Uncloud can't read are left out.
- **No folder sizes in the list.** The web measures every folder in view
  (`GET /api/files/size?path=`), and each one walks that folder on the host. A phone scrolling a
  long folder would fire dozens of those across the tunnel for a number nobody asked for. A folder's
  size is shown when its details are opened.
- **Icons come from the file's type** (`UTType(filenameExtension:)`) until there are thumbnails.

## Opening a file

`GET /api/files/download?path=` always answers `application/octet-stream` with the file's name in
`Content-Disposition`, and honours `Range`. Sending everything as a download is on purpose: an HTML
or SVG file opened in the browser must never run as Uncloud. The app doesn't change that, and
doesn't need to.

1. **Tapping a file downloads it** into the app's cache under its own name, then opens it in
   `QLPreviewController`. Quick Look tells the type from the extension, which is why the name
   matters, and covers photos (HEIC included), video, audio, PDF, text, Office and iWork documents.
   Anything it can't show offers the share sheet instead, so another app can open it.
2. **Progress shows over the list, with Cancel.** A dropped connection resumes with `Range` rather
   than starting again.
3. **Large files ask first.** Over 100 MB, or over 20 MB when the phone is on cellular or Low Data
   Mode (`NWPath.isExpensive` and `isConstrained`), the app says how big it is and asks before
   starting.
4. **Downloaded files are kept.** The cache is keyed by host, account and path, and remembers each
   file's size and modification time: if the listing still shows the same, it opens without
   downloading again. It lives in `Library/Caches`, so iOS can reclaim it, is capped at 1 GB with
   the least recently opened going first, is shown on the account screen (*"Downloaded files: 340 MB
   — Clear"*), and is deleted on sign-out. Files in it are protected with
   `completeUnlessOpen`: unreadable while the phone is locked, but still writable by a download that
   is finishing.
5. **Sharing comes with Quick Look,** which has a share button: Save Image or Save Video, Save to
   Files, AirDrop, Open in… Saving to Photos needs `NSPhotoLibraryAddUsageDescription` in
   `Info.plist`, or the share sheet crashes the app the first time somebody taps it.

**Why not stream video.** `AVPlayer` needs to know a file's type, the endpoint says
`application/octet-stream`, and the URL has no extension. Getting around that means feeding the
player through `AVAssetResourceLoaderDelegate`, which is real work, and Funnel's bandwidth would make
streaming stutter anyway. Downloading and then playing is fine for the clips people keep on their
phones. A typed endpoint for media is listed [below](#server-changes-worth-making).

## The code

```text
src/Uncloud.iOS/
  Uncloud.xcodeproj
  Uncloud/            the app: SwiftUI screens, Quick Look, the share sheet
  UncloudKit/         a Swift package: everything that talks to the host, and none of UIKit
    Sources/UncloudKit/
      UncloudClient.swift    one method per endpoint, async, throwing UncloudError
      Models.swift           Session, User, DirectoryListing, LibraryEntry, StorageReport
      Credentials.swift      the token in the Keychain, one per host
      Transfers.swift        downloads now, uploads later, behind one interface
      FileCache.swift        what's downloaded, and evicting it
      HostAddress.swift      reading a typed address; Bonjour
    Tests/UncloudKitTests/
```

Named after `Uncloud.Desktop`, beside it in `src/`. `UncloudKit` is kept free of UIKit so that the
share extension and the Files app extension that come later can use it as it is, and so its tests
run with `swift test`.

- **SwiftUI, iOS 18 and later, Swift 6 with strict concurrency.** `@Observable` for the app's state,
  which is a small machine: *no host* → *host, signed out* → *signed in*.
- **No third-party packages.** Everything needed is in the SDK, and this repository keeps an
  inventory and the licence of everything it ships.
- **The Keychain item takes an access group** from the start, so the extensions can share the
  session later without moving it.
- **Words match the web's.** The server's messages are written for people, so the app shows them as
  they are, and its own say things the same way (*"Uncloud can't…"*, no error codes).

Five things to get right, each with a test:

1. **`+` in a path.** ASP.NET reads `+` in a query string as a space. `URLComponents` leaves `+`
   alone, so `C++ notes.txt` would be asked for as `C   notes.txt` and not found. Encode it as `%2B`.
   The web uses `URLSearchParams`, which does this already.
2. **Dates.** `DateTimeOffset` is written with up to seven digits after the seconds, and an offset.
   `JSONDecoder`'s `.iso8601` has historically refused fractions of a second, so don't rely on it.
   Decode with a strategy that accepts any number of digits or none, tested against a real response.
3. **Errors.** A refusal is a problem response whose `detail` is the sentence to show, as the web
   does (`api.ts`). The host check's `403` has no body, so there has to be a fallback. A `401` from
   anywhere means signed out.
4. **Older and newer hosts.** Each host updates when its owner says so. Unknown fields are ignored,
   anything added later is optional, and a missing endpoint is a `404` with
   *"This endpoint doesn't exist."*, which the app can turn into *"This Uncloud needs updating to do
   that."*
5. **`X-Homebase-Request: 1` on every request,** so no future `POST` forgets it.

## Testing and shipping

- **Unit tests** in `UncloudKit`, against canned responses through a `URLProtocol` stub: sign-in
  reads the token, the five things above, the cache's keys and eviction.
- **A contract test against the real server.** On the macOS CI runner, start the server from source
  on loopback, make the first account with `/api/setup`, and run `UncloudKit`'s integration tests
  against it. The server and the app are in one repository, so a change to the API that would break
  the app fails the pull request that makes it, not somebody's phone afterwards. On the server's
  side, a test that pins the "native client" assumptions from [Signing in](#signing-in) keeps a
  future hardening change from shutting the app out without anyone noticing.
- **CI** gets a macOS job: `swift test` for `UncloudKit`, and `xcodebuild build` for the simulator.
- **TestFlight first,** for the household and friends. It needs the same Apple Developer membership
  that signs the Mac app; the bundle id would be `life.uncloud.ios`, beside the host's
  `life.uncloud.host`.
- **The App Store needs a host App Review can reach.** Review requires a working demo account for an
  app you sign in to (guideline 2.1), so somebody has to keep a demo Uncloud running with something
  in it, for every review.

## Server changes worth making

None of these is needed for the first version.

| Change | What it's for | When |
| --- | --- | --- |
| A `version` in `/api/health` | Saying *"This Uncloud needs updating"* instead of failing vaguely against an old host | With the first version, if it's cheap |
| `GET /api/files/thumbnail?path=&size=` | Photo grids. Without it a folder of photos is a list of names, and downloading each full image over Funnel isn't an option | Straight after the first version |
| An "Add a phone" QR code in **My account** | The phone scans it instead of anybody typing an address. A one-time sign-in code in it, like `PairingCodes`, would save typing the password too | After the first version |
| Naming sessions by device, and revoking one | A lost phone, without changing the password | Phase 2 of multi-user.md |
| A typed endpoint for media | Streaming video with `AVPlayer`. Only for an allowlist of media types, and never anything a browser could run | Later |

Thumbnails are the one with a real choice in it. .NET has no image decoding of its own. On a Mac
host, `sips` and Quick Look make thumbnails of everything the Mac can open, HEIC included, but
nothing on Linux. SkiaSharp works everywhere and is MIT-licensed, but is a native library to ship and
is weak on HEIC. ImageSharp is pure .NET but under a split licence that needs a commercial one past a
revenue threshold. Generated thumbnails should be cached under the account's `.homebase/`, where
nothing is browsed, copied or synced.

## After viewing: uploading and syncing

**Uploading** fits what's there. `/api/uploads` is begin, then a `PUT` per file, then finish, and it
is already open to every account: it checks there is room before anything is sent, nothing appears
until the end, and sending a file again under the same name replaces the earlier attempt. From the
phone that means a Photos picker, a document picker, and a share extension. Uploads carry on in the
background only through a background `URLSession`, which uploads from a file, so photos are written
out to files first. All of that goes behind `Transfers`.

**Backing up the camera roll** runs into the rule that Uncloud never overwrites: send `IMG_0001.jpg`
twice and the second becomes `IMG_0001 2.jpg`. So the phone has to know what it has already sent,
either from a record it keeps (each photo's `localIdentifier` against where it went) or because the
server can say a file with these contents is already there. The first needs no server change and is
where to start.

**Syncing** doesn't mean Syncthing on a phone. There is no official Syncthing for iOS, and iOS
wouldn't let it run in the background if there were. On an iPhone, "my Uncloud files, kept in step"
means Uncloud appearing in the **Files** app, which is a File Provider extension. That needs one
thing the server doesn't have: a way to ask what changed since last time. Today there are only whole
listings of one folder at a time. That change feed is the main server work for that phase.

What the first version does so this isn't in the way: `UncloudKit` has no UIKit and can run in an
extension; the session is in a Keychain item that can be shared; downloads go through `Transfers`,
which uploads will use too.

## Decisions to make

1. **The oldest iOS supported.** iOS 18 is recommended: it covers almost every phone in use and
   everything this needs. iOS 17 would work too; anything older loses `@Observable`.
2. **TestFlight only, or the App Store.** The App Store means keeping a demo host up for review, for
   as long as the app is listed.
3. **Thumbnails before or after the first version ships.** Recommended after, but soon: photos are
   most of what a family will look at on a phone, and that is where the first version will feel
   thinnest.
4. **iPhone only, or iPad too.** Recommended both. With SwiftUI the iPad comes nearly free; a
   two-column layout for it can wait.

## Milestones

1. **Viewing.** Typed address, sign-in, browsing, Quick Look, sharing, sign-out. `UncloudKit` tests.
   On TestFlight for the household.
2. **Finish.** Finding the host over Bonjour, room left, managing the cache, asking before large
   downloads, the contract test in CI, `version` in `/api/health`.
3. **Thumbnails.** The server endpoint, and a grid for folders that are mostly photos.
4. **Uploading.** From Photos, from Files, and from the share sheet in other apps.
5. **Backing up and the Files app.** The camera roll, and the File Provider extension with the
   change feed it needs.
