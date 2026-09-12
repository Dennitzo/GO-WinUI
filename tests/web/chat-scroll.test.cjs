const assert = require("node:assert/strict");
const test = require("node:test");
const create = require("../../src/GoWinUI.App/Assets/Web/chat-scroll.js");

function harness(reduced = false) {
  const handlers = {};
  const button = { hidden: true, addEventListener: (name, fn) => { handlers[`button:${name}`] = fn; } };
  const scroller = { scrollHeight: 1200, clientHeight: 400, scrollTop: 800,
    addEventListener: (name, fn) => { handlers[name] = fn; } };
  let counter = 0;
  const frames = new Map();
  let observer;
  const host = { requestAnimationFrame: fn => { frames.set(++counter, fn); return counter; },
    cancelAnimationFrame: id => frames.delete(id), matchMedia: () => ({ matches: reduced }),
    ResizeObserver: class { constructor(fn) { observer = fn; } observe() {} } };
  const controller = create({ scroller, content: {}, button, host });
  return { scroller, button, controller, handlers, resize: () => observer(),
    frame(time = 0) { const batch = [...frames.values()]; frames.clear(); batch.forEach(fn => fn(time)); } };
}

test("live growth follows latest output in either chat mode until the user scrolls up", () => {
  const h = harness();
  h.scroller.scrollHeight = 1600;
  h.resize(); h.frame();
  assert.equal(h.scroller.scrollTop, 1600);
  h.handlers.wheel({ deltaY: -100 });
  h.scroller.scrollTop = 500;
  h.handlers.scroll();
  assert.equal(h.button.hidden, false);
  h.scroller.scrollHeight = 2200;
  h.resize(); h.frame();
  assert.equal(h.scroller.scrollTop, 500);
  assert.equal(h.controller.following, false);
});

test("center arrow scrolls smoothly to the changing bottom and resumes following", () => {
  const h = harness();
  h.controller.restore(false);
  h.scroller.scrollTop = 200;
  h.handlers.scroll();
  h.handlers["button:click"]();
  h.frame(0); h.frame(110);
  assert.ok(h.scroller.scrollTop > 200 && h.scroller.scrollTop < 800);
  h.scroller.scrollHeight = 1800;
  h.frame(220); h.frame(221);
  assert.equal(h.scroller.scrollTop, 1800);
  assert.equal(h.controller.following, true);
  assert.equal(h.button.hidden, true);
});

test("manual navigation cancels a pending follow frame or smooth return", () => {
  const h = harness();
  h.controller.refresh();
  h.handlers.wheel({ deltaY: -1 });
  h.scroller.scrollTop = 400;
  h.frame();
  assert.equal(h.scroller.scrollTop, 400);
  h.controller.jump(); h.frame(0); h.frame(50);
  h.handlers.keydown({ key: "PageUp" });
  h.scroller.scrollTop = 100;
  h.frame(300);
  assert.equal(h.scroller.scrollTop, 100);
  assert.equal(h.button.hidden, false);
});

test("reduced motion jumps immediately; reaching bottom manually and restored sessions follow correctly", () => {
  const h = harness(true);
  h.controller.restore(false);
  h.scroller.scrollTop = 100;
  h.resize(); h.frame();
  assert.equal(h.scroller.scrollTop, 100);
  h.controller.jump(); h.frame();
  assert.equal(h.scroller.scrollTop, 1200);
  h.controller.restore(false);
  h.scroller.scrollTop = 800;
  h.handlers.scroll();
  assert.equal(h.controller.following, true);
  assert.equal(h.button.hidden, true);
});
