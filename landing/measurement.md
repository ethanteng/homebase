# Uncloud demand-test measurement

Configured September 20, 2026. The public page is https://www.uncloud.life/.

## Accounts and implementation

- GA4 account: Ethan Teng Consulting LLC, `380265295`.
- GA4 property: **Uncloud**, `555082273`. The original creation-wizard URL contained `519498279` (Ask Linc), but the completed new property is `555082273`.
- Web stream: Website, `15815125310`; measurement ID `G-233MB44VRQ`.
- GTM account `6333208997`; web container **www.uncloud.life**, `264734123`, public ID `GTM-KS2XST5Z`.
- Google Ads: Ethan Teng Consulting LLC, `772-829-3480`.
- Ads conversion: **Uncloud - Waitlist signup (GA4)**, ID `7783822035`; GA4 source `generate_lead`, Primary, count One, no value, 30-day click-through window, 3-day engaged-view window, data-driven attribution (Google paid channels).
- Ads auto-tagging enabled through the GA4 link. The Submit lead form goal remains outside account defaults and is used by **0 of the 5 existing campaigns**.
- GTM published version **2**, **Uncloud demand measurement v1**.
- Vercel: `ethan-teng-consulting-llc/homebase`.

The landing HTML loads GTM only on `uncloud.life` and `www.uncloud.life`. There is one Google tag, firing on Initialization – All Pages, and one GA4 Event tag with event name `{{Event}}`, restricted to `^(cta_click|generate_lead)$`. No second direct GA4 configuration or native Ads conversion tag is installed.

## Funnel

| Event/metric | Meaning | Conversion role |
| --- | --- | --- |
| `page_view` / sessions | Arrival, with Google Ads auto-tagging and campaign attribution | Diagnostic |
| GA4 engaged sessions / `user_engagement` | Standard GA4 engagement; use engagement rate and average engagement time | Diagnostic |
| `cta_click` | First attempt to submit Get early access on that page, including invalid/empty input; Enter key also counts | Diagnostic only |
| `generate_lead` | Signup endpoint confirms the address was written to the Airtable signup table | Primary business outcome |

`generate_lead` is a GA4 key event, counted once per session, with no default monetary value. This is a waitlist request, not a paid customer, verified email, or activated user.

GA4 enhanced measurement keeps page views, scrolls and outbound clicks. Automatic form interactions, site search, videos and downloads are off. Automatic form submission would only measure an attempt, so it is not used as a conversion.

The event tag includes `landing_version=cloud-subscriptions-v1`, registered as the event-scoped **Landing version** custom dimension. When materially changing the page message, update this value in GTM and publish together with the new copy. The rotating service names are one creative treatment, not a randomized experiment.

## Counting and data boundaries

- The browser waits for `/api/subscribe` to return `accepted: true`; failed submissions, honeypots, short-window repeats and legacy ambiguous success responses do not generate leads. Mailtrap sandbox mode no longer suppresses the lead: it affects only the notification email, while the Airtable record — the thing `accepted: true` now reports — is written either way.
- Concurrent/repeated submissions on the same rendered form are blocked. The server's in-memory duplicate guard still lasts ten minutes per warm instance, but the Airtable write upserts on the address, so the table holds one row per person no matter how many sessions or warm instances a repeat crosses. GA4 still counts a lead per session, so the table is the authority on how many people signed up and GA4 is the authority on which campaigns brought them.
- The signup record carries `utm_source` and the browser's referrer alongside the address, collected at submit time in the page. These go to Airtable only; they are never pushed into the data layer.
- Email addresses, typed form contents, API response errors, and user IDs are never pushed into the data layer or event parameters.
- GA4 email redaction remains enabled. Avoid personal data in campaign parameters or URLs.
- Google signals and advertising personalization signals are disabled by the page; personalized advertising is disabled on the GA4–Ads link. No enhanced conversions or remarketing audience was configured.
- Analytics cookies/tag collection follow the existing site behavior; no consent manager is implemented by this change. Evaluate the consent experience before expanding the geographic scope of paid traffic.

## Testing messaging

Use the Google Ads link and auto-tagging for Google Ads attribution; preserve `gclid`, `gbraid`, and `wbraid` through redirects. Use final URL `https://www.uncloud.life/` to avoid an unnecessary redirect.

For the first experiment, compare message-focused ad groups while keeping audience, geography, bids and landing page consistent. Use a readable campaign name such as `uncloud_demand_v1`. Optional final-URL suffix:

```
utm_source=google&utm_medium=cpc&utm_campaign=uncloud_demand_v1&utm_content={creative}&utm_term={keyword}
```

Use Google Ads campaign/ad/ad-group dimensions for click-to-lead performance. In GA4 Traffic acquisition, compare sessions, engagement rate, CTA users and generate_lead key-event rate by session campaign. Explore a funnel of page_view → cta_click → generate_lead, broken down by campaign. Use Landing version when comparing the custom CTA/lead events (that parameter is not attached to automatic page views). Evaluate cost per confirmed waitlist request alongside downstream lead quality; CTA rate alone does not validate demand.

Choose the Uncloud conversion explicitly for future Uncloud campaigns. Existing Ask Linc campaigns must retain their current conversion goals. No campaigns, budgets or ads are created by this measurement setup.

## Verification

- Landing production build succeeded.
- Repository checks passed: 15 signup API tests, 7 landing funnel tests, 45 backend tests, frontend build/typecheck.
- Production deployment `dpl_2estvkzVN929EXpsCpfGRezvx6qf` reached READY and was aliased to `https://www.uncloud.life/`; implementation commit `67137c5`.
- GTM version 2 is live. Tag Assistant detected `GTM-KS2XST5Z` and `G-233MB44VRQ`, with one Google base tag firing per instrumented page.
- Clicking the CTA with an empty email displayed the validation message, emitted `cta_click`, and emitted no `generate_lead`.
- With the user's explicit approval, submitted **measurement-test@example.com** once. The page displayed “You’re on the list. We’ll be in touch.” This created one test notification in the configured signup inbox; disregard that address when evaluating demand.
- Tag Assistant showed the event tag succeeded for `generate_lead`. The hit went to `https://analytics.google.com/g/collect` with `tid=G-233MB44VRQ`, `en=generate_lead`, `_c=1`, `_dbg=1`, `ep.landing_version=cloud-subscriptions-v1`, `seg=1`, and 8037ms of engagement time. The full displayed payload contained no email/form contents.
- GA4 DebugView received `cta_click` once at 10:58:22 PM Pacific and `generate_lead` once at 10:58:30 PM Pacific, alongside page views and scroll events. The two earlier page views came from Tag Assistant's connection/reload cycle, not duplicate base tags.
- Navigating away after the signup also delivered `user_engagement`, visible in Tag Assistant and GA4 DebugView at 11:00:07 PM Pacific.
- QA traffic was labeled `utm_source=measurement_test`, `utm_medium=qa`, `utm_campaign=measurement_validation`. Exclude it from experiment reporting. No real ad was clicked, so this proves live collection and GA4 receipt, not paid-click attribution or a billable Ads conversion.
- Google Ads saved the conversion with the settings above. Its initial “No recent conversions” status is expected: the test was not an ad-attributed visitor. Imported conversions can take up to 24 hours to appear after a genuine eligible ad interaction.
- A HEAD-only redirect check confirmed that `gclid`, `gbraid`, `wbraid`, and UTM parameters survive the apex-to-www 308 redirect; synthetic markers were never loaded into a browser or sent as analytics events.
- Post-deploy Vercel error-log query returned no error logs for this deployment.

For future debugging, open the page with `?measurement_debug=1&utm_source=measurement_test&utm_medium=qa&utm_campaign=measurement_validation`. This enables GA4 DebugView and labels the session. Do not invent a GCLID or click paid ads just to test. A test signup sends a notification and must be coordinated with the inbox owner. Standard GA4 reports and imported Ads conversions can lag; realtime/DebugView are the immediate verification surfaces.

## Links

- [GA4 Uncloud](https://analytics.google.com/analytics/web/#/a380265295p555082273/reports/intelligenthome)
- [GTM published version 2](https://tagmanager.google.com/#/versions/accounts/6333208997/containers/264734123/versions/2)
- [Google Ads conversion](https://ads.google.com/aw/conversions/detail?ocid=7934362476&ctId=7783822035)
- [Production deployment](https://vercel.com/ethan-teng-consulting-llc/homebase/2estvkzVN929EXpsCpfGRezvx6qf)
- [Google: import GA4 key events into Ads](https://support.google.com/google-ads/answer/2375435?hl=en)
- [Google: data-layer event configuration](https://developers.google.com/tag-platform/tag-manager/datalayer)
