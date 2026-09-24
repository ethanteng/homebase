# Uncloud

[![CI](https://github.com/ethanteng/homebase/actions/workflows/ci.yml/badge.svg)](https://github.com/ethanteng/homebase/actions/workflows/ci.yml)

**Turn a computer you already own into your household's private cloud.**

Uncloud keeps your family's files on a computer at home instead of on somebody else's servers.
Everyone gets their own private space, signs in from any browser, and can keep a folder on their
laptop in sync with it. There's no monthly storage bill, and nobody else holds your files.

**[Download Uncloud for Mac](https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-arm64.dmg)** (Apple silicon) ·
[Intel Macs](https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-x64.dmg) · [How to open it](#get-the-app)

> Uncloud is early. It works, but read [What isn't ready yet](#what-isnt-ready-yet) before you move
> anything important onto it.

- [How it works](#how-it-works)
- [Get the app](#get-the-app)
- [Set up Uncloud](#set-up-uncloud) — for the person who looks after it
- [Use Uncloud](#use-uncloud) — for everyone in the household
- [Keeping your files safe](#keeping-your-files-safe)
- [Questions and problems](#questions-and-problems)

## How it works

One computer at home is the **host**: a Mac or a Linux machine you already have, like an old
laptop or a desktop that's always on. Everyone's files live on its drive, or on an external drive
plugged into it.

- **Everyone has their own space.** Each person has their own username and password, and sees only
  their own files. Nobody else in the household can open them.
- **Bring your files home.** Click **Add files** and copy files and folders out of Dropbox, or out
  of a folder or drive on the host (such as the folder your Google Drive, OneDrive or iCloud app
  keeps), into your space. What you add is yours alone; sharing is something you choose to do.
- **Keep your laptop in step.** Choose a folder on your laptop and it stays in sync with Uncloud.
  Add, change or delete a file on one and the same happens on the other, even when you're away
  from home.
- **Reach it from anywhere.** Turn on remote access and everyone can sign in from their phone or
  any browser, at home or out, with a secure `https://` address.

### What you can do today

- Browse your folders, look at file details, and download files, from any browser.
- Add files from Dropbox, or from folders and drives on the host, all from one **Add files** button.
- See how much room is left on the host at a glance, on every page.
- Sync folders on your own computers with your space.
- Get back a file deleted on a synced laptop for 30 days afterwards.
- Add and manage everyone in the household from one screen.

### What isn't ready yet

- **No uploading, renaming or deleting in the browser.** Files get in by importing or syncing a
  computer.
- **The app isn't signed by Apple yet**, so macOS asks you to confirm it the first time you open
  it ([how](#get-the-app)). It's Mac only for now.
- **The host has to stay on and awake.** On a Mac, turn off sleep in System Settings, and tick
  **Open at Login** in the Uncloud menu so it starts again after a restart.
- **No sharing of files inside Uncloud** between people, and no storage limits per person. (A folder
  on the host can be shared for others to add from.)
- **No built-in backup.** Uncloud keeps one copy of everything. [Set up a backup](#keeping-your-files-safe).
- **No direct Google Drive, iCloud or Evernote sign-in.** Use the folder their app keeps on the
  host instead.
- Syncing a Windows or Linux computer needs a free app called Syncthing, set up by hand.
- Windows can't be the host yet, though Windows computers can use Uncloud in a browser.

## Get the app

There's one Uncloud app for Mac. The same app runs Uncloud on the host, and keeps everyone's own
Macs in sync with it.

1. Download the app for your Mac. To check which you have, open  → **About This Mac**: it says
   **Chip: Apple M…** or **Processor: Intel**.
   - **[Apple silicon (M1 and later)](https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-arm64.dmg)**
   - **[Intel](https://github.com/ethanteng/homebase/releases/download/mac-latest/Uncloud-osx-x64.dmg)**
2. Open the download, and drag **Uncloud** onto the **Applications** folder beside it. Then eject
   the **Uncloud** disk in Finder's sidebar.
3. Open it. Uncloud isn't signed by Apple yet, so the first time macOS stops it:
   - **macOS 15 Sequoia and later:** click **Done**, then open **System Settings → Privacy &
     Security**, scroll down to the message about Uncloud, and click **Open Anyway**.
   - **Earlier macOS:** right-click (or Control-click) Uncloud in Applications, choose **Open**,
     then **Open** again.

   After that it opens normally. Uncloud lives in the menu bar at the top of the screen, as a
   **U**, not in the Dock.

The app is about 60 MB to download because everything it needs is inside it: there is nothing
else to install. These links always give you the latest build. If it ever won't open, what went
wrong is in `~/Library/Application Support/Uncloud/uncloud.log`.

## Set up Uncloud

This part is for whoever looks after Uncloud. With the Mac app, setup takes five minutes or less.
Manual setup on Linux or from Terminal takes longer.

On a Mac, [get the app](#get-the-app), open it, and choose **Make this Mac the Uncloud host**. It
starts Uncloud and opens your browser. Then carry on from [step 3](#3-make-your-account).

To run Uncloud from Terminal instead, or on Linux, follow steps 1 and 2. You'll need to be
comfortable pasting commands into Terminal. For Linux, see the
[technical reference](docs/technical.md#running-from-source).

### 1. Install the tools Uncloud is built with

Install [Homebrew](https://brew.sh) if you don't have it, then open **Terminal** and run:

```sh
brew install --cask dotnet-sdk
brew install node go git
```

### 2. Download and start Uncloud from Terminal

```sh
git clone https://github.com/ethanteng/homebase.git ~/uncloud-app
cd ~/uncloud-app
./scripts/build-tunnel.sh
./scripts/run.sh
```

The first start takes a few minutes. It's ready when the messages stop scrolling. **Leave this
Terminal window open.** Closing it, or pressing Ctrl+C, stops Uncloud. To start it again later:

```sh
cd ~/uncloud-app
./scripts/run.sh
```

`./scripts/build-tunnel.sh` only has to be run once. It builds the tunnel that lets people reach
Uncloud from other devices, which you turn on in step 6 — there is nothing to type at Terminal
for that.

### 3. Make your account

On the host, open **http://127.0.0.1:5210** in a browser.

Uncloud asks **Who are you?** Choose a username and a password of at least 10 characters. This
first account looks after Uncloud: it can add people and change settings. This step only works on
the host itself, so nobody on the internet can claim your Uncloud before you do.

### 4. Choose where everyone's files will live

Choose a folder, either a new empty one or one on an external drive, and click **Use this folder**.
Everyone's files will go inside it, each person in their own space, and everyone shares that
drive's free space. Pick a drive with plenty of room.

If your Mac says Uncloud (or Terminal) can't read the folder, allow it under **System Settings →
Privacy & Security → Files and Folders**.

Uncloud never moves or changes files that are already there.

### 5. Add everyone else

Open **People** and click **Add someone**. Give them a username and a first password. Uncloud
can't send e-mail, so tell them the password yourself and ask them to change it.

Tick **Let them look after this Uncloud too** only for another adult who should be able to manage
accounts.

### 6. Let everyone in

Open **Settings**. Under **Reaching this host from anywhere**, click **Reach this host from
anywhere**, then **Allow this host**. You'll be taken to Tailscale, a free service Uncloud uses for
its secure connection. Sign in or make an account.

(With the app, ticking **Reach From Anywhere** under the **U** in the menu bar does the same
thing. Either one is enough; they are the same switch.)

Tailscale then asks one more thing, also once: that you let this tailnet be reached from the
public internet. Uncloud puts that in front of you too — the same panel shows **Open it to the
internet**. Click it, say yes at Tailscale, and come back.

After a minute Uncloud shows your address, something like `https://uncloud.tail1234.ts.net`. Send
that to everyone. It works at home and away, on any device.

To close it again later, use the same panel: **Stop reaching it from anywhere** takes the address
out of service at once, without stopping Uncloud for anybody at home.

This connection is fine for browsing and downloading everyday files. It's slow for very large
ones, like hours of video.

### 7. Dropbox (optional)

Nothing to do. Uncloud comes with its own Dropbox app, so anybody with an account here can connect
their own Dropbox from **Add files** by pressing **Connect** — no developer console and no app key.
It shares nobody's files: each person signs in to their own Dropbox.

From the computer Uncloud runs on that's one click. From anywhere else — another computer, through a
proxy you set up, or on an `https://` address with your own certificate — Uncloud can't be handed
the sign-in directly, so Dropbox shows a code at the end and you copy it across. Uncloud says so
before you start, and mistyping the code costs nothing.

You might still register your own Dropbox app if you would rather your household's sign-ins didn't
go through an app somebody else registered, or you'd rather nobody ever copied a code. Anybody can
set theirs under **My account** without waiting for you.

If you want your own, open **Settings**, scroll to **Dropbox for everyone here**, and click
**Advanced — Use a Dropbox app you registered**. It is four steps:

1. Go to [dropbox.com/developers/apps](https://www.dropbox.com/developers/apps) and click
   **Create app**. Choose **Scoped access** and **Full Dropbox**, and give it any name.
2. On the **Permissions** tab, tick `account_info.read`, `files.metadata.read` and
   `files.content.read`, then **Submit**. These only let Uncloud read a Dropbox, never change it.
3. On the **Settings** tab, add the **Redirect URI** that Uncloud shows you, and copy the
   **App key**.
4. Paste the app key into Uncloud.

Do this after step 6, because the redirect URI is based on your Uncloud's address. A key you set
here replaces Uncloud's own for everybody who hasn't set one of their own — and anyone can always
set their own under **My account**, which beats both.

### 8. Set up a backup

Do this before anyone relies on Uncloud. See [Keeping your files safe](#keeping-your-files-safe).

## Use Uncloud

### Sign in

The person who looks after your Uncloud gives you its address, your username and a first password.
Open the address in any browser and sign in. Then go to **My account → Change my password**.

Forgot your password? Ask them. They can give you a new one under **People**.

### Your files

**My files** is your space. Only you can see it. Click into folders, use the browser's back
button, filter and sort, and click a file to see its details or download it. The sidebar and the
top of every page show how much room is left on the host, and how much of it is yours. They turn
amber, then red, when it runs low.

### Add files

On **My files**, click **Add files** and choose where they are:

- **This computer** (for whoever looks after Uncloud): the folders and drives on the host, such as
  Documents, Pictures, an old backup drive, or the folder your Dropbox or Google Drive app keeps.
  Open one to look inside, or add the whole thing. **Choose another folder…** finds anything else.
- **Dropbox.** Click **Connect Dropbox** and sign in — there is nothing to set up first. Uncloud can
  only read your Dropbox, never change it.

  If you're using Uncloud from a different computer than the one it runs on, there's one extra step
  and Uncloud says so before you start: Dropbox shows you a code at the end instead of sending you
  back, and you copy it into the box Uncloud is waiting with. Mistyping it costs nothing — paste it
  again.
- **Shared with you**, if someone has shared a folder with everyone.

**Sign Uncloud out of Dropbox** at the bottom of the Dropbox list ends the connection whenever you
like. Files you already added stay where they are; only Uncloud's way back into Dropbox goes.

Google Drive and Evernote are coming. Until then, the folder the Google Drive app keeps on the host
works under **This computer**.

Click **Add** next to a file or folder. Uncloud checks it fits first, and the space left is shown
at the top of every page. You can close the page while it runs; **My files** shows how far it has
got, and **Stop** ends it early and keeps whatever has already arrived.

Files are copied, never moved: the originals stay where they were. Adding the same folder again
only brings what's new, and Uncloud never overwrites a file you already have. Added files appear
at the top of **My files**, in a folder named after where they came from, such as **Dropbox** or
**Documents**.

**What you add is yours alone.** A folder on this computer that you add can only be added from by
you. To let everyone in the house add from it too — a folder of family photos, say — open it in
**Add files** and click **Share this folder with everyone here**. **Make private again** undoes it.
Sharing lets others copy from that folder into their own space; it never shows them your files in
Uncloud.

### Keep a folder on your laptop in sync

**On a Mac, with the Uncloud app:**

1. [Get the app](#get-the-app) on your laptop, open it, and choose **Connect this computer to an
   Uncloud**.
2. In Uncloud in your laptop's browser, open **My computers** and click **Get a pairing code**.
3. Click **open in the Uncloud app**. (Or type the address and code Uncloud shows into the app.)

That's all. Your Uncloud files appear in a folder called **Uncloud** in your home folder, and stay
in step both ways. The Uncloud icon in the menu bar shows whether you're up to date, and pauses or
disconnects the laptop.

**On Windows or Linux, or without the app:**

1. Download and install [Syncthing](https://syncthing.net/downloads/) on your laptop, and open it.
2. In Uncloud, open **My computers** and copy this Uncloud's ID.
3. In Syncthing, click **Add Remote Device** and paste it.
4. In Syncthing, click **Actions → Show ID** and copy your laptop's ID.
5. Back in Uncloud, paste it and click **Add computer**.
6. In Uncloud, type the folder to sync (for example `Documents`, or leave it empty for everything)
   and click **Sync folder**. Accept the folder when Syncthing on your laptop offers it.

Or go the other way: share a folder from Syncthing on your laptop, and click **Keep it here** when
it appears in Uncloud under **Offered by your computers**.

From then on, changes on either side reach the other, whether you're at home or away.

**Deleting a file on your laptop deletes it in Uncloud too.** If that was a mistake, Uncloud keeps
deleted and replaced files for 30 days. Ask the person who looks after Uncloud to get it back;
there's no restore button yet. If a file changes on both sides at once, you'll get both versions,
one with `sync-conflict` in its name.

**Stop syncing** stops the folder syncing, and leaves the files where they are on both sides.

## Keeping your files safe

**Uncloud is not a backup.** It keeps one copy of everything, on one drive. If that drive fails
or the computer is stolen, the files are gone. Syncing a laptop doesn't protect you either,
because a deletion syncs just like a new file does.

Before you rely on Uncloud:

- **On a Mac, turn on Time Machine** with a second drive, and make sure it includes the folder you
  chose in step 4.
- Or use a backup tool or service that keeps **older versions** of files, not just a mirror, so
  you can get back something that was deleted weeks ago.
- To keep the accounts as well as the files, also back up
  `~/Library/Application Support/Homebase`.

**Who can see what:**

- In Uncloud, each person's files are visible only to them. That includes the people who look
  after Uncloud: they manage accounts, but can't browse anyone else's files. They can give someone
  a new password, but that signs the person out, so they'd notice.
- Anyone who can sign in to the **host computer itself** can open every folder on its drive, as
  with any computer. Keep the host's own login private.
- With remote access on, passwords are what keep strangers out, so everyone should use a good one.
  Uncloud slows down anybody who keeps guessing.
- Files travel encrypted between your devices and the host, and aren't stored with any cloud
  company.

## Questions and problems

**I can't reach Uncloud.** Check that the host is on and awake. With the app, click the **U** in
the host's menu bar: it says whether Uncloud is running, and **Start Uncloud Again** restarts it.
Tick **Open at Login** so it comes back after a restart. From Terminal, check the window from
step 2 is still open, and start it again if the computer restarted.

**My laptop isn't syncing.** Click the **U** in the laptop's menu bar. It says whether it's up to
date, syncing, paused, or can't reach Uncloud. If it can't, check that the host is on, and that
the laptop can open Uncloud's address in a browser.

**macOS says Uncloud can't be opened.** It isn't signed by Apple yet. See
[Get the app](#get-the-app) for how to open it the first time.

**Dropbox sign-in fails.** If you are using your own Dropbox app, the redirect URI registered with
Dropbox has to match the one Uncloud shows now. It changes if you turn on remote access after
setting up Dropbox. Add the new one on the app's **Settings** tab at dropbox.com.

**Dropbox showed me a code instead of sending me back.** That's expected when you aren't using
Uncloud on the computer it runs on — also when Uncloud is behind a proxy you set up, or answers on
an `https://` address with your own certificate. Uncloud can't be handed the sign-in directly in any
of those, so it asks Dropbox for a code instead. Copy it into the box Uncloud is showing and you're
connected; if you mistype it, paste it again. To never see that step, register a Dropbox app of your
own under **My account** — a sign-in through your own app comes straight back from anywhere.

**The external drive was unplugged.** Plug it back in and refresh. If it comes back under a new
name, choose the folder again under **Settings**.

**My computers says Syncthing isn't running.** The app has Syncthing built in. Run from Terminal,
Uncloud downloads its own copy when it starts; if the host was offline then, restart Uncloud once
it's back online.

**I want to change how Uncloud runs, or help build it.** See the
[technical reference](docs/technical.md). It covers configuration, running on a home network
without Tailscale, other remote-access options, how the privacy between accounts works, and
development.

---

The landing page for [uncloud.life](https://www.uncloud.life/) lives in [`landing/`](landing/README.md).
Inside the code, Uncloud is still called Homebase.
