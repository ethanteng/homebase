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
// Acquisition pages carry the widget twice, at the top and at the close; either one counts.
const widgets = [...document.querySelectorAll(".launchlist-widget")];

if (widgets.length) {
  const events = new Map([
    ["uncloud:signup_attempt", "cta_click"],
    ["uncloud:signup_sent", "generate_lead"],
  ]);
  const measured = new Set();
  const frames = () => widgets.map((widget) => widget.querySelector("iframe")).filter(Boolean);

  window.addEventListener("message", (message) => {
    if (message.origin !== "https://getlaunchlist.com" || !frames().some((frame) => message.source === frame.contentWindow)) return;
    const event = events.get(message.data && message.data.type);
    // The widget stays after a signup, so a second address is possible; count each event once.
    if (!event || measured.has(event)) return;
    measured.add(event);
    window.dataLayer = window.dataLayer || [];
    window.dataLayer.push({ event });
  });

  // widget.js adds its iframe without a title, which leaves screen readers announcing a nameless frame.
  document.addEventListener("DOMContentLoaded", () => {
    for (const frame of frames()) frame.title = "Early access signup";
  });
}
