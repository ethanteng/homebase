const dialog = document.querySelector("#teaser-modal");
const playerContainer = dialog?.querySelector("[data-teaser-player]");

// Native modal dialogs contain keyboard focus and make the background inert. If unavailable,
// leave the homepage usable, just as it is when JavaScript is disabled.
if (dialog && playerContainer && typeof dialog.showModal === "function") {
  const previousFocus = document.activeElement;
  const returnFocus = previousFocus instanceof HTMLElement && previousFocus !== document.body
    ? previousFocus
    : document.querySelector(".wordmark");

  function dismiss() {
    dialog.close();
  }

  dialog.querySelectorAll("[data-teaser-close]").forEach((button) => {
    button.addEventListener("click", dismiss);
  });

  dialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    dismiss();
  });

  function isBackdrop(event) {
    const bounds = dialog.getBoundingClientRect();
    return event.target === dialog && (
      event.clientX < bounds.left || event.clientX > bounds.right ||
      event.clientY < bounds.top || event.clientY > bounds.bottom
    );
  }

  // A drag that begins in the player or text should not dismiss the modal.
  let startedOnBackdrop = false;
  dialog.addEventListener("pointerdown", (event) => {
    startedOnBackdrop = isBackdrop(event);
  });
  dialog.addEventListener("click", (event) => {
    if (startedOnBackdrop && isBackdrop(event)) dismiss();
    startedOnBackdrop = false;
  });

  dialog.addEventListener("close", () => {
    // Removing the iframe stops both playback and further player work after dismissal.
    playerContainer.replaceChildren();
    document.documentElement.classList.remove("teaser-is-open");
    returnFocus?.focus({ preventScroll: true });
  });
  window.addEventListener("pagehide", dismiss, { once: true });

  // Open once per page load; scrolling and navigating within the page never reopen it.
  dialog.showModal();
  document.documentElement.classList.add("teaser-is-open");

  const player = document.createElement("iframe");
  const autoplay = window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "0" : "1";
  player.src = `https://www.youtube-nocookie.com/embed/oELh5dwlmHs?autoplay=${autoplay}&mute=1&playsinline=1&rel=0`;
  player.title = "Uncloud Home teaser video";
  player.allow = "autoplay; encrypted-media; picture-in-picture; fullscreen";
  player.allowFullscreen = true;
  player.referrerPolicy = "strict-origin-when-cross-origin";
  playerContainer.append(player);
}
