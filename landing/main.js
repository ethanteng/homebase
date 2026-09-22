const names = [...document.querySelectorAll(".service-name")];
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

if (names.length > 1) {
  let active = 0;
  let timer;

  function stop() {
    window.clearInterval(timer);
  }

  function update() {
    stop();
    if (!reducedMotion.matches && !document.hidden) {
      timer = window.setInterval(() => {
        names[active].classList.remove("is-active");
        active = (active + 1) % names.length;
        names[active].classList.add("is-active");
      }, 3000);
    }
  }

  reducedMotion.addEventListener("change", update);
  document.addEventListener("visibilitychange", update);
  window.addEventListener("pagehide", stop);
  window.addEventListener("pageshow", update);
  update();
}

const form = document.querySelector("#access-form");
const status = document.querySelector("#access-status");

if (form && status) {
  const field = form.querySelector("#access-email");
  const submit = form.querySelector("button[type=submit]");
  let intentMeasured = false;
  let submitting = false;

  function measure(event) {
    // Never put email, form values, or API error text in the data layer.
    window.dataLayer = window.dataLayer || [];
    window.dataLayer.push({ event });
  }

  function say(message, tone) {
    status.textContent = message;
    status.dataset.tone = tone;
  }

  // Where this visitor came from, recorded alongside the signup because it is
  // gone the moment the page is. This goes to the signup record, never to the
  // data layer, and never carries anything the visitor typed.
  function origin() {
    try {
      const params = new URLSearchParams(window.location.search);
      return { source: params.get("utm_source") || "", referrer: document.referrer || "" };
    } catch {
      return {};
    }
  }

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (submitting || form.hidden) return;
    if (!intentMeasured) {
      measure("cta_click");
      intentMeasured = true;
    }
    const email = field.value.trim();
    if (!email) {
      say("Enter your email address.", "problem");
      field.focus();
      return;
    }

    submitting = true;
    submit.disabled = true;
    field.readOnly = true;
    say("Sending…", "working");

    try {
      const response = await fetch("/api/subscribe", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          email,
          website: form.querySelector("#access-website").value,
          ...origin(),
        }),
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.error || "That didn’t go through.");

      // A successful HTTP response can also mean a bot trap or a repeat.
      if (result.accepted === true) {
        measure("generate_lead");
      }

      // The form has done its job; leaving it there invites a second submission.
      form.hidden = true;
      // role="status" announces the change; a <p> can't take focus anyway.
      say("You’re on the list. We’ll be in touch.", "done");
    } catch (problem) {
      submitting = false;
      say(problem.message || "That didn’t go through. Try again in a moment.", "problem");
      submit.disabled = false;
      field.readOnly = false;
      field.focus();
    }
  });
}
