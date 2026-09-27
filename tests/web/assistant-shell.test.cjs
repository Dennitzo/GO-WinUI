const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
const appSource = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
const bridgeSource = fs.readFileSync(path.join(webRoot, "bridge.js"), "utf8");
const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
const css = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8");

function functionSource(name) {
  const start = appSource.indexOf(`  function ${name}(`);
  const ending = appSource.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
  assert.ok(start >= 0 && ending, name);
  return appSource.slice(start, start + ending.index + ending[0].length);
}

test("the chat uses its full height without a redundant header", () => {
  assert.doesNotMatch(html, /chat-header|id="chat-heading"|>AI Assistent<\/h1>/);
  assert.match(html, /id="open-sessions" class="mobile-sidebar-trigger mobile-only"/);
  assert.doesNotMatch(appSource, /session\.pin|chat\.exportPdf|renderSessionPin|session\.isPinned/);
  assert.doesNotMatch(css, /session-item\.pinned|pin-chip|pdf-chip|header-actions/);
});

test("reasoning is an icon-only composer control and text inputs suppress nested browser outlines", () => {
  assert.match(html, /id="reasoning-button" class="tool-button icon-only"/);
  assert.doesNotMatch(html, /id="reasoning-label"/);
  assert.match(css, /\.composer textarea:focus-visible,[\s\S]*outline: 0 !important/);
  assert.match(css, /\.search-field input:focus-visible/);
});

test("Planmodus stays selected while planning keeps its decision flow", () => {
  assert.match(appSource, /persistentExtensionActionIds = new Set\(\["builtin\.audiobook\/create", "builtin\.coding\/plan-mode"\]\)/);
  assert.doesNotMatch(appSource, /builtin\.development|development\.execute/);
  assert.match(appSource, /```assistant-plan/);
  assert.match(appSource, /className = "plan-questions"/);
  assert.match(appSource, /textContent = "Plan implementieren"/);
  assert.match(appSource, /actionId: "builtin\.coding\/plan-mode"[\s\S]*enabled: false/);
});

test("sidebar exposes General and Coding as views rather than composer tools", () => {
  assert.match(html, /data-chat-mode="general"[\s\S]*Fragen, lernen und erkunden/);
  assert.match(html, /data-chat-mode="coding"[\s\S]*Erstellen, debuggen und ausliefern/);
  assert.match(appSource, /post\("mode\.switch", \{ chatMode \}\)/);
  assert.doesNotMatch(appSource, /const defaultActionDescriptors|displayName:\s*"(?:General|Coding)"/);
});

test("the grouped action menu is populated only by the current snapshot descriptors", () => {
  assert.match(html, /id="tools-button"[^>]*aria-label="Hinzufügen"/);
  assert.match(html, /id="tools-menu-content" class="action-menu__content"/);
  assert.doesNotMatch(html, /id="pick-document"|id="open-workflows"|id="export-pdf"/);
  assert.doesNotMatch(appSource, /const defaultActionDescriptors/);

  const toolsMenuContent = new TestNode("div");
  const state = {
    chatMode: "general",
    actionDescriptors: [
      { actionId: "builtin.workspace/attach-files-and-folders", displayName: "Dateien und Ordner",
        description: "Anhängen", iconKey: "attachment", groupId: "add", groupLabel: "Hinzufügen",
        groupOrder: 10, itemOrder: 10, supportedChatModes: ["general", "coding"], actionKind: "immediate" },
      { actionId: "builtin.documents/export-chat-pdf", displayName: "Chat als PDF exportieren",
        description: "Export", iconKey: "pdf", groupId: "export", groupLabel: "Exportieren",
        groupOrder: 40, itemOrder: 10, supportedChatModes: ["general", "coding"], actionKind: "immediate" },
      { actionId: "builtin.workflows/open-library", displayName: "Workflows öffnen",
        description: "Verwalten", iconKey: "workflow", groupId: "workflows", groupLabel: "Workflows",
        groupOrder: 60, itemOrder: 10, supportedChatModes: ["general", "coding"], actionKind: "immediate" },
      { actionId: "builtin.future/new-action", displayName: "Künftige Aktion",
        description: "Ohne WebView-Änderung", iconKey: "future", groupId: "extensions", groupLabel: "Erweiterungen",
        groupOrder: 70, itemOrder: 5, supportedChatModes: ["general"], actionKind: "selectableTool", selectionBehavior: "toggle" },
      { actionId: "builtin.coding", displayName: "Coding", groupId: "extensions", actionKind: "selectableTool" },
      { actionId: "third.party/coding-only", displayName: "Nur Coding", groupId: "extensions",
        supportedChatModes: ["coding"], actionKind: "immediate" }
    ],
    activeSessionId: "session-a", messages: [], selectedExtensionActionId: null,
    selectedToolAction: null, activeActionIds: new Set(), deepResearch: false
  };
  const context = vm.createContext({
    state,
    elements: { toolsMenuContent },
    document: { createElement: tag => new TestNode(tag) },
    actionIcons: { attachment: "attach", pdf: "pdf", workflow: "workflow", extension: "extension" },
    createToolIcon: () => new TestNode("svg"),
    normalizeChatMode: value => String(value || "").toLocaleLowerCase(),
    updateActionMenuState() {},
    invokeActionDescriptor() {}
  });
  vm.runInContext(functionSource("availableActionDescriptors"), context);
  vm.runInContext(functionSource("isSelectableAction"), context);
  vm.runInContext(functionSource("renderActionMenu"), context);
  context.renderActionMenu();

  assert.deepEqual(
    Array.from(toolsMenuContent.querySelectorAll(".action-menu__item"), item => item.dataset.actionId),
    ["builtin.workspace/attach-files-and-folders", "builtin.documents/export-chat-pdf",
      "builtin.workflows/open-library", "builtin.future/new-action"]);
  assert.equal(toolsMenuContent.querySelector('[data-action-id="builtin.coding"]'), null);
  assert.equal(toolsMenuContent.querySelector('[data-action-id="third.party/coding-only"]'), null);
  assert.match(toolsMenuContent.textContent, /Dateien und Ordner.*Exportieren.*Workflows.*Künftige Aktion/s);

  state.actionDescriptors = [];
  context.renderActionMenu();
  assert.equal(toolsMenuContent.children.length, 0, "missing snapshot actions must not revive removed built-ins");
  assert.doesNotMatch(appSource, /builtin\.future\/new-action/,
    "new built-ins are rendered from descriptors without a WebView allow-list");
  assert.match(html, /id="memory-overlay"[\s\S]*id="memory-auto-capture"[\s\S]*id="pin-memory-entry"/);
  assert.match(appSource, /post\("message\.exportPdf", \{ messageId:/, "per-message PDF export remains available");
});

test("whole-chat PDF and Workflow rows dispatch canonical extension actions", () => {
  const posts = [];
  let workflowsOpened = 0;
  const context = vm.createContext({
    state: { activeSessionId: "session-a", messages: [{ id: "message-a" }],
      selectedExtensionActionId: null, activeActionIds: new Set() },
    elements: { prompt: { focus() {} } },
    post: (type, payload) => posts.push({ type, payload }),
    setToolsMenuOpen() {},
    ensureEditableContext: () => true,
    extensionActionIdForToolAction: alias => alias === "deepResearch" ? "builtin.web/deep-research" : null,
    selectDeepResearch() {},
    selectToolAction() {},
    beginMediaCapture() {},
    openWorkflows: () => { workflowsOpened += 1; },
    openProjectMemory() {}
  });
  vm.runInContext(functionSource("isSelectableAction"), context);
  vm.runInContext(functionSource("isActionDescriptorActive"), context);
  vm.runInContext(functionSource("resolvedActionDisabledReason"), context);
  vm.runInContext(functionSource("invokeActionDescriptor"), context);
  context.invokeActionDescriptor({ actionId: "builtin.documents/export-chat-pdf" });
  context.invokeActionDescriptor({ actionId: "builtin.workflows/open-library" });
  context.invokeActionDescriptor({ actionId: "builtin.media/image-analysis", toolAction: "imageAnalysis" });
  assert.equal(posts.length, 3);
  assert.equal(posts[0].type, "action.invoke");
  assert.equal(posts[0].payload.actionId, "builtin.documents/export-chat-pdf");
  assert.equal(posts[0].payload.sessionId, "session-a");
  assert.equal(posts[1].type, "workflow.list");
  assert.equal(workflowsOpened, 1);
  assert.equal(posts[2].type, "action.invoke");
  assert.equal(posts[2].payload.actionId, "builtin.media/image-analysis");
  assert.equal(posts[2].payload.selectionBehavior, "toggle");
  assert.equal(posts[2].payload.enabled, true);
});

test("whole-chat PDF becomes available immediately after the first committed message", () => {
  const emptyReason = "Der Chat enthält noch keine Nachrichten.";
  const state = { activeSessionId: "session-a", messages: [] };
  const context = vm.createContext({ state });
  vm.runInContext(functionSource("resolvedActionDisabledReason"), context);
  const descriptor = {
    actionId: "builtin.documents/export-chat-pdf",
    disabledReason: emptyReason
  };

  assert.equal(context.resolvedActionDisabledReason(descriptor), emptyReason);
  state.messages.push({ id: "message-a" });
  assert.equal(context.resolvedActionDisabledReason(descriptor), null);
  assert.equal(context.resolvedActionDisabledReason({ actionId: "third.party/action", disabledReason: "Nicht bereit" }), "Nicht bereit");
});

test("the versioned bridge accepts new view/actions and rejects removed pin and whole-chat routes", () => {
  const sent = [];
  let receive;
  const context = vm.createContext({
    chrome: { webview: {
      postMessage: envelope => sent.push(envelope),
      addEventListener: (type, handler) => { if (type === "message") receive = handler; }
    } },
    crypto: { randomUUID: () => "request-a" },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    dispatchEvent: event => sent.push(event)
  });
  vm.runInContext(bridgeSource, context);
  context.goBridge.post("mode.switch", { chatMode: "coding" });
  context.goBridge.post("action.invoke", { actionId: "builtin.documents/export-chat-pdf" });
  context.goBridge.post("memory.list", { sessionId: "session-a" });
  context.goBridge.post("research.list", { sessionId: "session-a" });
  assert.throws(() => context.goBridge.post("session.pin", {}), /Nicht erlaubter Bridge-Typ/);
  assert.throws(() => context.goBridge.post("session.tool", {}), /Nicht erlaubter Bridge-Typ/);
  assert.throws(() => context.goBridge.post("chat.exportPdf", {}), /Nicht erlaubter Bridge-Typ/);
  receive({ data: { version: 1, type: "action.completed", payload: { actionId: "builtin.workflows/open-library" } } });
  assert.equal(sent.at(-1).type, "go:host-message");
  receive({ data: { version: 1, type: "chat.queued", payload: { sessionId: "session-a", position: 1 } } });
  assert.equal(sent.at(-1).detail.type, "chat.queued");
  receive({ data: { version: 1, type: "queue.changed", payload: { runQueue: { active: null, pending: [], queueDepth: 0, isIdle: true } } } });
  assert.equal(sent.at(-1).detail.type, "queue.changed");
  receive({ data: { version: 1, type: "research.snapshot", payload: { sessionId: "session-a", projects: [] } } });
  assert.equal(sent.at(-1).detail.type, "research.snapshot");
  assert.equal(context.goBridge.configureContract({ version: 1,
    clientActions: ["app.ready", "research.open"], hostEvents: ["state.snapshot", "host.error", "research.snapshot"] }), true);
  context.goBridge.post("research.open", { sessionId: "session-a", projectId: "research-a" });
  assert.throws(() => context.goBridge.post("memory.list", {}), /Nicht erlaubter Bridge-Typ/);
});

test("product-neutral storage keys migrate legacy values on first read", () => {
  const storage = new Map([["go.assistant.sessions-collapsed", "1"]]);
  const context = vm.createContext({
    localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
      removeItem: key => storage.delete(key)
    }
  });
  vm.runInContext(functionSource("readMigratedStorage"), context);
  assert.equal(context.readMigratedStorage("assistant.ui.sessions-collapsed", "go.assistant.sessions-collapsed"), "1");
  assert.equal(storage.get("assistant.ui.sessions-collapsed"), "1");
  assert.equal(storage.has("go.assistant.sessions-collapsed"), false);
  assert.doesNotMatch(appSource, /localStorage\.setItem\(`go\.assistant/);
  assert.doesNotMatch(bridgeSource, /"session\.tool"/);
});

test("the LAN browser owns its workspace dialog and local speech playback", () => {
  assert.match(html, /id="workspace-overlay"[\s\S]*id="workspace-path"/);
  assert.match(html, /Workspace auf dem Host öffnen/);
  assert.match(appSource, /if \(globalThis\.goBridge\?\.isLanBrowser\) \{[\s\S]*openWorkspacePicker\(\)/);
  assert.match(appSource, /post\("session\.projectCreate", \{ workspacePath, chatMode: state\.chatMode \}\)/);
  assert.match(appSource, /descriptor\.actionId === "builtin\.workflows\/open-library"[\s\S]*openWorkflows\(\)[\s\S]*post\("workflow\.list"/);
  assert.doesNotMatch(appSource, /payload\?\.actionId === "builtin\.workflows\/open-library"\) openWorkflows/);

  const events = [];
  let spoken = null;
  let paused = false;
  let cancelled = 0;
  class Socket {
    constructor() { this.readyState = 0; }
    addEventListener() {}
  }
  class Utterance {
    constructor(text) { this.text = text; }
  }
  const storage = new Map();
  const context = vm.createContext({
    URL,
    WebSocket: Socket,
    location: { protocol: "http:", href: "http://host/assistant/", host: "host", pathname: "/assistant/" },
    crypto: { randomUUID: () => "client-a" },
    sessionStorage: {
      getItem: key => storage.get(key) || null,
      setItem: (key, value) => storage.set(key, value)
    },
    SpeechSynthesisUtterance: Utterance,
    speechSynthesis: {
      cancel: () => { cancelled += 1; },
      speak: utterance => { spoken = utterance; utterance.onstart(); },
      pause: () => { paused = true; },
      resume: () => { paused = false; }
    },
    document: { documentElement: { lang: "de-DE" } },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    dispatchEvent: event => events.push(event),
    setTimeout,
    clearTimeout
  });
  vm.runInContext(bridgeSource, context);
  context.goBridge.post("microphone.speak", { text: "Hallo **Browser**" }, "speech-a");
  assert.equal(spoken.text, "Hallo Browser");
  assert.ok(events.some(event => event.detail?.type === "speech.status" && event.detail.payload.active));
  context.goBridge.post("microphone.toggleSpeechPause", {}, "speech-b");
  assert.equal(paused, true);
  context.goBridge.post("microphone.stopSpeech", {}, "speech-c");
  assert.ok(cancelled >= 2);
  assert.ok(events.some(event => event.detail?.type === "speech.status" && !event.detail.payload.active));
});
