// Evergreen acquisition pages: one template, copy swapped per page.
// Edit the copy here, then run `node landing/acquisition-pages.mjs` to rewrite the
// generated landing/<slug>.html files and commit them. landing/acquisition-pages.test.cjs
// fails when a generated page is out of step with this file.
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const ORIGIN = "https://www.uncloud.life";
const SRC = "U.S. prices · USD · monthly billing · checked October 2026";

const A = {
  cost: { q: "What does Uncloud cost?", a: "Join early access to lock in $79 at launch ($99 regular), paid once. Household members, devices, remote access, file migration and updates are included. No payment today; try it free for 30 days when you get access." },
  storage: { q: "How much storage do I get?", a: "As much as your computer and connected drives can hold. Add a bigger drive when you need more room. No storage tiers or per-person charges." },
  family: { q: "Does everyone get their own space?", a: "Yes. Each person gets their own account and private files. Unlimited household members and devices are included." },
  privacy: { q: "Where do my files live?", a: "On your own computer and drives. Each person has a private space, and files travel encrypted between your devices." },
};

const priceRows = (a1, a3, a5) => [
  { label: "1 year", a: a1, b: "$79" },
  { label: "3 years", a: a3, b: "$79" },
  { label: "5 years", a: a5, b: "$79" },
];

export const PAGES = {
  "dropbox-alternative": {
    label: "Dropbox alternative",
    title: "Dropbox Alternative With No Monthly Fee | Uncloud",
    description: "Keep your files. Lose the Dropbox bill. Uncloud uses your own computer, copies your Dropbox files and keeps them within reach. Early access: $79 once.",
    eyebrow: "Dropbox alternative",
    h1a: "Keep your files.", h1b: "Lose the Dropbox bill.",
    lede: "Cloud storage on a computer you already own.",
    detail: "Years of files shouldn’t keep you paying. Uncloud makes it easy to bring them home.",
    why: [
      { title: "End the monthly storage bill.", body: "Buy Uncloud once. Use the space on your computer and drives." },
      { title: "Move out without starting over.", body: "Connect Dropbox and copy your files. Your originals stay put." },
      { title: "Keep the access you rely on.", body: "Reach your files anywhere. Give everyone at home their own private space." },
    ],
    monthly: 1999,
    cmp: { eyebrow: "The cost of staying", heading: "The files stay. The bill doesn’t.", intro: "Dropbox Family renews monthly. Uncloud is a one-time purchase for your household, using your own storage.", caption: "Example: Dropbox Family · 2 TB", captionSub: SRC, colA: "Dropbox Family", colB: "Uncloud", align: "right", rows: priceRows("$239.88", "$719.64", "$1,199.40"), boxBig: "$1,120.40", boxSmall: "less in subscription costs over 5 years with $79 early access", source: "https://www.dropbox.com/buy?_tk=plus_last_button", sourceLabel: "Dropbox pricing" },
    sw: { eyebrow: "Switch without the project", heading: "Bring your Dropbox files home.", intro: "Set up Uncloud in 5 minutes or less. Then bring your files over.",
      steps: [
        { title: "Install Uncloud", body: "Use a Mac, PC or Linux computer you already own." },
        { title: "Connect Dropbox", body: "Sign in through Uncloud and choose a folder or everything." },
        { title: "Make yourself at home", body: "Uncloud copies your files. Access them anywhere; your Dropbox originals stay put." },
      ] },
    faqHeading: "Before you switch",
    faq: [
      { q: "How do I bring my Dropbox files over?", a: "In Uncloud, choose Add files, then Connect Dropbox. Sign in and choose your files. Uncloud copies them; it never changes or deletes your Dropbox originals." },
      { q: "Can I disconnect Dropbox afterward?", a: "Yes. Disconnect whenever you like. Files you already copied stay in Uncloud." },
      A.family, A.storage, A.cost,
    ],
    close: "Keep your files. Drop the monthly bill.",
  },
  "google-drive-alternative": {
    label: "Google Drive alternative",
    title: "Google Drive Alternative With No Monthly Fee | Uncloud",
    description: "More files shouldn’t mean a bigger Google Drive bill. Use your own computer with Uncloud: no storage tiers, access anywhere. Early access: $79 once.",
    eyebrow: "Google Drive alternative",
    h1a: "More room for your files.", h1b: "No bigger storage bill.",
    lede: "Cloud storage on a computer you already own.",
    detail: "Your files keep growing. Your monthly bill doesn’t have to. Put your own storage to work.",
    why: [
      { title: "Pay once for Uncloud.", body: "Keep using your own storage without a monthly fee." },
      { title: "Grow with a drive.", body: "Add space to your computer instead of upgrading a cloud plan." },
      { title: "Keep access from anywhere.", body: "Bring your Drive files home. Open them from your phone or browser." },
    ],
    monthly: 999,
    cmp: { eyebrow: "The cost of staying", heading: "More storage. Fewer renewals.", intro: "Uncloud replaces file storage. Gmail, Google Photos and Google’s AI features remain separate.", caption: "Example: Google AI Plus · 2 TB", captionSub: SRC, colA: "Google AI Plus", colB: "Uncloud", align: "right", rows: priceRows("$119.88", "$359.64", "$599.40"), boxBig: "$520.40", boxSmall: "less in subscription costs over 5 years with $79 early access", source: "https://one.google.com/about/plans", sourceLabel: "Google One pricing" },
    sw: { eyebrow: "Switch without the project", heading: "Bring your Google Drive files home.", intro: "Set up Uncloud in 5 minutes or less. Then bring your files over.",
      steps: [
        { title: "Install Uncloud", body: "Use a Mac, PC or Linux computer you already own." },
        { title: "Download your Drive files", body: "Download files from Drive. Export Google Docs, Sheets and Slides, too." },
        { title: "Add them to Uncloud", body: "Choose Add files, then This computer. Your Google Drive originals stay put." },
      ] },
    faqHeading: "Before you switch",
    faq: [
      { q: "How do I bring my Google Drive files over?", a: "Download your Drive files, including exports of Google Docs, Sheets and Slides. In Uncloud, choose Add files, then This computer, and select the downloaded folder." },
      { q: "Does Uncloud replace Gmail or Google Photos?", a: "No. Uncloud stores your files. Gmail, Google Photos and other Google plan benefits are separate." },
      A.privacy, A.storage, A.cost,
    ],
    close: "Keep your files. Drop the monthly bill.",
  },
  "icloud-alternative": {
    label: "iCloud alternative",
    title: "iCloud Alternative With No Monthly Storage Fee | Uncloud",
    description: "Skip another iCloud storage upgrade. Bring iCloud Drive files to your own computer with Uncloud and access them anywhere. Early access: $79 once.",
    eyebrow: "iCloud Drive alternative",
    h1a: "Your files don’t need", h1b: "another storage upgrade.",
    lede: "Cloud storage on a computer you already own.",
    detail: "Running out of room in iCloud Drive? Bring your files home and use the space you already have.",
    why: [
      { title: "Use space you already own.", body: "Keep files on your computer and connected drives. No storage tiers." },
      { title: "Drop the file-storage subscription.", body: "One Uncloud purchase covers your household and devices." },
      { title: "Reach files beyond your Mac.", body: "Open them from a phone or browser, at home or away." },
    ],
    monthly: 999,
    cmp: { eyebrow: "The cost of staying", heading: "Keep your files. Skip the upgrade.", intro: "Uncloud replaces iCloud Drive file storage. iPhone backups, iCloud Photos and Mail remain separate.", caption: "Example: iCloud+ · 2 TB", captionSub: SRC, colA: "iCloud+", colB: "Uncloud", align: "right", rows: priceRows("$119.88", "$359.64", "$599.40"), boxBig: "$520.40", boxSmall: "less in subscription costs over 5 years with $79 early access", source: "https://support.apple.com/en-us/108047", sourceLabel: "iCloud+ pricing" },
    sw: { eyebrow: "Switch without the project", heading: "Bring your iCloud Drive files home.", intro: "Set up Uncloud in 5 minutes or less. Then bring your files over.",
      steps: [
        { title: "Install Uncloud", body: "Use a Mac, PC or Linux computer you already own." },
        { title: "Download your iCloud files", body: "On a Mac, keep your iCloud Drive files downloaded. Or download them from iCloud.com." },
        { title: "Add them to Uncloud", body: "Choose Add files, then This computer. Your iCloud originals stay put." },
      ] },
    faqHeading: "Before you switch",
    faq: [
      { q: "How do I bring my iCloud Drive files over?", a: "Download them to the computer running Uncloud. On a Mac, turn off Optimize Mac Storage for iCloud Drive; on other computers, download from iCloud.com. Then choose Add files in Uncloud and select the folder." },
      { q: "Does Uncloud replace iPhone backups or iCloud Photos?", a: "No. Uncloud stores files. iPhone backups, iCloud Photos and iCloud Mail remain separate." },
      A.family,
      { q: "Can I open my files on my iPhone?", a: "Yes. Sign in from Safari or any browser to reach your Uncloud files at home or away." },
      A.cost,
    ],
    close: "Your files. Your space. One less bill.",
  },
  "onedrive-alternative": {
    label: "OneDrive alternative",
    title: "OneDrive Alternative With No Monthly Fee | Uncloud",
    description: "Use your own storage instead of renewing OneDrive. Uncloud keeps household files at home and within reach, with no storage tiers. Early access: $79 once.",
    eyebrow: "OneDrive alternative",
    h1a: "Your files at home.", h1b: "One less subscription.",
    lede: "Cloud storage on a computer you already own.",
    detail: "Already have room on your computer? Put it to work instead of paying for another storage plan.",
    why: [
      { title: "Pay once, for your household.", body: "One purchase includes everyone at home. No per-person fees." },
      { title: "Your drives set the limit.", body: "Use all the space your computer and connected drives can hold." },
      { title: "Keep files within reach.", body: "Bring your OneDrive files home and access them from anywhere." },
    ],
    monthly: 1299,
    cmp: { eyebrow: "The cost of staying", heading: "Own the storage. End the renewals.", intro: "Uncloud replaces OneDrive file storage. Word, Excel, Outlook and other Microsoft 365 benefits remain separate.", caption: "Example: Microsoft 365 Family", captionSub: SRC, colA: "Microsoft 365 Family", colB: "Uncloud", align: "right", rows: priceRows("$155.88", "$467.64", "$779.40"), boxBig: "$700.40", boxSmall: "less in subscription costs over 5 years with $79 early access", source: "https://www.microsoft.com/en-us/microsoft-365/buy/compare-all-microsoft-365-products", sourceLabel: "Microsoft 365 pricing" },
    sw: { eyebrow: "Switch without the project", heading: "Bring your OneDrive files home.", intro: "Set up Uncloud in 5 minutes or less. Then bring your files over.",
      steps: [
        { title: "Install Uncloud", body: "Use a Mac, PC or Linux computer you already own." },
        { title: "Download your OneDrive files", body: "Choose Always keep on this device, or download your files from OneDrive on the web." },
        { title: "Add them to Uncloud", body: "Choose Add files, then This computer. Your OneDrive originals stay put." },
      ] },
    faqHeading: "Before you switch",
    faq: [
      { q: "How do I bring my OneDrive files over?", a: "Set your OneDrive folder to Always keep on this device, or download your files from the web. In Uncloud, choose Add files, then This computer, and select the downloaded folder." },
      { q: "Does Uncloud replace Word, Excel or Outlook?", a: "No. Uncloud stores your files. Microsoft 365 apps and Outlook mail remain separate." },
      A.family, A.storage, A.cost,
    ],
    close: "Keep your files. Drop the monthly bill.",
  },
  "uncloud-vs-nextcloud": {
    label: "Uncloud vs. Nextcloud",
    title: "Uncloud vs. Nextcloud: Your Cloud, Without the Server Setup",
    description: "Files at home without a server project. Uncloud sets up in 5 minutes or less, with remote access and private household accounts. Compare with Nextcloud.",
    eyebrow: "Uncloud vs. Nextcloud",
    h1a: "Your own cloud.", h1b: "Without the server project.",
    lede: "Cloud storage on a computer you already own.",
    detail: "You want your files at home. Uncloud handles the complicated setup so you can get on with your day.",
    why: [
      { title: "Set up in 5 minutes or less.", body: "Install the app, choose where files live, and let Uncloud handle the setup." },
      { title: "Remote access is built in.", body: "Turn it on in Uncloud. Reach your files from a phone or browser." },
      { title: "Made for everyone at home.", body: "Separate accounts and private files, with no server apps to manage." },
    ],
    cmp: { eyebrow: "Choose the right fit", heading: "Your files, without a new IT hobby.", intro: "Choose Uncloud for simple household storage. Choose Nextcloud if you want a broader workspace and control over its server setup.", caption: "Uncloud vs. self-hosted Nextcloud", captionSub: "Nextcloud also offers hosted providers", colA: "Nextcloud", colB: "Uncloud", align: "left", rows: [
      { label: "Focus", a: "Files, office, calendars, chat and apps", b: "Your household’s files" },
      { label: "Setup", a: "Set up a server with a guided installer", b: "Install an app; 5 minutes or less" },
      { label: "Away from home", a: "Configure server access or choose a provider", b: "Turn on built-in remote access" },
      { label: "Storage", a: "Your own drives or a hosting plan", b: "Your computer and connected drives" },
      { label: "Price", a: "Free software; hardware or hosting extra", b: "$79 once with early access ($99 regular)" },
    ], boxBig: "5 minutes or less", boxSmall: "to set up Uncloud on a Mac, PC or Linux computer", source: "https://nextcloud.com/install/", sourceLabel: "Nextcloud setup options" },
    sw: { eyebrow: "Switch without the project", heading: "Bring your household’s files home.", intro: "Uncloud copies your files into each person’s private space.",
      steps: [
        { title: "Install Uncloud", body: "Use your own computer and choose where your files live." },
        { title: "Add your Nextcloud files", body: "Choose the local folder your Nextcloud desktop app keeps on your computer." },
        { title: "Invite your household", body: "Give each person their own account and private space." },
      ] },
    faqHeading: "Before you choose",
    faq: [
      { q: "When is Nextcloud the better fit?", a: "When you want office documents, calendars, contacts, chat and a large app ecosystem. Its All-in-One installer simplifies self-hosting, and hosted providers can handle the server for you." },
      { q: "Is Uncloud built on Nextcloud?", a: "No. Uncloud is its own app, focused on household file storage. It uses Syncthing for laptop sync and Tailscale for secure remote access." },
      { q: "Do I need a domain name or open router ports?", a: "No. Turn on remote access in Uncloud to get a secure address for your files." },
      A.family, A.cost,
    ],
    close: "Your household’s files. Your time back.",
  },
};

export const SLUGS = Object.keys(PAGES);

const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
// JSON inside <script> must not be able to close the element.
const jsonLd = (data) => JSON.stringify(data, null, 2).replace(/</g, "\\u003c");
const indent = (text, n) => text.split("\n").map((line) => (line ? " ".repeat(n) + line : line)).join("\n");

const YOUTUBE = '<svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true" focusable="false"><path fill="currentColor" d="M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z"/></svg>';
const INSTAGRAM = '<svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true" focusable="false"><g fill="none" stroke="currentColor" stroke-width="2.2"><rect x="2.6" y="2.6" width="18.8" height="18.8" rx="5.4"/><circle cx="12" cy="12" r="4.4"/></g><circle cx="17.5" cy="6.5" r="1.35" fill="currentColor"/></svg>';
const X = '<svg viewBox="-1.5 -1.5 27 27" width="22" height="22" aria-hidden="true" focusable="false"><path fill="currentColor" d="M18.901 1.153h3.68l-8.04 9.19L24 22.846h-7.406l-5.8-7.584-6.638 7.584H.474l8.6-9.83L0 1.154h7.594l5.243 6.932ZM17.61 20.644h2.039L6.486 3.24H4.298Z"/></svg>';
const GITHUB = '<svg viewBox="0 0 16 16" width="22" height="22" aria-hidden="true" focusable="false"><path fill="currentColor" d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/></svg>';

const WIDGET = '<div class="launchlist-widget" data-key-id="IH5CSi"></div>';
const ACCESS = `<p class="access-offer">Join early access to <strong>lock in $79 at launch.</strong></p>
${WIDGET}
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
  const reasons = p.why.map((r) => `<li>
  <span class="reason-check" aria-hidden="true">✓</span>
  <div><h3>${esc(r.title)}</h3><p>${esc(r.body)}</p></div>
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
    <meta property="og:image:alt" content="Uncloud — cloud storage without the monthly bill. Use your own computer. Move your files over easily." />
    <meta name="twitter:card" content="summary_large_image" />
    <meta name="twitter:title" content="${esc(p.title)}" />
    <meta name="twitter:description" content="${esc(p.description)}" />
    <meta name="twitter:image" content="${ORIGIN}/social-card.png" />
    <meta name="twitter:image:alt" content="Uncloud — cloud storage without the monthly bill. Use your own computer. Move your files over easily." />
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
  <body class="acquisition">
    <a class="skip-link" href="#main">Skip to content</a>
    <div class="page">
      <header class="site-header">
        <a class="wordmark" href="/" aria-label="Uncloud home">
          <img src="./uncloud.png" width="32" height="32" alt="" />
          Uncloud
        </a>
        <div class="header-actions">
          <!-- GA4 enhanced measurement records click + link_url; this ID supplies placement via link_id. -->
          <a id="github-header" class="github-link" href="https://github.com/ethanteng/homebase" aria-label="Uncloud on GitHub">
            ${GITHUB}
          </a>
          <a class="space-link" href="/hardware">Need more space?</a>
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
            <p class="hero-detail">${esc(p.detail)}</p>${p.monthly ? '\n            <p class="compare-setup"><span aria-hidden="true">✓</span> Set up in <strong>5 minutes or less.</strong></p>' : ""}
          </div>

          <aside class="why-uncloud" aria-labelledby="why-heading">
            <div class="why-heading">
              <h2 id="why-heading">Why choose Uncloud?</h2>
              <div class="solution-computer" aria-hidden="true">
                <div class="solution-screen"><img src="./uncloud.png" width="64" height="64" alt="" /></div>
                <div class="solution-base"></div>
              </div>
            </div>
            <ul class="why-reasons" role="list">
${indent(reasons, 14)}
            </ul>
            <p class="compare-platforms">
              <span><span class="platform-mark platform-mac" aria-hidden="true"></span>Mac</span>
              <span><span class="platform-mark platform-pc" aria-hidden="true"></span>PC</span>
              <span><span class="platform-mark platform-linux" aria-hidden="true"></span>Linux</span>
            </p>
          </aside>

          <div class="compare-offer">
            <div>
              <p class="compare-price"><strong>$79</strong><span>one-time<small><s>$99</s> regular</small></span></p>
              <p class="compare-lock">Early access locks in $79 at launch.</p>
            </div>
            <div class="compare-signup">
              ${WIDGET}
              <p>No payment today.</p>
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
              <thead><tr><th scope="col">${p.monthly ? "Over" : "Compare"}</th><th scope="col">${esc(p.cmp.colA)}</th><th scope="col">${esc(p.cmp.colB)}</th></tr></thead>
              <tbody>
${indent(rows, 16)}
              </tbody>
            </table>
            <p class="annual-total"><strong>${esc(p.cmp.boxBig)}</strong> <span>${esc(p.cmp.boxSmall)}</span></p>
            <p class="comparison-source"><a href="${esc(p.cmp.source)}">${esc(p.cmp.sourceLabel)} <span aria-hidden="true">↗</span></a></p>
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

        <section class="video" id="video" aria-labelledby="video-heading">
          <div class="video-card">
            <div class="video-card__header">
              <p class="eyebrow">Cloud storage without the monthly bill.</p>
              <h2 id="video-heading">Meet Uncloud Home</h2>
            </div>
            <div class="video-card__player">
              <iframe src="/video/embed" title="Uncloud Home teaser video" loading="lazy" allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen referrerpolicy="strict-origin-when-cross-origin"></iframe>
            </div>
            <div class="video-card__footer">
              <p>Use your own computer. Skip the server project.</p>
              <a href="/video" target="_blank" rel="noopener noreferrer">Watch in a new tab <span aria-hidden="true">↗</span></a>
            </div>
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
              <p class="price-promise">No monthly storage fees.</p>
              <a class="primary-link" href="#early-access">Get early access</a>
              <p class="trial-note">30-day free trial. No credit card.</p>
              <p class="access-note">Early access locks in $79 at launch. No payment today.</p>
            </div>
            <div class="pricing-includes">
              <h3>Included for your household</h3>
              <ul class="included-features" role="list">
                <li>Unlimited household members and devices</li>
                <li>Separate accounts and files for everyone</li>
                <li>Access your files from anywhere</li>
                <li>File migration from Dropbox, Google Drive &amp; more</li>
                <li>Software updates included</li>
                <li>1 primary computer</li>
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
          <div class="join-action" id="early-access">
${indent(ACCESS, 12)}
          </div>
        </section>
      </main>

      <footer class="site-footer">
${indent(compareNav(slug), 8)}
        <div class="footer-links">
          <a class="partners-link" href="/hardware">Buy more storage</a>
          <a class="partners-link" href="/partners">For Partners</a>
          <span class="social-links">
            <a id="instagram-footer" class="instagram-link" href="https://www.instagram.com/uncloudlife/" aria-label="Uncloud on Instagram">
              ${INSTAGRAM}
            </a>
            <a id="x-footer" class="x-link" href="https://x.com/uncloudlife" aria-label="Uncloud on X">
              ${X}
            </a>
            <a id="youtube-footer" class="youtube-link" href="https://www.youtube.com/@Uncloud-life" aria-label="Uncloud on YouTube">
              ${YOUTUBE}
            </a>
            <a id="github-footer" class="github-link" href="https://github.com/ethanteng/homebase" aria-label="Uncloud on GitHub">
              ${GITHUB}
            </a>
          </span>
        </div>
        <div class="footer-legal">
          <p>© 2026 Ethan Teng Consulting LLC</p>
          <a href="/privacy">Privacy</a>
          <a href="/terms">Terms</a>
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
