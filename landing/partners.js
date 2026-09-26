// /partners?partner=OWC names the storage company in the proposal line and fills in the form's
// Company field, so a link sent to one partner reads as written for them.
const partner = (new URLSearchParams(location.search).get("partner") || "").trim().slice(0, 60);
const form = document.getElementById("partner-form");

if (partner) {
  const article = document.querySelector("[data-partner-article]");
  if (article) article.textContent = `${/^[aeiou]/i.test(partner) ? "an" : "a"} ${partner}`;
  if (form) form.elements.company.value = partner;
}

if (form) {
  const error = form.querySelector(".partner-error");
  const submit = form.querySelector('button[type="submit"]');
  const chips = [...form.querySelectorAll(".partner-chips button")];
  const thanks = document.querySelector(".partner-thanks");
  let sent = false;

  for (const chip of chips) {
    chip.addEventListener("click", () => {
      chip.setAttribute("aria-pressed", String(chip.getAttribute("aria-pressed") !== "true"));
    });
  }

  function show(message) {
    error.textContent = message;
    error.hidden = !message;
  }

  // Typing clears a stale message, as in the design.
  form.addEventListener("input", () => show(""));

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (sent) return;
    const fields = form.elements;
    const name = fields.name.value.trim();
    const email = fields.email.value.trim();
    if (!name) return show("Please add your name.");
    if (!/^\S+@\S+\.\S+$/.test(email)) return show("Please add a valid work email.");

    show("");
    submit.disabled = true;
    submit.textContent = "Sending…";
    try {
      const response = await fetch("/api/partner-interest", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name,
          email,
          company: fields.company.value.trim(),
          note: fields.note.value.trim(),
          tests: chips.filter((c) => c.getAttribute("aria-pressed") === "true").map((c) => c.value),
          partner,
          website: fields.website.value
        })
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.error || "That didn’t go through. Try again, or email ethan@uncloud.life.");
    } catch (failure) {
      submit.disabled = false;
      submit.textContent = "Send";
      // A network failure has no message worth showing; the server's own messages are written for visitors.
      return show(failure instanceof TypeError ? "That didn’t go through. Try again, or email ethan@uncloud.life." : failure.message);
    }

    sent = true;
    window.dataLayer = window.dataLayer || [];
    window.dataLayer.push({ event: "partner_interest" });
    thanks.querySelector("[data-first-name]").textContent = name.split(/\s+/)[0];
    form.hidden = true;
    thanks.hidden = false;
    thanks.focus();
  });
}
