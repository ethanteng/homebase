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
// The homepage carries the widget twice, in the hero and at the close; either one counts.
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

// The teaser starts by itself, muted, the first time half of it is on screen, so nobody scrolls
// down to find it already halfway through. The page loads the plain player; this swaps in
// /video/embed?autoplay=1 then, and api/video.js makes that a muted autoplay, since no browser lets
// a page start sound on its own. Skipped for anyone who asks for reduced motion or to save data,
// and without JavaScript the player simply waits for Play.
const teaser = document.querySelector(".video-card__player iframe");

if (teaser && window.IntersectionObserver && !reducedMotion.matches && !window.navigator?.connection?.saveData) {
  const watcher = new window.IntersectionObserver((entries) => {
    if (!entries.some((entry) => entry.isIntersecting)) return;
    watcher.disconnect();
    teaser.src = "/video/embed?autoplay=1";
  }, { threshold: 0.5 });
  watcher.observe(teaser);
}
