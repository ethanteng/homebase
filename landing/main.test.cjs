const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

// The landing page's main.js, with `reserves` reserve buttons on the page, and the teaser's player
// when `teaser` is given. `observers` collects each IntersectionObserver main.js makes.
function page({ reserves = 1, teaser = null, reducedMotion = false, saveData = false, observing = true } = {}) {
  const links = Array.from({ length: reserves }, () => {
    const link = { listeners: {}, addEventListener(type, fn) { this.listeners[type] = fn; } };
    link.click = () => link.listeners.click();
    return link;
  });
  const listeners = {};
  const on = (type, fn) => { listeners[type] = fn; };
  const observers = [];
  class IntersectionObserver {
    constructor(callback, options) { Object.assign(this, { callback, options, targets: [], disconnected: false }); observers.push(this); }
    observe(target) { this.targets.push(target); }
    disconnect() { this.disconnected = true; }
  }
  const window = {
    dataLayer: [],
    matchMedia: () => ({ matches: reducedMotion, addEventListener() {} }),
    addEventListener: on,
    navigator: { connection: { saveData } },
    ...(observing ? { IntersectionObserver } : {}),
  };
  vm.runInNewContext(fs.readFileSync(__dirname + '/main.js', 'utf8'), {
    document: {
      querySelectorAll: (selector) => selector === '[data-reserve]' ? links : [],
      querySelector: (selector) => selector === '.video-card__player iframe' ? teaser : null,
      addEventListener: on,
    },
    window,
  });
  return {
    observers,
    /** Tells main.js's observer the teaser is (or isn't) half on screen. */
    see: (isIntersecting = true) => observers[0].callback([{ isIntersecting, target: teaser }]),
    links,
    events: () => window.dataLayer.map(e => e.event),
  };
}

test('a click on a reserve button records cta_click', () => {
  const p = page();
  assert.deepEqual(p.events(), []);
  p.links[0].click();
  assert.deepEqual(p.events(), ['cta_click']);
});

test('with three reserve buttons on the page, any one counts, once per page view', () => {
  const p = page({ reserves: 3 });
  p.links[2].click();
  p.links[2].click();
  p.links[0].click();
  assert.deepEqual(p.events(), ['cta_click']);
});

test('the teaser starts, muted, once half of it is on screen', () => {
  const teaser = { src: '/video/embed' };
  const p = page({ teaser });
  assert.equal(p.observers.length, 1);
  assert.equal(p.observers[0].options.threshold, 0.5);
  assert.deepEqual(p.observers[0].targets, [teaser]);
  p.see(false);
  assert.equal(teaser.src, '/video/embed');
  p.see();
  assert.equal(teaser.src, '/video/embed?autoplay=1');
  assert.ok(p.observers[0].disconnected);
});

test('the teaser waits for Play for anyone who asks for less motion or data', () => {
  for (const options of [{ reducedMotion: true }, { saveData: true }, { observing: false }]) {
    const teaser = { src: '/video/embed' };
    const p = page({ teaser, ...options });
    assert.equal(p.observers.length, 0, JSON.stringify(options));
    assert.equal(teaser.src, '/video/embed');
  }
});

test('a page without the teaser watches for nothing', () => {
  assert.equal(page().observers.length, 0);
});
