// /hardware: filters for the drive table, by type (a column) and minimum size (rows).
// The form ships hidden, so without JavaScript the page shows the whole table.

const form = document.querySelector("[data-filters]");
const table = document.querySelector(".drive-table");
const rows = [...table.querySelectorAll("tbody tr")];
const count = document.querySelector("[data-count]");
const empty = document.querySelector("[data-empty]");

function apply() {
  const type = form.elements.type.value;
  const min = Number(form.elements.min.value);
  table.dataset.type = type;
  let shown = 0;
  for (const row of rows) {
    const types = row.dataset.types.split(" ");
    const show = Number(row.dataset.tb) >= min && (type === "all" || types.includes(type));
    row.hidden = !show;
    if (show) shown++;
  }
  table.hidden = shown === 0;
  empty.hidden = shown !== 0;
  const filtered = type !== "all" || min > 0;
  count.textContent = filtered ? `Showing ${shown} of ${rows.length} sizes.` : "";
}

form.addEventListener("change", apply);
form.addEventListener("submit", (event) => event.preventDefault());
form.hidden = false;
apply();
