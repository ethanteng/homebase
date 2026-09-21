# Uncloud landing page

A landing page for **Uncloud**, with the public address **https://uncloud.life/**. The first-screen hero's large headline is **“Stop paying for [Dropbox].”**, rotating through **Dropbox → Google Drive → OneDrive → iCloud → Google Photos → Evernote → Notion**. A new name has to fit the orange box on one line at 320px without changing the headline's line count; check a long one in a browser before adding it. The supporting copy focuses on private household storage and ending recurring cloud-storage bills. A short, static explanation below the hero introduces private cloud storage and the difference from Dropbox and Google Drive.

The headline's service name has an orange box on its own centered line, so rotation never changes the number of lines. Beneath it, **“Your digital life belongs at home.”** uses the smaller bold supporting style, with “at home” in orange. The next line reads **“Turn any computer you own into private storage for your household.”** “Any” has a casual orange underline, and “private storage” is orange. The early-access action and **“Private. Secure. Yours.”** complete the hero. Keep them above the fold with the rest of the hero. This is early-access positioning for the product vision; see the repository README for the application's current capabilities. The app, source folders, and storage paths still use the internal name Homebase.

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

## Early-access signups

`api/subscribe.js` is a Vercel serverless function at `/api/subscribe`. It validates the address and asks Mailtrap to email each signup to the notification address. Nothing is stored: the inbox is the list.

The Mailtrap token is only ever read on the server. Never put it in `landing/main.js` or any other file the browser downloads — anything shipped to a browser is public.

Set these in the Vercel project under **Settings → Environment Variables**, for every environment the page is deployed to:

| Variable | Required | Purpose |
| --- | --- | --- |
| `MAILTRAP_TOKEN` | yes | Mailtrap API token. Sending tokens and sandbox tokens are different; use one that matches the mode below. |
| `SIGNUP_NOTIFY_TO` | yes | Address that receives the signup notifications. |
| `MAILTRAP_FROM` | no | Sender address. Defaults to `early-access@uncloud.life`. |
| `MAILTRAP_INBOX_ID` | no | Set it to route through the Mailtrap sandbox (`sandbox.api.mailtrap.io`) instead of live sending. Useful for a preview deployment. |

Live sending needs a **verified sending domain** in Mailtrap, and `MAILTRAP_FROM` has to be on that domain. Until the domain is verified, set `MAILTRAP_INBOX_ID` and read the signups in the sandbox inbox.

Changing an environment variable does not change a deployment that already exists. Redeploy after setting them.

The function answers with a generic message when Mailtrap fails or a variable is missing; the reason goes to the function log, not to the visitor. A missing variable never names itself in a response.

Run the function's tests from the repository root:

```sh
node --test "api/*.test.js"
```

`./scripts/check.sh` runs them along with everything else.

## Refine the message

Visitor-facing copy and metadata are in `index.html`; styles are in `styles.css`; `uncloud.svg` is the shared brand mark and favicon. `main.js` rotates provider names every three seconds with a brief fade. Rotation has no visible controls; it stops for reduced-motion preferences and pauses in background tabs. Screen readers get a stable list of providers, and Dropbox remains visible without JavaScript. Charcoal, orange, and bold sans-serif type give the landing page its own identity, with emphasis on “at home.” Keep the hero and signup within the first viewport at normal desktop/mobile sizes, with the explanatory section below it; allow natural scrolling on short screens and at enlarged accessibility text sizes. GA4 measurement is delivered through GTM; see [measurement.md](measurement.md). No signup database is included.

Canonical and Open Graph URLs point to `https://uncloud.life/`. These metadata tags do not configure DNS, hosting, or deployment.

“Get early access” submits an email address to the signup function described below. Keep the field and the button as one short row under “Private. Secure. Yours.”; on success the form is replaced by a confirmation line.

For the first few conversations, share the page and ask:

1. What do you think Uncloud does?
2. Which files would you want to bring home?
3. What would stop you from trying it?

Change one major message at a time. A useful next headline to compare is **“Uncloud your life.”** Keep the distinction between early-access positioning and available product features clear when inviting people to try the app.

## Search and sharing assets

The title, description, Open Graph/Twitter metadata, and JSON-LD live directly in `index.html`, alongside the crawlable copy. JSON-LD connects the organization, website, homepage, and software application, and describes the early-access positioning. It intentionally omits offers, ratings, and download links until there are real ones to publish.

The landing Vite config explicitly uses `landing/public/` as its public directory. Vite copies these files unchanged into the root of `artifacts/landing/`, which is the output directory published by `vercel.json`:

| Source | Published URL |
| --- | --- |
| `public/robots.txt` | `https://uncloud.life/robots.txt` |
| `public/sitemap.xml` | `https://uncloud.life/sitemap.xml` |
| `public/social-card.png` | `https://uncloud.life/social-card.png` |

The sitemap lists only the canonical homepage. Add URLs when additional public pages actually exist. The social card is a 1200 × 630 PNG, with editable artwork in `social-card.svg`; regenerate the PNG after changing that artwork. Both social metadata and JSON-LD use the absolute PNG URL so sharing crawlers can fetch it without JavaScript.

After `build:landing`, check that `index.html` and all three public files exist in `artifacts/landing/`. Run `npm --prefix src/homebase-web run preview:landing` and verify `/`, `/robots.txt`, `/sitemap.xml`, and `/social-card.png` from the preview server. Verify those URLs on the deployment as well; a successful app build alone does not verify the separate landing build.

