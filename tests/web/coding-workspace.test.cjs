const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

// Shared DOM models fragments, keyed reconciliation and real text nodes. Only
// static app icon markup is opaque; timeline/Markdown rendering stays real.
const { TestNode } = require("./test-dom.cjs");
class Node extends TestNode {
  set innerHTML(value) { this.markup = String(value); this.textContent = ""; }
  get innerHTML() { return this.markup || ""; }
  get open() { return this.hasAttribute("open"); }
  set open(value) { if (value) this.setAttribute("open", ""); else this.removeAttribute("open"); }
  prepend(...nodes) { for (const node of nodes.reverse()) this.insertBefore(node, this.firstChild); }
  cloneNode(deep = false) {
    const clone = new Node(this.nodeType === 3 ? "#text" : this.tagName, this.nodeType === 3 ? this.nodeValue : "");
    for (const attribute of this.attributes) clone.setAttribute(attribute.name, attribute.value);
    if (deep) for (const child of this.childNodes) clone.append(Node.prototype.cloneNode.call(child, true));
    return clone;
  }
}

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
function harness({ speech = false, codingToolStepsExpanded = false } = {}) {
  const posts = [];
  const state = { selectedToolAction: "coding", activeSessionId: "session-a", codingWorkspacePath: "C:\\Projects\\My App",
    model: "coding/Qwen3.5-27B-UD-Q4_K_XL.gguf~1234567890", isRunning: false,
    codingActivity: new Map(), codingActivityExpanded: new Map(), messageRunStatus: new Map(), messages: [], codingToolStepsExpanded };
  const elements = Object.fromEntries(["appShell", "prompt",
    "messageScroll", "messageList"].map(name => [name, new Node("div")]));
  elements.messageScroll.scrollTop = 50;
  elements.messageScroll.scrollTo = options => { elements.messageScroll.scrollTop = options.top; };
  const context = vm.createContext({ state, elements, URL,
    chatScroll: { following: false, refresh() {}, jump() {}, restore() {} },
    document: { body: new Node("body"), createElement: tag => new Node(tag), createTextNode: text => new Node("#text", text), createDocumentFragment: () => new Node("#fragment") },
    post: (type, payload) => posts.push({ type, payload }),
    goBridge: { post: (type, payload) => posts.push({ type, payload }) },
    setTimeout: () => {}, timeLabel: () => "12:34", annotateReadableSpeechBlocks: () => {},
    applySpeechHighlight: () => {}, requestAnimationFrame: callback => callback(),
    createToolIcon: () => new Node("svg"), toolVisuals: { coding: ["", "coding-icon"] },
    createArtifactList: items => {
      const list = new Node("div");
      list.className = "message-artifacts";
      for (const item of items) {
        const card = new Node("section");
        card.className = "artifact-card";
        card.dataset.artifactId = item.id;
        card.textContent = item.fileName;
        list.append(card);
      }
      return list;
    },
    messageCopyIcon: "copy", messagePdfIcon: "pdf", messageDoneIcon: "done"
  });
  for (const name of ["visibleModelLabel", "codingToolLabel", "codingStepState", "normalizeCodingStep", "recordCodingActivity", "compareReasoningStepUpdates",
    "createCodingActivity", "mergeCodingToolSteps", "codingPreviewHtml", "openCodingPreview", "closeCodingPreview", "enhanceCodingCodeBlocks", "renderCodingWorkspace", "renderCodingChanges", "applyCodingChanges", "cleanStatusMetadata",
    "uniqueStatusParts", "isTerminalMessageStatus", "statusLabel", "runStatusText", "sanitizeVisibleMessageContent", "createMessage",
    "createMessageFooter", "createMessageIconAction", "createMessageFooterLink", "flashMessageAction", "scrollMessageToTop", "renderMessages", "renderCodingMessages",
    "conversationMessagesDiffer", "sortCommittedMessages", "pruneTerminalMessageRunStatuses", "requestConversationRefresh", "acceptCommittedRevision",
    "applyCommittedMessage", "applyConversationSnapshot", "preparePdfMedia", "preparePdfMessage"]) {
    const start = app.indexOf(`  function ${name}(`);
    const next = app.indexOf(name === "preparePdfMessage" ? "\n  globalThis.goPrepareBookPdf" : "\n  function ", start + 1);
    assert.ok(start >= 0 && next > start, `production ${name} exists`);
    vm.runInContext(app.slice(start, next), context);
  }
  for (const script of ["markdown.js", "coding-timeline.js"]) vm.runInContext(fs.readFileSync(path.join(webRoot, script), "utf8"), context);
  if (speech) {
    for (const name of ["speechBlockCandidates", "annotateReadableSpeechBlocks"]) {
      const start = app.indexOf(`  function ${name}(`);
      const next = app.indexOf("\n  function ", start + 1);
      assert.ok(start >= 0 && next > start, `production ${name} exists`);
      vm.runInContext(app.slice(start, next), context);
    }
  }
  return { context, state, elements, posts };
}

const message = (extra = {}) => ({ id: "answer-1", role: "assistant", content: "**Fertig.**", status: "completed", ...extra });

test("General steering live receipts opt into the same ordered persisted timeline without replacing its answer", () => {
  const { context, state, elements } = harness();
  state.selectedToolAction = null;
  const first = "Zuerst die Erklärung.\n\n";
  state.messages = [message({ status: "streaming", content: first + "Nun die gewünschte Korrektur." })];
  const receipt = { id: "steer-1", tool: "assistant.steering", status: "running", contentOffset: first.length,
    detail: "Bitte korrigiere zuerst das Beispiel.", inputJson: '{"inputId":"input-1","sequence":1}', updatedAt: "2026-09-19T10:00:01Z" };
  context.recordCodingActivity({ messageId: "answer-1", sessionId: "session-a", toolStep: receipt });
  context.renderMessages(false);
  const article = elements.messageList.querySelector("article");
  assert.equal(article.querySelectorAll(".steering-message").length, 1);
  assert.ok(article.textContent.includes(first.trim()));
  assert.ok(article.textContent.includes("Nun die gewünschte Korrektur."));
  assert.equal(article.querySelectorAll(".coding-step").length, 0);
  context.applyConversationSnapshot({ activeSessionId: "session-a", conversationRevision: 3, messages: [
    { ...state.messages[0], status: "completed", toolSteps: [{ ...receipt, status: "completed", updatedAt: "2026-09-19T10:00:02Z" }] }
  ] });
  assert.equal(elements.messageList.querySelector("article"), article);
  assert.equal(article.querySelectorAll(".steering-message").length, 1);
  assert.ok(article.querySelector(".steering-message").textContent.includes("Angewendet"));
  assert.equal(state.messages[0].id, "answer-1");
});

test("late thumbnails remain attached to their original tool before subsequent narration and survive restored sessions", () => {
  for (const mode of ["coding", null]) {
    const { context, state, elements } = harness();
    state.selectedToolAction = mode;
    const first = "Ich analysiere das erste Bild.\n\n";
    const second = "Ich ändere jetzt die Szene.\n\n";
    const third = "Das zweite Bild ist geprüft.";
    const firstTool = { id: "image-1", tool: "media.analyze", status: "completed", contentOffset: first.length };
    const firstArtifact = { id: "thumb-1", stepId: "image-1", fileName: "first.png" };
    state.messages = [message({ content: first, status: "streaming", toolSteps: [firstTool], artifacts: [] })];
    context.renderMessages(false);
    const article = elements.messageList.querySelector("article");
    state.messages[0].artifacts = [firstArtifact];
    context.renderMessages(false);
    assert.equal(elements.messageList.querySelector("article"), article);
    const firstStep = article.querySelector('[data-step-id="image-1"]');
    assert.ok(firstStep.querySelector('[data-artifact-id="thumb-1"]'), "a late artifact invalidates the cached tool rendering");
    assert.equal(firstStep.querySelector("details").hasAttribute("open"), false);
    assert.equal(firstStep.querySelector("details .artifact-card"), null, "the thumbnail remains visible outside the collapsed details");

    const finished = { ...state.messages[0], status: "completed", content: first + second + third, toolSteps: [
      firstTool,
      { id: "edit", tool: "coding.edit", status: "completed", contentOffset: first.length + second.length },
      { id: "render", tool: "blender.execute", status: "completed", contentOffset: first.length + second.length },
      { id: "image-2", tool: "media.analyze", status: "completed", contentOffset: first.length + second.length }
    ], artifacts: [firstArtifact, firstArtifact, { id: "thumb-2", stepId: "image-2", fileName: "second.png" }] };
    state.messages = [finished];
    context.renderMessages(false);
    const assertOrder = list => {
      const current = list.querySelector("article");
      const timeline = current.querySelector(".coding-timeline");
      assert.deepEqual(timeline.children.map(item => item.dataset.stepId || item.textContent.trim()),
        [first.trim(), "image-1", second.trim(), "edit", "render", "image-2", third]);
      assert.equal(current.querySelectorAll(".artifact-card").length, 2, "replayed artifacts are not duplicated");
      assert.equal(timeline.querySelector('[data-step-id="image-1"] .artifact-card').dataset.artifactId, "thumb-1");
      assert.equal(timeline.querySelector('[data-step-id="image-2"] .artifact-card').dataset.artifactId, "thumb-2");
    };
    assertOrder(elements.messageList);
    state.messages = [];
    context.renderMessages(false);
    state.messages = [JSON.parse(JSON.stringify(finished))];
    context.renderMessages(false);
    assertOrder(elements.messageList);
    const restored = harness();
    restored.state.selectedToolAction = mode;
    restored.context.applyConversationSnapshot({ activeSessionId: "session-a", conversationRevision: 1,
      messages: [JSON.parse(JSON.stringify(finished))] });
    assertOrder(restored.elements.messageList);
  }
});

test("a legacy thumbnail with an unavailable step remains visible instead of being silently discarded", () => {
  const { context } = harness();
  const article = context.createMessage(message({ artifacts: [{ id: "legacy", stepId: "unknown-step", fileName: "old.png" }] }));
  assert.equal(article.querySelectorAll(".artifact-card").length, 1);
});

test("identical failure fallback appears once after live completion and reload while tools and copy/export remain intact", async () => {
  for (const mode of ["coding", null]) {
    for (const hasTools of [false, true]) {
      const { context, state, elements, posts } = harness({ codingToolStepsExpanded: true });
      state.selectedToolAction = mode;
      const error = "Der native Modellserver ist nicht erreichbar.";
      const tool = { id: "read", tool: "coding.read", status: "completed", contentOffset: error.length,
        inputJson: '{"path":"math.py"}', outputJson: '{"content":"return a * b"}' };
      const pending = message({ sessionId: "session-a", status: "streaming", content: "", revision: 1,
        toolSteps: hasTools ? [tool] : [] });
      context.applyCommittedMessage({ sessionId: "session-a", conversationRevision: 1, message: pending });
      const failed = { ...pending, status: "failed", content: error, error, revision: 2 };
      context.applyCommittedMessage({ sessionId: "session-a", conversationRevision: 2, message: failed });
      let article = elements.messageList.querySelector("article");
      assert.equal(article.textContent.split(error).length - 1, 1, "the final live error has no duplicate narration");
      context.applyConversationSnapshot({ activeSessionId: "session-a", conversationRevision: 2, messages: [failed] });
      article = elements.messageList.querySelector("article");
      assert.equal(article.textContent.split(error).length - 1, 1, "reopening a stored failure keeps one error block");
      assert.equal(article.querySelector(".message-error p").textContent, error);
      assert.equal(article.querySelectorAll(".coding-step").length, hasTools ? 1 : 0);
      assert.equal(article.querySelector(".coding-narration"), null);
      assert.equal(state.messages[0].content, error, "the transport and persisted message are not rewritten");
      if (hasTools) {
        assert.ok(article.querySelector(".coding-step__content").textContent.includes("return a * b"));
        assert.equal(failed.toolSteps[0].contentOffset, error.length);
      }
      await article.querySelectorAll("button").find(button => button.getAttribute("aria-label") === "Nachricht kopieren").dispatch("click");
      assert.equal(posts.at(-1).type, "message.copy");
      assert.equal(posts.at(-1).payload.text, error);
      await article.querySelectorAll("button").find(button => button.getAttribute("aria-label") === "Nachricht als PDF exportieren").dispatch("click");
      assert.equal(posts.at(-1).type, "message.exportPdf");
      assert.equal(posts.at(-1).payload.messageId, failed.id);
      const pdf = context.preparePdfMessage(article);
      assert.equal(pdf.textContent.split(error).length - 1, 1);
      if (hasTools) assert.equal(pdf.querySelector(".coding-step__disclosure").hasAttribute("open"), true);
    }
  }
});

test("an error never removes distinct generated narration or changes its persisted tool offsets", () => {
  const { context } = harness();
  const before = "Ich habe die Datei 🔎 geprüft.\n\n";
  const after = "Die Funktion multipliziert korrekt.";
  const error = "Die abschließende Prüfung wurde unterbrochen.";
  const source = message({ status: "failed", content: before + after, error, toolSteps: [
    { id: "read", tool: "coding.read", status: "completed", contentOffset: before.length,
      inputJson: '{"path":"math.py"}', outputJson: '{"content":"return a * b"}' }
  ] });
  const article = context.createMessage(source);
  assert.deepEqual(article.querySelector(".coding-timeline").children.map(child => child.dataset.stepId || child.textContent.trim()),
    [before.trim(), "read", after]);
  assert.equal(article.querySelector(".message-error p").textContent, error);
  assert.equal(source.content, before + after);
  assert.equal(source.toolSteps[0].contentOffset, before.length);
  const partiallyMatching = context.createMessage(message({ status: "failed", content: before + error, error }));
  assert.ok(partiallyMatching.querySelector(".message-content").textContent.includes(error), "substring matches are still generated content");
});

test("conversation snapshots restore persisted errors as literal text in Coding and General", () => {
  for (const mode of ["coding", null]) {
    const { context, state, elements } = harness();
    state.selectedToolAction = mode;
    const error = 'Der Serverlauf wurde beendet.\n<script>parent.goBridge.post("session.clear", {})</script>';
    const restored = JSON.parse(JSON.stringify(message({ status: "failed", error, revision: 1, sessionId: "session-a" })));
    context.applyConversationSnapshot({ activeSessionId: "session-a", conversationRevision: 1, messages: [restored] });
    assert.equal(state.messages[0].error, error);
    assert.equal(elements.messageList.querySelectorAll(".message-error").length, 1);
    assert.equal(elements.messageList.querySelector(".message-error p").textContent, error);
    assert.equal(elements.messageList.querySelector("script"), null);
    assert.ok(elements.messageList.querySelector(".message-footer"));

    const updated = { ...restored, error: "Die Verbindung zur nativen Runtime wurde unterbrochen." };
    assert.equal(context.conversationMessagesDiffer([restored], [updated]), true, "an error-only change must be observable");
    context.applyConversationSnapshot({ activeSessionId: "session-a", conversationRevision: 2, messages: [updated] });
    assert.equal(elements.messageList.querySelectorAll(".message-error").length, 1);
    assert.equal(elements.messageList.querySelector(".message-error p").textContent, updated.error);
  }
});

test("committed live failures retain the article and expanded tool while adding the final error once", () => {
  const { context, state, elements } = harness();
  context.document.body.append(elements.messageScroll);
  elements.messageScroll.append(elements.messageList);
  Object.assign(elements.messageScroll, { scrollTop: 400, scrollHeight: 2000, clientHeight: 300 });
  const tool = { id: "command-1", tool: "coding.command", status: "running",
    inputJson: '{"executable":"python","arguments":["checks.py"]}', outputJson: '{"stdout":"Test läuft"}' };
  const pending = message({ sessionId: "session-a", status: "streaming", content: "Ich prüfe die Änderung.", revision: 1, toolSteps: [tool] });
  context.applyCommittedMessage({ sessionId: "session-a", conversationRevision: 1, message: pending });
  const article = elements.messageList.querySelector("article");
  const disclosure = article.querySelector(".coding-step__disclosure");
  disclosure.setAttribute("open", "");
  state.messageRunStatus.set(pending.id, { status: "Denkt nach", detail: "Modellausgabe läuft" });
  const failed = { ...pending, status: "failed", revision: 2, error: "Die Modellverbindung ist abgebrochen.",
    toolSteps: [{ ...tool, status: "failed", outputJson: '{"stdout":"Test läuft","stderr":"Verbindung unterbrochen"}' }] };
  context.applyCommittedMessage({ sessionId: "session-a", conversationRevision: 2, message: failed });
  assert.equal(elements.messageList.querySelector("article"), article);
  assert.equal(article.querySelector(".coding-step__disclosure"), disclosure);
  assert.equal(disclosure.hasAttribute("open"), true);
  assert.equal(article.querySelectorAll(".message-error").length, 1);
  assert.equal(article.querySelector(".message-error p").textContent, failed.error);
  assert.equal(article.querySelector(".coding-live-phase"), null);
  assert.equal(state.messageRunStatus.has(pending.id), false);
  assert.ok(article.querySelector(".message-footer"));
  context.applyCommittedMessage({ sessionId: "session-a", conversationRevision: 2, message: failed });
  assert.equal(article.querySelectorAll(".message-error").length, 1, "replayed final commits do not duplicate the error");

  context.applyCommittedMessage({ sessionId: "session-a", conversationRevision: 3,
    message: { ...pending, revision: 3, error: null, toolSteps: [] } });
  assert.equal(article.querySelector(".message-error"), null, "a retried message removes its previous persisted error");
  assert.ok(article.querySelector(".message-footer"));
});

test("PDF preparation expands cloned tool disclosures and keeps errors and complete output without changing the chat", () => {
  const { context, state } = harness();
  const stdout = "First line\n" + "complete captured output ".repeat(500) + "\nLAST LINE";
  const failed = message({ status: "failed", error: "Die abschließende Modellantwort wurde unterbrochen.", toolSteps: [
    { id: "test", tool: "coding.command", status: "completed", outputJson: JSON.stringify({ stdout, stderr: "", exitCode: 0 }) }
  ] });
  state.messages = [failed];
  const source = context.createMessage(failed);
  const sourceDisclosure = source.querySelector(".coding-step__disclosure");
  assert.equal(sourceDisclosure.hasAttribute("open"), false);
  assert.equal(sourceDisclosure.querySelector(".coding-step__content"), null, "the live closed receipt has no body to clone");
  const pdf = context.preparePdfMessage(source);
  assert.equal(pdf.querySelector(".coding-step__disclosure").hasAttribute("open"), true);
  assert.equal(sourceDisclosure.hasAttribute("open"), false, "export changes only its clone");
  assert.equal(sourceDisclosure.querySelector(".coding-step__content"), null, "PDF generation does not materialize the live chat");
  assert.ok(pdf.querySelector(".coding-step__content").textContent.includes(stdout));
  assert.equal(pdf.querySelector(".message-error p").textContent, failed.error);
  assert.equal(pdf.querySelector("button"), null);
  assert.equal(pdf.querySelector(".message-footer"), null);
  assert.ok(source.querySelector(".message-footer"));
});

test("PDF export includes every lazy patch and legacy receipt once and preserves already open contents", async () => {
  const { context, state } = harness();
  const diff = "diff --git a/main.py b/main.py\n--- a/main.py\n+++ b/main.py\n@@ -1 +1 @@\n-before\n+after 日本語\n";
  const sourceMessage = message({ toolSteps: [
    { id: "edit", tool: "coding.edit", status: "completed", outputJson: JSON.stringify({ diff, applied: true }) },
    { id: "legacy", tool: "coding.read", status: "completed", detail: "Full historical receipt" }
  ] });
  state.messages = [sourceMessage];
  const source = context.createMessage(sourceMessage);
  const originalEditBody = source.querySelector('[data-step-id="edit"] .coding-step__content');
  assert.equal(originalEditBody, null, "file changes follow the collapsed preference");
  const openDisclosure = source.querySelector('[data-step-id="legacy"] details');
  openDisclosure.open = true;
  await openDisclosure.dispatch("toggle");
  const originalBody = openDisclosure.querySelector(".coding-step__content");
  const pdf = context.preparePdfMessage(source);
  assert.equal(pdf.querySelectorAll(".coding-step__content").length, 2);
  assert.ok(pdf.textContent.includes("+after 日本語"));
  assert.equal(pdf.textContent.split("Full historical receipt").length - 1, 1);
  assert.equal(pdf.querySelectorAll("button").length, 0);
  assert.equal(source.querySelector('[data-step-id="edit"] .coding-step__content'), originalEditBody,
    "export preserves the original live diff body");
  assert.equal(openDisclosure.querySelector(".coding-step__content"), originalBody);
});

test("empty General chat greets without an industry focus and Coding keeps its project guidance", () => {
  const { context, state, elements } = harness();
  state.selectedToolAction = null;
  context.renderMessages(false);
  assert.equal(elements.messageList.querySelector(".chat-empty-state").textContent, "Wobei kann ich dich unterstützen?");
  state.selectedToolAction = "coding";
  context.renderMessages(false);
  assert.ok(elements.messageList.textContent.includes("Woran arbeiten wir?"));
  assert.ok(elements.messageList.textContent.includes("Frage zum Projekt"));
  state.selectedToolAction = null;
  context.renderMessages(false);
  assert.equal(elements.messageList.textContent, "Wobei kann ich dich unterstützen?");
});

test("stored HTML creates an isolated frame only after a click and survives message rerenders", () => {
  const { context, state } = harness({ codingToolStepsExpanded: true });
  const html = "<script>parent.goBridge.post('session.clear', {});</script><button onclick=\"this.textContent='OK'\">Test</button>";
  const tool = { id: "html #1", tool: "coding.renderHtml", status: "completed", previewHtml: html };
  context.recordCodingActivity({ messageId: "answer-1", toolStep: tool });
  assert.equal(context.mergeCodingToolSteps(message({ toolSteps: [tool] }))[0].previewHtml, html);
  const panel = context.createCodingActivity(message({ toolSteps: [tool] }));
  assert.equal(context.document.body.childNodes.length, 0);
  assert.equal(panel.querySelector("iframe"), null);
  assert.ok(!panel.textContent.includes("parent.goBridge"));
  panel.querySelector(".coding-preview-button").listeners.click();
  const dialog = state.codingPreviewDialog;
  assert.equal(dialog.open, true);
  const frame = dialog.querySelector("iframe");
  assert.equal(frame.attributes.sandbox, "allow-scripts");
  assert.equal(frame.attributes.referrerpolicy, "no-referrer");
  assert.equal(frame.src, "https://go-coding-preview.local/coding/answer-1/html%20%231");
  assert.equal(frame.srcdoc, undefined);
  assert.equal(frame.textContent, "");
  context.createCodingActivity(message({ toolSteps: [tool] }));
  assert.equal(state.codingPreviewDialog, dialog, "stream updates do not recreate an opened preview");
  context.closeCodingPreview();
  assert.equal(dialog.removed, true);
  assert.equal(state.codingPreviewDialog, null);
});

test("only successful HTML steps offer previews, and stale session buttons cannot open them", () => {
  const { context, state } = harness({ codingToolStepsExpanded: true });
  const tool = { id: "html", tool: "coding.renderHtml", status: "completed", previewHtml: "<b>OK</b>" };
  for (const step of [{ ...tool, status: "running" }, { ...tool, status: "denied" }, { ...tool, status: "failed" },
    { ...tool, tool: "coding.read" }, { ...tool, previewHtml: "x".repeat(16001) }, { ...tool, previewHtml: "  " }]) {
    assert.equal(context.createCodingActivity(message({ toolSteps: [step] })).querySelector(".coding-preview-button"), null);
  }
  const panel = context.createCodingActivity(message({ toolSteps: [tool] }));
  state.activeSessionId = "another-session";
  panel.querySelector(".coding-preview-button").listeners.click();
  assert.equal(context.document.body.childNodes.length, 0);
  assert.match(app, /if \(sessionChanged\) \{ closeCodingPreview\(\); state\.changesSummary = null; \}/);
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  assert.ok(html.includes("frame-src https://go-coding-preview.local;"));
  assert.ok(html.includes("script-src 'self';"), "app script policy stays strict");
});

test("Coding and General share one header and no duplicate composer status", () => {
  const { context, state, elements } = harness();
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  const css = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8")
    + fs.readFileSync(path.join(webRoot, "coding-timeline.css"), "utf8");
  assert.match(html, /<h1 id="chat-heading">AI Assistent<\/h1>/);
  assert.doesNotMatch(html, /agent-tabs|Hauptagent|Subagent/);
  assert.doesNotMatch(app, /goAgentTabs|selectedAgentTab|subagent\.start/);
  assert.doesNotMatch(html, /id="coding-(?:project|model)"/);
  assert.doesNotMatch(css, /\.coding-mode\s+\.(?:chat-header|chat-title-row|chat-heading-group|header-actions)\b/);
  context.renderCodingWorkspace();
  assert.ok(elements.appShell.classList.contains("coding-mode"));
  state.isRunning = true;
  state.runStatus = "Quellen recherchieren";
  state.runDetail = "3 Quellen gelesen";
  context.renderCodingWorkspace();
  assert.equal(state.codingWorkspacePath, "C:\\Projects\\My App");
  assert.doesNotMatch(html, /coding-progress/);
  state.selectedToolAction = null;
  context.renderCodingWorkspace();
  assert.equal(elements.appShell.className.includes("coding-mode"), false);
  assert.equal(elements.prompt.placeholder, "Nachricht eingeben …");
});

test("running Coding status remains in the assistant message only", () => {
  const { context, state, elements } = harness();
  for (const workspace of ["C:\\Project", null]) {
    state.codingWorkspacePath = workspace;
    context.renderCodingWorkspace();
  }
  state.isRunning = true;
  state.runStatus = "Terminal läuft";
  state.runDetail = "Tests ausführen";
  context.renderCodingWorkspace();
  state.messageRunStatus.set("answer-1", { status: state.runStatus, detail: state.runDetail });
  const panel = context.createCodingActivity(message({ status: "running" }));
  assert.match(panel.querySelector(".coding-live-phase").textContent, /Terminal läuft/);
  assert.match(panel.querySelector(".coding-live-phase").textContent, /Tests ausführen/);
  state.isRunning = false;
  context.renderCodingWorkspace();
  assert.equal(context.createCodingActivity(message()), null, "finished runs clear the live phase");
  const css = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8");
  assert.doesNotMatch(css, /coding-progress/);
  assert.doesNotMatch(app, /codingProgress/);
});

test("the prompt has no workspace picker and Coding guidance points to sidebar projects", () => {
  const { context, state, elements, posts } = harness();
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  assert.doesNotMatch(html, /id="coding-workspace"|workspace-button/);
  assert.doesNotMatch(app, /coding\.pickWorkspace|pickCodingWorkspace|elements\.codingWorkspace/);
  state.selectedToolAction = null;
  context.renderCodingWorkspace();
  assert.equal(state.codingWorkspacePath, "C:\\Projects\\My App", "the project workspace remains unchanged");
  state.selectedToolAction = "coding";
  state.codingWorkspacePath = null;
  context.renderCodingWorkspace();
  context.renderMessages(false);
  assert.match(elements.messageList.textContent, /Projekte in der Sidebar/);
  assert.equal(posts.length, 0);
});

test("inline tool steps preserve every supplied input, stdout, stderr and diff line at full height", () => {
  const { context } = harness({ codingToolStepsExpanded: true });
  const json = JSON.stringify({ argument: "x".repeat(9000), finalMarker: "INPUT-END" });
  const stdout = Array.from({ length: 400 }, (_, index) => `stdout ${index} ${"x".repeat(60)}`).join("\n");
  const stderr = Array.from({ length: 200 }, (_, index) => `stderr ${index}`).join("\n");
  const diff = Array.from({ length: 250 }, (_, index) => `-old ${index}\n+new ${index}`).join("\n");
  const sources = [json, stdout, stderr, diff];
  const detail = sources.map((source, index) => `\x60\x60\x60${index === 3 ? "diff" : "text"}\n${source}\n\x60\x60\x60`).join("\n\n");
  const panel = context.createCodingActivity(message({ toolSteps: [
    { id: "complete-output", tool: "coding.command", status: "completed", detail }
  ] }));
  assert.deepEqual(panel.querySelectorAll("pre code").map(node => node.textContent), sources);
  const css = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8")
    + fs.readFileSync(path.join(webRoot, "coding-timeline.css"), "utf8");
  const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)];
  for (const selector of [".coding-timeline .coding-step__detail", ".coding-timeline .coding-step__detail .code-block pre",
    ".coding-timeline .coding-step__detail .code-block pre code", ".coding-output__text", ".coding-result__text"]) {
    const rule = rules.find(match => match[1].split(",").map(item => item.trim()).includes(selector))?.[2] || "";
    assert.match(rule, /max-height:\s*none;/);
    assert.match(rule, /overflow:\s*visible;/);
  }
  assert.ok(css.includes("white-space: pre-wrap; overflow-wrap: anywhere;"));
});

test("an earlier growing tool keeps the visible later step anchored without following the bottom", () => {
  const { context, state, elements } = harness();
  context.document.body.append(elements.messageScroll);
  elements.messageScroll.append(elements.messageList);
  Object.assign(elements.messageScroll, { scrollTop: 400, scrollLeft: 12, scrollHeight: 2000, clientHeight: 300 });
  state.messages = [message({ content: "", status: "streaming", toolSteps: [
    { id: "first", tool: "coding.command", status: "running", detail: "Erste Ausgabe" },
    { id: "second", tool: "coding.read", status: "completed", detail: "Sichtbare Datei" }
  ] })];
  context.renderMessages(false);
  const article = elements.messageList.querySelector(".message");
  const [first, second] = article.querySelectorAll(".coding-step");
  let grew = false;
  article.getBoundingClientRect = () => ({ top: -400, bottom: 1400 });
  first.getBoundingClientRect = () => ({ top: -380, bottom: 50 });
  second.getBoundingClientRect = () => ({ top: grew ? 220 : 120, bottom: grew ? 400 : 300 });
  const createMessage = context.createMessage;
  context.createMessage = item => { const next = createMessage(item); grew = true; return next; };
  state.messages[0].toolSteps[0].detail += "\nWeitere Ausgabe über dem betrachteten Schritt.";
  context.renderMessages(false);

  assert.equal(elements.messageList.querySelectorAll(".coding-step")[1], second);
  assert.equal(elements.messageScroll.scrollTop, 500, "the visible step, not its tall parent message, is the reading anchor");
  assert.equal(elements.messageScroll.scrollLeft, 12);
});

test("completing a cached streaming timeline refreshes readable narration and code speech annotations", () => {
  const { context, state, elements } = harness({ speech: true });
  const before = "Ich lese die Funktion.\n\n";
  const tail = "Die Prüfung ist beendet.\n\n```python\nreturn a * b\n```";
  const toolSteps = [{ id: "read", tool: "coding.read", status: "completed", contentOffset: before.length, detail: "Datei gelesen." }];
  state.messages = [message({ content: before + tail, status: "streaming", updatedAt: "2026-09-11T12:00:00Z", toolSteps })];
  context.renderMessages(false);
  const article = elements.messageList.querySelector(".message");
  const completedTool = article.querySelector(".coding-step");
  assert.equal(article.querySelectorAll("[data-speech-block-kind]").length, 0);

  state.messages = [{ ...state.messages[0], status: "completed", updatedAt: "2026-09-11T12:00:03Z" }];
  context.renderMessages(false);
  assert.equal(elements.messageList.querySelector(".message"), article);
  assert.equal(article.querySelector(".coding-step"), completedTool);
  const paragraphs = article.querySelectorAll('[data-speech-block-kind="paragraph"]');
  assert.deepEqual(paragraphs.map(node => node.dataset.speechBlockIndex), ["0", "1"]);
  assert.deepEqual(paragraphs.map(node => node.textContent), [before.trim(), "Die Prüfung ist beendet."]);
  const code = article.querySelector('[data-speech-block-kind="code"]');
  assert.ok(code);
  assert.equal(code.dataset.speechBlockIndex, "0");
  assert.ok(code.textContent.includes("return a * b"));
  assert.equal(completedTool.querySelectorAll("[data-speech-block-kind]").length, 0, "tool output is not inserted into spoken answer narration");
  assert.equal(article.dataset.messageUpdatedAt, "2026-09-11T12:00:03Z");
});

test("real tool IDs update once in both modes and stale sessions cannot populate their steps", () => {
  const { context, state } = harness();
  const event = { sessionId: "session-a", messageId: "answer-1", toolStep: { id: "call-1", tool: "web.deepResearch", status: "running", detail: "Suche" } };
  context.recordCodingActivity(event);
  context.recordCodingActivity({ ...event, toolStep: { ...event.toolStep, status: "completed", detail: "3 Quellen gelesen" } });
  context.recordCodingActivity({ ...event, sessionId: "other-session" });
  assert.equal(state.codingActivity.get("answer-1").length, 1);
  assert.equal(state.codingActivity.get("answer-1")[0].label, "Quellen recherchieren");
  assert.equal(state.codingActivity.get("answer-1")[0].status, "completed");
  state.selectedToolAction = null;
  context.recordCodingActivity({ ...event, messageId: "general-answer" });
  assert.equal(state.codingActivity.get("general-answer").length, 1);
  state.messages = [message({ id: "general-answer", content: "Recherche", status: "streaming" })];
  context.renderMessages(false);
  assert.ok(context.elements.messageList.querySelector(".coding-timeline"), "live General tools use the same chronological timeline");
});

test("token heartbeat updates the existing phase without manufacturing extra steps", () => {
  const { context, state } = harness();
  for (let seconds = 1; seconds <= 30; seconds++) {
    context.recordCodingActivity({ messageId: "answer-1", runStatus: "Coding-Modell arbeitet", runDetail: `${seconds} s` });
  }
  assert.equal(state.codingActivity.get("answer-1").length, 1);
  assert.equal(state.codingActivity.get("answer-1")[0].detail, "30 s");
});

test("live command output supersedes persisted start input and final result replaces it once", () => {
  const { context, state } = harness({ codingToolStepsExpanded: true });
  const start = { id: "command-1", tool: "coding.command", status: "running", detail: "Eingabe: python test.py" };
  const pending = message({ status: "streaming", toolSteps: [start] });
  for (const output of ["Test 1 läuft", "Test 1 läuft\nTest 1 bestanden\nTest 2 läuft"]) {
    context.recordCodingActivity({ sessionId: "session-a", messageId: "answer-1",
      toolStep: { ...start, detail: `${start.detail}\n\n\x60\x60\x60text\n${output}\n\x60\x60\x60` } });
    const steps = context.mergeCodingToolSteps(pending);
    assert.equal(steps.length, 1);
    assert.ok(steps[0].detail.includes(output), "the stored running start must not overwrite live partial output");
    const panel = context.createCodingActivity(pending);
    assert.equal(panel.querySelectorAll(".coding-step").length, 1);
    assert.ok(panel.querySelector(".coding-step__detail").textContent.includes(output));
  }
  const final = { ...start, status: "completed", detail: "Ergebnis:\n\n\x60\x60\x60text\n2 Tests bestanden\n\x60\x60\x60" };
  const committed = message({ toolSteps: [final] });
  assert.equal(context.mergeCodingToolSteps(committed)[0].detail, final.detail);
  let panel = context.createCodingActivity(committed);
  assert.equal(panel.querySelectorAll(".coding-step").length, 1);
  assert.ok(panel.textContent.includes("2 Tests bestanden"));
  assert.ok(!panel.textContent.includes("Test 2 läuft"));
  state.codingActivity.clear();
  panel = context.createCodingActivity(committed);
  assert.equal(panel.querySelectorAll(".coding-step").length, 1, "restart uses the single persisted final result");
  assert.ok(panel.textContent.includes("2 Tests bestanden"));
});

test("persisted tools restore after restart and merge without regressing a completed live tool", () => {
  const { context, state } = harness();
  const tool = { id: "call-1", tool: "coding.read", status: "completed", detail: "Datei gelesen" };
  assert.equal(context.createCodingActivity(message()), null, "old messages do not get invented work");
  let panel = context.createCodingActivity(message({ toolSteps: [tool] }));
  assert.equal(panel.querySelectorAll(".coding-step").length, 1);
  assert.ok(panel.textContent.includes("Datei lesen"));
  assert.ok(panel.textContent.includes("Abgeschlossen"));
  context.recordCodingActivity({ messageId: "answer-1", toolStep: tool });
  let steps = context.mergeCodingToolSteps(message({ toolSteps: [{ ...tool, status: "running" }] }));
  assert.equal(steps.length, 1);
  assert.equal(steps[0].status, "completed");
  state.codingActivity.get("answer-1")[0].status = "running";
  steps = context.mergeCodingToolSteps(message({ toolSteps: [{ ...tool, status: "denied" }] }));
  assert.equal(steps[0].status, "denied");
  panel = context.createCodingActivity(message({ toolSteps: [tool] }));
  assert.equal(panel.tagName, "DIV");
  assert.equal(panel.querySelector("details.coding-activity"), null, "tools are not grouped into an aggregate disclosure");
  assert.ok(panel.querySelector("details.coding-step__disclosure"));
});

test("unacknowledged tools are not presented as successful after cancellation", () => {
  const { context } = harness();
  const panel = context.createCodingActivity(message({ status: "cancelled", toolSteps: [
    { id: "call-1", tool: "coding.command", status: "running", detail: "pytest" }
  ] }));
  assert.ok(panel.textContent.includes("Nicht abgeschlossen"));
  assert.ok(!panel.textContent.includes("Abgeschlossen"));
});

test("completed answers show exactly four real tools and discard all transient phase history", () => {
  const { context, state } = harness();
  for (const status of ["Denkt nach", "In Warteschlange", "Modell gewählt", "Kontext bereit", "Modell wird geladen", "Modell generiert"]) {
    context.recordCodingActivity({ messageId: "answer-1", runStatus: status, runDetail: "Flüchtiger Status" });
  }
  const toolSteps = ["coding.list", "coding.search", "coding.read", "coding.edit"].map((tool, index) =>
    ({ id: `tool-${index}`, tool, status: "completed", detail: "Tatsächliches Ergebnis" }));
  // Even a delayed live status cannot revive a phase on an already completed answer.
  state.messageRunStatus.set("answer-1", { status: "Modell generiert", detail: "Flüchtiger Status" });
  const panel = context.createCodingActivity(message({ toolSteps }));
  assert.equal(panel.querySelectorAll(".coding-step").length, 4);
  assert.equal(panel.querySelector(".coding-activity__count"), null);
  assert.ok(!panel.textContent.includes("Werkzeugschritte"));
  assert.deepEqual(panel.querySelectorAll(".coding-step__title strong").map(node => node.textContent),
    ["Projekt erkunden", "Code durchsuchen", "Datei lesen", "Datei bearbeiten"]);
  assert.ok(!panel.textContent.includes("Flüchtiger Status"));
  assert.ok(!panel.textContent.includes("Modell generiert"));
  assert.equal(context.createCodingActivity(message()), null, "phase-only completed answers need no activity panel");
});

test("running answers show real tools followed by only the current live phase", () => {
  const { context, state } = harness();
  for (const status of ["In Warteschlange", "Modell gewählt", "Modell wird geladen"]) {
    context.recordCodingActivity({ messageId: "answer-1", runStatus: status });
  }
  state.messageRunStatus.set("answer-1", { status: "Antwort wird formuliert", detail: "12 s" });
  const panel = context.createCodingActivity(message({ status: "streaming", toolSteps: [
    { id: "read-1", tool: "coding.read", status: "completed", detail: "Datei gelesen" }
  ] }));
  assert.equal(panel.querySelectorAll(".coding-step").length, 1);
  assert.deepEqual(panel.querySelectorAll(".coding-step__title strong").map(node => node.textContent),
    ["Datei lesen"]);
  assert.equal(panel.querySelectorAll(".coding-live-phase").length, 1);
  assert.ok(panel.querySelector(".coding-live-phase").textContent.includes("Antwort wird formuliert"));
  assert.ok(panel.textContent.includes("12 s"));
  assert.ok(!panel.textContent.includes("In Warteschlange"));
  assert.ok(!panel.textContent.includes("Modell gewählt"));
  assert.ok(!panel.textContent.includes("Modell wird geladen"));
});

test("real Markdown diff decoration preserves exact source and copy payload", async () => {
  const { context, posts } = harness();
  const source = '--- a/example.py\n+++ b/example.py\n@@ -1 +1 @@\n-return a + b\n+return a * b\n unchanged\n';
  const root = new Node("div");
  root.append(context.goMarkdown.render("```diff\n" + source + "```"));
  const before = root.querySelector("code").textContent;
  context.enhanceCodingCodeBlocks(root);
  assert.equal(root.querySelector("code").textContent, before);
  assert.equal(root.querySelectorAll(".diff-line--added").length, 1);
  assert.equal(root.querySelectorAll(".diff-line--removed").length, 1);
  assert.equal(root.querySelectorAll(".diff-line--header").length, 3);
  assert.equal(root.querySelector(".code-diff-counts").textContent, "+1 −1");
  await root.querySelector("button").listeners.click();
  assert.equal(posts[0].payload.text, before);
});

test("persisted tool detail renders diff blocks safely with no HTML execution", () => {
  const { context } = harness({ codingToolStepsExpanded: true });
  const panel = context.createCodingActivity(message({ toolSteps: [{ id: "edit-1", tool: "coding.edit", status: "completed",
    detail: '```diff\n-previous\n+<script>alert(1)</script>\n```' }] }));
  assert.equal(panel.querySelectorAll(".code-block--diff").length, 1);
  assert.equal(panel.querySelectorAll("script").length, 0);
  assert.ok(panel.querySelector("code").textContent.includes("<script>alert(1)</script>"));
});

test("all five original message footer actions still execute in Coding and General", () => {
  for (const mode of ["coding", null]) {
    const { context, state, posts, elements } = harness();
    state.selectedToolAction = mode;
    const answer = message({ content: "**Fertig.**\n\n```python\nreturn a * b\n```" });
    const article = context.createMessage(answer);
    assert.equal(article.querySelectorAll(".code-block").length, 1);
    const footer = article.querySelector(".message-footer");
    assert.equal(footer.childNodes.length, 5);
    for (const button of footer.childNodes) button.listeners.click({ preventDefault() {}, stopPropagation() {} });
    assert.deepEqual(posts.map(post => post.type), ["message.copy", "message.exportPdf", "microphone.speak", "workflow.createFromMessage"]);
    assert.equal(posts[0].payload.text, answer.content);
    assert.equal(posts[1].payload.messageId, answer.id);
    assert.equal(posts[2].payload.sessionId, "session-a");
    assert.equal(posts[3].payload.messageId, answer.id);
    assert.equal(elements.messageScroll.scrollTop, 42);
  }
});
