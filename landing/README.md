# Uncloud landing page

A landing page for **Uncloud Home**, at **https://www.uncloud.life/**. The hero leads with **“Turn a computer you already own into your family’s private cloud.”** and the graphic’s **“$99 lifetime”** and **“No monthly storage fees.”** Signing up for early access locks in the $79 founding price when the product is ready. The animated hero compares “Multiple subscriptions.” and “Over $500 per year” on the left with a single $99 lifetime Uncloud Home purchase on the right. The early-access signup below it locks in $79 at launch, with a 30-day free trial and no credit card.

The page has four sections: the value proposition and animated subscription comparison, three short setup steps, a household subscription comparison, and one pricing card. Uncloud Home includes unlimited household members, unlimited devices, remote access, migrations, software updates, and 1 primary host computer. Capacity comes from the computer and connected drives; there are no storage tiers. See [pricing.md](pricing.md) for the offer and copy rules.

The blue accents, pale panels, typography and logo remain the visual foundation. A single computer illustration replaces the previous multi-device diagram. The subscription example uses Dropbox Family, Evernote Starter, Google One Basic, iCloud+ 200 GB, and Microsoft 365 Basic. The table links to official prices and shows $503.40 per year in subscription storage costs. The savings-animation.js enhancement highlights each subscription in sequence as monthly and annual totals count up over 3.5 seconds, once 80% of the example enters the viewport. Reduced-motion preferences and missing JavaScript preserve the final static totals; screen readers receive the final values throughout. Calculation details and assumptions are retained in pricing.md. The early-access offer is highlighted above the signup form, and “Compare with Uncloud” introduces the pricing card. Repeated benefits and the closing pitch remain removed. The app, source folders, and storage paths still use the internal name Homebase.

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

The page's signup form is a [LaunchList](https://getlaunchlist.com/) widget, waitlist key `21CSQp`. `index.html` loads `https://getlaunchlist.com/js/widget.js` and marks the spot with `<div class="launchlist-widget" data-key-id="21CSQp">`; the script fills that element with an iframe served by LaunchList. **The LaunchList waitlist is the list.** The form's fields, button label and colors, validation messages, and the confirmation a visitor sees after signing up are all configured in the LaunchList dashboard, not in this repository, and `styles.css` cannot reach inside the iframe. The widget forwards the page's query string to LaunchList, so `utm_*` parameters and ad click IDs travel with the signup.

The iframe submits into a new tab on getlaunchlist.com and, on its own, tells the page nothing but its height. [`launchlist-head.html`](launchlist-head.html) is the head code that makes up for that: saved in LaunchList under **Integration → Custom code → Head code**, it runs inside the widget, tells the page when a signup is attempted and sent so `main.js` can push `cta_click` and `generate_lead`, and gives the email field an accessible name and autofill. It is not part of the build; after changing it, paste the whole file into LaunchList again. See [measurement.md](measurement.md) for what the events mean now.

## Refine the message

Visitor-facing copy and metadata are in `index.html`; styles are in `styles.css`; `uncloud.png` is the shared brand mark and favicon. The landing page uses a light, product-led visual system with navy type, muted blue-gray interface details, and the blues of the Uncloud mark for actions and brand moments. The hero graphic is built in semantic HTML with `hero-visual.css` and `hero-visual.js`, so brand labels and prices remain crisp at every screen size. It plays once after 65% of the graphic enters the viewport: the subscription panel shrinks to 84% of its original size (90% on phones) and its cards become grayscale, a file travels left to right, and the Uncloud panel becomes bright blue and white. The 4.2-second sequence plays once, without a replay control. Reduced-motion preferences and missing JavaScript show the final static composition. Narrow screens retain the left/right comparison and put detailed plan names in the linked savings table. GA4 measurement is delivered through GTM; see [measurement.md](measurement.md). Signups go to LaunchList; see above.

Canonical, Open Graph, structured-data, and sitemap URLs use `https://www.uncloud.life/`, matching the production host that the bare domain redirects to. Keep these URLs and the robots.txt sitemap reference aligned. These metadata tags do not configure DNS, hosting, or deployment.

The LaunchList widget described above collects the address. The form is an early-access waitlist, not an immediate trial activation or checkout. Keep the widget between the $79 offer and the trial note. Its button label and confirmation live in the LaunchList dashboard; keep them consistent with the page: the confirmation should say their $79 one-time price is locked in for launch and that they will receive an email when their 30-day free trial is ready. The header and pricing-card calls to action return to the form. Pricing links jump to the single offer.

For the first few conversations, share the page and ask:

1. What do you think Uncloud does?
2. Which files would you want to bring home?
3. What would stop you from trying it?

Change one major message at a time. A useful next headline to compare is **“Uncloud your life.”** Keep the distinction between early-access positioning and available product features clear when inviting people to try the app.

## Search and sharing assets

The title, description, Open Graph/Twitter metadata, and JSON-LD live directly in `index.html`, alongside the crawlable copy. JSON-LD connects the organization, website, homepage, and software application, and describes the early-access positioning. It includes the announced one-time prices and trial terms in the application description. It omits a purchasable Offer, ratings, and download links while the site is an early-access waitlist.

The landing Vite config explicitly uses `landing/public/` as its public directory. Vite copies these files unchanged into the root of `artifacts/landing/`, which is the output directory published by `vercel.json`:

| Source | Published URL |
| --- | --- |
| `public/robots.txt` | `https://www.uncloud.life/robots.txt` |
| `public/sitemap.xml` | `https://www.uncloud.life/sitemap.xml` |
| `public/social-card.png` | `https://www.uncloud.life/social-card.png` |

The sitemap lists only the canonical homepage. Add URLs when additional public pages actually exist. The social card is a 1200 × 630 PNG, with editable artwork in `social-card.svg`; regenerate the PNG after changing that artwork. Both social metadata and JSON-LD use the absolute PNG URL so sharing crawlers can fetch it without JavaScript.

After `build:landing`, check that `index.html` and all three public files exist in `artifacts/landing/`. Run `npm --prefix src/homebase-web run preview:landing` and verify `/`, `/robots.txt`, `/sitemap.xml`, and `/social-card.png` from the preview server. Verify those URLs on the deployment as well; a successful app build alone does not verify the separate landing build.
