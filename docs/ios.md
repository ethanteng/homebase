# Uncloud for iPhone

The design for an iPhone and iPad app that signs in to an Uncloud host and shows the person their
files, and how to build it. The first version is for viewing only; uploading and syncing come after,
and the end of this document says what they will need so that nothing built now is in their way.

**Milestone 1 is built**, in [`src/Uncloud.iOS`](../src/Uncloud.iOS): typing the address, signing
in, browsing, opening files in Quick Look, sharing them, and signing out, on iPhone and iPad. The
rest of [milestone 2](#milestones) isn't yet: resuming a dropped download, asking before a large
one, the cache's size limit and its line on the account screen, how much room is left, and finding
the host over Bonjour. This document describes all of it; where something is still to come, the
milestones say so.

- [Why an app](#why-an-app)
- [What the first version does](#what-the-first-version-does)
- [How the phone reaches the host](#how-the-phone-reaches-the-host)
- [Connecting](#connecting) and [signing in](#signing-in)
- [Browsing](#browsing) and [opening a file](#opening-a-file)
- [The code](#the-code)
- [Testing and shipping](#testing-and-shipping), and [building it](#building-it)
- [Server changes worth making](#server-changes-worth-making)
- [After viewing: uploading and syncing](#after-viewing-uploading-and-syncing)
- [Backing up the camera roll](#backing-up-the-camera-roll), so Uncloud can take iCloud Photos' place
- [Decisions](#decisions) and [milestones](#milestones)

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
things above, and couldn't grow into the background uploads and Files app that come next.

## What the first version does

**Does:** connect to one host, sign in, browse **My files**, sort and filter the folder being
looked at, open any file in Quick Look, share or save it, show how much room is left, sign out.

**Doesn't:** upload; copy, move, rename or delete; the bin; **Add files**, Dropbox or imports;
**People** and **Settings**; offline browsing; more than one host or account at a time; thumbnails;
streaming video. An administrator still uses the web for everything administrative, and the app
links there.

## How the phone reaches the host

The host listens on `127.0.0.1` only. Nothing on the home network can reach it directly, the phone
included. The one way in is the address **Access from anywhere** gives it, such as
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
  Plain HTTP is allowed to loopback alone, for a simulator talking to a server run from source on
  the same Mac.
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
Ask whoever looks after it to turn on Access from anywhere."*

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
      UncloudClient.swift    one method per endpoint, async
      UncloudError.swift     every refusal, worded for the person holding the device
      Models.swift           User, SessionState, DirectoryListing, LibraryEntry
      Timestamp.swift        the host's dates
      TokenStore.swift       the token in the Keychain, one per host
      Transfers.swift        downloads now, uploads later, behind one interface
      FileCache.swift        what's downloaded
      HostAddress.swift      reading a typed address
    Tests/UncloudKitTests/
  Uncloud-Info.plist  the keys Xcode doesn't write itself
```

`Uncloud/PrivacyInfo.xcprivacy` declares the one API on Apple's required-reason list the app uses,
`UserDefaults`, for its own settings. App Store Connect refuses an upload that uses one of those
without declaring it, so anything that starts reading file timestamps, disk space or the time since
the device started adds it there too.

The project uses Xcode 16's synchronised folders, so a file added under `Uncloud/` is in the app
without touching `project.pbxproj`.

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
  reads the token, the five things above, downloads, and the cache. They run with `swift test`,
  which `./scripts/check.sh` does whenever there is a Swift 6 on the `PATH`, Linux included. On
  Linux, Foundation traps finishing a download task that a stub fed, so the three tests that finish
  one are skipped there and run on Apple's platforms, where the app does.
- **The server pins what the app relies on.** `NativeClientTests` signs in, browses, downloads and
  signs out the way the app does, with no `Origin` and the cookie sent by hand, and checks the `+`
  and the dates. A hardening change that would shut the app out fails there.
- **A contract test against the real server** (milestone 2). On the macOS CI runner, start the
  server from source on loopback, make the first account with `/api/setup`, and run `UncloudKit`'s
  integration tests against it, so a change to the API that would break the app fails the pull
  request that makes it, not somebody's phone afterwards.
- **CI's iOS job** runs `UncloudKit`'s tests on an iPhone and an iPad simulator, whichever this
  Xcode has the newest iOS for, and builds the app for the simulator and for a device, unsigned.
- **TestFlight only.** It needs the same Apple Developer membership that signs the Mac app; the
  bundle id is `life.uncloud.ios`, beside the host's `life.uncloud.host`. TestFlight has two kinds of
  tester, and the difference is who has to be reachable:
  - **Internal testers**, up to 100, are people added to the team in App Store Connect. Their builds
    need no review at all. This is the one to use for the household. Each person is added to App
    Store Connect with the narrowest role it has, limited to this app so they see nothing else the
    team publishes, and installs the TestFlight app.
  - **External testers**, by invitation or a public link, see a build only after Beta App Review,
    once for each new version. That review asks for an account to sign in with, so a demo Uncloud
    has to be up, with something in it, while each version is reviewed. Only needed once the app
    goes past the household.
- **A build stops opening 90 days after it was uploaded.** CI's TestFlight job uploads one from
  every push to `main`, so nobody's app expires because nothing changed for three months. It is off
  until it has been set up ([below](#turning-on-testflight)).
- **iPhone and iPad, one app.** The same screens on both for now; a two-column layout on iPad can
  come later. A share sheet the app opens itself, rather than Quick Look's, is a SwiftUI `ShareLink`:
  on iPad a share sheet must be anchored to what was tapped, and one shown without an anchor crashes.

## Building it

Open `src/Uncloud.iOS/Uncloud.xcodeproj` in Xcode 16 or later, choose your team under **Signing &
Capabilities**, and run. To try it in the simulator against a host on the same Mac, start one with
`./scripts/run.sh`, make its first account at `http://127.0.0.1:5210` in a browser, then type
`http://127.0.0.1:5210` into the app. On a real iPhone or iPad, use the host's Access from anywhere
address.

`swift test --package-path src/Uncloud.iOS/UncloudKit` runs the library's tests without Xcode.

### Turning on TestFlight

Once, in this order:

1. At [developer.apple.com](https://developer.apple.com/account/resources/identifiers/list), register
   the App ID `life.uncloud.ios` under the team that signs the Mac app.
2. In [App Store Connect](https://appstoreconnect.apple.com/apps), add a new iOS app with that bundle
   ID. Its name has to be unique on the App Store even though it will never be listed there, so it
   may need to be something like *Uncloud Home*; TestFlight shows that name, and the home screen
   shows *Uncloud* either way.
3. Under **Users and Access → Integrations → App Store Connect API**, make a team key with the
   **Admin** role, which automatic signing needs to make the distribution certificate and profile
   by itself. The key the Mac app notarizes with will do if it already has that role. No device has
   to be registered: CI builds the archive unsigned and signs it for the App Store as it exports it,
   because signing while archiving is development signing, which Apple only provisions for a team
   with a device.
4. In this repository's **Settings → Secrets and variables → Actions**, add the secrets
   `APP_STORE_CONNECT_KEY` (the whole `.p8` file), `APP_STORE_CONNECT_KEY_ID` and
   `APP_STORE_CONNECT_ISSUER_ID`, and the variables `APPLE_TEAM_ID` and `IOS_TESTFLIGHT` set to
   `true`.
5. Run **CI** by hand from the Actions tab to upload the first build, then add the household as
   internal testers in App Store Connect under **TestFlight**. After that every push to `main`
   uploads a build, numbered by the run.

## Server changes worth making

None of these is needed for the first version.

| Change | What it's for | When |
| --- | --- | --- |
| A `version` in `/api/health` | Saying *"This Uncloud needs updating"* instead of failing vaguely against an old host | With the first version, if it's cheap |
| `GET /api/files/thumbnail?path=&size=` | Photo grids. Without it a folder of photos is a list of names, and downloading each full image over Funnel isn't an option | Straight after the first version |
| An "Add a phone" QR code in **My account** | The phone scans it instead of anybody typing an address. A one-time sign-in code in it, like `PairingCodes`, would save typing the password too | After the first version |
| Naming sessions by device, and revoking one | A lost phone, without changing the password | Phase 2 of multi-user.md |
| A typed endpoint for media | Streaming video with `AVPlayer`. Only for an allowlist of media types, and never anything a browser could run | Later |
| `POST /api/camera-roll` | A photo sent by iOS while the app isn't running, in one request ([below](#the-server-one-request-per-file)) | Milestone 5 |

Thumbnails are the one with a real choice in it. .NET has no image decoding of its own. On a Mac
host, `sips` and Quick Look make thumbnails of everything the Mac can open, HEIC included, but
nothing on Linux. SkiaSharp works everywhere and is MIT-licensed, but is a native library to ship and
is weak on HEIC. ImageSharp is pure .NET but under a split licence that needs a commercial one past a
revenue threshold. Generated thumbnails should be cached under the account's `.homebase/`, where
nothing is browsed, copied or synced.

## After viewing: uploading and syncing

**Uploading** fits what's there. `/api/uploads` is begin, then a `PUT` per file, then finish, and it
is already open to every account: it checks there is room before anything is sent, nothing appears
until the end, and sending a file again under the same name replaces the earlier attempt. Before
beginning, `/api/uploads/compare` says which files are already in the destination with the same
size and modified time, and finishing with `merge` adds to folders already there rather than beside
them. From the
phone that means a Photos picker, a document picker, and a share extension. Uploads carry on in the
background only through a background `URLSession`, which uploads from a file, so photos are written
out to files first. All of that goes behind `Transfers`.

**Backing up the camera roll** is its own thing, with its own way of running in the background, and
has [its own section](#backing-up-the-camera-roll).

**Syncing** doesn't mean Syncthing on a phone. There is no official Syncthing for iOS, and iOS
wouldn't let it run in the background if there were. On an iPhone, "my Uncloud files, kept in step"
means Uncloud appearing in the **Files** app, which is a File Provider extension. That needs one
thing the server doesn't have: a way to ask what changed since last time. Today there are only whole
listings of one folder at a time. That change feed is the main server work for that phase.

What the first version does so this isn't in the way: `UncloudKit` has no UIKit and can run in an
extension; the session is in a Keychain item that can be shared; downloads go through `Transfers`,
which uploads will use too.

## Backing up the camera roll

What a household needs before Uncloud can take the place of iCloud Photos: every photo and video
taken on the phone arrives in Uncloud by itself, without anybody opening the app.

iOS has no setting that swaps iCloud Photos for another service. The Photos app keeps the library on
the phone either way, and a backup app copies it out, which is how Google Photos and Immich work. So
this is the app's job, and none of it is built yet.

Facts about Apple's side come from [Uploading asset resources in the
background](https://developer.apple.com/documentation/photokit/uploading-asset-resources-in-the-background)
and the [`PHAssetResourceUploadJob`](https://developer.apple.com/documentation/photos/phassetresourceuploadjob)
pages, read in November 2026. Where something comes from a developer forum instead, it says so.

### How iOS lets it run

**iOS 26.1 added a way for exactly this.** The app hands Photos a `URLRequest` for each thing to
send, with `PHAssetResourceUploadJobChangeRequest.creationRequestForJob(destination:resource:)`
inside `PHPhotoLibrary.performChanges`. The system sends it when it judges the network, power and
how the phone is being used are right, fetching the original from iCloud first if it isn't on the
phone. From time to time it wakes the app's background upload extension
(`com.apple.photos.background-upload`), which makes new jobs, acknowledges the ones that have
finished and retries the ones that failed.

- **It needs full access to the library** (`.readWrite`, `.authorized`), so the app asks for
  `NSPhotoLibraryUsageDescription` too; the add-only key it has today isn't enough. The app turns the
  extension on and off with `PHPhotoLibrary.shared().setUploadJobExtensionEnabled(_:)`, and turns it
  off on sign-out.
- **The extension sees little of what happened.** A job is registered, pending, succeeded, failed or
  cancelled. A failed one has a `URLError`. A finished one, either way, has the response's headers,
  with their names in lower case. The body isn't there, and the status code isn't documented as
  readable. So anything the server has to tell the phone, it says in a header.
- **Finished jobs still count.** `jobLimit` is the most jobs not yet acknowledged, *"in-flight and
  those that have succeeded or failed"*, and its number isn't given. The extension records each
  finished job and acknowledges it on the same wake, or backups stop once the limit fills.
- **Two versions of the extension.** On iOS 26.1 it is `PHBackgroundResourceUploadExtension`, whose
  entry point is `process()`. iOS 27 replaces it with `PHBackgroundResourceUploadJobExtension` and
  an async `processJobs()`. The work is the same; the app ships both, chosen by `#available`.
- **It doesn't run in the Simulator.** The logic lives in `UncloudKit` and is tested there; the
  extension itself only on a real phone. A forum answer says iOS 27 has a *Resource Upload Test Mode*
  under **Settings → Developer**, which is worth trying first.

**Before iOS 26.1**, and if [the address check](#first-check-the-address) rules the extension out, the
app backs up only while it is open, and a background `URLSession` finishes what it started. That is
what most backup apps did before 26.1, and why they asked people to open them now and then.

### First, check the address

The extension's `Info.plist` must have `BackgroundUploadURLBase`, a string Apple describes only as
*"the base URL for your upload server"*, which *"the system requires for network access
validation."* It is set when the app is built. Every household's Uncloud has its own address, which
somebody types in.

Every address **Access from anywhere** gives is under `ts.net` (`https://uncloud.tail1234.ts.net`),
so the thing to find out, on a real phone, before anything else is built: **does a base of
`https://ts.net` let a job go to a household's address?** Apple's page doesn't say how the check
matches, or whether a wildcard or more than one value is allowed.

If it doesn't:

- **Sending photos through a fixed Uncloud address is ruled out.** Photos would pass through a
  server Uncloud runs, which is what people choose Uncloud to avoid.
- **A household's own build** could have its address built in. That works for the one household on
  TestFlight and goes no further.
- Otherwise it is the fallback above, and it is worth asking Apple (Feedback, or a code-level support
  request) whether the key can be anything else.

### Which photos, and where they go

- **The original of each photo or video** (the `.photo` or `.video` resource), and for a Live Photo
  its `.pairedVideo` beside it, under the same name with `.MOV`, as Apple's own exports do. Edits
  aren't sent in the first version: the original is the thing that can't be made again. Sending the
  edited version (`.fullSizePhoto`) beside it can come later.
- **Into `Camera roll/<year>/` in My files**, under the resource's `originalFilename`, with its
  modified time set to when the photo was taken. The year is the photo's `creationDate` in UTC, and
  one with no date goes in `Undated`: the same rules as the photos brought in from an iCloud or
  Google export (`ExportPhotos.Year`), so the two can be put together by moving year folders.
- **The phone chooses the path; the server never overwrites.** A name already taken, by a photo
  from the person's iPad, or because the camera's numbering has gone round, becomes `IMG_1234 2.HEIC`,
  as everywhere else in Uncloud.
- **It's a backup, not a sync.** Deleting a photo on the phone leaves it in Uncloud, unlike iCloud
  Photos. That is what lets somebody clear room on the phone once a photo is backed up.

**Asked once, when backup is turned on:** *Back up everything on this iPhone*, or *Only new photos and
videos*. Somebody who has already brought their library home from an iCloud export has all of it,
and *everything* would send every photo again into `Camera roll`. *Only new* starts from the
library's change token at that moment.

### What the phone remembers

Names can't say what has been sent, since the server makes new ones. The phone keeps a small
record, in an App Group container that the app and the extension share:

- For each photo's `localIdentifier` and resource type: the job's state and the path the server
  stored it under.
- The library's last `PHPersistentChangeToken`. `fetchPersistentChanges(since:)` says what was added
  since, so neither the app nor the extension walks the whole library each time.

That record is where *"All 12,408 backed up"* and *"312 to go"* come from on the account screen,
along with when the phone last got through, which matters on the day the host's tunnel is down. If
the app is deleted the record goes with it, and setting backup up again should choose *Only new*.
A server that could say *"a file with these contents is already here"* would remove that caveat, and
can come later.

### The server: one request per file

Uploads today take three steps, begin, a `PUT` for each file, then finish
([Program.cs](../src/Homebase.Server/Program.cs)), written for a page that stays open from the first
to the last. A job is one request that the system sends by itself, and nothing of the app's runs
between it and the next. So the camera roll gets an endpoint of its own:

`POST /api/camera-roll?path=Camera%20roll/2026/IMG_1234.HEIC&modified=…`, with the file as the body,
`X-Homebase-Request: 1`, and `X-Uncloud-Item:` an id for this resource of this photo, a hash of its
`localIdentifier` and resource type.

- **Room is checked from `Content-Length`** before anything is read, as beginning an upload does.
- **The file is gathered out of sight** and put in place only when it is whole, with the same
  `Place` that never overwrites. Only ever inside the account's own folder, which comes from the
  session, as with every edit.
- **The path it was stored under comes back in `X-Uncloud-Path`**, since the extension sees headers
  and not the body.
- **Sending the same item twice keeps one copy.** The system retries, and a photo whose answer was
  lost on the way back arrives again. The server keeps each item's id against its path in the
  account's `.homebase/`, and answers the same path a second time rather than keeping
  `IMG_1234 2.HEIC`.
- **A refusal says why in `X-Uncloud-Refused`**: `full`, `signed-out` or `not-allowed`, which the
  app words the way the web would. A `401` should also reach the extension as
  `URLError.userAuthenticationRequired`, which Apple's own example looks for.
- **No resuming in the first version.** The system resumes a dropped upload if the server follows
  the [Resumable Uploads for HTTP](https://datatracker.ietf.org/doc/draft-ietf-httpbis-resumable-upload/)
  draft: answering `OPTIONS` with `Upload-Limit`, and a `104` interim response with a `Location`
  while the body arrives. The first version answers `OPTIONS` with `501`, and a dropped upload
  starts again: fine for photos, costly for a long video over Funnel. A `104` has to get from the
  server through `uncloud-tunnel` as well, so check both before promising it.

**The session.** The token is the `uncloud_session` cookie. Whether a job may carry a `Cookie` header
isn't documented, and Apple's example uses `Authorization` instead. Try the cookie first. If it
doesn't arrive, this endpoint alone also takes `Authorization: Bearer` with the same token: the case
[Not a bearer token](#signing-in) left open. A backup that runs keeps the session alive, since it
lasts 30 days from when it was last used. Once it has gone (somebody changed the password), the
extension stops making jobs and the app says *"Sign in again to carry on backing up."*

`NativeClientTests` grows a test for each rule: the same item twice is one file, a name taken becomes
`name 2`, the path comes back in the header, and no `Origin` or browser header is needed.

### Over the network

All of it crosses Funnel, which is bandwidth-limited
([technical.md](technical.md#which-tunnel-and-what-it-costs-you)). Photos are fine. A phone that
films a lot will take a while, and the system chooses when anyway. Each request sets
`allowsExpensiveNetworkAccess` and `allowsConstrainedNetworkAccess` to false unless the person turns
on **Use cellular data**; whether the system honours them on a job is one more thing to check. The
phone on the host's tailnet ([How the phone reaches the host](#how-the-phone-reaches-the-host))
helps most here.

### Leaving iCloud Photos

What somebody does on their side, for the README once this ships:

1. **Bring the library home**, one of two ways. Add an iCloud export
   ([README](../README.md#photos-and-files-from-icloud-and-google)), then choose *Only new*. Or set
   iCloud Photos to **Download and Keep Originals**, wait until the phone has all of them, and
   choose *Everything*. With Advanced Data Protection on, Apple's export leaves photos out, so it's
   the second way.
2. **Wait until the app says everything is backed up.** Running both for a while is fine. An Apple
   engineer [says on the forums](https://developer.apple.com/forums/thread/822256) that iCloud
   Photos being on or off has no bearing on the extension; a developer in the same thread found it
   never ran with iCloud Photos on, on iOS 26.4, which Apple called a bug. Check it on a real phone.
3. **Turn off iCloud Photos** (Settings → their name → iCloud → Photos).
4. **Turn Photo Library off in the phone's iCloud Backup.** Otherwise the phone's backup starts
   holding the library instead.
5. **Know what stops.** Photos on a Mac or iPad no longer gets new photos, edits and albums stay on
   the phone, and an iCloud Shared Photo Library needs iCloud Photos.
6. **Turn on [Uncloud Backup](backup.md).** Without it, the host is the only copy apart from the
   phone.

### The code, and what to check first

```text
src/Uncloud.iOS/
  UncloudKit/Sources/UncloudKit/
    CameraRoll.swift       which photos and resources, their paths, the record of what's sent
  UploadExtension/         the background upload extension, using UncloudKit
```

The app and the extension share the record through an App Group (`group.life.uncloud.ios`), and the
session through the Keychain access group that is [already there](#the-code).

Before the server endpoint or any screen, a throwaway extension on a real phone, against a real host,
answers five questions:

1. Does a `BackgroundUploadURLBase` of `https://ts.net` let a job go to a household's address?
2. Does a `Cookie` header get through?
3. Is the body the file's bytes as they are, and with what `Content-Type`? Apple doesn't say. If it
   is multipart, the endpoint has to read that.
4. Does it run with iCloud Photos on?
5. Are `allowsExpensiveNetworkAccess` and `allowsConstrainedNetworkAccess` honoured?

The first decides whether this section is built as written or as the fallback.

## Decisions

1. **iOS 18 and later.** It covers almost every phone in use and everything this needs.
2. **TestFlight only,** with the household as internal testers. Not the App Store, so there is no
   demo host to keep up for App Review; one is needed only if the app goes to external testers.
3. **Thumbnails after the first version,** as the next thing after it: photos are most of what a
   family will look at on a phone, and that is where the first version will feel thinnest.
4. **iPhone and iPad.** One app, the same screens on both to start with.
5. **The camera roll is backed up through Photos' background upload,** on iOS 26.1 and later, if the
   address check allows it. The rest of the app stays on iOS 18, and an older phone backs up while
   the app is open.
6. **Originals, into `Camera roll`, and never deleted there** when they're deleted from the phone.

## Milestones

1. **Viewing** (built). Typed address, sign-in, browsing, Quick Look, sharing, sign-out, on iPhone
   and iPad. `UncloudKit` tests, and the server's `NativeClientTests`. On TestFlight for the
   household, uploaded by CI from `main`, once it is [turned on](#turning-on-testflight).
2. **Finish.** Finding the host over Bonjour, room left, the cache's size limit and its line on the
   account screen, asking before large downloads and resuming dropped ones, the contract test in
   CI, `version` in `/api/health`.
3. **Thumbnails.** The server endpoint, and a grid for folders that are mostly photos.
4. **Uploading.** From Photos, from Files, and from the share sheet in other apps.
5. **Backing up the camera roll.** First the [five questions](#the-code-and-what-to-check-first) on
   a real phone, then `POST /api/camera-roll`, the extension, and the screen that turns it on and
   says how far it has got. Then the steps for [leaving iCloud Photos](#leaving-icloud-photos) go in
   the README.
6. **The Files app.** The File Provider extension, with the change feed it needs.
