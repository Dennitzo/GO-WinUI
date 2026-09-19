const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const root = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");

function harness(storage = new Map()) {
  const elements = Object.fromEntries(["mainTab", "subagentTab", "mainPanel", "subagentPanel", "subagentList", "composer"]
    .map(name => [name, new TestNode("div")]));
  for (const tab of [elements.mainTab, elements.subagentTab]) tab.focus = () => { tab.focused = true; };
  elements.subagentPanel.append(elements.subagentList);
  Object.assign(elements.subagentPanel, { scrollTop: 0, scrollLeft: 0, scrollHeight: 2000, clientHeight: 500 });
  const document = { createElement: name => new TestNode(name), createTextNode: text => new TestNode("#text", text),
    createDocumentFragment: () => new TestNode("#fragment") };
  const context = vm.createContext({ document, URL,
    localStorage: { getItem: key => storage.get(key) || null, setItem: (key, value) => storage.set(key, value) } });
  for (const file of ["markdown.js", "coding-timeline.js", "agent-tabs.js"]) vm.runInContext(fs.readFileSync(path.join(root, file), "utf8"), context);
  const options = { codingToolStepsExpanded: true, renderMarkdown: text => context.goMarkdown.render(text) };
  let mainRenders = 0, mainSaves = 0, mainRestores = 0;
  const tabs = context.goAgentTabs.attach({ elements, timeline: options, steps: message => message.toolSteps || [],
    renderMain: () => mainRenders++, saveMainScroll: () => mainSaves++, restoreMainScroll: () => mainRestores++ });
  return { context, elements, tabs, options, storage, counts: () => ({ mainRenders, mainSaves, mainRestores }) };
}

const step = (id, tool, extra = {}) => ({ id, tool, kind: "tool", label: tool, status: "completed", contentOffset: 0, ...extra });
const child = (id, tool, extra = {}) => step(id, tool, { agentId: "agent-abc123", ...extra });
const message = (extra = {}) => ({ id: "message-a", role: "assistant", status: "streaming", content: "Hauptagent antwortet.", toolSteps: [], ...extra });
const state = (messages, activeSessionId = "session-a") => ({ activeSessionId, messages, isRunning: true,
  runStatus: "Denkt nach", runDetail: "Hauptmodell 42 Token", contextUsed: 900, contextLimit: 4096 });
const lifecycle = (extra = {}) => child("agent-abc123", "coding.agentStart", {
  status: "running", inputJson: JSON.stringify({ task: "Tests für Modul A ergänzen.", writePaths: ["tests/a/"] }), ...extra });

test("main chat exposes only open start and result notices while preserving its own tools and reasoning", () => {
  const h = harness();
  const mainReasoning = step("main-think", "assistant.reasoning", { detail: "Hauptagent denkt.", inputJson: '{"round":1}' });
  const mainTool = step("main-read", "coding.read", { inputJson: '{"path":"src/main.cs"}', outputJson: '{"content":"Hauptdatei"}' });
  const source = message({ status: "completed", toolSteps: [mainReasoning, mainTool,
    lifecycle({ status: "completed", outputJson: '{"status":"completed","result":"Drei Tests bestanden."}' }),
    child("child-reason", "assistant.reasoning", { detail: "Nur im Subagenten sichtbarer Gedankengang." }),
    child("child-read", "coding.read", { outputJson: '{"content":"Nur Subagentenwerkzeug"}' }),
    step("wait", "coding.agentWait", { outputJson: '{"agents":[{"agentId":"agent-abc123","status":"completed","result":"Drei Tests bestanden."}]}' })
  ] });
  const original = JSON.stringify(source);
  const main = h.context.goAgentTabs.mainSteps(source, source.toolSteps);
  const timeline = h.context.goCodingTimeline.render(source, main, h.options);
  assert.equal(timeline.querySelectorAll(".agent-notice").length, 2);
  assert.equal(timeline.querySelectorAll(".coding-step").length, 1);
  assert.equal(timeline.querySelectorAll(".coding-reasoning").length, 1);
  assert.ok(timeline.textContent.includes("Tests für Modul A ergänzen."));
  assert.ok(timeline.textContent.includes("Drei Tests bestanden."));
  assert.equal(timeline.textContent.includes("Nur im Subagenten"), false);
  assert.equal(timeline.textContent.includes("Nur Subagentenwerkzeug"), false);
  assert.equal(timeline.querySelector(".agent-notice details"), null, "start/result are always expanded");
  assert.equal(JSON.stringify(source), original, "projection never rewrites the stored conversation");
});

test("subagent tab renders persisted reasoning, narration, plan, tool receipts and measured progress independently", () => {
  const h = harness();
  const source = message({ toolSteps: [
    step("main-only", "coding.read", { outputJson: '{"content":"Hauptagent intern"}' }), lifecycle(),
    child("reason-1", "assistant.reasoning", { detail: "Ich prüfe zuerst die Randfälle.", status: "running", inputJson: '{"round":1,"phase":"subagent"}' }),
    child("text-1", "assistant.narration", { detail: "Die Testdatei wird ergänzt.", inputJson: '{"round":1,"phase":"subagent"}' }),
    child("plan-1", "coding.updatePlan", { inputJson: '{"plan":[{"step":"Randfälle prüfen","status":"in_progress"}]}' }),
    child("command-1", "coding.command", { status: "running", inputJson: '{"executable":"dotnet","arguments":["test"]}', outputJson: '{"stdout":"Tests laufen"}' }),
    child("progress-1", "assistant.progress", { status: "running", detail: "Subagent denkt nach", outputJson: JSON.stringify({
      generation: { generatedTokens: 20, promptTokens: 100 }, context: { estimatedInputTokens: 100, contextLimit: 8192 },
      metrics: { inputTokens: 110, outputTokens: 30, metrics: { cachedPromptTokens: 90 } } }) })
  ] });
  h.tabs.refresh(state([source]));
  h.tabs.select("subagent");
  const body = h.elements.subagentList;
  assert.equal(h.elements.mainPanel.hidden, true);
  assert.equal(h.elements.subagentPanel.hidden, false);
  assert.equal(h.elements.subagentTab.getAttribute("aria-selected"), "true");
  assert.ok(body.textContent.includes("Ich prüfe zuerst die Randfälle."));
  assert.ok(body.textContent.includes("Die Testdatei wird ergänzt."));
  assert.ok(body.textContent.includes("Randfälle prüfen"));
  assert.ok(body.textContent.includes("Tests laufen"));
  assert.ok(body.textContent.includes("90 wiederverwendet"));
  assert.equal(body.textContent.includes("Hauptagent intern"), false);
  assert.equal(body.querySelectorAll(".coding-reasoning").length, 1);
  assert.equal(body.querySelectorAll(".subagent-activity--progress").length, 1);
  assert.equal(h.elements.subagentTab.dataset.running, "true");
});

test("subagent history uses the same user and assistant chat structure without a card or composer", () => {
  const h = harness();
  const source = message({ status: "completed", toolSteps: [
    lifecycle({ status: "completed", inputJson: '{"task":"Bitte **Randfälle** prüfen.","writePaths":["tests/a/"]}',
      outputJson: '{"status":"completed","result":"Drei Tests bestanden."}' }),
    child("reason", "assistant.reasoning", { detail: "Ich prüfe die Grenzen." }),
    child("read", "coding.read", { inputJson: '{"path":"tests/a/example.cs"}', outputJson: '{"content":"Prüfbare Datei"}' })
  ] });
  h.tabs.refresh(state([source]));
  h.tabs.select("subagent");
  const body = h.elements.subagentList;
  assert.deepEqual(body.querySelectorAll(".message").map(item => item.className), ["message user", "message assistant"]);
  const user = body.querySelector(".message.user"), assistant = body.querySelector(".message.assistant");
  assert.equal(user.querySelector(".message-body .message-content strong").textContent, "Randfälle");
  assert.ok(user.textContent.includes("Schreibbereich: tests/a/"));
  assert.equal(user.querySelector(".avatar"), null);
  assert.equal(assistant.querySelector(".avatar").textContent, "AI");
  assert.equal(assistant.querySelector(".message-body").children[0].className, "message-meta");
  assert.equal(assistant.querySelector(".message-body").children[1].className, "coding-timeline");
  assert.equal(assistant.querySelectorAll(".coding-reasoning").length, 1);
  assert.equal(assistant.querySelectorAll(".coding-step").length, 1);
  assert.equal(assistant.querySelector(".coding-narration").textContent, "Drei Tests bestanden.");
  assert.equal(body.querySelector(".subagent-run"), null);
  assert.equal(body.querySelector("textarea"), null);
  assert.equal(h.elements.composer.hidden, true);
  h.tabs.select("main");
  assert.equal(h.elements.composer.hidden, false);
});

test("active tab and independent scroll positions survive session switches and fresh page creation", () => {
  const h = harness();
  const a = state([message({ toolSteps: [lifecycle()] })]);
  const unchanged = JSON.stringify(a);
  h.tabs.refresh(a);
  h.tabs.select("subagent");
  h.elements.subagentPanel.scrollTop = 321;
  h.tabs.refresh(state([], "session-b"));
  assert.equal(h.tabs.activeTab, "main");
  h.tabs.refresh(a);
  assert.equal(h.tabs.activeTab, "subagent");
  assert.equal(h.elements.subagentPanel.scrollTop, 321);
  assert.equal(JSON.stringify(a), unchanged, "tab selection cannot alter run, token, or message state");
  const restarted = harness(h.storage);
  restarted.tabs.refresh(JSON.parse(JSON.stringify(a)));
  assert.equal(restarted.tabs.activeTab, "subagent");
  assert.ok(restarted.elements.subagentList.textContent.includes("Tests für Modul A ergänzen."));
  h.tabs.select("main");
  assert.equal(h.counts().mainRestores, 1);
  assert.equal(h.elements.mainTab.getAttribute("aria-selected"), "true");
});

test("final child narration and identical result appear once while earlier and distinct text is retained", () => {
  for (const distinctResult of [false, true]) {
    const h = harness();
    const finalText = "Drei Randfälle geprüft.";
    h.tabs.refresh(state([message({ toolSteps: [
      lifecycle({ status: "completed", outputJson: JSON.stringify({ status: "completed",
        result: distinctResult ? "Abschließende Zusatzbewertung." : finalText }) }),
      child("earlier", "assistant.narration", { detail: "Zuerst lese ich die Tests." }),
      child("read", "coding.read", { inputJson: '{"path":"tests/a/test.cs"}' }),
      child("final", "assistant.narration", { detail: finalText })
    ] })]));
    h.tabs.select("subagent");
    const text = h.elements.subagentList.textContent;
    assert.equal(text.split(finalText).length - 1, 1);
    assert.ok(text.includes("Zuerst lese ich die Tests."));
    assert.equal(text.includes("Abschließende Zusatzbewertung."), distinctResult);
    assert.equal(h.elements.subagentList.querySelectorAll(".coding-step").length, 1);
  }
});

test("stream updates reconcile the real child timeline without resetting its disclosure or scroll", () => {
  const h = harness();
  const source = message({ toolSteps: [lifecycle(), child("cmd", "coding.command", { status: "running",
    outputJson: '{"stdout":"Zeile eins"}' })] });
  h.tabs.refresh(state([source]));
  h.tabs.select("subagent");
  const article = h.elements.subagentList.children[0];
  const taskMessage = article.querySelector(".message.user");
  const answerMessage = article.querySelector(".message.assistant");
  const disclosure = article.querySelector("details");
  disclosure.removeAttribute("open");
  h.elements.subagentPanel.scrollTop = 250;
  source.toolSteps[1] = { ...source.toolSteps[1], outputJson: '{"stdout":"Zeile eins\\nZeile zwei"}' };
  h.tabs.refresh(state([source]));
  assert.equal(h.elements.subagentList.children[0], article);
  assert.equal(article.querySelector(".message.user"), taskMessage);
  assert.equal(article.querySelector(".message.assistant"), answerMessage);
  assert.equal(article.querySelector("details"), disclosure);
  assert.equal(disclosure.hasAttribute("open"), false);
  assert.equal(h.elements.subagentPanel.scrollTop, 250);
  source.status = "completed";
  source.toolSteps[0] = lifecycle({ status: "completed", outputJson: '{"status":"completed","result":"Werkzeug beendet."}' });
  h.tabs.refresh(state([source]));
  assert.ok(article.textContent.includes("Werkzeug beendet."));
  assert.equal(h.elements.subagentTab.dataset.running, "false");
});

test("empty state, failed task and multiple owned histories remain distinct and do not invent success", () => {
  const h = harness();
  h.tabs.refresh(state([]));
  h.tabs.select("subagent");
  assert.ok(h.elements.subagentList.textContent.includes("Noch kein Subagent gestartet"));
  const failed = lifecycle({ status: "failed", outputJson: '{"status":"failed","result":"Testdatei fehlt."}' });
  const second = child("agent-def456", "coding.agentStart", { agentId: "agent-def456", status: "running", inputJson: '{"task":"Zweiter Auftrag"}' });
  h.tabs.refresh(state([message({ status: "completed", toolSteps: [failed, second] })]));
  assert.equal(h.elements.subagentList.querySelectorAll(".subagent-conversation").length, 2);
  assert.ok(h.elements.subagentList.textContent.includes("Fehlgeschlagen"));
  assert.ok(h.elements.subagentList.textContent.includes("Nicht abgeschlossen"));
  assert.ok(h.elements.subagentList.textContent.includes("Testdatei fehlt."));
  assert.equal(h.elements.subagentList.textContent.includes("Noch kein Subagent"), false);
});

test("steered child lifecycle retains its result and closes all child activity while the parent continues", () => {
  const h = harness();
  const result = "Bereits quittierte Änderungen bleiben erhalten.";
  const source = message({ toolSteps: [
    step("main-think", "assistant.reasoning", { status: "running", detail: "Der Hauptagent setzt den neuen Auftrag um." }),
    lifecycle({ status: "failed", detail: "Voriger Status", outputJson: JSON.stringify({ status: "steered", result }) }),
    child("child-think", "assistant.reasoning", { status: "interrupted", detail: "Bisherige Überlegung.", outputJson: '{"state":"steered","lastEventId":4}' }),
    child("child-progress", "assistant.progress", { status: "interrupted", detail: "Umgeleitet", outputJson: '{"generation":{"state":"steered","generatedTokens":25}}' }),
    child("old-progress", "assistant.progress", { status: "running", detail: "Subagent generiert" }),
    child("old-tool", "coding.read", { status: "running", inputJson: '{"path":"tests/a/example.cs"}' })
  ] });
  const original = JSON.stringify(source);
  h.tabs.refresh(state([source]));
  h.tabs.select("subagent");
  const assertTerminalChild = current => {
    const body = current.elements.subagentList;
    assert.equal(current.elements.subagentTab.dataset.running, "false");
    assert.equal(body.querySelector(".message-meta .message-status").textContent, "Umgeleitet");
    assert.equal(body.querySelector(".coding-reasoning__status").textContent, "Umgeleitet");
    assert.equal(body.querySelector(".coding-step__status").textContent, "Umgeleitet");
    assert.equal(body.querySelector(".message-status-spinner"), null);
    assert.ok(body.textContent.includes(result));
    assert.equal(body.textContent.includes("Subagent generiert"), false);
    assert.equal(body.textContent.includes("Fehlgeschlagen"), false);
  };
  assertTerminalChild(h);
  const main = h.context.goCodingTimeline.render(source, h.context.goAgentTabs.mainSteps(source, source.toolSteps), h.options);
  assert.equal(main.querySelectorAll(".agent-notice").length, 2);
  assert.equal(main.querySelector(".agent-notice--result .agent-state").textContent, "Umgeleitet");
  assert.ok(main.querySelector(".coding-reasoning .message-status-spinner"), "the parent still reasons independently");
  assert.ok(main.textContent.includes(result));
  const restarted = harness(h.storage);
  restarted.tabs.refresh(state(JSON.parse(JSON.stringify([source]))));
  assert.equal(restarted.tabs.activeTab, "subagent");
  assertTerminalChild(restarted);
  assert.equal(JSON.stringify(source), original, "terminal display never changes the stored parent run");
});

test("legacy persisted subagent receipts restore ownership and tool results without new event traffic", () => {
  const h = harness();
  const legacy = message({ status: "completed", toolSteps: [
    step("agent-abc123", "coding.agentStart", { inputJson: '{"task":"Historischer Auftrag"}', outputJson: '{"Status":"completed","Result":"Historisches Ergebnis"}' }),
    step("proposal-old", "coding.read", { explanation: "Subagent GPU1 · coding.read · Historischer Auftrag", outputJson: '{"content":"Historische Werkzeugdaten"}' })
  ] });
  h.tabs.refresh(state([legacy]));
  h.tabs.select("subagent");
  assert.equal(h.elements.subagentList.querySelectorAll(".subagent-conversation").length, 1);
  assert.ok(h.elements.subagentList.textContent.includes("Historisches Ergebnis"));
  assert.ok(h.elements.subagentList.textContent.includes("Historische Werkzeugdaten"));
});

test("untrusted child task and reasoning render as text without executing markup", () => {
  const h = harness();
  const malicious = '<script>globalThis.compromised=true</script>';
  h.tabs.refresh(state([message({ toolSteps: [lifecycle({ inputJson: JSON.stringify({ task: malicious }) }),
    child("thought", "assistant.reasoning", { detail: malicious })] })]));
  h.tabs.select("subagent");
  assert.equal(h.elements.subagentList.querySelector("script"), null);
  assert.equal(h.context.compromised, undefined);
  assert.ok(h.elements.subagentList.textContent.includes(malicious));
});
