# Uncloud Home offer

Messaging approved for the homepage on September 24, 2026. Prices are USD.

| Item | Included |
| --- | --- |
| Price | $99 one-time |
| Household | Unlimited household members |
| Devices | Unlimited |
| Host | 1 primary host computer |
| Remote access | Included |
| Migrations | Included |
| Software updates | Included |
| Storage | As much as the host computer and connected drives can hold |

There are no monthly storage fees, capacity tiers, or per-member charges in this offer. The homepage emphasizes reusing a computer and storage people already own, with setup in minutes. File transfers can take longer; keep the host on and online for remote access.

The website sells Uncloud Home through Stripe Checkout at $99: each “Buy now” button goes to `/buy`, and the buyer is emailed the download link for the Mac app (see [README.md](README.md#checkout)). The amount charged is the Stripe Price in `STRIPE_PRICE_ID`, not anything in this repository, so change the Price and the pages together. From October 12 to November 25, 2026 the site took $5 reservations through Buy Me a Coffee (https://buymeacoffee.com/ethanteng/e/586408) instead, promising early access at the introductory $79 price with the $5 going toward it; honor that with a single-use $25-off Stripe promotion code for each of those buyers, so they pay $74. From November 25, 2026 the early-access price ended: everyone else pays $99. Before October 12, 2026 the site collected free email signups through LaunchList, saying “Sign up today to lock in $79. No credit card needed.”

Each buy point (hero, pricing card, closing section) carries the “Buy now” button; the closing sections add **“Pay $99 once. No monthly storage bill.”** above it. Keep the hero’s $99 price, pricing card, closing section, comparison tables, metadata, JSON-LD description and the Stripe Price aligned when changing the offer. Do not introduce storage-capacity tiers or describe the one-time purchase as a subscription.

## Household savings example

Checked September 24, 2026. All prices are USD for U.S. consumer plans on monthly billing, before tax. Annual billing discounts, trials and promotions are not used.

| Subscription | Monthly price | Official source |
| --- | ---: | --- |
| Dropbox Family (2 TB, up to 6 people) | $19.99 | [Dropbox plans](https://www.dropbox.com/buy), with **Billed monthly** selected |
| Evernote Starter | $14.99 | [Official migration and pricing FAQ](https://help.evernote.com/hc/en-us/articles/46317642175763-Discontinuing-Evernote-Personal-Professional-Introducing-Starter-Advanced-FAQ), USD monthly column |
| Google One Basic (100 GB) | $1.99 | [Google One plans](https://one.google.com/about/plans), U.S. monthly pricing |
| iCloud+ (200 GB) | $2.99 | [Apple iCloud+](https://www.apple.com/icloud/) |
| Microsoft 365 Basic / OneDrive (100 GB) | $1.99 | [Microsoft Store](https://www.microsoft.com/en-us/microsoft-365/p/microsoft-365-basic/cfq7ttc0ktxs), monthly billing |

The household example uses one subscription to each plan. Dropbox Family replaces the old example's individual Dropbox Plus plan because this offer is for a household. Its $19.99 monthly price was verified in the rendered Dropbox pricing page after selecting monthly billing; $16.99 is the annual-billing monthly equivalent and must not be substituted.

Calculation in cents:

- Monthly bills: 1,999 + 1,499 + 199 + 299 + 199 = 4,195 ($41.95).
- Twelve monthly bills: 4,195 × 12 = 50,340 ($503.40).
- First year after the purchase: 50,340 − 9,900 = 40,440 ($404.40).
- Subsequent years: $503.40 in avoided subscription bills, assuming unchanged prices and no renewals of those subscriptions.

“Save up to $500 a year in subscription costs” and the hero’s shorter “Save up to $500 a year” round down this conditional example. The detailed comparison retains the exact $503.40 annual total. These are not claims of $500 in first-year net savings or of typical household savings. All five plans must be canceled for twelve months; retaining any reduces the result. Electricity, additional drives, backup and tax are excluded from this calculation. These assumptions are retained here as the pricing source record.

This is a spending comparison, not feature equivalence. Moving files does not replace Evernote note editing, Gmail/Outlook storage, iCloud device backups, or other bundled benefits. Cancel only plans whose other features are no longer needed. The homepage shows the subscription costs and source links without a disclaimer or first-year savings breakdown.

When changing the example, verify the same country and billing period for every row, then update the comparison table, savings headline, hero savings benefit, source date and this record (including the first-year calculations above) together.

## Acquisition page comparisons

Checked September 26, 2026 by web search (the official pricing pages were not reachable from the build environment; spot-check them before paid traffic). USD, U.S. consumer plans, monthly billing. The 2 TB family-sized plans are used because the 100–200 GB plans above cost about as much as Uncloud's $99, or less, over three years.

| Page | Plan | Monthly | Per year | 3 years | 5 years | 5 years minus $99 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Dropbox alternative | Dropbox Family (2 TB) | $19.99 | $239.88 | $719.64 | $1,199.40 | $1,100.40 |
| Google Drive alternative | Google One 2 TB | $9.99 | $119.88 | $359.64 | $599.40 | $500.40 |
| iCloud alternative | iCloud+ 2 TB | $9.99 | $119.88 | $359.64 | $599.40 | $500.40 |
| OneDrive alternative | Microsoft 365 Family (6 × 1 TB) | $12.99 | $155.88 | $467.64 | $779.40 | $680.40 |

The pages say "Google One · 2 TB", not "Google AI Plus": Google has relabeled its $9.99 2 TB tier under the AI Plus name, but since June 2026 "Google AI Plus" is also sold as a separate $4.99, 400 GB plan, so that label would pair the wrong plan with the price. Microsoft 365 Family is $129.99 billed yearly; the monthly-billing price is used for consistency with the other rows. Dropbox Family's yearly-billing equivalent ($16.99) must not be substituted, as above.

The same spending-not-feature-equivalence caveat applies: each page's FAQ says which bundled benefits (Gmail, Google Photos, iPhone backups, Office apps) Uncloud does not replace. When a price changes, update `monthly` and the price strings in `acquisition-pages.mjs` together; the test checks they agree.
