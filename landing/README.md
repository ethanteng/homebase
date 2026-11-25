# Uncloud landing page

A landing page for **Uncloud Home**, at **https://www.uncloud.life/**. The homepage follows the [October 2026 messaging source of truth](https://app.notion.com/p/3edbb8ed8da28156991af46426c65cca): recurring storage bills persist because moving years of files feels like a project. Uncloud makes switching easy on a computer the household already owns, without a home-server project. The hero leads with **“Cloud storage without the monthly bill.”** alongside the video on desktop and above it on mobile. The hero lists three equal points on one line (5-minute setup, access anywhere, no new hardware), then a large $99 one-time price with the “Buy now” button beneath it, and up to $500 in yearly subscription savings. On phones the hero drops the benefit list and the header's “Get early access” button and sets the price and button in a card, so the offer comes straight after the headline and one line of support; setup time and access anywhere are covered further down the page. The lines around the button say Uncloud is $99 once with no monthly storage bill, and that the download link for the Mac app comes by email. The search title describes private cloud storage without monthly fees, while the social-sharing titles echo the headline.

The page then shows a highlighted subscription statement, easy migration and separate household accounts, three setup steps with the Mac icon and Windows marked “coming soon”, the household savings example, the pricing card, and the buy button again. Copy is kept brief: one point per section, short setup instructions, and the full offer details in the pricing and signup sections. The savings example focuses on subscription bills; purchase pricing stays in the separate offer sections. The statement is illustrative, not a representation of a visitor’s actual bills. The existing blue accents, pale background, navy type, and simple computer illustration continue the film’s visual direction. Homepage-only styles live in `homepage.css`; shared acquisition, hardware, and partner page styles remain unchanged.

Uncloud Home is $99 one-time. Unlimited household members, unlimited devices, remote access, migrations, software updates, and 1 primary host computer are included. Capacity comes from the computer and connected drives; there are no storage tiers. See [pricing.md](pricing.md) for the offer and source record.

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

The homepage places the video in the hero, alongside “Cloud storage without the monthly bill.” on desktop and below the hero copy on mobile. The “Watch the story” link opens `/video` in a new tab; on phones, where the player is the whole width, the link is hidden and its “Meet Uncloud” heading is kept only for screen readers. The supplied MP4 is not checked into the repository; the hero uses the existing configurable video routes. The acquisition pages use the same card after their setup steps and before the savings comparison. The player starts by itself, muted, the first time half of it is on screen: `main.js` swaps the iframe to `/video/embed?autoplay=1` then, so nobody scrolls down to find it already partway through. Browsers only allow muted autoplay, so visitors unmute with the player's own control. Visitors who ask for reduced motion or to save data, and anyone without JavaScript, get the player waiting for Play. The video link opens the same video on its own. The video is either a YouTube privacy-enhanced embed or a self-hosted MP4 (see below). The base player styles live in `styles.css`, with the homepage presentation in `homepage.css`; the homepage has no video modal, focus capture, or scroll lock.

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

## Checkout

Each call to action is a “Buy now” button linking to `/buy` (the price is shown beside it, not on it), with nothing below it. The download link is emailed after payment; the page doesn't say so. The closing sections, which show no price above the button, also carry **“Pay $99 once. No monthly storage bill.”** in bold above it; the hero and pricing card don't, since the $99 is right there. The header's button reads “Buy now” and goes to the closing section. The homepage and each acquisition page have the button in the hero, the pricing card and the closing section; `/faq` and `/hardware` have it in their closing sections.

How a purchase goes:

1. **`/buy`** is rewritten by `vercel.json` to [`api/checkout.js`](../api/checkout.js), which creates a Stripe Checkout Session for `STRIPE_PRICE_ID` and redirects (303) to Stripe's hosted page. Stripe collects the email address and payment. Promotion codes are allowed. The session is marked `metadata.product = uncloud-home`. If checkout can't start (settings missing, Stripe down), the buyer gets a short page saying nothing was charged and to email hello@uncloud.life. `/buy` is a plain link, so it can be pasted into an email or a post; `robots.txt` keeps crawlers off it.
2. **After paying**, Stripe returns the buyer to **`/thanks`** (`thanks.html`, noindex, not in the sitemap), which tells them to check their email. Cancelling returns them to `/#pricing`.
3. **Stripe calls [`api/stripe-webhook.js`](../api/stripe-webhook.js)** with `checkout.session.completed` (or `checkout.session.async_payment_succeeded` for a payment method that settles later). It checks Stripe's signature, ignores anything not marked `uncloud-home` or not paid yet, and emails the buyer the two Mac downloads (Apple silicon and Intel), the way to tell which Mac they have, and a link to `/help#set-up`, through Mailtrap. If the email can't be sent it answers 500, and Stripe retries for up to three days. Each send is logged with the Checkout Session id, which finds the buyer in the Stripe dashboard.

The download links in the email are the same `mac-latest` release files the help page links to; `help.test.cjs` fails if the two disagree.

### Settings (Vercel → homebase → Settings → Environment Variables)

| Variable | Used by | Value |
| --- | --- | --- |
| `STRIPE_SECRET_KEY` | checkout | A secret key (`sk_live_…`), or a restricted key with **Checkout Sessions: Write**. Use `sk_test_…` on Preview. |
| `STRIPE_PRICE_ID` | checkout | The `price_…` of a one-time Price for Uncloud Home. Keep its amount the same as the price on the pages ($99). |
| `STRIPE_WEBHOOK_SECRET` | webhook | The endpoint's signing secret (`whsec_…`), from step 2 below. |
| `MAILTRAP_TOKEN` | webhook | Already set for the partner form. `uncloud.life` must be a verified sending domain. |
| `ORDER_EMAIL_FROM` | webhook | Optional. The sender, default `hello@uncloud.life`. Replies go to hello@uncloud.life. |
| `MAILTRAP_INBOX_ID` | webhook | Optional. Captures the email in Mailtrap's sandbox instead of sending it, for trying a Preview with test keys. |
| `MAC_DOWNLOAD_ARM64_URL`, `MAC_DOWNLOAD_X64_URL` | webhook | Optional. Where the email's download links go, if the DMGs move off GitHub. Anything but an https address is ignored. |

### One-time Stripe setup

1. **Product and price.** In Stripe, create a product **Uncloud Home** with a one-time price of $99, and copy the price's id into `STRIPE_PRICE_ID`.
2. **Webhook.** Under Developers → Webhooks, add an endpoint at `https://www.uncloud.life/api/stripe-webhook` listening to `checkout.session.completed` and `checkout.session.async_payment_succeeded`. Copy its signing secret into `STRIPE_WEBHOOK_SECRET`. Test mode has its own endpoint and secret.
3. **Promotion codes.** People who paid the $5 reservation on Buy Me a Coffee were promised early access at $79 with the $5 going toward it, so they pay $74: make a $25-off coupon, then give each reserver their own promotion code limited to one redemption, and email it to them; Checkout shows a field to enter it. Don't rely on Stripe's “first-time order only” restriction: Checkout here doesn't create Customer records (`customer_creation` is left at `if_required`), and Stripe treats such codes as invalid on those sessions.
4. **Redeploy** so the functions see the new variables, then buy once in test mode on a Preview (card `4242 4242 4242 4242`) with `MAILTRAP_INBOX_ID` set, and check the email in the Mailtrap sandbox. If Preview deployments are behind Vercel Authentication, Stripe can't reach them: add the project's Protection Bypass for Automation secret to the test-mode endpoint's URL as `?x-vercel-protection-bypass=…`. `stripe trigger checkout.session.completed` doesn't carry the `uncloud-home` mark, so the webhook acknowledges it without emailing anyone.

Until `STRIPE_SECRET_KEY` and `STRIPE_PRICE_ID` are set, `/buy` shows the “Checkout isn’t available” page, so set them before this reaches production. `dev:landing` and `preview:landing` don't run these functions, so `/buy` is a 404 there; try checkout on a Vercel Preview. `api/checkout.test.js` and `api/stripe-webhook.test.js` cover both functions, including Stripe's signature scheme.

### Before checkout

Until November 25, 2026 the price shown was $79 for early access, $99 regular; it is now $99 for everyone.

From October 12 to November 25, 2026, the button was “Reserve your spot – $5”, linking to a reservation on Buy Me a Coffee (`https://buymeacoffee.com/ethanteng/e/586408`); those reservations are still recorded there. Before October 12 it was an email form, a [LaunchList](https://getlaunchlist.com/) widget (waitlist key `IH5CSi`) that promised $79 with no credit card. Those signups are still in LaunchList; the site no longer loads the widget.

## Refine the message

Visitor-facing homepage copy and metadata are in `index.html`; its layout and story illustrations are styled in `homepage.css`. `styles.css` and `hero-visual.css` remain shared with other landing pages. The statement and migration graphic use HTML and CSS with the existing brand icons and logo. GA4 measurement is delivered through GTM; see [measurement.md](measurement.md). Purchases go through Stripe Checkout; see above.

Canonical, Open Graph, structured-data, and sitemap URLs use `https://www.uncloud.life/`, matching the production host that the bare domain redirects to. Keep these URLs and the robots.txt sitemap reference aligned. These metadata tags do not configure DNS, hosting, or deployment.

The button goes to Stripe Checkout in the same tab. Keep the hero's button under the $99 price and the lines around each button as described above, and keep the Stripe price, the thanks page and the purchase email consistent with them. The header's “Get early access” reaches the closing section at `#early-access`. On phones the homepage and acquisition pages hide the header's button, which would only repeat the hero's button a few lines below it. Pricing links jump to the single offer.

For the first few conversations, share the page and ask:

1. What do you think Uncloud does?
2. Which files would you want to move out of a paid storage service?
3. What would stop you from trying it?

Keep the story succinct: the bill, the friction of moving files, and a practical way to switch. Lead with cost; use private cloud to explain the product after the hook. Keep the distinction between early-access positioning and available product features clear when inviting people to try the app.

## Search and sharing assets

The title, description, Open Graph/Twitter metadata, and JSON-LD live directly in `index.html`, alongside the crawlable copy. JSON-LD connects the organization, website, homepage, and software application, and describes the early-access positioning. It includes the announced one-time prices in the application description. It omits a structured Offer, ratings, and download links.

The landing Vite config explicitly uses `landing/public/` as its public directory. Vite copies these files unchanged into the root of `artifacts/landing/`, which is the output directory published by `vercel.json`:

| Source | Published URL |
| --- | --- |
| `public/robots.txt` | `https://www.uncloud.life/robots.txt` |
| `public/sitemap.xml` | `https://www.uncloud.life/sitemap.xml` |
| `public/social-card.png` | `https://www.uncloud.life/social-card.png` |

The sitemap lists the canonical homepage and the five acquisition pages. Add URLs when additional public pages actually exist. The social card is a 1200 × 630 PNG, with editable artwork in `social-card.svg`; regenerate the PNG after changing that artwork. Both social metadata and JSON-LD use the absolute PNG URL so sharing crawlers can fetch it without JavaScript.

After `build:landing`, check that `index.html` and all three public files exist in `artifacts/landing/`. Run `npm --prefix src/homebase-web run preview:landing` and verify `/`, `/robots.txt`, `/sitemap.xml`, and `/social-card.png` from the preview server. Verify those URLs on the deployment as well; a successful app build alone does not verify the separate landing build.

## Acquisition pages

Five evergreen search landing pages share one template and end on the same buy button as the homepage:

| URL | Page |
| --- | --- |
| `/dropbox-alternative` | Dropbox alternative |
| `/google-drive-alternative` | Google Drive alternative |
| `/icloud-alternative` | iCloud alternative |
| `/onedrive-alternative` | OneDrive alternative |
| `/uncloud-vs-nextcloud` | Uncloud vs. Nextcloud |

Each has the homepage header, a hero comparing Uncloud Home with that service with the buy button under its $99 price (in one column the offer comes before the “Why choose Uncloud?” card, and on phones it sits in a card as on the homepage, without the setup-time line), three switching steps, the homepage's setup steps, the teaser video, a price (or side-by-side) comparison, the pricing card, an FAQ, and a closing signup. The footer on every page, including the homepage, links all five.

The copy and per-page SEO (title of 60 characters or fewer, description of 155 or fewer, canonical URL, Open Graph, and WebPage, BreadcrumbList, FAQPage and SoftwareApplication JSON-LD) live in [`acquisition-pages.mjs`](acquisition-pages.mjs). The `<slug>.html` files are generated from it and committed so they build and preview like `index.html`; edit the copy there, then regenerate:

```sh
node landing/acquisition-pages.mjs
```

`acquisition-pages.test.cjs` fails if a generated page is stale, a title or description is too long, a price row doesn't add up from the monthly price, or a page is missing from the sitemap or the footer links, or a buy button is missing or goes anywhere but `/buy`. Page-only styles are in `acquisition.css`. The landing Vite config builds the homepage plus every slug listed in `acquisition-pages.mjs`, and `vercel.json` sets `cleanUrls` so `/dropbox-alternative.html` is served at `/dropbox-alternative`. Price sources are recorded in [pricing.md](pricing.md#acquisition-page-comparisons). Every page carries the buy button three times; `main.js` counts a click on any of them as one `cta_click` per page view.

## Partner pitch page

`/partners` is a founder-written pitch to storage companies (OWC first) for a bundle, co-marketing, or customer-education partnership. It shares `styles.css` with the homepage; its own styles are in `partners.css` and its form script in `partners.js`. It is in the sitemap, and every page's footer links to it as “For Partners”; it is not in the site header.

- **Personalised links.** `/partners?partner=OWC` names the company in the proposal line (“Add an OWC drive…”) and pre-fills the form's Company field.
- **Interest form.** It posts JSON to `/api/partner-interest` ([`api/partner-interest.js`](../api/partner-interest.js)), which emails each submission through Mailtrap with Reply-To set to the sender. It reads the same Vercel variables the retired early-access function used: `MAILTRAP_TOKEN`, `MAILTRAP_FROM` (a sender on a Mailtrap-verified domain; defaults to `partners@uncloud.life`), and `PARTNER_NOTIFY_TO`, falling back to `SIGNUP_NOTIFY_TO`. Set `MAILTRAP_INBOX_ID` to capture mail in Mailtrap's sandbox instead of delivering it. A successful send pushes `partner_interest` to the dataLayer; it is not `generate_lead`, so partner leads stay out of the early-access conversion.

`/partners-one-pager` is the printable US Letter version (marked `noindex`). Open it and use **Print or save as PDF**; it fits on one page.

## FAQ page

`/faq` answers the questions visitors ask most, gathered from real conversations. Every page with the full header and footer links to it as “FAQ” in both; the partner pitch, with its own minimal header and footer, doesn't. On phones the link stays in the homepage and comparison-page headers, which hide their “Get early access” button there, and drops from the others' along with “Pricing”. It shares the comparison pages' question list (the `.faq-list` styles in `styles.css`); its own styles are in `faq.css`.

The questions and answers live in [`faq-questions.mjs`](faq-questions.mjs), which writes both the list and the page's FAQPage structured data, so the two can't disagree. Edit them there, then regenerate:

```sh
node landing/faq-questions.mjs
```

The page's title and description stay in `faq.html` and name the structured data too. `faq.test.cjs` fails if `faq.html` is stale, if the title or description is too long, or if a full header or footer lacks the link.

## Help page

`/help` is the user-facing guide: setting up the Uncloud computer, adding people, bringing files in, syncing computers, sharing by link, settings, backup and troubleshooting. Every full footer links to it as "Help", and the FAQ's opening line points to it. It's plain HTML in `help.html`, with its own styles in `help.css`: a contents list beside the page on wide screens, and under the title on narrow ones.

The screenshots are WebP files in `help/`, taken from a throwaway Uncloud with a made-up household (Sam, Jordan and Riley Taylor) at 1280 × 800 and twice the pixels, then cropped. Retake one when the screen it shows changes, keep the same file name, and update the image's `width` and `height` (half its pixel size) in `help.html`. `help.test.cjs` fails if a screenshot the page shows is missing, a contents link has no section, the page drops out of the build or the sitemap, or a footer lacks the link.

When the app changes what someone sees or does, update `help.html` and the repository README together.

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
