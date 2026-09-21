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

  function say(message, tone) {
    status.textContent = message;
    status.dataset.tone = tone;
  }

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const email = field.value.trim();
    if (!email) {
      say("Enter your email address.", "problem");
      field.focus();
      return;
    }

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
        }),
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.error || "That didn’t go through.");

      // The form has done its job; leaving it there invites a second submission.
      form.hidden = true;
      // role="status" announces the change; a <p> can't take focus anyway.
      say("You’re on the list. We’ll be in touch.", "done");
    } catch (problem) {
      say(problem.message || "That didn’t go through. Try again in a moment.", "problem");
      submit.disabled = false;
      field.readOnly = false;
      field.focus();
    }
  });
}
