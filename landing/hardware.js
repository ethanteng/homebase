// /hardware: the household picker chooses a suggested capacity, and each drive card follows it.
// The page's markup holds the drives, their sizes and their Newegg URLs; this only keeps the
// selection, the notes and the outbound links in step. Without JavaScript the page shows the
// 8 TB suggestion and every link still works.
//
// Clicks are measured by GA4 enhanced measurement's outbound `click` event, as with the GitHub
// links (see measurement.md): link_id names the card, and link_url's utm_content and utm_term
// carry the drive, the capacity chosen and the capacity suggested.

const rows = [...document.querySelectorAll("[data-household]")];
const cards = [...document.querySelectorAll("[data-drive]")];
const picks = {};
let household = Math.max(0, rows.findIndex((row) => row.getAttribute("aria-checked") === "true"));

const need = () => Number(rows[household].dataset.tb);
const needLabel = () => rows[household].dataset.tbLabel;
const sizes = (card) => [...card.querySelectorAll("[data-tb]")].map((b) => Number(b.dataset.tb));
const fit = (caps, tb) => caps.find((c) => c >= tb) ?? caps[caps.length - 1];

export function link(url, drive, tb, placement, needTb) {
  const target = new URL(url);
  target.searchParams.set("utm_source", "uncloud");
  target.searchParams.set("utm_medium", "hardware_page");
  target.searchParams.set("utm_campaign", "hardware_v1");
  target.searchParams.set("utm_content", `${drive}-${tb}tb-${placement}`);
  target.searchParams.set("utm_term", `need-${needTb}tb`);
  return target.href;
}

function note(caps, picked, needTb) {
  const largest = caps[caps.length - 1];
  if (needTb > largest) return `Tops out at ${largest} TB. A desktop drive holds more.`;
  if (picked < needTb) return `Smaller than the ${needLabel()} we suggest.`;
  if (picked === needTb) return `Matches the ${needLabel()} we suggest.`;
  return `More room than the ${needLabel()} we suggest.`;
}

// One tab stop per radio group: the checked option.
function check(button, on) {
  button.setAttribute("aria-checked", String(on));
  button.tabIndex = on ? 0 : -1;
}

function render() {
  const tb = need();
  rows.forEach((row, i) => check(row, i === household));
  for (const el of document.querySelectorAll("[data-selected-tb]")) el.textContent = needLabel();

  for (const card of cards) {
    const drive = card.dataset.drive;
    const caps = sizes(card);
    const suggested = fit(caps, tb);
    const picked = picks[drive] ?? suggested;
    const isFit = card.dataset.fits.split(" ").includes(String(household));
    const buttons = [...card.querySelectorAll("[data-tb]")];
    card.classList.toggle("is-fit", isFit);
    for (const b of buttons) {
      const size = Number(b.dataset.tb);
      check(b, size === picked);
      b.classList.toggle("is-suggested", size === suggested);
    }
    const chosen = buttons.find((b) => Number(b.dataset.tb) === picked);
    const cta = card.querySelector("[data-cta]");
    cta.href = link(chosen.dataset.url, drive, picked, "card", tb);
    cta.querySelector("[data-cta-label]").textContent = `View ${picked} TB at Newegg`;
    card.querySelector("[data-note]").textContent = note(caps, picked, tb);
  }
}

rows.forEach((row, i) => {
  row.addEventListener("click", () => {
    household = i;
    for (const key of Object.keys(picks)) delete picks[key];
    render();
  });
});

// Arrow keys move between options, as in a native radio group.
function arrows(group, buttons, choose) {
  group.addEventListener("keydown", (event) => {
    const step = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[event.key];
    if (!step) return;
    event.preventDefault();
    const current = buttons.findIndex((b) => b.getAttribute("aria-checked") === "true");
    const next = (current + step + buttons.length) % buttons.length;
    choose(next);
    buttons[next].focus();
  });
}

arrows(rows[0].parentElement, rows, (i) => rows[i].click());

for (const card of cards) {
  const buttons = [...card.querySelectorAll("[data-tb]")];
  for (const b of buttons) {
    b.addEventListener("click", () => {
      picks[card.dataset.drive] = Number(b.dataset.tb);
      render();
    });
  }
  arrows(buttons[0].parentElement, buttons, (i) => buttons[i].click());
}

render();
