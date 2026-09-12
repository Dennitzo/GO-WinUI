const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web/app.js"), "utf8");
function harness() {
  const layout = { paneHeight: 800, overhead: 112, contentHeight: 58 };
  const prompt = { value: "", clientWidth: 790, scrollTop: 0, style: { height: "58px" },
    getBoundingClientRect() { return { height: parseFloat(this.style.height) }; },
    get scrollHeight() { return layout.contentHeight; } };
  const elements = { prompt, chatPane: { getBoundingClientRect: () => ({ top: 20, bottom: layout.paneHeight + 20, height: layout.paneHeight }) },
    chatHeader: { getBoundingClientRect: () => ({ height: 56 }) },
    composerRegion: { getBoundingClientRect: () => ({ height: parseFloat(prompt.style.height) + layout.overhead }) } };
  const frames = [];
  const context = vm.createContext({ elements, innerHeight: 1000, getComputedStyle: () => ({ minHeight: "58px" }),
    requestAnimationFrame: fn => { frames.push(fn); return frames.length; } });
  vm.runInContext("let promptResizeFrame = 0;", context);
  for (const name of ["resizePrompt", "schedulePromptResize", "setPromptValue"]) {
    const start = source.indexOf(`  function ${name}(`);
    vm.runInContext(source.slice(start, source.indexOf("\n  function ", start + 1)), context);
  }
  return { context, layout, prompt, frames };
}

test("draft grows to show all lines, then scrolls only within the available window", () => {
  const { context, layout, prompt } = harness();
  context.resizePrompt();
  assert.equal(prompt.style.height, "58px");
  layout.contentHeight = 420;
  context.resizePrompt();
  assert.equal(prompt.style.height, "420px");
  assert.equal(prompt.style.overflowY, "hidden");
  layout.contentHeight = 1400;
  prompt.scrollTop = 200;
  context.resizePrompt();
  assert.equal(prompt.style.height, "620px");
  assert.equal(prompt.style.overflowY, "auto");
  assert.equal(prompt.scrollTop, 200, "resizing a long draft retains its reading position");
  layout.contentHeight = 58;
  context.resizePrompt();
  assert.equal(prompt.style.height, "58px");
  assert.equal(prompt.style.overflowY, "hidden");
  assert.equal(prompt.scrollTop, 0);
});

test("window resize, wrapped controls and the visual viewport reserve actual usable space", () => {
  const { context, layout, prompt } = harness();
  layout.contentHeight = 1600;
  context.resizePrompt();
  layout.paneHeight = 500;
  layout.overhead = 148;
  context.resizePrompt();
  assert.equal(prompt.style.height, "284px");
  context.visualViewport = { offsetTop: 0, height: 430 };
  context.resizePrompt();
  assert.equal(prompt.style.height, "194px");
  layout.paneHeight = 1000;
  delete context.visualViewport;
  layout.overhead = 112;
  context.resizePrompt();
  assert.equal(prompt.style.height, "800px");
});

test("restoring and clearing programmatic drafts coalesces layout work and respects new wrapping", () => {
  const { context, layout, prompt, frames } = harness();
  context.setPromptValue("A restored multiline draft");
  context.schedulePromptResize();
  assert.equal(frames.length, 1);
  layout.contentHeight = 300;
  frames.shift()();
  assert.equal(prompt.value, "A restored multiline draft");
  assert.equal(prompt.style.height, "300px");
  context.setPromptValue("");
  layout.contentHeight = 58;
  frames.shift()();
  assert.equal(prompt.style.height, "58px");
});
