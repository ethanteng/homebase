# Uncloud landing page

A single-screen page for **Uncloud**, with the intended public address **https://uncloud.life/**. The large headline is **“Stop paying for [Dropbox].”**, rotating through **Dropbox → Evernote → Google Drive**. The supporting copy focuses on private household storage and ending recurring cloud-storage bills.

The headline's service name has an orange box on its own centered line, so rotation never changes the number of lines. Beneath it, **“Your digital life belongs at home.”** uses the smaller bold supporting style, with “at home” in orange. The next line reads **“Turn any computer you own into private storage for your household.”** “Any” has a casual orange underline, and “private storage” is orange. The early-access action and **“Private. Secure. Yours.”** complete the page. Keep it above the fold with the rest of the page. This is early-access positioning for the product vision; see the repository README for the application's current capabilities. The app, source folders, and storage paths still use the internal name Homebase.

## Preview and build

From the repository root:

```sh
npm --prefix src/homebase-web run dev:landing
# Open http://127.0.0.1:5174

npm --prefix src/homebase-web run build:landing
# Static output: artifacts/landing/
```

This uses the app’s existing Vite installation, but builds independently. The static output contains only the landing page and its assets. Host this directory on a static host; never expose the local file server to publish this page.

## Deploy to Vercel

The repository-root `vercel.json` installs the frontend dependencies, builds the landing page, and publishes only `artifacts/landing`.

In the Vercel project's Build and Deployment settings, keep **Root Directory** at the repository root (leave it empty). Use Node.js 22.12 or newer. The checked-in configuration supplies these settings:

| Setting | Value |
| --- | --- |
| Framework Preset | Other (`framework: null`) |
| Install Command | `npm --prefix src/homebase-web ci` |
| Build Command | `npm --prefix src/homebase-web run build:landing` |
| Output Directory | `artifacts/landing` |

Commit and push the configuration to trigger a new Git deployment. Redeploying an older commit will not include it. If the homepage returns 404 after a successful deployment, verify the root directory and that the deployment output contains `index.html` at its top level. The source page lives in `landing/`; it is not an index page at the repository root. The ordinary frontend `build` script builds the local file-browser app, so use `build:landing` for this website.

## Refine the message

Visitor-facing copy and metadata are in `index.html`; styles are in `styles.css`; `uncloud.svg` is the shared brand mark and favicon. `main.js` rotates provider names every three seconds with a brief fade. Rotation has no visible controls; it stops for reduced-motion preferences and pauses in background tabs. Screen readers get a stable list of providers, and Dropbox remains visible without JavaScript. Charcoal, orange, and bold sans-serif type give the landing page its own identity, with emphasis on “at home.” Keep all content within the first viewport at normal desktop/mobile sizes; allow natural scrolling at enlarged accessibility text sizes. No analytics or signup database is included.

Canonical and Open Graph URLs point to `https://uncloud.life/`. These metadata tags do not configure DNS, hosting, or deployment.

The early-access link is awaiting an owner-provided email address or signup-form URL. Until that destination is supplied, the button is disabled. The button reads “Get early access” without an arrow or a status note above it. Don’t publish the page as an active signup funnel until the link is connected.

For the first few conversations, share the page and ask:

1. What do you think Uncloud does?
2. Which files would you want to bring home?
3. What would stop you from trying it?

Change one major message at a time. A useful next headline to compare is **“Uncloud your life.”** Keep the distinction between early-access positioning and available product features clear when inviting people to try the app.
