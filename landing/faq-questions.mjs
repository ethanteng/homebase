// The /faq questions and answers.
// Edit them here, then run `node landing/faq-questions.mjs` to rewrite the question list and its
// FAQPage structured data in landing/faq.html, and commit both. landing/faq.test.cjs fails when
// they are out of step.
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// Each answer is a list of paragraphs.
export const QUESTIONS = [
  {
    q: "What happens when my computer is off or asleep and I need a file from my phone?",
    a: [
      "Uncloud serves your files from your host computer, so for access around the clock, keep the host on and awake. It doesn’t need to be a powerful machine: an old laptop that stays plugged in, or a desktop you already leave on, works great.",
      "It only takes a minute to set up. On a Mac, turn off sleep in System Settings and tick Open at Login in the Uncloud menu, so Uncloud starts again on its own after a restart.",
      "And if your laptop keeps a folder in sync with Uncloud, those files live on your laptop too, so you can open them even while the host is off. Any changes catch up as soon as it’s back.",
    ],
  },
  {
    q: "What happens when my internet provider gives me a new IP address?",
    a: [
      "Nothing you need to worry about. Uncloud uses Tailscale under the hood for its secure connection, and Tailscale finds your host again automatically whenever your IP address changes.",
      "Your Uncloud address stays the same, so everyone in your household keeps using the same link at home and on the go. There’s no domain name to buy, no router settings to change and no ports to open.",
    ],
  },
  {
    q: "If my computer’s disk dies, are my files gone?",
    a: [
      "Not if you back them up, and that part is easy. Uncloud keeps your files on your host computer, so look after it like any computer that holds things you care about. On a Mac, turn on Time Machine with a second drive and make sure it includes your Uncloud folder.",
      "Laptops that keep a folder in sync have their own copy too, which helps. A separate backup is what protects you from a disk failure or a file deleted by mistake.",
      "We want backup to be effortless, so we’re in discussions with a leading offsite secure backup & recovery partner to offer it as a value-add through Uncloud. Details are still to be decided, and we hope to share good news soon.",
    ],
  },
  {
    q: "Does Uncloud replace a notes app like Evernote?",
    a: [
      "Not yet, but it’s on our list. Turning notes into everyday files you can open, search and keep for good is a problem we want to solve, for our own notes too.",
      "Right now we’re focused on making the move from Dropbox and Google Drive as smooth as possible. In the meantime, you can export your notes from Evernote and keep the export safe in Uncloud.",
      "Have an idea for how notes should work? We’d love to hear it at hello@uncloud.life.",
    ],
  },
];

const ORIGIN = "https://www.uncloud.life";
const PAGE = `${ORIGIN}/faq`;

const esc = (s) => String(s).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");
const unesc = (s) => s.replaceAll("&quot;", '"').replaceAll("&gt;", ">").replaceAll("&lt;", "<").replaceAll("&amp;", "&");
// JSON inside <script> must not be able to close the element.
const jsonLd = (data) => JSON.stringify(data, null, 2).replace(/</g, "\\u003c");
const indent = (text, n) => text.split("\n").map((line) => (line ? " ".repeat(n) + line : line)).join("\n");

// The page's own title and description name the WebPage, so they are written once, in faq.html.
function structuredData(page) {
  const name = unesc(page.match(/<title>(.*?)<\/title>/)[1]);
  const description = unesc(page.match(/<meta name="description" content="(.*?)" \/>/)[1]);
  return {
    "@context": "https://schema.org",
    "@graph": [
      { "@type": "WebPage", "@id": `${PAGE}#webpage`, url: PAGE, name, description, inLanguage: "en", isPartOf: { "@id": `${ORIGIN}/#website` }, about: { "@id": `${ORIGIN}/#software` }, breadcrumb: { "@id": `${PAGE}#breadcrumb` }, image: `${ORIGIN}/social-card.png` },
      { "@type": "BreadcrumbList", "@id": `${PAGE}#breadcrumb`, itemListElement: [
        { "@type": "ListItem", position: 1, name: "Uncloud", item: `${ORIGIN}/` },
        { "@type": "ListItem", position: 2, name: "FAQ", item: PAGE },
      ] },
      { "@type": "FAQPage", "@id": `${PAGE}#faq`, mainEntity: QUESTIONS.map((f) => ({ "@type": "Question", name: f.q, acceptedAnswer: { "@type": "Answer", text: f.a.join(" ") } })) },
      { "@type": "WebSite", "@id": `${ORIGIN}/#website`, name: "Uncloud", url: `${ORIGIN}/` },
      { "@type": "SoftwareApplication", "@id": `${ORIGIN}/#software`, name: "Uncloud Home", url: `${ORIGIN}/`, applicationCategory: "UtilitiesApplication", applicationSubCategory: "Private cloud storage", operatingSystem: ["macOS", "Windows", "Linux"] },
    ],
  };
}

const renderList = () => QUESTIONS.map((f) => `<details>
  <summary><h2>${esc(f.q)}</h2><span class="faq-sign" aria-hidden="true">+</span></summary>
${f.a.map((para) => `  <p>${esc(para)}</p>`).join("\n")}
</details>`).join("\n");

export const file = fileURLToPath(new URL("./faq.html", import.meta.url));
const LD = ["    <!-- ld:start (generated by faq-questions.mjs) -->\n", "    <!-- ld:end -->\n"];
const LIST = ["            <!-- questions:start (generated by faq-questions.mjs) -->\n", "            <!-- questions:end -->\n"];

function between(page, [start, end], content) {
  const a = page.indexOf(start), b = page.indexOf(end);
  if (a < 0 || b < a) throw new Error(`faq.html lacks the ${start.trim()} / ${end.trim()} markers`);
  return page.slice(0, a + start.length) + content + page.slice(b);
}

export function render(page = readFileSync(file, "utf8")) {
  const ld = `    <script type="application/ld+json">\n${indent(jsonLd(structuredData(page)), 6)}\n    </script>\n`;
  return between(between(page, LD, ld), LIST, indent(renderList(), 12) + "\n");
}

if (process.argv[1] === fileURLToPath(import.meta.url)) writeFileSync(file, render());
