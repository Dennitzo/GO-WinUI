const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { randomUUID } = require("node:crypto");
const { TestNode } = require("./test-dom.cjs");
const root = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
const app = fs.readFileSync(path.join(root, "app.js"), "utf8");

function harness(mode = "coding", storage = new Map()) {
  const posted = [], toasts = [], savedDrafts = [];
  const state = { activeSessionId: "session-a", activeRunSessionId: "session-a", activeRunId: "run-a", activeRunMessageId: "answer-a", isRunning: true,
    isAiBusy: true, selectedToolAction: mode, documents: [{ id: "existing-document" }], sessions: [],
    contextUsed: 100, contextLimit: 4096, contextSource: "measured", runStatus: "Denkt nach", runDetail: "Hauptmodell läuft",
    messages: [], codingActivity: new Map(), messageRunStatus: new Map(), voiceTurn: { text: "Original" }, pendingCaptureRequest: null };
  const elements = Object.fromEntries(["send", "prompt", "newSession", "clearSessions", "sessionList", "context", "contextLabel"]
    .map(name => [name, new TestNode("div")]));
  elements.prompt.value = "Bitte zuerst den Fehler im Import beheben.";
  elements.context.style.setProperty = () => {};
  let receive;
  const context = vm.createContext({ state, elements, URL, crypto: { randomUUID },
    document: { createElement: name => new TestNode(name), createTextNode: text => new TestNode("#text", text), createDocumentFragment: () => new TestNode("#fragment") },
    localStorage: { getItem: key => storage.get(key) || null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
    chrome: { webview: { postMessage: value => posted.push(value), addEventListener: (type, callback) => { receive = callback; } } },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    dispatchEvent: event => context.handleHostMessage(event), showToast: (text, error) => toasts.push({ text, error }),
    renderContext() {}, renderCodingWorkspace() {}, schedulePromptResize() {},
    selectToolAction: action => { state.selectedToolAction = action; }, beginMediaCapture() {},
    belongsToActiveSession: payload => !payload.sessionId || payload.sessionId === state.activeSessionId,
    recordCodingActivity() {}, persistMeasuredContext() {}, renderMessages() {}, renderSessions() {}, syncVoiceCaptureSuspension() {},
    scheduleDraftSave: () => savedDrafts.push({ sessionId: state.activeSessionId, draft: elements.prompt.value }), flushDraft() {},
    chatScroll: { jump() {} }, clearTimeout() {},
  });
  vm.runInContext("let draftTimer = 0, pendingDraft = null;", context);
  for (const script of ["bridge.js", "run-steering.js", "markdown.js", "coding-timeline.js"]) vm.runInContext(fs.readFileSync(path.join(root, script), "utf8"), context);
  for (const name of ["post", "postChatRequest", "submitPrompt", "handleComposerAction", "setPromptValue", "renderComposerAction", "renderStatus", "handleHostMessage", "ensureEditableContext"]) {
    const start = app.search(new RegExp(`  (?:async )?function ${name}\\(`));
    const ending = app.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending, name);
    vm.runInContext(app.slice(start, start + ending.index + ending[0].length), context);
  }
  vm.runInContext(app.match(/  elements\.send\.addEventListener\("click", [^;]+;/)[0], context);
  return { state, elements, context, posted, toasts, savedDrafts, storage,
    host: (type, payload, requestId = "host-id") => receive({ data: { version: 1, type, payload, requestId } }) };
}

for (const mode of ["coding", null]) {
  test(`${mode || "General"} sends text steering immediately without cancel, queue, mode change or clearing before acknowledgement`, async () => {
    const h = harness(mode);
    h.context.renderStatus();
    assert.equal(h.elements.send.hidden, false);
    assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
    assert.equal(h.elements.send.getAttribute("aria-label"), "Umlenken");
    assert.equal(h.elements.send.textContent, "");
    const before = JSON.stringify(h.state);
    const prompt = h.elements.prompt.value;
    await h.context.submitPrompt();
    const request = h.posted[0];
    assert.equal(h.posted.length, 1);
    assert.equal(request.type, "chat.steer");
    assert.equal(request.payload.expectedRunId, "run-a");
    assert.equal(request.payload.sessionId, "session-a");
    assert.equal(request.payload.prompt, prompt);
    assert.match(request.payload.inputId, /^[a-f0-9-]{36}$/i);
    assert.equal(request.requestId, request.payload.inputId);
    assert.equal(Object.hasOwn(request.payload, "documentIds"), false);
    assert.equal(Object.hasOwn(request.payload, "toolAction"), false);
    assert.equal(h.elements.prompt.value, prompt);
    assert.equal(JSON.stringify(h.state), before, "run and cached generation state are unchanged");
    h.host("chat.steer.accepted", { inputId: request.payload.inputId, sessionId: "session-a", runId: "run-a" });
    assert.equal(h.elements.prompt.value, "");
    assert.equal(h.savedDrafts.at(-1).draft, "");
    assert.equal(h.state.isRunning, true);
    assert.equal(h.state.activeRunId, "run-a");
    assert.equal(h.state.contextUsed, 100);
  });
}

test("retry reuses one input ID through a bridge error and fresh page without discarding the draft", async () => {
  const h = harness();
  await h.context.submitPrompt();
  const first = h.posted[0];
  h.host("host.error", { message: "Verbindung unterbrochen." }, first.requestId);
  assert.ok(h.elements.prompt.value);
  assert.equal(h.state.isRunning, true);
  assert.equal(h.toasts.at(-1).text, "Verbindung unterbrochen.");
  await h.context.submitPrompt();
  assert.equal(h.posted[1].payload.inputId, first.payload.inputId);
  const restarted = harness("coding", h.storage);
  await restarted.context.submitPrompt();
  assert.equal(restarted.posted[0].payload.inputId, first.payload.inputId);
  restarted.state.activeRunId = "new-run";
  await restarted.context.submitPrompt();
  assert.equal(restarted.posted[1].payload.inputId, first.payload.inputId, "an unresolved prior input cannot accidentally become steering for a new run");
  assert.equal(restarted.posted[1].payload.expectedRunId, "run-a");
  restarted.host("chat.steer.accepted", { inputId: first.payload.inputId, sessionId: "session-a", runId: "run-a" });
  restarted.elements.prompt.value = first.payload.prompt;
  await restarted.context.submitPrompt();
  assert.notEqual(restarted.posted[2].payload.inputId, first.payload.inputId, "a new input after acknowledgement gets a new key");
});

test("foreign active runs and stale mismatched session ownership cannot be steered through Enter", async () => {
  for (const isRunning of [false, true]) {
    const h = harness();
    h.state.activeSessionId = "session-b";
    h.state.isRunning = isRunning;
    h.context.renderStatus();
    assert.equal(h.elements.send.disabled, true);
    await h.context.submitPrompt();
    assert.equal(h.posted.length, 0);
    assert.ok(h.elements.prompt.value);
    assert.ok(h.toasts.at(-1).text.includes("anderen Sitzung"));
  }
});

test("delayed acknowledgement never clears another session or newly edited text", async () => {
  for (const change of ["session", "text"]) {
    const h = harness();
    await h.context.submitPrompt();
    const inputId = h.posted[0].payload.inputId;
    if (change === "session") h.state.activeSessionId = "session-b";
    h.elements.prompt.value = "Neu geschriebener, noch nicht versendeter Text";
    h.host("chat.steer.accepted", { inputId, sessionId: "session-a", runId: "run-a" });
    assert.equal(h.elements.prompt.value, "Neu geschriebener, noch nicht versendeter Text");
    assert.equal(h.savedDrafts.length, 0);
  }
});

test("optional run ID can arrive after sending without changing the retry identity", async () => {
  const h = harness();
  h.state.activeRunId = null;
  await h.context.submitPrompt();
  const first = h.posted[0].payload;
  assert.equal(Object.hasOwn(first, "expectedRunId"), false);
  h.state.activeRunId = "run-a";
  await h.context.submitPrompt();
  assert.equal(h.posted[1].payload.inputId, first.inputId);
  assert.equal(h.posted[1].payload.expectedRunId, "run-a");
});

test("idle submit retains its original General/Coding path and context changes are blocked only during a run", async () => {
  const h = harness(null);
  assert.equal(h.context.ensureEditableContext(), false);
  assert.ok(h.toasts.at(-1).text.includes("Anhänge"));
  h.state.isRunning = false;
  h.state.isAiBusy = false;
  h.context.renderStatus();
  assert.equal(h.elements.send.getAttribute("aria-label"), "Senden");
  assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
  assert.equal(h.context.ensureEditableContext(), true);
  await h.context.submitPrompt();
  assert.equal(h.posted[0].type, "chat.send");
  assert.equal(h.posted[0].payload.documentIds[0], "existing-document");
});

for (const mode of ["coding", null]) {
  test(`${mode || "General"} uses one icon button for text steering and empty-field stopping without making empty Enter cancel`, async () => {
    const h = harness(mode);
    h.context.renderStatus();
    await h.elements.send.dispatch("click");
    const request = h.posted[0];
    assert.equal(request.type, "chat.steer");
    assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
    h.host("chat.steer.accepted", { inputId: request.payload.inputId, sessionId: "session-a", runId: "run-a" });
    assert.equal(h.elements.send.getAttribute("aria-label"), "Antwort stoppen");
    assert.equal(h.elements.send.classList.contains("send-button--stop"), true);
    assert.equal(h.elements.send.disabled, false);
    h.context.setPromptValue("  \n ");
    await h.context.submitPrompt();
    assert.equal(h.posted.length, 1, "empty Enter still does nothing");
    await h.elements.send.dispatch("click");
    assert.equal(h.posted[1].type, "chat.cancel");
    assert.equal(h.posted.some(item => item.type.startsWith("microphone.")), false, "AI stop keeps speech independent");
    h.context.setPromptValue("Weitere Eingabe");
    assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
    assert.equal(h.elements.send.getAttribute("aria-label"), "Umlenken");
  });
}

test("combined button guards preparation and foreign runs across session switches and retains existing attachments", async () => {
  const h = harness(null);
  h.state.isRunning = h.state.isAiBusy = false;
  h.context.setPromptValue("");
  assert.equal(h.elements.send.disabled, true);
  assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
  await h.elements.send.dispatch("click");
  assert.equal(h.posted.length, 0);
  h.context.setPromptValue("Auftrag mit bestehendem Dokument");
  await h.elements.send.dispatch("click");
  const request = h.posted[0];
  assert.equal(request.type, "chat.send");
  assert.deepEqual(Array.from(request.payload.documentIds), ["existing-document"]);
  assert.equal(h.elements.send.disabled, true);
  assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
  h.context.setPromptValue("Neue Priorität");
  await h.elements.send.dispatch("click");
  assert.equal(h.posted.length, 1, "preparing is neither a second send nor a cancellation");
  assert.equal(h.elements.prompt.value, "Neue Priorität");
  h.state.activeSessionId = "session-b";
  h.host("chat.started", { sessionId: "session-a", runId: "run-created", message: { id: "new-answer" } }, request.requestId);
  h.state.isAiBusy = true;
  h.context.renderStatus();
  await h.elements.send.dispatch("click");
  assert.equal(h.posted.length, 1);
  h.context.setPromptValue("");
  assert.equal(h.elements.send.disabled, true);
  assert.equal(h.elements.send.classList.contains("send-button--stop"), false);
  await h.elements.send.dispatch("click");
  assert.equal(h.posted.length, 1, "an empty composer in another session must never cancel the foreign run");
});

test("composer markup keeps the original arrow and stop icons inside one unlabeled action button", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const button = html.match(/<button id="send"[\s\S]*?<\/button>/)[0];
  assert.match(button, /class="send-button__send-icon"/);
  assert.match(button, /d="M12 19V5m-6 6 6-6 6 6"/);
  assert.match(button, /class="send-button__stop-icon"/);
  assert.doesNotMatch(button, /<span|Umlenken/);
  assert.doesNotMatch(html, /id="stop"|id="send-label"/);
});

test("persisted steering becomes a user bubble at its exact response offset without an extra tool execution", () => {
  const h = harness();
  const before = "Erster Antwortteil.\n\n", after = "Jetzt priorisiere ich den Import.";
  const receipt = { id: "steer-a", tool: "assistant.steering", status: "running", kind: "tool", contentOffset: before.length,
    detail: "Bitte Import zuerst. <script>evil()</script>", inputJson: '{"inputId":"input-a","sequence":1}' };
  const response = { id: "same-answer", status: "streaming", content: before + after };
  const options = { renderMarkdown: text => h.context.goMarkdown.render(text), codingToolStepsExpanded: true };
  const timeline = h.context.goCodingTimeline.render(response, [receipt], options);
  assert.deepEqual(timeline.children.map(child => child.className), ["message-content coding-narration", "steering-message", "message-content coding-narration"]);
  assert.equal(timeline.querySelectorAll(".coding-step").length, 0);
  assert.ok(timeline.querySelector(".steering-message").textContent.includes("wird angewendet"));
  assert.equal(timeline.querySelector("script"), null);
  const restored = h.context.goCodingTimeline.render({ ...response, status: "completed" }, [JSON.parse(JSON.stringify({ ...receipt, status: "completed" }))], options);
  assert.ok(restored.querySelector(".steering-message").textContent.includes("Angewendet"));
  assert.equal(restored.querySelectorAll(".steering-message").length, 1);
});

test("a second Enter before chat.started preserves the new text and never dispatches a second initial run", async () => {
  const h = harness();
  h.state.isRunning = h.state.isAiBusy = false;
  await h.context.submitPrompt();
  const start = h.posted[0];
  assert.equal(start.type, "chat.send");
  assert.equal(h.elements.prompt.value, "");
  assert.equal(h.elements.send.getAttribute("aria-label"), "Wird vorbereitet");
  h.elements.prompt.value = "Neue Priorität während der Vorbereitung";
  await h.context.submitPrompt();
  assert.equal(h.posted.length, 1);
  assert.equal(h.elements.prompt.value, "Neue Priorität während der Vorbereitung");
  h.host("chat.started", { sessionId: "session-a", runId: "run-created", message: { id: "new-answer" } }, start.requestId);
  assert.equal(h.state.pendingChatSend, null);
  assert.equal(h.elements.send.getAttribute("aria-label"), "Umlenken");
  await h.context.submitPrompt();
  assert.equal(h.posted.length, 2);
  assert.equal(h.posted[1].type, "chat.steer");
  assert.equal(h.posted[1].payload.expectedRunId, "run-created");
  assert.equal(h.posted[1].payload.prompt, "Neue Priorität während der Vorbereitung");
});

test("correlated initial-send errors restore only an empty composer in their own session", async () => {
  for (const newer of [false, true, "different-session"]) {
    const h = harness(null);
    h.state.isRunning = h.state.isAiBusy = false;
    const original = h.elements.prompt.value;
    await h.context.submitPrompt();
    const request = h.posted[0];
    if (newer) h.elements.prompt.value = "Neue Eingabe behalten";
    if (newer === "different-session") h.state.activeSessionId = "session-b";
    h.host("host.error", { message: "Vorbereitung fehlgeschlagen" }, request.requestId);
    assert.equal(h.state.pendingChatSend, null);
    assert.equal(h.elements.prompt.value, newer ? "Neue Eingabe behalten" : original);
    assert.equal(h.posted.length, 1);
    assert.equal(h.elements.send.disabled, false);
  }
});

test("lost steering response followed by terminal run retries the same input and never creates a new run", async () => {
  const h = harness();
  await h.context.submitPrompt();
  const first = h.posted[0];
  h.state.isRunning = h.state.isAiBusy = false;
  h.state.activeRunId = h.state.activeRunSessionId = null;
  h.context.renderStatus();
  assert.equal(h.elements.send.getAttribute("aria-label"), "Umlenken");
  await h.context.submitPrompt();
  assert.equal(h.posted.length, 2);
  assert.equal(h.posted[1].type, "chat.steer");
  assert.equal(h.posted[1].payload.inputId, first.payload.inputId);
  assert.equal(h.posted[1].payload.expectedRunId, "run-a");
  assert.equal(h.elements.prompt.value, first.payload.prompt);
  h.host("chat.steer.accepted", { sessionId: "session-a", inputId: first.payload.inputId, runId: "run-a" });
  assert.equal(h.elements.send.getAttribute("aria-label"), "Senden");
  h.elements.prompt.value = first.payload.prompt;
  await h.context.submitPrompt();
  assert.equal(h.posted[2].type, "chat.send", "the same text is a genuine new prompt after an acknowledged receipt");
});

test("steering sent before the server ID exists learns that ID for exact terminal retries", async () => {
  const h = harness();
  h.state.activeRunId = null;
  await h.context.submitPrompt();
  const inputId = h.posted[0].payload.inputId;
  h.state.activeRunId = "run-late";
  h.context.goRunSteering.observeRun(h.state);
  h.state.isRunning = h.state.isAiBusy = false;
  h.state.activeRunId = null;
  await h.context.submitPrompt();
  assert.equal(h.posted[1].type, "chat.steer");
  assert.equal(h.posted[1].payload.expectedRunId, "run-late");
  assert.equal(h.posted[1].payload.inputId, inputId);
});

test("an unresolved steering input without a server ID cannot silently start a new run", async () => {
  const h = harness();
  h.state.activeRunId = null;
  await h.context.submitPrompt();
  h.state.isRunning = h.state.isAiBusy = false;
  await h.context.submitPrompt();
  assert.equal(h.posted.length, 1);
  assert.ok(h.elements.prompt.value);
  assert.ok(h.toasts.at(-1).text.includes("kein neuer Auftrag"));
});

test("an unbound input cannot acquire the identity of a subsequent run in the same session", async () => {
  const h = harness();
  h.state.activeRunId = null;
  await h.context.submitPrompt();
  const original = h.posted[0];
  assert.equal(Object.hasOwn(original.payload, "originMessageId"), false, "local identity does not extend the bridge contract");
  h.state.activeRunMessageId = "different-answer";
  h.state.activeRunId = "different-run";
  h.context.goRunSteering.observeRun(h.state);
  await h.context.submitPrompt();
  assert.equal(h.posted.length, 1);
  assert.ok(h.toasts.at(-1).text.includes("kein neuer Auftrag"));
  const restored = harness("coding", h.storage);
  restored.state.activeRunMessageId = "different-answer";
  restored.state.activeRunId = "different-run";
  restored.context.goRunSteering.observeRun(restored.state);
  await restored.context.submitPrompt();
  assert.equal(restored.posted.length, 0, "reload must retain the original message ownership");
});

test("unknown acknowledgements never clear drafts and capture cancellation preserves later typing", async () => {
  const h = harness(null);
  h.state.isRunning = h.state.isAiBusy = false;
  await h.context.submitPrompt();
  const first = h.posted[0];
  h.elements.prompt.value = "Neu verfasste Eingabe";
  h.host("chat.steer.accepted", { sessionId: "session-a", inputId: "unknown", runId: "unknown" });
  assert.equal(h.elements.prompt.value, "Neu verfasste Eingabe");
  assert.ok(h.state.pendingChatSend, "an unrelated receipt cannot unlock the pending initial request");
  h.host("capture.required", { action: "imageAnalysis" }, first.requestId);
  assert.equal(h.state.pendingChatSend, null);
  assert.equal(h.elements.send.disabled, false);
  h.host("capture.cancelled", { action: "imageAnalysis" });
  assert.equal(h.elements.prompt.value, "Neu verfasste Eingabe");
  assert.equal(h.elements.send.getAttribute("aria-label"), "Senden");
});
