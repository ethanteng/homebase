const savings = document.querySelector('#savings');

if (savings) {
  const rows = [...savings.querySelectorAll('tbody tr')];
  const monthly = savings.querySelector('[data-savings-monthly]');
  const annual = savings.querySelector('[data-savings-annual]');
  const amounts = rows.map((row) => Math.round(Number(row.querySelector('td').textContent.replace(/[$,]/g, '')) * 100));
  const total = amounts.reduce((sum, amount) => sum + amount, 0);
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const currency = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
  let observer;
  let frame;

  function showTotal(cents) {
    monthly.textContent = currency.format(cents / 100);
    annual.textContent = currency.format(cents * 12 / 100);
  }

  function finish() {
    observer?.disconnect();
    cancelAnimationFrame(frame);
    savings.dataset.savingsState = 'complete';
    rows.forEach((row) => row.removeAttribute('data-savings-row'));
    showTotal(total);
  }

  function play() {
    observer.disconnect();
    savings.dataset.savingsState = 'playing';
    const started = performance.now();
    const stepDuration = 700;

    function tick(now) {
      const progress = (now - started) / stepDuration;
      const step = Math.floor(progress);
      if (step >= rows.length) return finish();

      rows.forEach((row, index) => {
        row.dataset.savingsRow = index < step ? 'added' : index === step ? 'current' : 'waiting';
      });
      // Add each monthly bill in turn; annual costs always reflect the same subtotal.
      const previous = amounts.slice(0, step).reduce((sum, amount) => sum + amount, 0);
      const fraction = 1 - Math.pow(1 - (progress - step), 3);
      showTotal(previous + Math.round(amounts[step] * fraction));
      frame = requestAnimationFrame(tick);
    }
    frame = requestAnimationFrame(tick);
  }

  // Keep the original totals when motion is disabled or enhancement is unavailable.
  if (monthly && annual && rows.length && amounts.every(Number.isFinite) && !reducedMotion.matches && 'IntersectionObserver' in window) {
    savings.dataset.savingsState = 'ready';
    rows.forEach((row) => { row.dataset.savingsRow = 'waiting'; });
    showTotal(0);
    observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting && entry.intersectionRatio >= 0.8)) play();
    }, { threshold: 0.8 });
    observer.observe(savings.querySelector('.savings-example'));

    reducedMotion.addEventListener('change', finish);
    savings.addEventListener('focusin', finish, { once: true });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden && savings.dataset.savingsState === 'playing') finish();
    });
    window.addEventListener('pagehide', finish, { once: true });
  }
}
