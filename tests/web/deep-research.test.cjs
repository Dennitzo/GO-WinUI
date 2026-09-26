const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
const source = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");

function harness(storage = new Map()) {
  const state = { activeSessionId: "session-a", selectedToolAction: null, persistentToolAction: null,
    deepResearch: false, documents: [], attachments: [], messages: [], isRunning: false, isAiBusy: false };
  const elements = Object.fromEntries(["activeTools", "toolsButton", "prompt", "contextStrip"].map(key => [key, new TestNode("div")]));
  elements.prompt.value = "";
  const body = new TestNode("body");
  const menuItem = new TestNode("button");
  menuItem.dataset.toolToggle = "deepResearch";
  body.append(menuItem, elements.activeTools);
  const posts = [], notices = [];
  const noOp = () => {};
  const context = vm.createContext({ state, elements,
    document: { body, createElement: tag => new TestNode(tag),
      querySelectorAll: selector => body.querySelectorAll(selector), querySelector: selector => body.querySelector(selector) },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    persistentToolActions: new Set(["coding", "audiobook"]),
    toolVisuals: { coding: ["Coding", "code"], webSearch: ["Websuche", "search"], imageAnalysis: ["Bild analysieren", "image"] },
    createToolIcon: () => new TestNode("svg"),
    isAudioCaptureActive: () => false, isScreenClipActive: () => false,
    renderStatus: noOp, renderMessages: noOp, chatScroll: { jump: noOp }, clearTimeout: noOp,
    crypto: { randomUUID: () => "request-1" },
    post: (type, payload) => { posts.push({ type, payload }); return "sent"; },
    showToast: message => notices.push(message),
    setPromptValue: value => { elements.prompt.value = value; }
  });
  context.renderContext = () => { elements.activeTools.replaceChildren(); context.renderDeepResearch(); };
  vm.runInContext("let draftTimer = 0; let pendingDraft = null;", context);
  for (const name of ["normalizeToolAction", "ensureEditableContext", "persistDeepResearch", "restoreDeepResearch",
    "selectDeepResearch", "renderDeepResearch", "selectToolAction", "clearCompletedOneShotToolAction", "postChatRequest", "submitPrompt", "updateContextStripVisibility"]) {
    const start = source.search(new RegExp(`  (?:async )?function ${name}\\(`));
    const ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending, name);
    vm.runInContext(source.slice(start, start + ending.index + ending[0].length), context);
  }
  return { context, state, elements, menuItem, storage, posts, notices };
}

test("Deep Research adds a real request flag without replacing General or Coding mode, and deselection clears it", async () => {
  for (const mode of [null, "coding"]) {
    const { context, state, elements, posts, menuItem } = harness();
    state.selectedToolAction = state.persistentToolAction = mode;
    context.selectDeepResearch(true);
    assert.equal(state.selectedToolAction, mode);
    assert.equal(menuItem.getAttribute("aria-checked"), "true");
    assert.equal(menuItem.classList.contains("active"), true);
    assert.equal(elements.activeTools.querySelectorAll(".active-tool-chip").length, 1);
    context.updateContextStripVisibility();
    assert.equal(elements.contextStrip.hidden, false, "General research is visible even without another tool or attachment");
    elements.prompt.value = "Recherchiere das Thema mit mehreren Quellen.";
    await context.submitPrompt();
    assert.equal(posts.at(-1).type, "chat.send");
    assert.equal(posts.at(-1).payload.deepResearch, true);
    assert.equal(posts.at(-1).payload.toolAction, mode);
    assert.equal(posts.at(-1).payload.sessionId, "session-a");
    state.pendingChatSend = null;
    await elements.activeTools.querySelector("button").dispatch("click");
    assert.equal(menuItem.getAttribute("aria-checked"), "false");
    assert.equal(elements.activeTools.children.length, 0);
    elements.prompt.value = "Beantworte jetzt eine normale Frage.";
    await context.submitPrompt();
    assert.equal(posts.at(-1).payload.deepResearch, false);
    assert.equal(posts.at(-1).payload.toolAction, mode);
  }
});

test("research selection is scoped to its session and survives tab recreation without leaking", () => {
  const first = harness();
  first.context.selectDeepResearch(true);
  first.state.activeSessionId = "session-b";
  first.context.restoreDeepResearch();
  assert.equal(first.state.deepResearch, false);
  first.state.activeSessionId = "session-a";
  first.context.restoreDeepResearch();
  assert.equal(first.state.deepResearch, true);
  const restored = harness(first.storage);
  restored.context.restoreDeepResearch();
  assert.equal(restored.state.deepResearch, true);
  restored.context.selectDeepResearch(false);
  const reopened = harness(restored.storage);
  reopened.context.restoreDeepResearch();
  assert.equal(reopened.state.deepResearch, false);
});

test("incompatible media actions clear research while Coding and ordinary chat preserve it", () => {
  const { context, state } = harness();
  context.selectDeepResearch(true);
  context.selectToolAction("coding");
  assert.equal(state.deepResearch, true);
  context.selectToolAction("imageAnalysis");
  assert.equal(state.deepResearch, false);
  context.selectDeepResearch(true);
  assert.equal(state.selectedToolAction, "coding", "return to the session's existing Coding mode");
  assert.equal(state.deepResearch, true);
  context.selectToolAction(null);
  assert.equal(state.selectedToolAction, null);
  assert.equal(state.deepResearch, true);
});

test("the running request cannot be silently changed by toggling research", () => {
  const { context, state, notices } = harness();
  state.isRunning = true;
  context.selectDeepResearch(true);
  assert.equal(state.deepResearch, false);
  assert.equal(notices.length, 1);
  state.isRunning = false;
  context.selectDeepResearch(true);
  state.pendingChatSend = { requestId: "sending" };
  context.selectDeepResearch(false);
  assert.equal(state.deepResearch, true);
});

test("HTML exposes Deep Research as an independent checkbox in Tools", () => {
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  assert.match(html, /role="menuitemcheckbox" aria-checked="false" data-tool-toggle="deepResearch"/);
  assert.doesNotMatch(html, /data-tool-action="deepResearch"/);
});
