const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

const LAUNCHLIST = 'https://getlaunchlist.com';

// The landing page's main.js, with each widget's iframe already in place, and the teaser's player
// when `teaser` is given. `observers` collects each IntersectionObserver main.js makes.
function page({ widgets = 1, teaser = null, reducedMotion = false, saveData = false, observing = true } = {}) {
  const frames = Array.from({ length: widgets }, () => ({ contentWindow: {} }));
  const elements = frames.map((frame) => ({ querySelector: (selector) => selector === 'iframe' ? frame : null }));
  const frame = frames[0];
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
      querySelectorAll: (selector) => selector === '.launchlist-widget' ? elements : [],
      querySelector: (selector) => selector === '.video-card__player iframe' ? teaser : null,
      addEventListener: on,
    },
    window,
  });
  return {
    observers,
    /** Tells main.js's observer the teaser is (or isn't) half on screen. */
    see: (isIntersecting = true) => observers[0].callback([{ isIntersecting, target: teaser }]),
    frame,
    frames,
    events: () => window.dataLayer.map(e => e.event),
    post: (type, { origin = LAUNCHLIST, source = frame.contentWindow, data = { type } } = {}) =>
      listeners.message({ origin, source, data }),
    ready: () => listeners.DOMContentLoaded(),
  };
}

// launchlist-head.html's script, inside a stand-in for the widget's iframe.
function widget({ postMessage } = {}) {
  const posted = [];
  const sent = [];
  const listeners = {};
  const email = { attributes: {}, setAttribute(name, value) { this.attributes[name] = value; } };
  function HTMLFormElement() {}
  HTMLFormElement.prototype.submit = function () { sent.push(this); };
  const self = {};
  self.parent = { postMessage: postMessage || ((data, origin) => posted.push({ data, origin })) };
  const script = fs.readFileSync(__dirname + '/launchlist-head.html', 'utf8').match(/<script>([\s\S]*)<\/script>/)[1];
  vm.runInNewContext(script, {
    window: self,
    document: {
      addEventListener: (type, fn, capture) => { listeners[type] = { fn, capture }; },
      getElementById: (id) => id === 'll_email' ? email : null,
    },
    HTMLFormElement,
  });
  const form = new HTMLFormElement();
  return {
    posted, sent, listeners, email,
    attempt: () => listeners.submit.fn({ target: form }),
    send: () => form.submit(),
    ready: () => listeners.DOMContentLoaded.fn(),
    form,
  };
}

test('an attempt and a sent signup record cta_click then generate_lead, once each', () => {
  const p = page();
  p.post('uncloud:signup_attempt');
  p.post('uncloud:signup_attempt');
  p.post('uncloud:signup_sent');
  p.post('uncloud:signup_attempt');
  p.post('uncloud:signup_sent');
  assert.deepEqual(p.events(), ['cta_click', 'generate_lead']);
});

test('an attempt LaunchList rejects records intent and never a lead', () => {
  const p = page();
  p.post('uncloud:signup_attempt');
  assert.deepEqual(p.events(), ['cta_click']);
});

test('messages from anywhere but the widget are ignored', () => {
  const p = page();
  p.post('uncloud:signup_sent', { origin: 'https://evil.example' });
  p.post('uncloud:signup_sent', { source: {} });
  p.post('uncloud:signup_sent', { origin: 'https://getlaunchlist.com.evil.example' });
  assert.deepEqual(p.events(), []);
});

test('the widget\'s other messages, and anything malformed, are ignored', () => {
  const p = page();
  p.post('launchlist:resize');
  p.post('constructor');
  p.post('toString');
  p.post(null, { data: null });
  p.post(null, { data: 'uncloud:signup_sent' });
  assert.deepEqual(p.events(), []);
});

test('with two widgets on the page, either one counts, still once per event', () => {
  const p = page({ widgets: 2 });
  p.post('uncloud:signup_attempt', { source: p.frames[1].contentWindow });
  p.post('uncloud:signup_sent', { source: p.frames[1].contentWindow });
  p.post('uncloud:signup_sent', { source: p.frames[0].contentWindow });
  assert.deepEqual(p.events(), ['cta_click', 'generate_lead']);
});

test('every widget\'s iframe gets a name', () => {
  const p = page({ widgets: 2 });
  p.ready();
  assert.deepEqual(p.frames.map((f) => f.title), ['Early access signup', 'Early access signup']);
});

test('the widget\'s iframe gets a name', () => {
  const p = page();
  p.ready();
  assert.equal(p.frame.title, 'Early access signup');
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

test('the head code announces an attempt before LaunchList validates it', () => {
  const w = widget();
  assert.equal(w.listeners.submit.capture, true);
  w.attempt();
  // The objects come from the script's own realm; compare them as plain data.
  assert.deepEqual(JSON.parse(JSON.stringify(w.posted)), [{ data: { type: 'uncloud:signup_attempt' }, origin: '*' }]);
  assert.equal(w.sent.length, 0);
});

test('the head code announces a sent signup and still sends it', () => {
  const w = widget();
  w.attempt();
  w.send();
  assert.deepEqual(w.posted.map(m => m.data.type), ['uncloud:signup_attempt', 'uncloud:signup_sent']);
  assert.deepEqual(w.sent, [w.form]);
});

test('the head code never sends anything but an event name', () => {
  const w = widget();
  w.attempt();
  w.send();
  for (const { data } of w.posted) assert.deepEqual(Object.keys(data), ['type']);
});

test('a failing postMessage never stops the signup', () => {
  const w = widget({ postMessage: () => { throw new Error('detached'); } });
  w.attempt();
  w.send();
  assert.deepEqual(w.sent, [w.form]);
});

test('the head code names the email field and lets browsers fill it', () => {
  const w = widget();
  w.ready();
  assert.deepEqual(w.email.attributes, { 'aria-label': 'Your email address', autocomplete: 'email' });
});
