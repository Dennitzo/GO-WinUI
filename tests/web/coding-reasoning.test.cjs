const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
function harness() {
  const posts = [];
  const document = { body: new TestNode("body"), createElement: tag => new TestNode(tag),
    createTextNode: text => new TestNode("#text", text), createDocumentFragment: () => new TestNode("#fragment") };
  const context = vm.createContext({ document, URL, setTimeout: () => {},
    goBridge: { post: (type, payload) => posts.push({ type, payload }) },
    state: { selectedToolAction: "coding", activeSessionId: "session-a", codingActivity: new Map() } });
  for (const file of ["vendor/highlightjs/11.11.1/highlight.min.js", "code-highlighting.js", "markdown.js", "coding-timeline.js"])
    vm.runInContext(fs.readFileSync(path.join(webRoot, file), "utf8"), context, { filename: file });
  const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
  for (const name of ["normalizeCodingStep", "codingToolLabel", "codingStepState", "codingPreviewHtml", "recordCodingActivity",
    "mergeCodingToolSteps", "compareReasoningStepUpdates", "cleanStatusMetadata", "speechBlockCandidates",
    "handleHostMessage", "isTerminalMessageStatus", "persistMeasuredContext"]) {
    const start = app.indexOf(`  function ${name}(`);
    const ending = app.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    const end = ending ? start + ending.index + ending[0].length : -1;
    assert.ok(start >= 0 && end > start);
    vm.runInContext(app.slice(start, end), context, { filename: `app.js:${name}` });
  }
  const render = (message, steps, options = {}) => context.goCodingTimeline.render(message, steps, {
    renderMarkdown: text => context.goMarkdown.render(text), codingToolStepsExpanded: false, ...options });
  return { context, document, posts, render };
}
const message = (extra = {}) => ({ id: "message-a", sessionId: "session-a", role: "assistant", content: "", status: "streaming", ...extra });
const reasoning = (extra = {}) => ({ id: "reasoning-a-1", tool: "assistant.reasoning", detail: "Ich prüfe die relevante Stelle.",
  status: "running", inputJson: '{"round":1,"phase":"main"}', contentOffset: 0, ...extra });

function statusHarness() {
  const result = harness();
  const { context, document, render } = result;
  const status = { status: "Modell generiert", detail: "Runde 2 · 12 s · 250 Token", model: "coding-model" };
  Object.assign(context.state, { isRunning: true, messages: [message()], runStatus: status.status,
    runDetail: status.detail, model: status.model, messageRunStatus: new Map([["message-a", status]]) });
  let timeline;
  const calls = { messages: 0, context: 0, status: 0 };
  context.renderMessages = () => {
    calls.messages++;
    const currentMessage = context.state.messages[0];
    const next = render(currentMessage, context.mergeCodingToolSteps(currentMessage), {
      previousTimeline: timeline, liveStatus: context.state.messageRunStatus.get(currentMessage.id)
    });
    if (timeline) context.goCodingTimeline.reconcile(timeline, next);
    else { timeline = next; document.body.append(timeline); }
  };
  context.renderContext = () => calls.context++;
  context.renderStatus = () => calls.status++;
  context.renderMessages();
  calls.messages = 0;
  return { ...result, calls, status, timeline: () => timeline,
    post: payload => context.handleHostMessage({ detail: { type: "status.changed", payload: {
      sessionId: "session-a", messageId: "message-a", ...payload
    } } }) };
}

test("reasoning status packets grow the live card without replacing or rerendering the generation status", () => {
  const { context, calls, status, post, timeline } = statusHarness();
  const detail = "Ich prüfe die relevante Stelle.";
  post({ runStatus: "Denkt nach", runDetail: "assistant.reasoning",
    toolStep: reasoning({ detail, outputJson: '{"lastEventId":1}' }) });
  const card = timeline().querySelector(".coding-reasoning");
  assert.ok(card);
  for (let cursor = 2; cursor <= 5; cursor++) {
    post({ runStatus: "Denkt nach", runDetail: "assistant.reasoning",
      toolStep: reasoning({ detail: `${detail}\n\n${"Weitere Belege. ".repeat(cursor)}`,
        outputJson: JSON.stringify({ lastEventId: cursor }) }) });
    assert.equal(timeline().querySelector(".coding-reasoning"), card, "streaming updates retain the mounted card");
    assert.ok(card.querySelector(".coding-reasoning__body").textContent.includes("Weitere Belege. ".repeat(cursor).trim()));
    assert.equal(context.state.runStatus, status.status);
    assert.equal(context.state.runDetail, status.detail);
    assert.equal(context.state.messageRunStatus.get("message-a"), status);
    const phase = timeline().querySelector(".coding-live-phase");
    assert.ok(phase.textContent.includes("Modell generiert"));
    assert.ok(phase.textContent.includes(status.detail));
    assert.equal(phase.textContent.includes("Denkt nach"), false);
    assert.equal(phase.textContent.includes("assistant.reasoning"), false);
  }
  assert.equal(calls.messages, 5, "every reasoning packet still renders the live text");
  assert.equal(calls.context, 0, "reasoning tokens do not rebuild the prompt context strip");
  assert.equal(calls.status, 0, "reasoning tokens do not rerender the unrelated overall status");
});

test("model and tool status updates remain functional between display-only reasoning packets", () => {
  const { context, calls, post, timeline } = statusHarness();
  post({ runStatus: "Modell generiert", runDetail: "Runde 3 · 25 s · 700 Token", model: "current-model",
    contextUsed: 12000, contextLimit: 262144 });
  const generation = context.state.messageRunStatus.get("message-a");
  assert.equal(context.state.runDetail, "Runde 3 · 25 s · 700 Token");
  assert.equal(context.state.model, "current-model");
  assert.equal(context.state.contextUsed, 12000);
  assert.equal(context.state.contextLimit, 262144);
  post({ runStatus: "Denkt nach", runDetail: "assistant.reasoning", toolStep: reasoning() });
  assert.equal(context.state.messageRunStatus.get("message-a"), generation);
  post({ runStatus: "Datei lesen", runDetail: "src/main.py", toolStep: {
    id: "read-a", tool: "coding.read", status: "running", detail: "src/main.py"
  } });
  assert.equal(context.state.runStatus, "Datei lesen");
  assert.equal(context.state.runDetail, "src/main.py");
  assert.equal(context.state.messageRunStatus.get("message-a").status, "Datei lesen");
  assert.ok(timeline().querySelector(".coding-step"));
  assert.ok(timeline().querySelector(".coding-reasoning"));
  assert.equal(calls.messages, 3);
  assert.equal(calls.context, 2);
  assert.equal(calls.status, 2);
});

test("steering closes the previous reasoning round without stopping the next round or reopening on replay", () => {
  const { context, status, post, timeline, render } = statusHarness();
  const first = reasoning({ outputJson: '{"lastEventId":1,"state":"running"}' });
  post({ toolStep: first });
  const card = timeline().querySelector(".coding-reasoning");
  assert.ok(card.querySelector(".message-status-spinner"));
  const stopped = { ...first, status: "interrupted", outputJson: '{"lastEventId":2,"state":"steered"}' };
  post({ toolStep: stopped });
  assert.equal(timeline().querySelector(".coding-reasoning"), card);
  assert.equal(card.querySelector(".coding-reasoning__status").textContent, "Umgeleitet");
  assert.equal(card.querySelector(".message-status-spinner"), null);
  const next = reasoning({ id: "reasoning-a-2", detail: "Ich bearbeite den neuen Auftrag.", inputJson: '{"round":2}',
    outputJson: '{"lastEventId":3,"state":"running"}' });
  post({ toolStep: next });
  post({ toolStep: first });
  const cards = timeline().querySelectorAll(".coding-reasoning");
  assert.equal(cards.length, 2);
  assert.equal(cards[0].querySelector(".coding-reasoning__status").textContent, "Umgeleitet");
  assert.equal(cards[0].querySelector(".message-status-spinner"), null);
  assert.ok(cards[1].querySelector(".message-status-spinner"));
  assert.equal(context.state.messageRunStatus.get("message-a"), status);
  assert.equal(context.state.isRunning, true);
  const restored = render(message(), JSON.parse(JSON.stringify([stopped, next])));
  assert.equal(restored.querySelectorAll(".coding-reasoning")[0].querySelector(".coding-reasoning__status").textContent, "Umgeleitet");
  assert.equal(restored.querySelectorAll(".message-status-spinner").length, 1);
});

test("reasoning packets from another session or after completion cannot resurrect a live card", () => {
  for (const condition of ["other-session", "completed", "stopped"]) {
    const { context, post, timeline } = statusHarness();
    if (condition === "completed") context.state.messages[0].status = "completed";
    if (condition === "stopped") context.state.isRunning = false;
    post({ ...(condition === "other-session" ? { sessionId: "session-b" } : {}),
      runStatus: "Denkt nach", runDetail: "assistant.reasoning", toolStep: reasoning() });
    assert.equal(context.state.codingActivity.size, 0, condition);
    assert.equal(timeline().querySelector(".coding-reasoning"), null, condition);
    assert.equal(context.state.runStatus, "Modell generiert", condition);
  }
});

test("reasoning is a separate default-open card with accent identity, round, live status and safe Markdown", () => {
  const { render, posts } = harness();
  const source = "**Gezieltes Vorgehen**\n\n1. `math.py` lesen.\n2. Einen Fehler beheben.\n\n```python\nreturn a + 2\n```";
  const timeline = render(message(), [reasoning({ detail: source })]);
  const card = timeline.querySelector(".coding-reasoning");
  assert.ok(card);
  assert.equal(card.getAttribute("data-speech-exclude"), "true");
  assert.equal(card.getAttribute("aria-label"), "Denkprozess · Runde 1");
  assert.equal(card.querySelector("details").hasAttribute("open"), true);
  assert.equal(card.querySelector("summary").querySelector("button"), null);
  assert.equal(card.querySelector(".coding-reasoning__status").textContent, "Denkt nach");
  assert.ok(card.querySelector(".message-status-spinner"));
  assert.equal(card.querySelector(".coding-reasoning__body strong").textContent, "Gezieltes Vorgehen");
  assert.equal(card.querySelectorAll("li").length, 2);
  assert.equal(card.querySelector(".hljs-keyword").textContent, "return");
  assert.equal(card.querySelector(".coding-reasoning__body").getAttribute("aria-live"), "off");
  assert.equal(timeline.querySelector(".coding-step"), null);
  assert.equal(timeline.querySelector(".coding-step__number"), null);
  assert.equal(timeline.querySelector(".coding-facts"), null);
  assert.equal(timeline.querySelector(".coding-step__copy"), null);
  assert.equal(posts.length, 0, "rendering does not execute bridge calls or speak reasoning");
});

test("live reasoning colors embedded Python commands before fence completion and copies the latest exact source", async () => {
  const { render, context, document, posts } = harness();
  const command = '.venv/Scripts/python.exe -c "import sys,json;info={};\nfor m in (\'numpy\',\'psutil\'):\n    print(m)';
  const initial = reasoning({ detail: "Ich prüfe die Umgebung.\n\n```\n" + command });
  const timeline = render(message(), [initial]);
  document.body.append(timeline);
  const card = timeline.querySelector(".coding-reasoning");
  let code = card.querySelector("pre code");
  assert.equal(code.textContent, command);
  assert.ok(code.querySelectorAll(".hljs-keyword").some(node => node.textContent === "import"));
  assert.ok(code.querySelectorAll(".hljs-keyword").some(node => node.textContent === "for"));
  const complete = command + '\nprint(json.dumps(info))"';
  context.goCodingTimeline.reconcile(timeline, render(message(), [reasoning({
    detail: "Ich prüfe die Umgebung.\n\n```\n" + complete + "\n```\n\nDanach bewerte ich das Ergebnis."
  })], { previousTimeline: timeline }));
  assert.equal(timeline.querySelector(".coding-reasoning"), card);
  code = card.querySelector("pre code");
  assert.equal(code.textContent, complete);
  assert.ok(code.querySelectorAll(".hljs-built_in").some(node => node.textContent === "print"));
  assert.equal(card.getAttribute("data-speech-exclude"), "true");
  assert.equal(posts.length, 0, "streamed reasoning does not trigger speech or any host operation");
  await card.querySelector(".code-header button").dispatch("click");
  assert.equal(posts.length, 1);
  assert.equal(posts[0].type, "message.copy");
  assert.equal(posts[0].payload.text, complete);
});

test("reasoning and narration retain their stream boundaries without consuming execution numbers", () => {
  const { render } = harness();
  const before = "Ich untersuche die Datei.\n\n", after = "Die Änderung ist fertig.";
  const tools = [reasoning(), { id: "read", tool: "coding.read", status: "completed", contentOffset: before.length },
    reasoning({ id: "reasoning-a-2", status: "completed", inputJson: '{"round":2}', contentOffset: before.length }),
    { id: "edit", tool: "coding.edit", status: "completed", contentOffset: before.length }];
  const timeline = render(message({ content: before + after }), tools);
  assert.deepEqual(timeline.children.map(item => item.dataset.stepId || item.textContent.trim()),
    ["reasoning-a-1", before.trim(), "read", "reasoning-a-2", "edit", after]);
  assert.deepEqual(timeline.querySelectorAll(".coding-step__number").map(item => item.textContent), ["01", "02"]);
  assert.equal(timeline.querySelectorAll(".coding-narration").length, 2);
});

test("live reasoning appends to existing selected text and retains completed paragraphs", () => {
  const { render, context, document } = harness();
  const first = reasoning({ detail: "Der Befund steht fest.\n\nIch prüfe" });
  const current = render(message(), [first]);
  document.body.append(current);
  const card = current.querySelector(".coding-reasoning");
  const paragraphs = current.querySelectorAll(".coding-reasoning__body p");
  const selectedText = paragraphs[1].firstChild;
  context.goCodingTimeline.reconcile(current, render(message(), [{ ...first, detail: first.detail + " nun die Änderung." }], { previousTimeline: current }));
  assert.equal(current.querySelector(".coding-reasoning"), card);
  assert.equal(current.querySelectorAll(".coding-reasoning__body p")[0], paragraphs[0]);
  assert.equal(current.querySelectorAll(".coding-reasoning__body p")[1].firstChild, selectedText);
  assert.equal(selectedText.textContent, "Ich prüfe nun die Änderung.");
  assert.equal(selectedText.isConnected, true);
  assert.equal(current.querySelectorAll(".coding-reasoning").length, 1);
});

test("manual collapse survives new tokens and terminal status while reopening shows complete latest text", async () => {
  const { render, context, document } = harness();
  const first = reasoning();
  const current = render(message(), [first]);
  document.body.append(current);
  const disclosure = current.querySelector("details"), summary = disclosure.firstChild;
  disclosure.removeAttribute("open");
  await disclosure.dispatch("toggle");
  assert.equal(disclosure.querySelector(".coding-step__content"), null);
  const detail = first.detail + "\n\nNeue Erkenntnis: die Änderung bleibt klein.";
  context.goCodingTimeline.reconcile(current, render(message(), [{ ...first, detail }], { previousTimeline: current }));
  assert.equal(disclosure.hasAttribute("open"), false);
  assert.equal(disclosure.firstChild, summary);
  assert.ok(disclosure.querySelector(".coding-reasoning__preview").textContent.includes("Neue Erkenntnis"));
  context.goCodingTimeline.reconcile(current, render(message({ status: "completed" }), [{ ...first, detail, status: "completed" }]));
  assert.equal(disclosure.hasAttribute("open"), false, "reader state survives even cache-free reconciliation");
  assert.equal(disclosure.querySelector(".coding-reasoning__status").textContent, "Abgeschlossen");
  assert.equal(disclosure.querySelector(".message-status-spinner"), null);
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.ok(disclosure.querySelector(".coding-reasoning__body").textContent.includes("Neue Erkenntnis"));
  assert.ok(disclosure.querySelector(".coding-reasoning__body").textContent.includes(first.detail));
  assert.equal(disclosure.listeners.get("toggle").length, 1);
});

test("interrupted and failed reasoning keep received text without stale spinner or invented success", () => {
  const { render } = harness();
  for (const status of ["failed", "cancelled", "interrupted", "completed"]) {
    const timeline = render(message({ status }), [reasoning()]);
    assert.ok(timeline.querySelector(".coding-reasoning--interrupted"));
    assert.equal(timeline.querySelector(".message-status-spinner"), null);
    assert.ok(timeline.querySelector(".coding-reasoning__body").textContent.includes(reasoning().detail));
  }
  for (const status of ["failed", "cancelled", "completed"]) {
    const timeline = render(message({ status: "completed" }), [reasoning({ status })]);
    assert.ok(timeline.querySelector(`.coding-reasoning--${status}`));
    assert.equal(timeline.querySelector(".message-status-spinner"), null);
  }
});

test("hostile reasoning is inert and complete long output retains all paragraphs", () => {
  const { render, posts } = harness();
  const source = '<script>goBridge.post("session.clear", {})</script>\n\n<img src=x onerror=alert(1)>\n\n[unsafe](javascript:alert(1))';
  const paragraphs = Array.from({ length: 200 }, (_, index) => `Befund ${index}: ${"Beleg ".repeat(30)}`);
  const timeline = render(message(), [reasoning({ detail: [source, ...paragraphs, "Letzter Beleg 日本語"].join("\n\n") })]);
  assert.equal(timeline.querySelector("script"), null);
  assert.equal(timeline.querySelector("img"), null);
  assert.equal(timeline.querySelector("iframe"), null);
  assert.ok(!timeline.querySelectorAll("a").some(link => String(link.href).startsWith("javascript:")));
  assert.ok(timeline.textContent.includes("Letzter Beleg 日本語"));
  assert.ok(timeline.querySelectorAll(".coding-reasoning__body p").length >= 200);
  assert.equal(posts.length, 0);
});

test("reasoning is excluded from all read-aloud block kinds while answer narration remains selectable", () => {
  const { render, context } = harness();
  const detail = "# Denküberschrift\n\nNicht vorlesen.\n\n1. Interner Punkt\n\n```python\nreturn 1\n```";
  const timeline = render(message({ content: "Die Antwort wird vorgelesen.", status: "completed" }), [reasoning({ detail, status: "completed" })]);
  const body = timeline.querySelector(".coding-reasoning__body");
  for (const kind of ["heading", "paragraph", "listItem", "tableRow", "quote", "math", "code"]) {
    assert.equal(context.speechBlockCandidates(body, kind).length, 0, `direct ${kind} selection excludes reasoning`);
    assert.ok(!context.speechBlockCandidates(timeline, kind).some(item => item.textContent.includes("Nicht vorlesen")));
  }
  assert.deepEqual(Array.from(context.speechBlockCandidates(timeline, "paragraph"), item => item.textContent), ["Die Antwort wird vorgelesen."]);
});

test("live reasoning merges into persisted order once and rejects stale reopening events", () => {
  const { context } = harness();
  const saved = reasoning({ status: "completed", updatedAt: "2026-09-12T20:00:02Z", detail: "Vollständiger Gedanke." });
  context.recordCodingActivity({ sessionId: "session-a", messageId: "message-a", toolStep: reasoning({ updatedAt: "2026-09-12T20:00:01Z" }) });
  const steps = context.mergeCodingToolSteps(message({ toolSteps: [saved] }));
  assert.equal(steps.length, 1);
  assert.equal(steps[0].status, "completed");
  assert.equal(steps[0].detail, saved.detail);
  assert.equal(steps[0].label, "Denkprozess");
});

test("a strictly newer reasoning event reopens the same round after retry while replay and equal clocks do not", () => {
  for (const useCursor of [false, true]) {
    const { context } = harness();
    const saved = reasoning({ status: "completed", updatedAt: "2026-09-12T20:00:02Z",
      ...(useCursor ? { outputJson: '{"lastEventId":21}' } : {}), detail: "Voriger Versuch." });
    const post = toolStep => context.recordCodingActivity({ sessionId: "session-a", messageId: "message-a", toolStep });
    post(saved);
    post({ ...saved, status: "running", detail: "Replay darf nicht öffnen." });
    assert.equal(context.mergeCodingToolSteps(message({ toolSteps: [saved] }))[0].status, "completed");
    const retry = { ...saved, status: "running", updatedAt: "2026-09-12T20:00:03Z",
      ...(useCursor ? { outputJson: '{"lastEventId":22}' } : {}), detail: "Neuer Versuch." };
    post(retry);
    const reopened = context.mergeCodingToolSteps(message({ toolSteps: [saved] }))[0];
    assert.equal(reopened.status, "running");
    assert.equal(reopened.detail, "Neuer Versuch.");
    assert.equal(context.state.codingActivity.get("message-a").length, 1);
    post({ ...saved, updatedAt: useCursor ? "2026-09-12T20:00:04Z" : "2026-09-12T20:00:01Z" });
    assert.equal(context.mergeCodingToolSteps(message({ toolSteps: [saved] }))[0].detail, "Neuer Versuch.",
      "an old cursor stays old even if replay delivery has a later timestamp");
  }
});

test("newer persisted running reasoning wins over an older cached terminal result", () => {
  for (const useCursor of [false, true]) {
    const { context } = harness();
    const old = reasoning({ status: "completed", detail: "Alter Abschluss", updatedAt: "2026-09-12T20:00:01Z",
      ...(useCursor ? { outputJson: '{"lastEventId":9}' } : {}) });
    context.recordCodingActivity({ sessionId: "session-a", messageId: "message-a", toolStep: old });
    const stored = { ...old, status: "running", detail: "Wiederaufnahme", updatedAt: "2026-09-12T20:00:02Z",
      ...(useCursor ? { outputJson: '{"lastEventId":10}' } : {}) };
    const merged = context.mergeCodingToolSteps(message({ toolSteps: [stored] }));
    assert.equal(merged[0].status, "running");
    assert.equal(merged[0].detail, "Wiederaufnahme");
  }
});

test("missing or invalid reasoning clocks never reopen terminal receipts and ordinary tools remain terminal", () => {
  for (const tool of ["assistant.reasoning", "coding.command"]) {
    const { context } = harness();
    const saved = reasoning({ tool, status: "completed", detail: "Abgeschlossen" });
    const post = toolStep => context.recordCodingActivity({ sessionId: "session-a", messageId: "message-a", toolStep });
    post(saved);
    post({ ...saved, status: "running", detail: "Nicht bestätigt", updatedAt: "2026-09-12T20:00:02Z", outputJson: '{"lastEventId":2}' });
    const merged = context.mergeCodingToolSteps(message({ toolSteps: [saved] }));
    assert.equal(merged[0].status, "completed");
  }
  const { context } = harness();
  const saved = reasoning({ tool: "coding.command", status: "completed", updatedAt: "2026-09-12T20:00:01Z", outputJson: '{"lastEventId":1}' });
  context.recordCodingActivity({ messageId: "message-a", toolStep: saved });
  context.recordCodingActivity({ messageId: "message-a", toolStep: { ...saved, status: "running", updatedAt: "2026-09-12T20:00:02Z", outputJson: '{"lastEventId":2}' } });
  assert.equal(context.mergeCodingToolSteps(message({ toolSteps: [saved] }))[0].status, "completed");
});

test("compaction gets a distinct badge and changing phase invalidates cached reasoning card", () => {
  const { render, context } = harness();
  const first = reasoning({ inputJson: '{"round":3,"phase":"main"}' });
  const current = render(message(), [first]);
  assert.equal(current.querySelector(".coding-reasoning__phase"), null);
  context.goCodingTimeline.reconcile(current, render(message(), [{ ...first, inputJson: '{"round":3,"phase":"compaction"}' }], { previousTimeline: current }));
  assert.equal(current.querySelector(".coding-reasoning__phase").textContent, "Kontextverdichtung");
  assert.equal(current.querySelector(".coding-reasoning").getAttribute("aria-label"), "Denkprozess · Runde 3 · Kontextverdichtung");
});
