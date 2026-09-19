const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const file = "../../src/GoWinUI.App/Assets/Web/reasoning-menu.js";
const { options, selection } = require(file);

function harness() {
  const nodes = new Map(), documentEvents = new Map(), sent = [];
  let receive;
  const node = id => {
    if (!nodes.has(id)) {
      const item = new TestNode("button");
      item.focus = () => { document.activeElement = item; };
      nodes.set(id, item);
    }
    return nodes.get(id);
  };
  const document = { getElementById: node, activeElement: null,
    addEventListener: (type, handler) => documentEvents.set(type, handler), createElement: tag => {
      const item = new TestNode(tag);
      item.focus = () => { document.activeElement = item; };
      return item;
    } };
  node("reasoning-menu").hidden = true;
  const context = { document, goBridge: { post(type, payload) {
    const id = String(sent.length + 1); sent.push({ type, payload, id }); return id;
  } }, addEventListener: (type, handler) => { receive = handler; } };
  vm.runInNewContext(fs.readFileSync(require.resolve(file), "utf8"), context);
  const emit = (type, payload, requestId) => receive({ detail: { type, payload, requestId } });
  const snapshot = (payload = {}, requestId = sent.at(-1)?.id) => emit("reasoning.snapshot", {
    modelId: "A", role: "coding", selected: "auto", defaultLevel: "high", levels: ["auto", "low", "high"], available: true, ...payload
  }, requestId);
  return { node, document, sent, emit, snapshot,
    key: key => documentEvents.get("keydown")({ key, preventDefault() {} }) };
}

test("only explicit supported levels are offered, including when old capabilities contain auto", () => {
  assert.deepEqual(options({}), []);
  assert.deepEqual(options({ levels: ["auto", " AUTO ", "low", "future_level", "low", "<script>", null] }).map(item => item.value), ["low", "future_level"]);
  assert.equal(selection({ selected: "auto", defaultLevel: "high", levels: ["auto", "low", "high"] }), "high");
  assert.equal(selection({ selected: "low", defaultLevel: "high", levels: ["low", "high"] }), "low");
  assert.equal(selection({ selected: "unavailable", defaultLevel: "auto", levels: ["auto", "medium"] }), "medium");
  assert.equal(selection({ selected: "auto", defaultLevel: "auto", levels: ["auto"] }), null);
});

test("model switches reject stale responses and opening the menu survives its actual refresh roundtrip", async () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" });
  h.snapshot({ selected: "high" }, "1");
  assert.equal(h.node("reasoning-button").disabled, true);
  assert.equal(h.node("reasoning-options").children.length, 0);
  h.snapshot({ modelId: "B" }, "2");
  assert.equal(h.node("reasoning-button").title, "Reasoning: Hoch");
  assert.equal(h.node("reasoning-options").children.length, 2);
  await h.node("reasoning-button").dispatch("click");
  assert.equal(h.sent.at(-1).type, "reasoning.get");
  assert.equal(h.node("reasoning-menu").hidden, false, "refreshing an opened menu must not close it");
  assert.equal(h.node("reasoning-options").children.every(item => item.disabled), true);
  h.snapshot({ modelId: "B" });
  assert.equal(h.node("reasoning-menu").hidden, false);
  assert.equal(h.document.activeElement.textContent, "Hoch");
  await h.node("reasoning-options").children[0].dispatch("click");
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent.at(-1).payload)), { modelId: "B", role: "coding", effort: "low" });
  assert.equal(h.node("reasoning-button").title, "Reasoning: Hoch", "the displayed selection waits for confirmation");
  h.snapshot({ modelId: "B", selected: "low" });
  assert.equal(h.node("reasoning-menu").hidden, true);
  assert.equal(h.node("reasoning-button").title, "Reasoning: Niedrig");
  assert.equal(h.node("reasoning-label").textContent, "Reasoning");
  h.emit("chat.started", {});
  assert.equal(h.node("reasoning-button").disabled, true);
  h.emit("chat.completed", {});
  assert.equal(h.node("reasoning-button").disabled, false);
});

test("keyboard navigation contains no automatic item and cannot choose while a request is pending", async () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.snapshot({ levels: ["auto", "low", "medium", "high"] });
  await h.node("reasoning-button").dispatch("click");
  const sentBefore = h.sent.length;
  await h.node("reasoning-options").children[0].dispatch("click");
  h.key("ArrowDown");
  assert.equal(h.sent.length, sentBefore);
  h.snapshot({ levels: ["auto", "low", "medium", "high"] });
  h.key("Home");
  assert.equal(h.document.activeElement.textContent, "Niedrig");
  h.key("ArrowDown");
  assert.equal(h.document.activeElement.textContent, "Mittel");
  h.key("End");
  assert.equal(h.document.activeElement.textContent, "Hoch");
  h.key("ArrowDown");
  assert.equal(h.document.activeElement.textContent, "Niedrig");
  assert.equal(h.node("reasoning-options").textContent.includes("Automatisch"), false);
  h.key("Escape");
  assert.equal(h.node("reasoning-menu").hidden, true);
  assert.equal(h.document.activeElement, h.node("reasoning-button"));
});

test("models without explicit levels expose no fallback choice and the button is disabled", async () => {
  for (const profile of [{ levels: [] }, { levels: ["auto"], defaultLevel: "auto" }, { levels: [], available: false }]) {
    const h = harness();
    h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
    h.snapshot(profile);
    assert.equal(h.node("reasoning-button").disabled, true);
    assert.equal(h.node("reasoning-options").children.length, 0);
    assert.equal(h.node("reasoning-button").title.includes("Aus"), false);
    await h.node("reasoning-button").dispatch("click");
    h.key("Enter");
    assert.equal(h.sent.length, 1);
    assert.equal(h.node("reasoning-menu").hidden, true);
  }
});

test("metadata errors clear the choices and a later same-model snapshot can recover without reintroducing auto", () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.emit("host.error", { message: "Fixture connection lost" }, "1");
  assert.equal(h.node("reasoning-button").disabled, true);
  assert.equal(h.node("reasoning-options").children.length, 0);
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.snapshot({ selected: "auto", defaultLevel: "on", levels: ["auto", "on"] });
  assert.equal(h.node("reasoning-button").disabled, false);
  assert.equal(h.node("reasoning-button").title, "Reasoning: Ein");
  assert.deepEqual(h.node("reasoning-options").children.map(item => item.textContent), ["Ein"]);
});
