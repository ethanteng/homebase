// The /hardware drive table: OWC drives by size and type.
// Edit the drives here, then run `node landing/hardware-drives.mjs` to rewrite the table in
// landing/hardware.html and commit both. landing/hardware.test.cjs fails when they are out of step.
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// Uncloud's Impact tracking link for OWC (partner 7852392, OWC's default tracking ad, campaign
// 50228), without a query string. Every drive link goes through it with the OWC page as `u` and
// the drive and size as `subId1`, so Impact reports sales by drive and size.
// While it is empty, links go straight to OWC.
export const IMPACT = "https://otherworldcomputing.pxf.io/c/7852392/3834135/50228";

const item = (sku) => `https://eshop.macsales.com/item/OWC/${sku}/`;

export const TYPES = [
  { id: "ssd", label: "Portable SSD", detail: "USB-C, fast and silent, no power cable" },
  { id: "desktop", label: "Desktop drive", detail: "One drive, plugs into power" },
  { id: "dual", label: "Two-drive desktop", detail: "Can mirror its drives, then holds half" },
];

// Each size links to its own OWC product page.
export const DRIVES = [
  { id: "envoy-pro-elektron", type: "ssd", name: "Envoy Pro Elektron", sizes: { 1: item("ENVPK01"), 2: item("ENVPK02"), 4: item("ENVPK04") } },
  { id: "mercury-elite-pro", type: "desktop", name: "Mercury Elite Pro", sizes: {
    2: item("ME3NH7T02"), 4: item("ME3NH7T04"), 8: item("ME3NH7T08"), 12: item("ME3NH7T12"),
    16: item("ME3NH7T16"), 20: item("ME3NH7T20"), 24: item("ME3NH7T24"),
  } },
  { id: "mercury-elite-pro-dual", type: "dual", name: "Mercury Elite Pro Dual", sizes: {
    8: item("MEDCH7T08"), 16: item("MEDCH7T16"), 24: item("MEDCH7T24"),
    32: item("MEDCH7T32"), 40: item("MEDCH7T40"), 48: item("MEDCH7T48"),
  } },
];

export const HINTS = { 2: "Documents and photos", 4: "A family photo and video library", 8: "A large family library", 16: "A serious photo and video collection" };
export const MIN_SIZES = [2, 4, 8, 16, 24];

const esc = (s) => String(s).replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;");

export function link(drive, tb) {
  const page = new URL(drive.sizes[tb]);
  const tag = `${drive.id}-${tb}tb`;
  if (!IMPACT) {
    for (const [k, v] of Object.entries({ utm_source: "uncloud", utm_medium: "hardware_page", utm_campaign: "hardware_v1", utm_content: tag })) page.searchParams.set(k, v);
    return page.href;
  }
  const out = new URL(IMPACT);
  out.searchParams.set("u", page.href);
  out.searchParams.set("subId1", tag);
  return out.href;
}

export const sizes = () => [...new Set(DRIVES.flatMap((d) => Object.keys(d.sizes).map(Number)))].sort((a, b) => a - b);

function cell(type, tb) {
  const here = DRIVES.filter((d) => d.type === type.id && d.sizes[tb]);
  if (!here.length) return `<td class="col-${type.id} drive-none" data-label="${type.label}"><span aria-hidden="true">—</span><span class="sr-only">None</span></td>`;
  const items = here.map((d) => `<li><a href="${esc(link(d, tb))}" target="_blank" rel="sponsored noopener">${esc(d.name)}&nbsp;<span aria-hidden="true">↗</span><span class="sr-only"> (opens in a new tab)</span></a></li>`);
  return `<td class="col-${type.id}" data-label="${type.label}"><ul role="list">${items.join("")}</ul></td>`;
}

export function renderTable() {
  const radio = (name, value, label, checked) => `              <label><input type="radio" name="${name}" value="${value}"${checked ? " checked" : ""} /><span>${label}</span></label>`;
  const rows = sizes().map((tb) => {
    const types = TYPES.filter((t) => DRIVES.some((d) => d.type === t.id && d.sizes[tb])).map((t) => t.id);
    return `                <tr data-tb="${tb}" data-types="${types.join(" ")}">
                  <th scope="row">${tb} TB${HINTS[tb] ? `<span>${HINTS[tb]}</span>` : ""}</th>
${TYPES.map((t) => `                  ${cell(t, tb)}`).join("\n")}
                </tr>`;
  });
  return `          <form class="drive-filters" data-filters hidden aria-label="Filter drives">
            <fieldset>
              <legend>Type</legend>
${[radio("type", "all", "All", true), ...TYPES.map((t) => radio("type", t.id, t.label))].join("\n")}
            </fieldset>
            <fieldset>
              <legend>Space</legend>
${[radio("min", 0, "Any", true), ...MIN_SIZES.map((tb) => radio("min", tb, `${tb} TB+`))].join("\n")}
            </fieldset>
          </form>
          <p class="drive-count" data-count aria-live="polite"></p>
          <div class="drive-table-wrap">
            <table class="drive-table">
              <thead>
                <tr>
                  <th scope="col">Space</th>
${TYPES.map((t) => `                  <th scope="col" class="col-${t.id}">${t.label} <span>${t.detail}</span></th>`).join("\n")}
                </tr>
              </thead>
              <tbody>
${rows.join("\n")}
              </tbody>
            </table>
            <p class="drive-empty" data-empty hidden>No drive of that type holds that much. Try another type or a smaller size.</p>
          </div>
`;
}

export const file = fileURLToPath(new URL("./hardware.html", import.meta.url));
const START = "          <!-- drives:start (generated by hardware-drives.mjs) -->\n";
const END = "          <!-- drives:end -->\n";

export function render(page = readFileSync(file, "utf8")) {
  const a = page.indexOf(START), b = page.indexOf(END);
  if (a < 0 || b < a) throw new Error("hardware.html lacks the drives:start / drives:end markers");
  return page.slice(0, a + START.length) + renderTable() + page.slice(b);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) writeFileSync(file, render());
