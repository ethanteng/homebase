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

// The signup form is LaunchList's widget, an iframe from another origin. The head code kept in
// launchlist-head.html runs inside it and posts an event name here; nothing typed ever crosses.
const widget = document.querySelector(".launchlist-widget");

if (widget) {
  const events = new Map([
    ["uncloud:signup_attempt", "cta_click"],
    ["uncloud:signup_sent", "generate_lead"],
  ]);
  const measured = new Set();

  window.addEventListener("message", (message) => {
    const frame = widget.querySelector("iframe");
    if (message.origin !== "https://getlaunchlist.com" || !frame || message.source !== frame.contentWindow) return;
    const event = events.get(message.data && message.data.type);
    // The widget stays after a signup, so a second address is possible; count each event once.
    if (!event || measured.has(event)) return;
    measured.add(event);
    window.dataLayer = window.dataLayer || [];
    window.dataLayer.push({ event });
  });

  // widget.js adds its iframe without a title, which leaves screen readers announcing a nameless frame.
  document.addEventListener("DOMContentLoaded", () => {
    const frame = widget.querySelector("iframe");
    if (frame) frame.title = "Early access signup";
  });
}
