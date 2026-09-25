const comparison = document.querySelector('#home-comparison');
const replay = comparison?.querySelector('.replay-animation');

if (comparison && replay) {
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  let observer;

  function finish() {
    comparison.dataset.state = 'complete';
    replay.disabled = false;
  }

  function play() {
    if (reducedMotion.matches) return finish();
    comparison.dataset.state = 'ready';
    // Restart the same short sequence when the visitor chooses Replay.
    void comparison.offsetWidth;
    comparison.dataset.state = 'playing';
    replay.disabled = true;
  }

  comparison.addEventListener('animationend', (event) => {
    if (event.animationName === 'focus-uncloud') finish();
  });
  replay.addEventListener('click', play);
  replay.hidden = reducedMotion.matches;

  if (!reducedMotion.matches && 'IntersectionObserver' in window) {
    comparison.dataset.state = 'ready';
    observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting && entry.intersectionRatio >= 0.65)) {
        observer.disconnect();
        play();
      }
    }, { threshold: 0.65 });
    observer.observe(comparison.querySelector('.comparison-stage'));
  } else {
    finish();
  }

  reducedMotion.addEventListener('change', () => {
    observer?.disconnect();
    finish();
    replay.hidden = reducedMotion.matches;
  });
  window.addEventListener('pagehide', finish);
}
