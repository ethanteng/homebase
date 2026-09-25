const comparison = document.querySelector('#home-comparison');

if (comparison) {
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  let observer;

  function finish() {
    comparison.dataset.state = 'complete';
  }

  function play() {
    if (reducedMotion.matches) return finish();
    comparison.dataset.state = 'playing';
  }

  comparison.addEventListener('animationend', (event) => {
    if (event.animationName === 'focus-uncloud') finish();
  });

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
  });
  window.addEventListener('pagehide', finish);
}
