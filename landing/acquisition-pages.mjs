// Evergreen acquisition pages: one template, copy swapped per page.
// Edit the copy here, then run `node landing/acquisition-pages.mjs` to rewrite the
// generated landing/<slug>.html files and commit them. landing/acquisition-pages.test.cjs
// fails when a generated page is out of step with this file.
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const ORIGIN = "https://www.uncloud.life";
const SRC = "U.S. prices · USD · billed monthly · checked September 2026";

const A = {
  cost: { q: "What does Uncloud cost?", a: "Uncloud Home is $99 once. Join early access to lock in $79 at launch, with a 30-day free trial and no credit card. Unlimited household members and devices, remote access, migrations and software updates are included." },
  storage: { q: "How much storage do I get?", a: "As much as your computer and connected drives can hold. Plug in a bigger drive and you have more room. There are no storage tiers and no per-person charges." },
  family: { q: "Can everyone in my family have their own space?", a: "Yes. Each person gets their own username, password and private space, and sees only their own files. The people who look after Uncloud manage accounts but can’t browse anyone else’s files." },
  privacy: { q: "Who can see my files?", a: "Only you. Your files live on a computer in your home, not with a cloud company. Each person sees only their own space, and files travel encrypted between your devices and the host." },
  remote: { q: "Can I reach my files away from home?", a: "Yes. Turn on remote access and everyone can sign in from their phone or any browser, at home or out, with a secure https:// address." },
};

const priceRows = (a1, a3, a5) => [
  { label: "After 1 year", a: a1, b: "$79" },
  { label: "After 3 years", a: a3, b: "$79" },
  { label: "After 5 years", a: a5, b: "$79" },
];
const yearRows = (name, plan, yr, mark) => [1, 2, 3].map((n) => ({ name, plan: `${plan} · year ${n}`, value: yr, unit: "/yr", mark }));

export const PAGES = {
  "dropbox-alternative": {
    label: "Dropbox alternative",
    title: "Dropbox Alternative With No Monthly Fee | Uncloud",
    description: "Uncloud is a Dropbox alternative that turns a computer you own into your family’s private cloud. $99 once, no monthly fees. Early access: $79.",
    eyebrow: "Dropbox alternative",
    h1a: "A Dropbox alternative", h1b: "that lives in your home.",
    lede: "Keep your family’s files on your own computer. Reach them anywhere.",
    detail: "Connect your Dropbox, copy everything home in one step, and stop paying $19.99 a month for storage.",
    monthly: 1999,
    hero: { aria: "Uncloud Home compared with Dropbox Family", kicker: "One price. Yours to keep.", heading: "Dropbox Family, every year.", rows: yearRows("Dropbox", "Family · 2 TB", "$239.88", "dropbox"), totalBig: "$719.64", totalSmall: "over three years" },
    sw: { eyebrow: "Bring your files home", heading: "Move from Dropbox in three steps.", intro: "Uncloud comes with its own Dropbox connection, so there’s no developer console and no app key. Each person signs in to their own Dropbox.",
      steps: [
        { title: "Connect your Dropbox", body: "On My files, click Add files, then Connect Dropbox and sign in. Uncloud can only read your Dropbox, never change it." },
        { title: "Choose what comes home", body: "Add one folder or everything. Uncloud checks it fits first, then copies it into your own private space." },
        { title: "Carry on while it copies", body: "Close the page if you like. My files shows how far it has got, and your originals stay in Dropbox until you decide." },
      ] },
    cmp: { eyebrow: "How the savings add up", heading: "Dropbox Family costs $239.88 a year. Uncloud is $79 once.", intro: "Dropbox Family renews every month. Uncloud Home is a single purchase, and your storage is whatever your computer and drives can hold.", caption: "Dropbox Family vs. Uncloud Home", captionSub: SRC, colA: "Dropbox Family", colB: "Uncloud Home", align: "right", rows: priceRows("$239.88", "$719.64", "$1,199.40"), boxBig: "$1,120.40", boxSmall: "kept over five years at the $79 early-access price" },
    faqHeading: "Switching from Dropbox",
    faq: [
      { q: "How do I move my files from Dropbox to Uncloud?", a: "In Uncloud, click Add files, then Connect Dropbox and sign in. Choose a folder or your whole Dropbox and click Add. Files are copied, never moved, so the originals stay in Dropbox. Adding the same folder again only brings what’s new." },
      { q: "Can Uncloud change or delete anything in my Dropbox?", a: "No. Uncloud can only read your Dropbox. Sign Uncloud out of Dropbox whenever you like; files you already added stay where they are." },
      A.family, A.storage, A.cost,
    ],
    close: "Bring your Dropbox home.",
  },
  "google-drive-alternative": {
    label: "Google Drive alternative",
    title: "Google Drive Alternative With No Monthly Fee | Uncloud",
    description: "Uncloud is a Google Drive alternative that keeps your family’s files on a computer you own. $99 once, no storage plan. Early access: $79.",
    eyebrow: "Google Drive alternative",
    h1a: "A Google Drive alternative", h1b: "that keeps your files at home.",
    lede: "Your files on your own computer. Reachable from any browser.",
    detail: "Bring your Drive home, give everyone their own private space, and stop renting storage by the month.",
    monthly: 999,
    hero: { aria: "Uncloud Home compared with a Google One 2 TB plan", kicker: "One price. Yours to keep.", heading: "Google One 2 TB, every year.", rows: yearRows("Google Drive", "Google One · 2 TB", "$119.88", "drive"), totalBig: "$359.64", totalSmall: "over three years" },
    sw: { eyebrow: "Bring your files home", heading: "Move from Google Drive in three steps.", intro: "Uncloud brings Drive home through the folder the Google Drive app keeps on your computer. Nothing is deleted from Google.",
      steps: [
        { title: "Mirror Drive to the host", body: "On the computer that runs Uncloud, install Google Drive for desktop and set it to mirror your files, so they’re stored on the drive." },
        { title: "Add the Drive folder", body: "In Uncloud, click Add files, choose This computer, and pick the Google Drive folder. Add all of it or just what you need." },
        { title: "Your copy is yours", body: "Files are copied into your own space and the originals stay put. Adding the folder again later only brings what’s new." },
      ] },
    cmp: { eyebrow: "How the savings add up", heading: "Stop renting storage by the month.", intro: "A Google One 2 TB plan renews every month. Uncloud Home is a single purchase, and your storage is whatever your computer and drives can hold.", caption: "Google One 2 TB vs. Uncloud Home", captionSub: SRC, colA: "Google One · 2 TB", colB: "Uncloud Home", align: "right", rows: priceRows("$119.88", "$359.64", "$599.40"), boxBig: "$520.40", boxSmall: "kept over five years at the $79 early-access price" },
    faqHeading: "Switching from Google Drive",
    faq: [
      { q: "How do I move my files from Google Drive to Uncloud?", a: "Install Google Drive for desktop on the computer that runs Uncloud and set it to mirror your files. Then click Add files, choose This computer, and add the Google Drive folder. Files are copied, never moved." },
      { q: "Does Uncloud replace Gmail or Google Photos?", a: "No. Uncloud holds your files. Gmail, Google Photos and the other benefits of a Google One plan are separate, so keep the plan you need for those." },
      A.privacy, A.storage, A.cost,
    ],
    close: "Bring your Google Drive home.",
  },
  "icloud-alternative": {
    label: "iCloud alternative",
    title: "iCloud Alternative With No Monthly Storage Fee | Uncloud",
    description: "Uncloud is an iCloud alternative that turns a Mac you already own into your family’s private cloud. $99 once, no iCloud+ plan. Early access: $79.",
    eyebrow: "iCloud alternative",
    h1a: "An iCloud alternative", h1b: "on the Mac you already own.",
    lede: "Your family’s files at home. Reachable from any browser.",
    detail: "Bring iCloud Drive home, give everyone their own space, and keep a folder on each Mac in step.",
    monthly: 999,
    hero: { aria: "Uncloud Home compared with iCloud+ 2 TB", kicker: "One price. Yours to keep.", heading: "iCloud+ 2 TB, every year.", rows: yearRows("iCloud+", "2 TB", "$119.88", "icloud"), totalBig: "$359.64", totalSmall: "over three years" },
    sw: { eyebrow: "Bring your files home", heading: "Move from iCloud Drive in three steps.", intro: "On a Mac, iCloud Drive is already a folder. Uncloud copies it into your own space and leaves iCloud as it was.",
      steps: [
        { title: "Download iCloud Drive", body: "On the Mac that runs Uncloud, turn off Optimize Mac Storage in iCloud Drive settings so your files are stored on the Mac." },
        { title: "Add the iCloud Drive folder", body: "In Uncloud, click Add files, choose This computer, and pick iCloud Drive, or use Choose another folder… to find it." },
        { title: "Keep your laptop in step", body: "With the Uncloud app on your MacBook, a folder stays in sync both ways, at home or away." },
      ] },
    cmp: { eyebrow: "How the savings add up", heading: "iCloud+ 2 TB costs $119.88 a year. Uncloud is $79 once.", intro: "iCloud+ renews every month. Uncloud Home is a single purchase, and your storage is whatever your Mac and drives can hold.", caption: "iCloud+ 2 TB vs. Uncloud Home", captionSub: SRC, colA: "iCloud+ · 2 TB", colB: "Uncloud Home", align: "right", rows: priceRows("$119.88", "$359.64", "$599.40"), boxBig: "$520.40", boxSmall: "kept over five years at the $79 early-access price" },
    faqHeading: "Switching from iCloud",
    faq: [
      { q: "How do I move my files out of iCloud Drive?", a: "On the Mac that runs Uncloud, make sure your iCloud Drive files are downloaded by turning off Optimize Mac Storage. Then click Add files, choose This computer, and add your iCloud Drive folder. Files are copied; the originals stay in iCloud." },
      { q: "Does Uncloud replace iPhone backups or iCloud Photos?", a: "No. Uncloud holds your files. iPhone backups, iCloud Photos and iCloud Mail are separate, so keep the iCloud plan you need for those." },
      A.family,
      { q: "Can I open my files on my iPhone?", a: "Yes. Sign in from Safari or any browser, at home or out, with your Uncloud’s secure https:// address." },
      A.cost,
    ],
    close: "Bring your iCloud Drive home.",
  },
  "onedrive-alternative": {
    label: "OneDrive alternative",
    title: "OneDrive Alternative With No Monthly Fee | Uncloud",
    description: "Uncloud is a OneDrive alternative that keeps your household’s files on a computer you own. $99 once, no monthly storage fee. Early access: $79.",
    eyebrow: "OneDrive alternative",
    h1a: "A OneDrive alternative", h1b: "that runs on your own computer.",
    lede: "Your files on a computer at home. Reachable from anywhere.",
    detail: "Bring OneDrive home, give everyone their own private space, and stop paying for storage by the month.",
    monthly: 1299,
    hero: { aria: "Uncloud Home compared with Microsoft 365 Family", kicker: "One price. Yours to keep.", heading: "Microsoft 365 Family, every year.", rows: yearRows("OneDrive", "Microsoft 365 Family", "$155.88", "onedrive"), totalBig: "$467.64", totalSmall: "over three years" },
    sw: { eyebrow: "Bring your files home", heading: "Move from OneDrive in three steps.", intro: "Uncloud brings OneDrive home through the folder the OneDrive app keeps on your computer. Nothing is deleted from Microsoft.",
      steps: [
        { title: "Keep OneDrive on the host", body: "On the computer that runs Uncloud, choose Always keep on this device for your OneDrive folder so the files are stored locally." },
        { title: "Add the OneDrive folder", body: "In Uncloud, click Add files, choose This computer, and pick the OneDrive folder. Add all of it or one folder at a time." },
        { title: "Sign in from anywhere", body: "Turn on remote access and everyone reaches their files from a phone or any browser, with a secure https:// address." },
      ] },
    cmp: { eyebrow: "How the savings add up", heading: "Microsoft 365 Family costs $155.88 a year. Uncloud is $79 once.", intro: "Microsoft 365 Family renews every month. Uncloud Home is a single purchase, and your storage is whatever your computer and drives can hold.", caption: "Microsoft 365 Family vs. Uncloud Home", captionSub: SRC, colA: "Microsoft 365 Family", colB: "Uncloud Home", align: "right", rows: priceRows("$155.88", "$467.64", "$779.40"), boxBig: "$700.40", boxSmall: "kept over five years at the $79 early-access price" },
    faqHeading: "Switching from OneDrive",
    faq: [
      { q: "How do I move my files from OneDrive to Uncloud?", a: "On the computer that runs Uncloud, set your OneDrive folder to Always keep on this device. Then click Add files, choose This computer, and add the OneDrive folder. Files are copied, never moved." },
      { q: "Does Uncloud replace Word, Excel and Outlook?", a: "No. Uncloud holds your files. The Microsoft 365 apps and Outlook mail are separate, so keep the plan you need for those." },
      { q: "Can I use Uncloud from a Windows PC?", a: "Yes. Sign in to Uncloud from any browser on a Windows PC, at home or away." },
      A.storage, A.cost,
    ],
    close: "Bring your OneDrive home.",
  },
  "uncloud-vs-nextcloud": {
    label: "Uncloud vs. Nextcloud",
    title: "Uncloud vs. Nextcloud: Private Cloud for Your Household",
    description: "Uncloud vs. Nextcloud for a household private cloud: setup, remote access, accounts and cost. Uncloud sets up in minutes on a computer you own.",
    eyebrow: "Uncloud vs. Nextcloud",
    h1a: "Uncloud vs. Nextcloud:", h1b: "a private cloud without the server admin.",
    lede: "Both keep your files off somebody else’s servers. Uncloud is built for a household.",
    detail: "Nextcloud is a full self-hosted suite. Uncloud is one app that turns a computer at home into your family’s cloud in minutes.",
    hero: { aria: "Uncloud Home compared with a self-hosted Nextcloud server", kicker: "Built for a household.", heading: "A Nextcloud server.", rows: [
      { name: "Software", plan: "Nextcloud Hub, open source", value: "Free", unit: "" },
      { name: "Runs on", plan: "A server, NAS or rented VPS", value: "You set up", unit: "" },
      { name: "Install", plan: "Docker, or web server + PHP + database", value: "Hands-on", unit: "" },
      { name: "Remote access", plan: "Domain, HTTPS certificate, open ports", value: "You configure", unit: "" },
    ], totalBig: "Self-hosted", totalSmall: "and maintained by you" },
    sw: { eyebrow: "Coming from Nextcloud", heading: "Move a household over in three steps.", intro: "Your Nextcloud files are already on a drive you control. Uncloud copies them into each person’s own space.",
      steps: [
        { title: "Install Uncloud at home", body: "Open the Mac app and choose Make this Mac the Uncloud host. Make your account and pick where everyone’s files live." },
        { title: "Add your Nextcloud folder", body: "Click Add files, choose This computer, and add the folder the Nextcloud desktop client keeps, or the drive your files are on." },
        { title: "Add everyone else", body: "Open People and click Add someone. Each person gets their own username, password and private space." },
      ] },
    cmp: { eyebrow: "Side by side", heading: "Two private clouds, built for different jobs.", intro: "Nextcloud covers files, calendars, office documents and chat for teams of any size. Uncloud does one job for a household: keeping everyone’s files at home and within reach.", caption: "Uncloud Home vs. Nextcloud", captionSub: "Self-hosted Nextcloud Hub · home use", colA: "Nextcloud", colB: "Uncloud Home", align: "left", rows: [
      { label: "Built for", a: "Individuals, teams and organizations", b: "A household" },
      { label: "Setup", a: "A server with Docker, or a web server, PHP and a database", b: "A Mac app, five minutes or less" },
      { label: "Remote access", a: "Your own domain and certificate, or a hosting provider", b: "Built in, with a secure https:// address" },
      { label: "Accounts", a: "Users and groups, managed by an admin", b: "A private space for each person" },
      { label: "Beyond files", a: "Calendar, contacts, office, chat and hundreds of apps", b: "Files, laptop sync and imports" },
      { label: "Cost", a: "Free software; hardware or hosting extra", b: "$99 once, $79 with early access" },
    ], boxBig: "5 minutes", boxSmall: "or less to set up Uncloud with the Mac app" },
    faqHeading: "Uncloud vs. Nextcloud questions",
    faq: [
      { q: "Is Uncloud built on Nextcloud?", a: "No. Uncloud is its own app. It uses Syncthing to keep laptops in sync and Tailscale, a free service, for its secure connection from anywhere." },
      { q: "Which is easier to set up?", a: "On a Mac, Uncloud sets up in five minutes or less: open the app, choose Make this Mac the Uncloud host, make your account and pick a folder. Nextcloud is usually installed on a server with Docker, or with a web server, PHP and a database." },
      { q: "Do I need a domain name or open ports?", a: "Not with Uncloud. Turn on remote access and Uncloud gives you a secure https:// address that works at home and away, on any device." },
      { q: "When is Nextcloud the better fit?", a: "If you want calendars, contacts, office documents, chat and a large app ecosystem on one server, Nextcloud covers far more ground. Uncloud focuses on your household’s files." },
      A.cost,
    ],
    close: "A private cloud your whole household can use.",
  },
};

export const SLUGS = Object.keys(PAGES);

const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
// JSON inside <script> must not be able to close the element.
const jsonLd = (data) => JSON.stringify(data, null, 2).replace(/</g, "\\u003c");
const indent = (text, n) => text.split("\n").map((line) => (line ? " ".repeat(n) + line : line)).join("\n");

const YOUTUBE = '<svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true" focusable="false"><path fill="currentColor" d="M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z"/></svg>';
const GITHUB = '<svg viewBox="0 0 16 16" width="22" height="22" aria-hidden="true" focusable="false"><path fill="currentColor" d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/></svg>';

const ACCESS = `<p class="access-offer">Join early access to <strong>lock in $79 at launch.</strong></p>
<div class="launchlist-widget" data-key-id="21CSQp"></div>
<p class="trust-note">30-day free trial when you get access. <span>No credit card.</span></p>`;

/** The footer's links to every acquisition page; the homepage uses it too. */
export function compareNav(current) {
  const links = SLUGS.map((s) => `<a href="/${s}"${s === current ? ' aria-current="page"' : ""}>${esc(PAGES[s].label)}</a>`);
  return `<nav class="compare-nav" aria-label="Compare Uncloud">\n  ${links.join("\n  ")}\n</nav>`;
}

function structuredData(slug, p) {
  const url = `${ORIGIN}/${slug}`;
  return {
    "@context": "https://schema.org",
    "@graph": [
      { "@type": "WebPage", "@id": `${url}#webpage`, url, name: p.title, description: p.description, inLanguage: "en", isPartOf: { "@id": `${ORIGIN}/#website` }, about: { "@id": `${ORIGIN}/#software` }, breadcrumb: { "@id": `${url}#breadcrumb` }, image: `${ORIGIN}/social-card.png` },
      { "@type": "BreadcrumbList", "@id": `${url}#breadcrumb`, itemListElement: [
        { "@type": "ListItem", position: 1, name: "Uncloud", item: `${ORIGIN}/` },
        { "@type": "ListItem", position: 2, name: p.label, item: url },
      ] },
      { "@type": "FAQPage", "@id": `${url}#faq`, mainEntity: p.faq.map((f) => ({ "@type": "Question", name: f.q, acceptedAnswer: { "@type": "Answer", text: f.a } })) },
      { "@type": "WebSite", "@id": `${ORIGIN}/#website`, name: "Uncloud", url: `${ORIGIN}/` },
      { "@type": "SoftwareApplication", "@id": `${ORIGIN}/#software`, name: "Uncloud Home", url: `${ORIGIN}/`, applicationCategory: "UtilitiesApplication", applicationSubCategory: "Private cloud storage", operatingSystem: ["macOS", "Windows", "Linux"] },
    ],
  };
}

export function render(slug) {
  const p = PAGES[slug];
  const url = `${ORIGIN}/${slug}`;
  const hero = p.hero.rows.map((r) => `<li class="bill${r.mark ? ` bill-${r.mark}` : " bill-plain"}">
  ${r.mark ? '<span class="bill-mark" aria-hidden="true"></span>\n  ' : ""}<span class="bill-service">${esc(r.name)} <small>${esc(r.plan)}</small></span>
  <span class="bill-cost"><strong>${esc(r.value)}</strong>${r.unit ? `<small>${esc(r.unit)}</small>` : ""}</span>
</li>`).join("\n");
  const steps = p.sw.steps.map((s, i) => `<li>
  <span class="step-number" aria-hidden="true">${i + 1}</span>
  <div>
    <h3>${esc(s.title)}</h3>
    <p>${esc(s.body)}</p>
  </div>
</li>`).join("\n");
  const rows = p.cmp.rows.map((r) => `<tr><th scope="row">${esc(r.label)}</th><td>${esc(r.a)}</td><td>${esc(r.b)}</td></tr>`).join("\n");
  const faq = p.faq.map((f) => `<details>
  <summary><h3>${esc(f.q)}</h3><span class="faq-sign" aria-hidden="true">+</span></summary>
  <p>${esc(f.a)}</p>
</details>`).join("\n");

  return `<!doctype html>
<!-- Generated by acquisition-pages.mjs. Edit that file and rerun it; don't edit this page directly. -->
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <!-- Only the public landing site sends measurement. Local/preview builds stay quiet. -->
    <script>
      window.dataLayer = window.dataLayer || [];
      function gtag() { dataLayer.push(arguments); }
      gtag('set', { allow_google_signals: false, allow_ad_personalization_signals: false });
      if (new URLSearchParams(location.search).get('measurement_debug') === '1') {
        gtag('set', { debug_mode: true });
      }
      if (['uncloud.life', 'www.uncloud.life'].includes(location.hostname)) {
        (function(w,d,s,l,i){w[l]=w[l]||[];w[l].push({'gtm.start':
        new Date().getTime(),event:'gtm.js'});var f=d.getElementsByTagName(s)[0],
        j=d.createElement(s),dl=l!='dataLayer'?'&l='+l:'';j.async=true;j.src=
        'https://www.googletagmanager.com/gtm.js?id='+i+dl;f.parentNode.insertBefore(j,f);
        })(window,document,'script','dataLayer','GTM-KS2XST5Z');
      }
    </script>
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta name="theme-color" content="#f7fafd" />
    <title>${esc(p.title)}</title>
    <meta name="description" content="${esc(p.description)}" />
    <meta property="og:title" content="${esc(p.title)}" />
    <meta property="og:description" content="${esc(p.description)}" />
    <meta property="og:type" content="website" />
    <meta property="og:site_name" content="Uncloud" />
    <meta property="og:url" content="${url}" />
    <meta property="og:locale" content="en_US" />
    <meta property="og:image" content="${ORIGIN}/social-card.png" />
    <meta property="og:image:type" content="image/png" />
    <meta property="og:image:width" content="1200" />
    <meta property="og:image:height" content="630" />
    <meta property="og:image:alt" content="Uncloud — private cloud storage on a computer you already own." />
    <meta name="twitter:card" content="summary_large_image" />
    <meta name="twitter:title" content="${esc(p.title)}" />
    <meta name="twitter:description" content="${esc(p.description)}" />
    <meta name="twitter:image" content="${ORIGIN}/social-card.png" />
    <meta name="twitter:image:alt" content="Uncloud — private cloud storage on a computer you already own." />
    <link rel="canonical" href="${url}" />
    <script type="application/ld+json">
${indent(jsonLd(structuredData(slug, p)), 6)}
    </script>
    <link rel="icon" type="image/png" href="./uncloud.png" />
    <link rel="stylesheet" href="./styles.css" />
    <link rel="stylesheet" href="./hero-visual.css" />
    <link rel="stylesheet" href="./acquisition.css" />
    <script type="module" src="./main.js"></script>
    <script src="https://getlaunchlist.com/js/widget.js" defer></script>
  </head>
  <body>
    <a class="skip-link" href="#main">Skip to content</a>
    <div class="page">
      <header class="site-header">
        <a class="wordmark" href="/" aria-label="Uncloud home">
          <img src="./uncloud.png" width="32" height="32" alt="" />
          Uncloud
        </a>
        <div class="header-actions">
          <!-- GA4 enhanced measurement records click + link_url; these IDs supply placement via link_id. -->
          <a id="youtube-header" class="youtube-link" href="https://www.youtube.com/@Uncloud-life" aria-label="Uncloud on YouTube">
            ${YOUTUBE}
          </a>
          <a id="github-header" class="github-link" href="https://github.com/ethanteng/homebase" aria-label="Uncloud on GitHub">
            ${GITHUB}
          </a>
          <a class="pricing-link" href="#pricing">Pricing</a>
          <a class="header-cta" href="#early-access">Get early access</a>
        </div>
      </header>

      <main id="main">
        <section class="hero" aria-labelledby="hero-heading">
          <div class="hero-copy">
            <p class="eyebrow">${esc(p.eyebrow)}</p>
            <h1 id="hero-heading">
              ${esc(p.h1a)}
              <span>${esc(p.h1b)}</span>
            </h1>
            <p class="hero-description">
              <strong>${esc(p.lede)}</strong>
            </p>
            <p class="hero-detail">${esc(p.detail)}</p>
          </div>

          <figure class="home-comparison" aria-label="${esc(p.hero.aria)}">
            <div class="comparison-stage">
              <div class="solution-pane">
                <p class="solution-kicker">${esc(p.hero.kicker)}</p>
                <h2>Uncloud Home</h2>
                <div class="solution-computer" aria-hidden="true">
                  <div class="solution-screen"><img src="./uncloud.png" width="64" height="64" alt="" /></div>
                  <div class="solution-base"></div>
                </div>
                <p class="solution-price"><strong>$99</strong><span>lifetime</span></p>
                <p class="solution-promise">No monthly storage fees.</p>
                <p class="solution-platforms">
                  <span class="sr-only">Works on Mac, PC and Linux.</span>
                  <span class="platform-mark platform-mac" aria-hidden="true" title="Mac"></span>
                  <span class="platform-mark platform-pc" aria-hidden="true" title="PC"></span>
                  <span class="platform-mark platform-linux" aria-hidden="true" title="Linux"></span>
                </p>
                <a class="solution-founder" href="#early-access">Lock in $79 with early access <span aria-hidden="true">→</span></a>
              </div>

              <div class="comparison-divider" aria-hidden="true">vs.</div>

              <div class="bills-pane">
                <h2>${esc(p.hero.heading)}</h2>
                <ul class="bill-list" role="list">
${indent(hero, 18)}
                </ul>
                <p class="bills-total"><strong>${esc(p.hero.totalBig)}</strong> <span>${esc(p.hero.totalSmall)}</span></p>
              </div>
            </div>
          </figure>

          <div class="access-action" id="early-access">
${indent(ACCESS, 12)}
          </div>
        </section>

        <section class="switch" id="switch" aria-labelledby="switch-heading">
          <div>
            <p class="eyebrow">${esc(p.sw.eyebrow)}</p>
            <h2 id="switch-heading">${esc(p.sw.heading)}</h2>
            <p class="section-intro">${esc(p.sw.intro)}</p>
          </div>
          <ol class="switch-steps" role="list">
${indent(steps, 12)}
          </ol>
        </section>

        <section class="setup" id="setup" aria-labelledby="setup-heading">
          <div class="section-heading">
            <p class="eyebrow">How it works</p>
            <h2 id="setup-heading">Set up in minutes.</h2>
          </div>
          <ol class="setup-steps" role="list">
            <li>
              <span class="step-number" aria-hidden="true">1</span>
              <h3>Choose your computer</h3>
              <p>A Mac, PC or Linux computer, with space on its own drive or a connected drive.</p>
            </li>
            <li>
              <span class="step-number" aria-hidden="true">2</span>
              <h3>Install Uncloud</h3>
              <p>Choose where your files live. Keep your computer on and online to reach them from anywhere.</p>
            </li>
            <li>
              <span class="step-number" aria-hidden="true">3</span>
              <h3>Bring your files home</h3>
              <p>Move files from Dropbox, Google Drive and more.</p>
            </li>
          </ol>
        </section>

        <section class="video" id="video" aria-labelledby="video-heading">
          <div class="video-card">
            <div class="video-card__header">
              <p class="eyebrow">Your files. Your home.</p>
              <h2 id="video-heading">Meet Uncloud Home</h2>
            </div>
            <div class="video-card__player">
              <iframe src="https://www.youtube-nocookie.com/embed/oELh5dwlmHs?rel=0&amp;playsinline=1" title="Uncloud Home teaser video" loading="lazy" allow="encrypted-media; picture-in-picture; fullscreen" allowfullscreen referrerpolicy="strict-origin-when-cross-origin"></iframe>
            </div>
            <div class="video-card__footer">
              <p>A first look at your family’s private cloud.</p>
              <a href="https://www.youtube.com/watch?v=oELh5dwlmHs" target="_blank" rel="noopener noreferrer">Watch on YouTube <span aria-hidden="true">↗</span><span class="sr-only"> (opens in a new tab)</span></a>
            </div>
          </div>
        </section>

        <section class="savings" id="savings" aria-labelledby="savings-heading">
          <div class="savings-copy">
            <p class="eyebrow">${esc(p.cmp.eyebrow)}</p>
            <h2 id="savings-heading">${esc(p.cmp.heading)}</h2>
            <p class="section-intro">${esc(p.cmp.intro)}</p>
          </div>
          <div class="savings-example">
            <table class="cost-table cost-table--${p.cmp.align}">
              <caption>${esc(p.cmp.caption)} <span>${esc(p.cmp.captionSub)}</span></caption>
              <thead><tr><td></td><th scope="col">${esc(p.cmp.colA)}</th><th scope="col">${esc(p.cmp.colB)}</th></tr></thead>
              <tbody>
${indent(rows, 16)}
              </tbody>
            </table>
            <p class="annual-total"><strong>${esc(p.cmp.boxBig)}</strong> <span>${esc(p.cmp.boxSmall)}</span></p>
          </div>
        </section>

        <section class="pricing" id="pricing" aria-labelledby="pricing-heading">
          <div class="section-heading">
            <p class="eyebrow">Compare with Uncloud</p>
            <h2 id="pricing-heading">One home. One price.</h2>
          </div>
          <div class="pricing-card">
            <div class="pricing-offer">
              <h3>Uncloud Home</h3>
              <p class="price"><s class="standard-price"><span class="sr-only">Standard price </span>$99</s> <strong><span class="sr-only">Early-access price </span>$79</strong> <span>one-time</span></p>
              <p class="regular-price">Standard price $99 · <strong>Save $20 with early access</strong></p>
              <p class="price-promise">No monthly storage fees.</p>
              <a class="primary-link" href="#early-access">Get early access</a>
              <p class="trial-note">30-day free trial. No credit card.</p>
              <p class="access-note">Sign up for early access to lock in $79 when Uncloud is ready. No payment today.</p>
            </div>
            <div class="pricing-includes">
              <h3>Everything your household needs</h3>
              <ul class="included-features" role="list">
                <li>Unlimited household members</li>
                <li>Unlimited devices</li>
                <li>Access your files from anywhere</li>
                <li>Bring your files from Dropbox, Google Drive &amp; more</li>
                <li>Software updates included</li>
                <li>1 primary host computer</li>
              </ul>
              <p class="storage-note">Use as much storage as your computer and connected drives can hold. No storage tiers.</p>
            </div>
          </div>
        </section>

        <section class="faq" id="faq" aria-labelledby="faq-heading">
          <div class="section-heading">
            <p class="eyebrow">Questions</p>
            <h2 id="faq-heading">${esc(p.faqHeading)}</h2>
          </div>
          <div class="faq-list">
${indent(faq, 12)}
          </div>
        </section>

        <section class="join" id="join" aria-labelledby="join-heading">
          <p class="eyebrow">Get early access</p>
          <h2 id="join-heading">${esc(p.close)}</h2>
          <div class="join-action">
${indent(ACCESS, 12)}
          </div>
        </section>
      </main>

      <footer class="site-footer">
        <p>© 2026 Uncloud</p>
${indent(compareNav(slug), 8)}
        <div class="footer-links">
          <a id="youtube-footer" class="youtube-link" href="https://www.youtube.com/@Uncloud-life" aria-label="Uncloud on YouTube">
            ${YOUTUBE}
          </a>
          <a id="github-footer" class="github-link" href="https://github.com/ethanteng/homebase" aria-label="Uncloud on GitHub">
            ${GITHUB}
          </a>
        </div>
      </footer>
    </div>
  </body>
</html>
`;
}

export const file = (slug) => fileURLToPath(new URL(`./${slug}.html`, import.meta.url));

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  for (const slug of SLUGS) writeFileSync(file(slug), render(slug));
  console.log(`Wrote ${SLUGS.map((s) => `${s}.html`).join(", ")}`);
}
