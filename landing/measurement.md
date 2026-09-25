# Uncloud demand-test measurement

Configured September 20, 2026. The public page is https://www.uncloud.life/.

## Signup events stopped with the LaunchList widget

As of September 25, 2026 the early-access form is a LaunchList widget (see [README.md](README.md#early-access-signups)), and the page no longer sends `cta_click` or `generate_lead`. The widget is a cross-origin iframe: clicks inside it never reach this page's data layer, its form submits into a new tab on getlaunchlist.com, and the only message it posts back is its own height. Nothing on the page can tell that a signup happened.

Until a signup can be observed on an Uncloud page again, the GA4 key event and the **Uncloud - Waitlist signup (GA4)** Ads conversion below record nothing new, and the page_view → cta_click → generate_lead funnel ends at page views. Count signups in LaunchList, which receives the page's query string and so keeps `utm_*` and click IDs with each signup. The GTM tags are unchanged and will fire again if the data layer events come back. The rest of this document describes the setup as it was when the page's own form sent them.

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

## GitHub link clicks (September 23, 2026)

GitHub clicks reuse GA4 enhanced measurement's existing **`click`** event. The
live Website stream has Outbound clicks enabled, and published GTM version 2
contains one Google tag plus the CTA/lead event tag. There is no separate
outbound-click tag. Do not add a `github_link_click` emitter or another GTM
link-click event tag: that would record the same interaction twice.

The header and footer anchors now have stable IDs. GA4 collects them as its
built-in `link_id` parameter, so placement is available without another event,
custom JavaScript handler, or custom dimension:

| Parameter | Header | Footer |
| --- | --- | --- |
| `event_name` | `click` | `click` |
| `link_id` | `github-header` | `github-footer` |
| `link_url` | `https://github.com/ethanteng/homebase` | Same |
| `link_domain` | `github.com` | Same |
| `outbound` | `true` | Same |
| `link_classes` | `github-link` | Same |

The Google tag handles the click before the anchor's normal same-tab navigation.
There is no additional navigation delay, redirect, `target`, style, accessible
label, or click handler introduced by this change. As with the site's other
analytics, collection depends on the Google tag loading successfully; local and
preview hosts intentionally do not load GTM. GitHub clicks remain diagnostic
engagement, not waitlist conversions or Google Ads conversion actions.

### Validate

1. In [Uncloud GTM](https://tagmanager.google.com/#/container/accounts/6333208997/containers/264734123/workspaces/3),
   choose **Preview** and connect to
   `https://www.uncloud.life/?measurement_debug=1&utm_source=measurement_test&utm_medium=qa&utm_campaign=github_tracking_validation`.
   Keep Tag Assistant open and wait for the Google tag to load.
2. Activate the header GitHub icon. Confirm the same tab reaches the repository.
   In Tag Assistant, select the **G-233MB44VRQ** tag, then **Hits Sent** and the
   `click` hit. Expect one `click` with the header parameters above. The GTM
   timeline shows `Link Click`; the CTA/lead event tag should not fire.
3. Return to the site and repeat with the footer icon, then with keyboard focus
   and Enter. Expect one `click` for each activation, with the corresponding ID.
   A page load or hover alone should not produce a `click` event.
4. In [Uncloud GA4 DebugView](https://analytics.google.com/analytics/web/#/a380265295p555082273/admin/debugview/overview),
   select the test browser, open `click`, and check `link_url`, `link_id`, and
   `outbound`. GTM Preview supplies the debug signal. Realtime is also useful;
   standard reports can take 24–48 hours to populate.
5. For ongoing reporting, use an Exploration with **Event count** and **Total
   users**, dimensions **Event name**, **Link URL**, and **Link ID**. Filter Event
   name to `click` and Link URL to `https://github.com/ethanteng/homebase`, and use
   Link ID for header/footer placement. No new custom dimension is required.

Before this change, a live Tag Assistant test confirmed one automatic `click`
hit to `G-233MB44VRQ`, with the correct GitHub URL and `outbound=true`, but an
empty `link_id`. The landing build and all eight existing landing tests passed
after adding the IDs. No GTM or GA4 configuration changes are required.

References: [GA4 enhanced measurement parameters](https://support.google.com/analytics/answer/9216061?hl=en),
[built-in Link ID dimension](https://developers.google.com/analytics/devguides/reporting/data/v1/api-schema).
