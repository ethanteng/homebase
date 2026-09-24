# Uncloud's own Dropbox app

This is for whoever publishes Uncloud. Nobody running Uncloud needs to read it.

## The problem it solves

Dropbox will only return a sign-in to an address registered with the app being signed in to. Every
Uncloud is at a different address on a different computer, and none of those addresses can be
registered by the person sitting at it — registering one needs an account on
dropbox.com/developers. That single step, not the OAuth flow, is what made connecting Dropbox a
job for somebody willing to use a developer console.

So it is done once, centrally. One Dropbox app, one registered address, and a page at that address
that forwards each sign-in the last hop to whichever Uncloud started it.

## Why a relay in the middle of a sign-in is safe

Because of PKCE, and only because of PKCE.

The verifier is generated on the computer running Uncloud and never leaves it. Only the
`code_challenge` — a SHA-256 of it — goes to Dropbox. So the authorization code that passes through
the relay cannot be exchanged for a token by the relay, by the host it runs on, by anyone reading
its logs, or by anyone who intercepts the hop. Redeeming it requires the verifier, which only the
Uncloud that started the sign-in has. There is no client secret anywhere in Uncloud, which is why
the app key can sit in the open in `DropboxRelay.cs`.

**The one thing that would undo all of it** is a relay that forwards to an address somebody else
chose. An attacker could then start a sign-in with a verifier they hold, send a victim through
Uncloud's own app — Uncloud's name on the consent screen and all — and collect the code at their own
server. That is a Dropbox account takeover, and PKCE does not prevent it, because the attacker holds
both halves.

So the destination is not anybody's to choose. `api/dropbox-callback.js` fixes the scheme, the host
and the path, and takes exactly one thing from the request: a port number. The most a crafted state
can ask for is a different port on the person's own computer, which reaches nothing that isn't
already theirs. `api/dropbox-callback.test.js` is mostly that one property, and it is the test to
keep working.

It follows that **the relay must never grow support for remote access.** Sending a sign-in to a
tunnel address means accepting a hostname from the request, and there is no shared secret between
the relay and each Uncloud to sign one with. People reaching Uncloud from another computer set their
own app key instead; Uncloud tells them so when they press Connect.

## Setting it up

1. **Register the app.** At [dropbox.com/developers/apps](https://www.dropbox.com/developers/apps),
   create a **Scoped access**, **Full Dropbox** app. On **Permissions**, tick `account_info.read`,
   `files.metadata.read` and `files.content.read` — the same three every Uncloud asks for — and
   **Submit**.
2. **Register the one redirect URI**, on the app's **Settings** tab:

   ```
   https://www.uncloud.life/api/dropbox-callback
   ```

   This address is baked into every released build, so a build already out there is waiting for its
   sign-in to come back to exactly this. Treat it as permanent: changing it strands every sign-in
   from every build that shipped before the change.
3. **Put the app key in the build.** `BuiltIn` in
   `src/Homebase.Core/Providers/DropboxRelay.cs`. It is left empty in the repository, and while it is
   empty none of this exists as far as Uncloud is concerned: accounts connect through their own key
   or the host's, exactly as before. Filling it in is what turns the feature on.

   A secret scanner may flag an app key on the way in. It is a false positive — an app key
   authorises nothing on its own, which is the whole premise above — but it is the kind of false
   positive that turns CI red, so expect to allowlist it.
4. **Deploy.** The function lives in this repository's existing Vercel project alongside the landing
   page, so deploying the site deploys the relay. Nothing to configure: it holds no secret, reads no
   environment variable and stores nothing.

`Homebase:Dropbox:RelayAppKey` and `Homebase:Dropbox:RelayCallback` override both at runtime, for
anyone running their own build against their own app and relay.

## What to know once it is running

- **Dropbox caps how many accounts may link to an app in development mode** until the app is
  approved for production. Until that approval, the number of people who can connect Dropbox this
  way across all Uncloud installs is limited — check the current figure and the approval process in
  Dropbox's own documentation, because this is the one limit that bites without warning. Anybody
  affected can set their own app key and is then unaffected by it.
- **Codes appear in the relay's request logs**, because they arrive in a query string. They are
  single-use, short-lived, and useless without a verifier the relay never sees. Worth knowing rather
  than worth fixing.
- **The relay holds no state**, so there is nothing to back up, migrate or expire, and an outage of
  it stops new Dropbox sign-ins without touching any connection already made or any file already
  brought home.
