# Homebase landing page

A single-screen, static page for trying the Homebase message: **“Turn your computer into your personal cloud.”** One headline, one sentence, one early-access action. “Personal cloud” is the product direction; the supporting copy describes the first Mac file-storage milestone. Use “your computer” while broader hardware support remains unproven.

## Preview and build

From the repository root:

```sh
npm --prefix src/homebase-web run dev:landing
# Open http://127.0.0.1:5174

npm --prefix src/homebase-web run build:landing
# Static output: artifacts/landing/
```

This uses the app’s existing Vite installation, but builds independently. The static output contains only the landing page and its assets. Host this directory on a static host; never expose the local file server to publish this page.

## Refine the message

All visitor-facing copy is in `index.html`; styles are in `styles.css`. Charcoal, orange, and bold sans-serif type give the landing page its own identity. Keep all content within the first viewport at normal desktop/mobile sizes; allow natural scrolling at enlarged accessibility text sizes. No analytics or signup database is included.

The early-access link is awaiting an owner-provided email address or signup-form URL. Until that destination is supplied, the final button is disabled and says requests open soon. Don’t publish the page as an active signup funnel until the link is connected.

For the first few conversations, share the page and ask:

1. What do you think Homebase does?
2. Which files would you want to bring home?
3. What would stop you from trying it?

Change one major message at a time. A useful next headline to compare is **“A home for your files. On a drive you own.”** Favor clear expectations over clicks. Imports are planned; avoid promising remote access, syncing, backup, subscription savings, or finished migration tools.
