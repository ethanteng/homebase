# Uncloud landing page

A landing page for **Uncloud**, with the public address **https://www.uncloud.life/**. The hero leads with **“Your private cloud. In 5 minutes or less.”** and highlights $407.40/year in avoided bills, conditional on canceling the five plans linked below. The subhero explains that people can use a computer they already own and cancel cloud storage plans they no longer need. The existing device illustration, blue accents, pale panels, and typography remain the visual foundation.

The primary message is intentionally about **reusing hardware people already own** — no NAS, proprietary box, or special equipment required. “Turn a computer you already own into private cloud storage” has its own highlighted line. The page keeps the early-access email signup above the main visual, with labeled Mac, PC, and Linux icons in the computer illustration. “Your private cloud, in 5 minutes or less” introduces three steps: pick a computer, make it your cloud, and bring files home. Early-access links return to the signup form.

The value section uses current U.S. monthly prices for five named plans: Dropbox Plus, Evernote Starter, Google One Basic (Google Drive), iCloud+ 200 GB, and Microsoft 365 Basic (OneDrive). Together they cost $33.95/month, or $407.40 over 12 months of monthly billing. The example is explicitly conditional on canceling all five; it distinguishes avoided charges from net savings and asks people to check backups and other subscription features before canceling. Official pricing links, the check date, and billing assumptions are visible on the page. See [pricing.md](pricing.md) for the source record and update procedure. The app, source folders, and storage paths still use the internal name Homebase.

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

`api/subscribe.js` is a Vercel serverless function at `/api/subscribe`. It validates the address, writes it to an Airtable table, and then emails a notification. **The table is the list.** The email is only how a signup gets noticed, which is why the order matters: a signup that cannot be stored is a failure the visitor is asked to retry, and a notification that cannot be sent is a line in the log.

Every API token is only ever read on the server. Never put one in `landing/main.js` or any other file the browser downloads — anything shipped to a browser is public.

### The Airtable table

The base is **Uncloud**, and the table is `Signups` unless you set `AIRTABLE_TABLE`. Field names have to match exactly; Airtable rejects a write that names a field the table does not have.

| Field | Type | Written when |
| --- | --- | --- |
| `Email` | Email, and the table's primary field | Always. The row is upserted on this field, so one person is one row however many times they sign up. |
| `Signed Up` | Date, with time enabled | Always. The most recent request; a repeat signup moves it. |
| `Source` | Single line text | Only when the visit carried a `utm_source`. |
| `Referrer` | Single line text | Only when the browser reported one. |
| `Created` | Created time | Never by the function. Airtable computes it, so it holds the *first* signup even after `Signed Up` has moved. |

`Source` and `Referrer` are left out of the write when empty rather than sent blank: the upsert sets every field it is given, so a later visit with no campaign would otherwise erase what the first one recorded.

The base also has a `Preview Signups` table with the same four written fields. Point `AIRTABLE_TABLE` at it from Vercel's Preview environment and test signups stay out of the real list; drop the table if previews should not take signups at all.

Create a **personal access token** at [airtable.com/create/tokens](https://airtable.com/create/tokens) with the `data.records:read` and `data.records:write` scopes, granted to this base only. The old API keys stopped working in February 2024.

The free Airtable plan allows 1,000 records per base and 1,000 API calls per month per workspace. One signup is one call. That is comfortable for early access and the ceiling is a signal to move the list somewhere else, not a surprise.

### Environment variables

Set these in the Vercel project under **Settings → Environment Variables**, for every environment the page is deployed to:

| Variable | Required | Purpose |
| --- | --- | --- |
| `AIRTABLE_TOKEN` | yes | Personal access token, scoped to the signup base. |
| `AIRTABLE_BASE_ID` | yes | The base id, starting `app…`. It is in the base's API documentation URL. |
| `AIRTABLE_TABLE` | no | Table name. Defaults to `Signups`. Point a preview deployment at a different table to keep test signups out of the real list. |
| `MAILTRAP_TOKEN` | no | Mailtrap API token. Sending tokens and sandbox tokens are different; use one that matches the mode below. |
| `SIGNUP_NOTIFY_TO` | no | Address that receives the signup notifications. |
| `MAILTRAP_FROM` | no | Sender address. Defaults to `early-access@uncloud.life`. |
| `MAILTRAP_INBOX_ID` | no | Set it to route through the Mailtrap sandbox (`sandbox.api.mailtrap.io`) instead of live sending. Useful for a preview deployment. |

Only the Airtable settings are required. With the Mailtrap pair unset the function still records every signup; it just does so without telling anyone. Setting one of that pair without the other is the same as setting neither.

Live sending needs a **verified sending domain** in Mailtrap, and `MAILTRAP_FROM` has to be on that domain. Until the domain is verified, set `MAILTRAP_INBOX_ID` and read the notifications in the sandbox inbox. Sandbox mode affects only the notification — the Airtable row is real either way.

Changing an environment variable does not change a deployment that already exists. Redeploy after setting them.

The function answers with a generic message when Airtable fails or a required variable is missing; the reason goes to the function log, not to the visitor. A missing variable never names itself in a response.

Run the function's tests from the repository root:

```sh
node --test "api/*.test.js"
```

`./scripts/check.sh` runs them along with everything else.

## Refine the message

Visitor-facing copy and metadata are in `index.html`; styles are in `styles.css`; `uncloud.png` is the shared brand mark and favicon. The landing page uses a light, product-led visual system with navy type, muted blue-gray interface details, and the blues of the Uncloud mark for actions and brand moments. The product diagram is built in semantic HTML and CSS so it scales cleanly without shipping a large raster illustration. GA4 measurement is delivered through GTM; see [measurement.md](measurement.md). Signups are stored in Airtable; see below.

Canonical, Open Graph, structured-data, and sitemap URLs use `https://www.uncloud.life/`, matching the production host that the bare domain redirects to. Keep these URLs and the robots.txt sitemap reference aligned. These metadata tags do not configure DNS, hosting, or deployment.

“Get early access” submits an email address to the signup function described above. Keep the field and the button as one short row above “Private. Yours.”; on success the form is replaced by a confirmation line. The header, setup, and closing early-access links return to the signup form.

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
| `public/robots.txt` | `https://www.uncloud.life/robots.txt` |
| `public/sitemap.xml` | `https://www.uncloud.life/sitemap.xml` |
| `public/social-card.png` | `https://www.uncloud.life/social-card.png` |

The sitemap lists only the canonical homepage. Add URLs when additional public pages actually exist. The social card is a 1200 × 630 PNG, with editable artwork in `social-card.svg`; regenerate the PNG after changing that artwork. Both social metadata and JSON-LD use the absolute PNG URL so sharing crawlers can fetch it without JavaScript.

After `build:landing`, check that `index.html` and all three public files exist in `artifacts/landing/`. Run `npm --prefix src/homebase-web run preview:landing` and verify `/`, `/robots.txt`, `/sitemap.xml`, and `/social-card.png` from the preview server. Verify those URLs on the deployment as well; a successful app build alone does not verify the separate landing build.
