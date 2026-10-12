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

// "Reserve your spot" leaves for Buy Me a Coffee, which takes the $5 reservation; whether it was
// paid never comes back to this page. A click on any of the page's reserve buttons is the cta_click,
// once per page view. GA4's enhanced measurement records the outbound click itself as well.
const reserveLinks = [...document.querySelectorAll("[data-reserve]")];
let reserveClicked = false;

for (const link of reserveLinks) {
  link.addEventListener("click", () => {
    if (reserveClicked) return;
    reserveClicked = true;
    window.dataLayer = window.dataLayer || [];
    window.dataLayer.push({ event: "cta_click" });
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
