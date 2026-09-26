const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web/app.js"), "utf8");

class Node extends TestNode {
  set innerHTML(value) { assert.match(value, /^<svg /); this.textContent = ""; }
}

function harness(storage = new Map()) {
  const posts = [];
  const toolSelections = [];
  const state = { sessions: [], sessionGroups: [], messages: [], messageRunStatus: new Map(),
    contextSource: "estimated", contextProfile: null, activeSessionId: null, codingActivity: new Map() };
  const elements = Object.fromEntries(["sessionList", "sessionSearch", "overlay", "prompt", "send",
    "newSession", "clearSessions", "context", "contextLabel", "pinSession"].map(name => [name, new Node("div")]));
  elements.sessionSearch.value = "";
  elements.prompt.value = "";
  elements.overlay.hidden = true;
  elements.context.style.setProperty = () => {};
  const noOp = () => {};
  const context = vm.createContext({ state, elements,
    document: { body: new Node("body"), createElement: tag => new Node(tag),
      createElementNS: (ns, tag) => { const element = new Node(tag); element.namespaceURI = ns; return element; } },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    ungroupedSessionStorageKey: "ungrouped", sidebarStorageKey: "sidebar",
    post: (type, payload) => posts.push({ type, payload }), flushDraft: noOp,
    sessionShortLabel: value => value[0],
    persistentToolActions: new Set(["coding"]), normalizeToolAction: value => value,
    closeCodingPreview: noOp, persistSessionScrollPosition: noOp, resetTransientVoiceStateForSessionChange: noOp,
    applyCodingChanges: noOp, selectToolAction: value => { toolSelections.push(value); state.selectedToolAction = value; },
    setSessionsCollapsed: noOp, setPromptValue: value => { elements.prompt.value = value; },
    renderMessages: noOp, restoreSessionScrollPosition: noOp, renderContext: noOp,
    renderCodingWorkspace: noOp, renderLiveCaption: noOp, renderMicrophone: noOp,
    syncVoiceCaptureSuspension: noOp, renderScreenClip: noOp, renderWorkflows: noOp,
    recordCodingActivity: noOp, clearCompletedOneShotToolAction: noOp, showToast: noOp, setTimeout: noOp
  });
  for (const name of ["readUngroupedCollapsed", "persistUngroupedCollapsed", "createSessionItem", "createProjectRow",
    "createWorkspaceProject", "renderSessions", "renderSessionPin", "contextProfileForSnapshot", "persistMeasuredContext", "restoreSnapshotContext",
    "belongsToActiveSession", "restoreDeepResearch", "clearCompletedOneShotToolAction", "applySnapshot", "handleHostMessage", "isTerminalMessageStatus",
    "pruneTerminalMessageRunStatuses", "conversationMessagesDiffer", "renderComposerAction", "renderStatus"]) {
    const start = source.indexOf(`  function ${name}(`);
    const ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending, name);
    vm.runInContext(source.slice(start, start + ending.index + ending[0].length), context);
  }
  return { state, elements, context, posts, storage, toolSelections,
    emit: (type, payload) => context.handleHostMessage({ detail: { type, payload } }) };
}

function snapshot(extra = {}) {
  return { activeSessionId: "session-a", sessions: [{ id: "session-a", title: "Sitzung A" }],
    messages: [{ id: "answer-a", sessionId: "session-a", role: "assistant", content: "", status: "streaming" }],
    reasoningModelId: "model-a", reasoningRole: "general", contextUsed: 300, contextLimit: 32768,
    contextSource: "estimated", isRunning: false, ...extra };
}

test("workspace headers keep every session visible and project compose emits its exact workspace", async () => {
  const { context, elements, posts } = harness();
  context.applySnapshot(snapshot({ sessions: [
    { id: "one", title: "Erste", sessionGroupId: "project-a" },
    { id: "two", title: "Zweite", sessionGroupId: "project-b", isPinned: true },
    { id: "general", title: "General", sessionGroupId: null },
    { id: "orphan", title: "Frühere Sitzung", sessionGroupId: "unavailable-project" }
  ], sessionGroups: [
    { id: "project-a", name: "GO-WinUI", workspacePath: "C:\\Projects\\GO-WinUI" },
    { id: "project-b", name: "Other", workspacePath: "D:\\Projects\\Other" },
    { id: "empty", name: "Leeres Projekt", workspacePath: "C:\\Projects\\Empty" }
  ] }));
  assert.equal(elements.sessionList.querySelectorAll(".session-item").length, 4);
  assert.equal(elements.sessionList.querySelectorAll(".session-group__add").length, 4);
  await elements.sessionList.querySelector(".session-group__add").dispatch("click");
  assert.equal(posts.at(-1).type, "session.projectCreate");
  assert.equal(posts.at(-1).payload.workspacePath, "C:\\Projects\\GO-WinUI");
  elements.sessionSearch.value = "go-winui";
  context.renderSessions();
  assert.equal(elements.sessionList.querySelectorAll(".session-item").length, 1, "project search includes its sessions");
  assert.ok(elements.sessionList.textContent.includes("Erste"));
});

test("tab reload and session switching restore measured context and live per-message status", () => {
  const first = harness();
  const active = snapshot({ contextSource: "measured", contextUsed: 16384, isRunning: true,
    runMessageId: "answer-a", runStatus: "Denkt nach", runDetail: "16.384 Token" });
  first.context.applySnapshot(active);
  assert.equal(first.elements.contextLabel.textContent, "50%");
  assert.equal(first.state.messageRunStatus.get("answer-a").detail, "16.384 Token");
  first.context.applySnapshot(snapshot({ activeSessionId: "session-b", messages: [], isAiBusy: true }));
  assert.equal(first.state.messageRunStatus.size, 0);
  assert.equal(first.elements.send.disabled, true);
  first.context.applySnapshot(active);
  assert.equal(first.state.contextUsed, 16384);
  assert.equal(first.state.runStatus, "Denkt nach");
  const freshPage = harness(first.storage);
  freshPage.context.applySnapshot(active);
  assert.equal(freshPage.state.messageRunStatus.get("answer-a").detail, "16.384 Token");
  assert.equal(freshPage.elements.contextLabel.textContent, "50%");
});

test("full snapshots restore only the research preference of the selected session", () => {
  const storage = new Map([["go.assistant.deep-research.v1:session-a", "1"]]);
  const { context, state } = harness(storage);
  context.applySnapshot(snapshot());
  assert.equal(state.deepResearch, true);
  context.applySnapshot(snapshot({ activeSessionId: "session-b", messages: [] }));
  assert.equal(state.deepResearch, false);
  context.applySnapshot(snapshot());
  assert.equal(state.deepResearch, true);
  context.applySnapshot(snapshot({ contextUsed: 500 }));
  assert.equal(state.deepResearch, true, "ordinary status snapshots do not clear the active chip");
});

test("a one-shot chip still survives idle refreshes while composing", () => {
  const { context, state } = harness();
  context.applySnapshot(snapshot({ selectedToolAction: "coding" }));
  state.selectedToolAction = "webSearch";
  context.applySnapshot(snapshot({ selectedToolAction: "coding", isRunning: false }));
  assert.equal(state.selectedToolAction, "webSearch", "idle refresh must retain the unsent request capability");
  context.applySnapshot(snapshot({ activeSessionId: "session-b", messages: [], selectedToolAction: "coding" }));
  assert.equal(state.selectedToolAction, "coding");
});

test("global project plus requests the native folder picker before creating anything", () => {
  const { context, state, posts } = harness();
  context.createWorkspaceProject();
  assert.equal(posts.length, 1);
  assert.equal(posts[0].type, "session.workspaceCreate");
  assert.deepEqual(Object.keys(posts[0].payload), [], "a workspace must be selected by the native picker");
  assert.equal(state.sessions.length, 0, "no optimistic project or session is created before picker acceptance");
  state.isAiBusy = true;
  context.createWorkspaceProject();
  assert.equal(posts.length, 1, "a busy UI cannot start another folder selection");
});

test("project-local compose preserves each workspace and pins stay first inside their own project", async () => {
  const { context, elements, posts } = harness();
  context.applySnapshot(snapshot({ sessions: [
    { id: "a-normal", title: "A normal", sessionGroupId: "a", updatedAt: "2026-09-19T08:44:00Z" },
    { id: "b-normal", title: "B normal", sessionGroupId: "b" },
    { id: "b-pin", title: "B angepinnt", isPinned: true, sessionGroupId: "b" },
    { id: "a-pin", title: "A angepinnt", isPinned: true, sessionGroupId: "a" }
  ], sessionGroups: [
    { id: "a", name: "Projekt A", workspacePath: "C:\\Projects\\A" },
    { id: "b", name: "Projekt B", workspacePath: "D:\\Projects\\B" }
  ] }));
  const groups = elements.sessionList.querySelectorAll(".session-group");
  assert.equal(groups.length, 2);
  for (const [index, group] of groups.entries()) {
    const rows = group.querySelectorAll(".session-item");
    assert.equal(rows.length, 2);
    assert.equal(rows[0].classList.contains("pinned"), true);
    assert.equal(rows[1].classList.contains("pinned"), false);
    assert.ok(group.querySelector(".session-group__folder"));
    const compose = group.querySelector(".session-group__add");
    assert.match(compose.getAttribute("aria-label"), /^Neue Sitzung im Projekt Projekt [AB]$/);
    const icon = compose.querySelector(".session-group__compose");
    assert.equal(icon.tagName, "SVG");
    assert.equal(icon.getAttribute("aria-hidden"), "true");
    assert.equal(icon.querySelectorAll("path").length, 1);
    assert.equal(icon.querySelector("path").getAttribute("d"), "M12 5v14M5 12h14");
    assert.equal(group.querySelector(".session-item__date"), null);
    for (const row of rows) {
      const main = row.querySelector(".session-item__main");
      assert.equal(main.children.length, 1);
      assert.equal(main.textContent, row.querySelector(".session-item__title").textContent);
    }
    assert.equal(group.textContent.includes("2026-09-19"), false);
    await compose.dispatch("click");
    assert.equal(posts.at(-1).type, "session.projectCreate");
    assert.equal(posts.at(-1).payload.workspacePath, index === 0 ? "C:\\Projects\\A" : "D:\\Projects\\B");
  }
});

test("projects are ordered by their most recently changed session", () => {
  const { context, elements } = harness();
  context.applySnapshot(snapshot({ sessions: [
    { id: "old", title: "Alt", sessionGroupId: "old-project", updatedAt: "2026-09-20T10:00:00Z" },
    { id: "new", title: "Neu", sessionGroupId: "new-project", updatedAt: "2026-09-24T10:00:00Z" }
  ], sessionGroups: [
    { id: "old-project", name: "Altes Projekt", createdAt: "2026-09-20T09:00:00Z", workspacePath: "C:\\Old" },
    { id: "new-project", name: "Neues Projekt", createdAt: "2026-09-19T09:00:00Z", workspacePath: "C:\\New" }
  ] }));
  const groups = elements.sessionList.querySelectorAll(".session-group");
  assert.equal(groups[0].querySelector(".session-group__name").textContent, "Neues Projekt");
  assert.equal(groups[1].querySelector(".session-group__name").textContent, "Altes Projekt");
});

test("live captions use the normal assistant message stream without the old panel", () => {
  const start = source.indexOf("  function renderLiveCaption(");
  const ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
  assert.ok(start >= 0 && ending);
  const implementation = source.slice(start, start + ending.index + ending[0].length);
  assert.match(implementation, /state\.messages\.push\(/);
  assert.match(implementation, /status: "streaming"/);
  assert.match(implementation, /replace\(\/\\r\?\\n\/g, "\\n\\n"\)/);
  assert.match(implementation, /renderMessages\(Boolean\(caption\.isActive\)\)/);
  assert.doesNotMatch(source, /Live-Untertitel beenden/);
});

test("project heading and row actions share the same compact layout including the scrollbar gutter", () => {
  const css = fs.readFileSync(path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web/styles.css"), "utf8");
  const rule = selector => css.split("\n").find(line => line.startsWith(selector + " {"));
  assert.ok(rule(".session-pane").includes("grid-template-rows: auto auto auto minmax(0, 1fr)"), "the fourth area, the session list, must own the remaining scrollable height");
  const layout = rule(".sidebar-projects-heading, .session-group__headrow");
  assert.ok(layout.includes("grid-template-columns: minmax(0, 1fr) var(--project-action-size)"));
  assert.ok(layout.includes("padding-right: var(--project-action-inset)"));
  const action = rule(".new-project-button, .session-group__add");
  assert.ok(action.includes("place-items: center"));
  assert.ok(action.includes("width: var(--project-action-size)"));
  assert.ok(action.includes("margin: 0"));
  assert.ok(rule(".sidebar-projects-heading").includes("margin-right: var(--project-scrollbar-size)"));
  assert.ok(rule(".session-list").includes("scrollbar-gutter: stable"));
  assert.ok(rule(".session-list::-webkit-scrollbar").includes("width: var(--project-scrollbar-size)"));
  assert.ok(rule(".sidebar-projects-heading .sidebar-title, .session-group__head").includes("font-size: 11px; font-weight: 600; line-height: 1.4"));
  assert.ok(rule(".new-project-button svg, .session-group__add svg").includes("width: 16px; height: 16px"));
});


test("context measurement survives app restart and model switch without reviving a finished run", () => {
  for (const role of ["general", "coding"]) {
    const first = harness();
    const completed = snapshot({ reasoningRole: role, contextUsed: 14000, contextSource: "measured",
      messages: [{ id: "answer-a", sessionId: "session-a", role: "assistant", content: "Fertig", status: "completed" }] });
    first.context.applySnapshot(completed);
    const restart = harness(first.storage);
    restart.context.applySnapshot({ ...completed, contextSource: "estimated", contextUsed: 100, contextLimit: 262144 });
    assert.equal(restart.state.contextUsed, 14000);
    assert.equal(restart.state.contextLimit, 32768, "the observed fitted context beats the catalog estimate after restart");
    assert.equal(restart.state.isRunning, false);
    assert.equal(restart.state.messageRunStatus.size, 0);
    restart.context.applySnapshot({ ...completed, reasoningModelId: "model-b", contextSource: "estimated", contextUsed: 200 });
    assert.equal(restart.state.contextUsed, 200, "another model cannot inherit the former token measurement");
    restart.context.applySnapshot({ ...completed, contextSource: "estimated", contextUsed: 100 });
    assert.equal(restart.state.contextUsed, 14000, "returning to the model restores its measurement");
    restart.context.applySnapshot({ ...completed, contextSource: "estimated", contextUsed: 500,
      messages: [{ id: "next-turn", role: "assistant", status: "completed" }] });
    assert.equal(restart.state.contextUsed, 500, "another prompt cannot restore the earlier message measurement");
  }
});

test("late background status, starts, completion and sidebar refresh never reset the open session", () => {
  const { context, state, elements, emit } = harness();
  context.applySnapshot(snapshot({ contextSource: "measured", contextUsed: 12000, isRunning: true,
    runMessageId: "answer-a", runStatus: "Denkt nach", runDetail: "12.000 Token" }));
  elements.prompt.value = "Aktueller Entwurf";
  emit("status.changed", { sessionId: "session-b", runStatus: "Fremder Lauf", contextUsed: 1 });
  emit("chat.started", { message: { id: "answer-b", sessionId: "session-b" }, contextUsed: 2 });
  emit("chat.completed", { message: { id: "answer-b", sessionId: "session-b" }, runStatus: "Fertig" });
  emit("session.grouped", { sessions: state.sessions, sessionGroups: [] });
  assert.equal(state.contextUsed, 12000);
  assert.equal(state.runStatus, "Denkt nach");
  assert.equal(state.isRunning, true);
  assert.equal(elements.prompt.value, "Aktueller Entwurf");
  assert.equal(state.messageRunStatus.get("answer-a").detail, "12.000 Token");
});

test("invalid optional storage and missing snapshot context cannot leak a previous session measurement", () => {
  const { context, state, storage } = harness();
  context.applySnapshot(snapshot({ contextSource: "measured", contextUsed: 18000 }));
  const next = snapshot({ activeSessionId: "session-b", messages: [], contextUsed: null, contextLimit: null });
  storage.set(`go.assistant.context.v1:${context.contextProfileForSnapshot(next)}`, "broken json");
  context.applySnapshot(next);
  assert.equal(state.contextUsed, 0);
  assert.equal(state.contextLimit, 8192);
});

test("a new prompt without token progress cannot label an older measurement as its own", () => {
  const first = harness();
  first.context.applySnapshot(snapshot({ contextSource: "measured", contextUsed: 15000 }));
  first.state.messages = [{ id: "next-answer", sessionId: "session-a", role: "assistant", status: "streaming" }];
  first.emit("chat.started", { message: first.state.messages[0], contextUsed: null });
  const restarted = harness(first.storage);
  restarted.context.applySnapshot(snapshot({ messages: first.state.messages, contextUsed: 700 }));
  assert.equal(restarted.state.contextUsed, 700);
  first.emit("status.changed", { sessionId: "session-a", messageId: "next-answer", contextUsed: 17000 });
  restarted.context.applySnapshot(snapshot({ messages: first.state.messages, contextUsed: 700 }));
  assert.equal(restarted.state.contextUsed, 17000);
});

test("a background completion refreshes its sidebar title and reenables project plus", () => {
  const { context, state, elements, emit } = harness();
  context.applySnapshot(snapshot({ isAiBusy: true,
    sessions: [{ id: "session-a", title: "Neue Sitzung", sessionGroupId: "project" }],
    sessionGroups: [{ id: "project", name: "Projekt", workspacePath: "C:\\Project" }] }));
  assert.equal(elements.sessionList.querySelector(".session-group__add").disabled, true);
  emit("chat.completed", { message: { id: "answer-b", sessionId: "session-b" },
    session: { id: "session-b", title: "Fertige Hintergrundaufgabe", sessionGroupId: "project" } });
  assert.equal(state.activeSessionId, "session-a");
  assert.equal(elements.sessionList.querySelector(".session-group__add").disabled, false);
  assert.ok(elements.sessionList.textContent.includes("Fertige Hintergrundaufgabe"));
});