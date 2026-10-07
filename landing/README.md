# Uncloud landing page

A landing page for **Uncloud Home**, at **https://www.uncloud.life/**. The homepage follows the [October 2026 messaging source of truth](https://app.notion.com/p/3edbb8ed8da28156991af46426c65cca): recurring storage bills persist because moving years of files feels like a project. Uncloud makes switching easy on a computer the household already owns, without a home-server project. The hero leads with **“Cloud storage without the monthly bill.”** alongside the video on desktop and above it on mobile. The hero highlights setup in 5 minutes or less, a large $79 one-time early-access price with the signup form beneath it, and up to $500 in yearly subscription savings. The offer explicitly says that signing up for early access locks in $79 at launch; $99 is the regular price, and no payment is taken today. The search title describes private cloud storage without monthly fees, while the social-sharing titles echo the headline.

The page then shows a highlighted subscription statement, easy migration and separate household accounts, three setup steps with Mac/PC/Linux icons, the household savings example, the pricing card, and the LaunchList signup again. Copy is kept brief: one point per section, short setup instructions, and the full offer details in the pricing and signup sections. The savings example focuses on subscription bills; purchase pricing stays in the separate offer sections. The statement is illustrative, not a representation of a visitor’s actual bills. The existing blue accents, pale background, navy type, and simple computer illustration continue the film’s visual direction. Homepage-only styles live in `homepage.css`; shared acquisition, hardware, and partner page styles remain unchanged.

Uncloud Home is $99 one-time. Early access locks in $79 at launch, with a 30-day free trial and no credit card when access is available. Unlimited household members, unlimited devices, remote access, migrations, software updates, and 1 primary host computer are included. Capacity comes from the computer and connected drives; there are no storage tiers. See [pricing.md](pricing.md) for the offer and source record.

The savings example retains the five existing plans and $503.40 annual subscription total, with links to official pricing. `savings-animation.js` highlights each subscription as the totals count up once the example enters view. Reduced-motion preferences and missing JavaScript preserve the static totals; screen readers receive the final values throughout. The app and source folders still use the internal name Homebase.

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

## Homepage teaser

The homepage places the video in the hero, alongside “Cloud storage without the monthly bill.” on desktop and below the hero copy on mobile. The “Watch the story” link opens `/video` in a new tab. The supplied MP4 is not checked into the repository; the hero uses the existing configurable video routes. The acquisition pages use the same card after their setup steps and before the savings comparison. The player starts by itself, muted, the first time half of it is on screen: `main.js` swaps the iframe to `/video/embed?autoplay=1` then, so nobody scrolls down to find it already partway through. Browsers only allow muted autoplay, so visitors unmute with the player's own control. Visitors who ask for reduced motion or to save data, and anyone without JavaScript, get the player waiting for Play. The video link opens the same video on its own. The video is either a YouTube privacy-enhanced embed or a self-hosted MP4 (see below). The base player styles live in `styles.css`, with the homepage presentation in `homepage.css`; the homepage has no video modal, focus capture, or scroll lock.

### Changing the video without a deploy

The pages don't name a video. The player loads `/video/embed` and “Watch in a new tab” opens `/video`; `vercel.json` rewrites both, and `/video/captions`, to [`api/video.js`](../api/video.js). The video is the **`video`** item in the Vercel Global Config (formerly Edge Config) connected to the project. Edit it in the dashboard and visitors get the new video within a minute or two — the CDN holds each answer for 60 seconds. The item is one of:

| Value | What visitors get |
| --- | --- |
| `"oELh5dwlmHs"`, or any YouTube link (watch, youtu.be, embed, shorts, live) | YouTube's player; the link opens YouTube |
| `"https://…/teaser.mp4"` | Our own player page, a plain `<video>`; the link opens the file |
| `{ "mp4": "https://…", "poster": "https://…", "captions": "https://…" }` | The same, with a poster frame and WebVTT captions (both optional) |

If Global Config isn't connected, the item is missing, or the value is none of those, the function falls back to `DEFAULT_VIDEO` in `api/video.js` (currently `oELh5dwlmHs`), so a typo can't leave the player empty. A poster or captions address that isn't https is left out and the video still plays. The card's heading and caption stay in the HTML. If the new video needs different words, that is still a deploy.

One-time setup: in the `homebase` project, open **Storage**, choose **Create Database** (the docs call it **Create Storage**), pick **Global Config**, and create a store. Creating it from the project connects it and adds the `GLOBAL_CONFIG` variable. Under **Items**, add `"video": "<ID or link>"` and save, then redeploy once so the functions see the variable. After that, only the item changes. A store connected before the rename, as `EDGE_CONFIG`, works too. `/video` is also a stable link to share: it always goes to whichever video is current.

`dev:landing` and `preview:landing` answer `/video` with the same function, without Global Config, so they always play the default.

### Self-hosting the video

Video files live in a **public** Vercel Blob store (Storage → Create Database → Blob; public or private is fixed when the store is created), never in this repository, where every swap would be a deploy. Upload the MP4, and optionally a poster image and a `.vtt` captions file, in the store's file browser, then put their URLs in the `video` item.

- **Give each version a new filename** (`teaser-2026-10.mp4`). Browsers and Vercel's CDN keep a public blob for up to a month, so a file replaced under the same name keeps playing the old version for returning visitors.
- **Encode for the web:** H.264 video and AAC audio in an MP4, with the index at the front so playback starts before the download finishes. For example, `ffmpeg -i teaser.mov -c:v libx264 -crf 23 -preset slow -vf "scale=-2:1080" -c:a aac -b:a 128k -movflags +faststart teaser-2026-10.mp4`. One file serves every connection, so keep it lean. A still from the video makes a good poster: `ffmpeg -ss 2 -i teaser-2026-10.mp4 -frames:v 1 -q:v 3 teaser-2026-10.jpg`.
- **The file downloads when the player scrolls into view and starts**, so Blob Data Transfer is roughly file size × visitors who reach the video, not only those who would have pressed Play. Keep the file lean. The poster shows while it loads, and where the player waits for Play, nothing downloads until then.
- **Captions are relayed** through `/video/captions`, so the player loads them from this site and they never depend on the file host's CORS headers. A file that doesn't start with `WEBVTT` isn't served.
- The player page has no script and a Content Security Policy that lets it load only https media and images and be framed only by this site.

## Early-access signups

The page's signup form is a [LaunchList](https://getlaunchlist.com/) widget, waitlist key `IH5CSi`. `index.html` loads `https://getlaunchlist.com/js/widget.js` and marks two spots, in the hero and in the closing section, with `<div class="launchlist-widget" data-key-id="IH5CSi">`; the script fills each with an iframe served by LaunchList. **The LaunchList waitlist is the list.** The form's fields, button label and colors, validation messages, and the confirmation a visitor sees after signing up are all configured in the LaunchList dashboard, not in this repository, and `styles.css` cannot reach inside the iframe. The widget forwards the page's query string to LaunchList, so `utm_*` parameters and ad click IDs travel with the signup.

LaunchList sizes the form to its content, about 660px wide at the font size set in the dashboard, and crops it on both sides in any frame narrower than that down to 426px; at 425px and below it stacks the field over the button. `homepage.css` and `acquisition.css` hold every form to 420px so it always stacks. After changing the font size or layout in the dashboard, check the form at desktop width.

The iframe submits into a new tab on getlaunchlist.com and, on its own, tells the page nothing but its height. [`launchlist-head.html`](launchlist-head.html) is the head code that makes up for that: saved in LaunchList under **Integration → Custom code → Head code**, it runs inside the widget, tells the page when a signup is attempted and sent so `main.js` can push `cta_click` and `generate_lead`, and gives the email field an accessible name and autofill. It is not part of the build; after changing it, paste the whole file into LaunchList again. See [measurement.md](measurement.md) for what the events mean now.

## Refine the message

Visitor-facing homepage copy and metadata are in `index.html`; its layout and story illustrations are styled in `homepage.css`. `styles.css` and `hero-visual.css` remain shared with other landing pages. The statement and migration graphic use HTML and CSS with the existing brand icons and logo. GA4 measurement is delivered through GTM; see [measurement.md](measurement.md). Signups go to LaunchList; see above.

Canonical, Open Graph, structured-data, and sitemap URLs use `https://www.uncloud.life/`, matching the production host that the bare domain redirects to. Keep these URLs and the robots.txt sitemap reference aligned. These metadata tags do not configure DNS, hosting, or deployment.

The LaunchList widget described above collects the address. The form is an early-access waitlist, not an immediate trial activation or checkout. Keep the hero's form under the $79 offer, and the closing form between the offer and the trial note. Its button label and confirmation live in the LaunchList dashboard; keep them consistent with the page: the confirmation should say their $79 one-time price is locked in for launch and that they will receive an email when their 30-day free trial is ready. The header and pricing-card calls to action reach the closing signup section at `#early-access`. Pricing links jump to the single offer.

For the first few conversations, share the page and ask:

1. What do you think Uncloud does?
2. Which files would you want to move out of a paid storage service?
3. What would stop you from trying it?

Keep the story succinct: the bill, the friction of moving files, and a practical way to switch. Lead with cost; use private cloud to explain the product after the hook. Keep the distinction between early-access positioning and available product features clear when inviting people to try the app.

## Search and sharing assets

The title, description, Open Graph/Twitter metadata, and JSON-LD live directly in `index.html`, alongside the crawlable copy. JSON-LD connects the organization, website, homepage, and software application, and describes the early-access positioning. It includes the announced one-time prices and trial terms in the application description. It omits a purchasable Offer, ratings, and download links while the site is an early-access waitlist.

The landing Vite config explicitly uses `landing/public/` as its public directory. Vite copies these files unchanged into the root of `artifacts/landing/`, which is the output directory published by `vercel.json`:

| Source | Published URL |
| --- | --- |
| `public/robots.txt` | `https://www.uncloud.life/robots.txt` |
| `public/sitemap.xml` | `https://www.uncloud.life/sitemap.xml` |
| `public/social-card.png` | `https://www.uncloud.life/social-card.png` |

The sitemap lists the canonical homepage and the five acquisition pages. Add URLs when additional public pages actually exist. The social card is a 1200 × 630 PNG, with editable artwork in `social-card.svg`; regenerate the PNG after changing that artwork. Both social metadata and JSON-LD use the absolute PNG URL so sharing crawlers can fetch it without JavaScript.

After `build:landing`, check that `index.html` and all three public files exist in `artifacts/landing/`. Run `npm --prefix src/homebase-web run preview:landing` and verify `/`, `/robots.txt`, `/sitemap.xml`, and `/social-card.png` from the preview server. Verify those URLs on the deployment as well; a successful app build alone does not verify the separate landing build.

## Acquisition pages

Five evergreen search landing pages share one template and end on the same early-access form as the homepage:

| URL | Page |
| --- | --- |
| `/dropbox-alternative` | Dropbox alternative |
| `/google-drive-alternative` | Google Drive alternative |
| `/icloud-alternative` | iCloud alternative |
| `/onedrive-alternative` | OneDrive alternative |
| `/uncloud-vs-nextcloud` | Uncloud vs. Nextcloud |

Each has the homepage header, a hero comparing Uncloud Home with that service with the early-access signup under its $79 offer, three switching steps, the homepage's setup steps, the teaser video, a price (or side-by-side) comparison, the pricing card, an FAQ, and a closing signup. The footer on every page, including the homepage, links all five.

The copy and per-page SEO (title of 60 characters or fewer, description of 155 or fewer, canonical URL, Open Graph, and WebPage, BreadcrumbList, FAQPage and SoftwareApplication JSON-LD) live in [`acquisition-pages.mjs`](acquisition-pages.mjs). The `<slug>.html` files are generated from it and committed so they build and preview like `index.html`; edit the copy there, then regenerate:

```sh
node landing/acquisition-pages.mjs
```

`acquisition-pages.test.cjs` fails if a generated page is stale, a title or description is too long, a price row doesn't add up from the monthly price, or a page is missing from the sitemap or the footer links. Page-only styles are in `acquisition.css`. The landing Vite config builds the homepage plus every slug listed in `acquisition-pages.mjs`, and `vercel.json` sets `cleanUrls` so `/dropbox-alternative.html` is served at `/dropbox-alternative`. Price sources are recorded in [pricing.md](pricing.md#acquisition-page-comparisons). Every page carries the widget twice; `main.js` measures signups from either one, still once per page view.

## Partner pitch page

`/partners` is a founder-written pitch to storage companies (OWC first) for a bundle, co-marketing, or customer-education partnership. It shares `styles.css` with the homepage; its own styles are in `partners.css` and its form script in `partners.js`. It is in the sitemap, and every page's footer links to it as “For Partners”; it is not in the site header.

- **Personalised links.** `/partners?partner=OWC` names the company in the proposal line (“Add an OWC drive…”) and pre-fills the form's Company field.
- **Interest form.** It posts JSON to `/api/partner-interest` ([`api/partner-interest.js`](../api/partner-interest.js)), which emails each submission through Mailtrap with Reply-To set to the sender. It reads the same Vercel variables the retired early-access function used: `MAILTRAP_TOKEN`, `MAILTRAP_FROM` (a sender on a Mailtrap-verified domain; defaults to `partners@uncloud.life`), and `PARTNER_NOTIFY_TO`, falling back to `SIGNUP_NOTIFY_TO`. Set `MAILTRAP_INBOX_ID` to capture mail in Mailtrap's sandbox instead of delivering it. A successful send pushes `partner_interest` to the dataLayer; it is not `generate_lead`, so partner leads stay out of the early-access conversion.

`/partners-one-pager` is the printable US Letter version (marked `noindex`). Open it and use **Print or save as PDF**; it fits on one page.

## Storage page

`/hardware` recommends OWC drives, by size and type; Uncloud is an OWC affiliate (through Impact), and the page says so under the table. The homepage and acquisition-page headers link to it as “Need more space?” (hidden at 700px and narrower, where it would wrap the header), and every footer links to it as “Buy more storage.” Its own styles are in `hardware.css`, and `hardware.js` adds filters by type (which hides the other columns) and minimum size (which hides smaller rows). The filters ship hidden, so without JavaScript the page is the whole table.

The table is generated. The drives, their sizes and OWC pages, and the Impact tracking link live in [`hardware-drives.mjs`](hardware-drives.mjs); edit them there, then regenerate:

```sh
node landing/hardware-drives.mjs
```

| Type | Drive | Sizes |
| --- | --- | --- |
| Portable SSD | Envoy Pro Elektron | 1, 2, 4 TB |
| Desktop drive | Mercury Elite Pro | 2–24 TB |
| Two-drive desktop | Mercury Elite Pro Dual | 8–48 TB (half that when mirrored) |

Each size links to its own OWC product page. Every link goes through Uncloud's Impact tracking link (`IMPACT`) with the OWC page as `u` and `<drive>-<size>tb` as `subId1`, so Impact reports sales by drive and size; if `IMPACT` is emptied, links go straight to OWC with UTM tags instead. `hardware.test.cjs` fails if `hardware.html` is stale, if a link leaves OWC or lacks its drive-and-size tag, if a row's filter data disagrees with its cells, or if the affiliate disclosure is missing. Click measurement is in [measurement.md](measurement.md#storage-page-clicks).

## Privacy Policy and Terms of Service

`/privacy` and `/terms` are the site's and the app's legal pages, from Ethan Teng Consulting LLC, with `hello@uncloud.life` as the contact. They are plain HTML (`privacy.html`, `terms.html`) with the storage page's header and footer; their own styles are in `legal.css`. Every footer ends with a line holding the copyright and links to both, and `legal.test.cjs` fails if a page's footer lacks them or if either page drops out of the build or the sitemap.

The Privacy Policy describes what the site and the apps actually collect. Update it, and its effective date, when that changes: a new analytics, advertising, signup or embed service on the site; anything the apps send off the user's computers (telemetry, update checks, crash reports, a license or payment server); or a new service users can connect.
