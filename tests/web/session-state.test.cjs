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
  const codingUpdates = [];
  const state = { sessions: [], sessionGroups: [], messages: [], messageRunStatus: new Map(),
    contextSource: "estimated", contextProfile: null, activeSessionId: null, codingActivity: new Map(), chatMode: "general",
    selectedExtensionActionId: null, persistentExtensionActionId: null,
    runQueue: { active: null, pending: [], queueDepth: 0, isIdle: true } };
  const elements = Object.fromEntries(["sessionList", "sessionSearch", "overlay", "prompt", "send",
    "newSession", "clearSessions", "context", "contextLabel", "chatHeading"].map(name => [name, new Node("div")]));
  elements.sessionSearch.value = "";
  elements.prompt.value = "";
  elements.overlay.hidden = true;
  elements.context.style.setProperty = () => {};
  const noOp = () => {};
  const context = vm.createContext({ state, elements,
    document: { body: new Node("body"), createElement: tag => new Node(tag),
      createElementNS: (ns, tag) => { const element = new Node(tag); element.namespaceURI = ns; return element; } },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
    ungroupedSessionStorageKey: "ungrouped", sidebarStorageKey: "sidebar",
    post: (type, payload) => posts.push({ type, payload }), flushDraft: noOp,
    sessionShortLabel: value => value[0],
    persistentToolActions: new Set(["audiobook", "planMode"]),
    persistentExtensionActionIds: new Set(["builtin.audiobook/create", "builtin.coding/plan-mode"]),
    normalizeToolAction: value => value || null,
    legacyExtensionActionIds: {
      webSearch: "builtin.web/web-search",
      audiobook: "builtin.audiobook/create",
      planMode: "builtin.coding/plan-mode"
    },
    closeCodingPreview: noOp, persistSessionScrollPosition: noOp, resetTransientVoiceStateForSessionChange: noOp,
    applyCodingChanges: noOp, selectToolAction: value => { toolSelections.push(value); state.selectedToolAction = value; },
    setSessionsCollapsed: noOp, setPromptValue: value => { elements.prompt.value = value; },
    renderMessages: noOp, restoreSessionScrollPosition: noOp, renderContext: noOp,
    renderCodingWorkspace: noOp, renderLiveCaption: noOp, renderMicrophone: noOp,
    syncVoiceCaptureSuspension: noOp, renderScreenClip: noOp, renderWorkflows: noOp,
    recordCodingActivity: payload => codingUpdates.push(payload),
    clearCompletedOneShotToolAction: noOp, showToast: noOp, setTimeout: noOp
  });
  for (const name of ["normalizeExtensionActionId", "extensionActionIdForToolAction", "toolActionForExtensionActionId",
    "normalizeScheduledRun", "normalizeRunQueue", "scheduledRunForSession", "applyRunQueue", "removeScheduledRun",
    "normalizeChatMode", "sessionMatchesMode", "sessionBelongsToGroup", "readMigratedStorage", "readUngroupedCollapsed", "persistUngroupedCollapsed", "createSessionItem", "createProjectRow",
    "createWorkspaceProject", "renderSessions", "contextProfileForSnapshot", "persistMeasuredContext", "restoreSnapshotContext",
    "belongsToActiveSession", "restoreDeepResearch", "clearCompletedOneShotToolAction", "applySnapshot",
    "sortCommittedMessages", "requestConversationRefresh", "applyConversationSnapshot", "acceptCommittedRevision", "applyCommittedMessage",
    "upsertLiveMessage", "applyLiveDelta", "handleHostMessage", "isTerminalMessageStatus", "pruneTerminalMessageRunStatuses", "conversationMessagesDiffer",
    "renderComposerAction", "renderStatus"]) {
    const start = source.indexOf(`  function ${name}(`);
    const ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending, name);
    vm.runInContext(source.slice(start, start + ending.index + ending[0].length), context);
  }
  return { state, elements, context, posts, storage, toolSelections, codingUpdates,
    emit: (type, payload) => context.handleHostMessage({ detail: { type, payload } }) };
}

function snapshot(extra = {}) {
  return { activeSessionId: "session-a", sessions: [{ id: "session-a", title: "Sitzung A" }],
    messages: [{ id: "answer-a", sessionId: "session-a", role: "assistant", content: "", status: "streaming" }],
    reasoningModelId: "model-a", reasoningRole: "general", contextUsed: 300, contextLimit: 32768,
    contextSource: "estimated", chatMode: "general", isRunning: false, ...extra };
}

test("workspace headers keep every session visible and project compose emits its exact workspace", async () => {
  const { context, elements, posts, state } = harness();
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
  assert.equal(elements.sessionList.querySelectorAll(".session-group__add").length, 3,
    "an empty project group is not part of the active mode sidebar");
  state.selectedToolAction = "planMode";
  state.persistentToolAction = "planMode";
  state.selectedExtensionActionId = "builtin.coding/plan-mode";
  state.persistentExtensionActionId = "builtin.coding/plan-mode";
  await elements.sessionList.querySelector(".session-group__add").dispatch("click");
  assert.equal(posts.at(-1).type, "session.projectCreate");
  assert.equal(posts.at(-1).payload.workspacePath, "C:\\Projects\\GO-WinUI");
  assert.equal(posts.at(-1).payload.chatMode, "general");
  assert.equal(state.selectedExtensionActionId, null);
  assert.equal(state.persistentExtensionActionId, null);
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
  context.applySnapshot(snapshot({ selectedExtensionActionId: null, selectedToolAction: null }));
  state.selectedExtensionActionId = "builtin.web/web-search";
  state.selectedToolAction = "webSearch";
  context.applySnapshot(snapshot({ selectedExtensionActionId: null, selectedToolAction: null, isRunning: false }));
  assert.equal(state.selectedToolAction, "webSearch", "idle refresh must retain the unsent request capability");
  assert.equal(state.selectedExtensionActionId, "builtin.web/web-search");
  context.applySnapshot(snapshot({ activeSessionId: "session-b", messages: [], selectedExtensionActionId: null, selectedToolAction: null }));
  assert.equal(state.selectedToolAction, null);
  assert.equal(state.selectedExtensionActionId, null);
});

test("global project plus requests the native folder picker before creating anything", () => {
  const { context, state, posts } = harness();
  context.createWorkspaceProject();
  assert.equal(posts.length, 1);
  assert.equal(posts[0].type, "session.workspaceCreate");
  assert.deepEqual(Object.keys(posts[0].payload), ["chatMode"], "only the active mode is sent before the native picker resolves a path");
  assert.equal(posts[0].payload.chatMode, "general");
  assert.equal(state.sessions.length, 0, "no optimistic project or session is created before picker acceptance");
  state.isAiBusy = true;
  context.createWorkspaceProject();
  assert.equal(posts.length, 1, "a busy UI cannot start another folder selection");
});

test("project-local compose preserves each workspace and ignores removed legacy pin metadata", async () => {
  const { context, elements, posts } = harness();
  context.applySnapshot(snapshot({ sessions: [
    { id: "a-normal", title: "A normal", sessionGroupId: "a", updatedAt: "2026-09-19T08:44:00Z" },
    { id: "b-normal", title: "B normal", sessionGroupId: "b", updatedAt: "2026-09-19T08:44:00Z" },
    { id: "b-pin", title: "B alter Pin", isPinned: true, sessionGroupId: "b", updatedAt: "2026-09-18T08:44:00Z" },
    { id: "a-pin", title: "A alter Pin", isPinned: true, sessionGroupId: "a", updatedAt: "2026-09-18T08:44:00Z" }
  ], sessionGroups: [
    { id: "a", name: "Projekt A", workspacePath: "C:\\Projects\\A" },
    { id: "b", name: "Projekt B", workspacePath: "D:\\Projects\\B" }
  ] }));
  const groups = elements.sessionList.querySelectorAll(".session-group");
  assert.equal(groups.length, 2);
  for (const [index, group] of groups.entries()) {
    const rows = group.querySelectorAll(".session-item");
    assert.equal(rows.length, 2);
    assert.equal(rows[0].classList.contains("pinned"), false);
    assert.equal(rows[1].classList.contains("pinned"), false);
    assert.match(rows[0].textContent, /normal/, "activity ordering wins over legacy pin metadata");
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
    assert.equal(posts.at(-1).payload.chatMode, "general");
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

test("snapshot chatMode filters project and ungrouped sessions before rendering", () => {
  const { context, state, elements } = harness();
  const sessions = [
    { id: "general-project", title: "General Projekt", chatMode: "General", sessionGroupId: "general-workspace" },
    { id: "coding-project", title: "Coding Projekt", chatMode: "Coding", sessionGroupId: "coding-workspace" },
    { id: "general-free", title: "General Frei", chatMode: "General" },
    { id: "coding-free", title: "Coding Frei", chatMode: "Coding" }
  ];
  const sessionGroups = [
    { id: "general-workspace", name: "General Workspace", workspacePath: "C:\\General" },
    { id: "coding-workspace", name: "Coding Workspace", workspacePath: "C:\\Coding" },
    { id: "empty-workspace", name: "Leeres Workspace", workspacePath: "C:\\Empty" }
  ];
  context.applySnapshot(snapshot({
    chatMode: "Coding",
    activeSessionId: "coding-project",
    selectedToolAction: null,
    messages: [],
    sessions,
    sessionGroups
  }));
  assert.equal(state.chatMode, "coding");
  assert.equal(elements.sessionList.querySelectorAll(".session-item").length, 2);
  assert.match(elements.sessionList.textContent, /Coding Workspace/);
  assert.match(elements.sessionList.textContent, /Coding Projekt/);
  assert.match(elements.sessionList.textContent, /Coding Frei/);
  assert.doesNotMatch(elements.sessionList.textContent, /General Workspace|General Projekt|General Frei|Leeres Workspace/);

  context.applySnapshot(snapshot({
    chatMode: "general",
    activeSessionId: "general-project",
    messages: [],
    sessions,
    sessionGroups
  }));
  assert.equal(elements.sessionList.querySelectorAll(".session-item").length, 2);
  assert.match(elements.sessionList.textContent, /General Workspace/,
    "General keeps its own workspace-backed project group");
  assert.match(elements.sessionList.textContent, /General Projekt/);
  assert.match(elements.sessionList.textContent, /General Frei/);
  assert.doesNotMatch(elements.sessionList.textContent, /Coding Workspace|Coding Projekt|Coding Frei|Leeres Workspace/);
});

test("a stale grouping snapshot from another chat mode cannot replace the active sidebar", () => {
  const { context, state, elements, emit } = harness();
  context.applySnapshot(snapshot({
    chatMode: "coding",
    activeSessionId: "coding-session",
    messages: [],
    sessions: [{ id: "coding-session", title: "Coding Sitzung", chatMode: "coding", sessionGroupId: "coding-project" }],
    sessionGroups: [{ id: "coding-project", name: "Coding Projekt", workspacePath: "C:\\Coding" }]
  }));

  emit("session.grouped", {
    chatMode: "general",
    selectedChatMode: "general",
    sessions: [{ id: "general-session", title: "General Sitzung", chatMode: "general", sessionGroupId: "general-project" }],
    sessionGroups: [{ id: "general-project", name: "General Projekt", workspacePath: "C:\\General" }]
  });

  assert.equal(state.chatMode, "coding");
  assert.deepEqual(Array.from(state.sessions, session => session.id), ["coding-session"]);
  assert.match(elements.sessionList.textContent, /Coding Projekt.*Coding Sitzung/s);
  assert.doesNotMatch(elements.sessionList.textContent, /General Projekt|General Sitzung/);
});

test("profile queue state stays visible across session snapshots and position changes", () => {
  const { context, state, elements, emit } = harness();
  const active = { ticketId: "ticket-a", sessionId: "session-a", requestId: "request-a", state: "running", position: 0 };
  const waiting = { ticketId: "ticket-b", sessionId: "session-b", requestId: "request-b", state: "queued", position: 1 };
  context.applySnapshot(snapshot({
    activeSessionId: "session-c",
    messages: [],
    sessions: [
      { id: "session-a", title: "Aktiver Auftrag" },
      { id: "session-b", title: "Wartender Auftrag" },
      { id: "session-c", title: "Offene Sitzung" }
    ],
    isAiBusy: true,
    runQueue: { active, pending: [waiting], queueDepth: 1, isIdle: false }
  }));
  assert.equal(state.runQueue.active.sessionId, "session-a");
  assert.match(elements.sessionList.textContent, /Aktiver AuftragLäuft/);
  assert.match(elements.sessionList.textContent, /Wartender AuftragWarteschlange · Platz 1/);

  emit("queue.changed", {
    runQueue: {
      active: waiting,
      pending: [{ ...active, state: "queued", position: 1 }],
      queueDepth: 1,
      isIdle: false
    }
  });
  assert.match(elements.sessionList.textContent, /Wartender AuftragLäuft/);
  assert.match(elements.sessionList.textContent, /Aktiver AuftragWarteschlange · Platz 1/);
});

test("canonical persistent extension action restores its local UI alias and legacy snapshots remain readable", () => {
  const { context, state } = harness();
  context.applySnapshot(snapshot({
    selectedExtensionActionId: "builtin.audiobook/create",
    sessions: [{ id: "session-a", title: "Hörbuch", persistentExtensionActionId: "builtin.audiobook/create" }]
  }));
  assert.equal(state.persistentExtensionActionId, "builtin.audiobook/create");
  assert.equal(state.selectedExtensionActionId, "builtin.audiobook/create");
  assert.equal(state.persistentToolAction, "audiobook");
  assert.equal(state.selectedToolAction, "audiobook");

  context.applySnapshot(snapshot({ selectedToolAction: "audiobook" }));
  assert.equal(state.selectedExtensionActionId, "builtin.audiobook/create");
  assert.equal(state.selectedToolAction, "audiobook");

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
  assert.ok(rule(".session-group__head").includes("grid-template-columns: 15px 16px minmax(0, 1fr) minmax(16px, auto)"));
  assert.ok(rule(".session-group__head").includes("overflow: hidden"));
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

test("an already-open LAN conversation applies a host prompt, stream, tool update and final answer without a refresh", () => {
  const { context, state, codingUpdates, emit } = harness();
  context.applySnapshot(snapshot({ messages: [], conversationRevision: 0, isRunning: false }));
  const userMessage = {
    id: "question-live", sessionId: "session-a", role: "user", content: "Bitte prüfe den Host-Lauf.",
    status: "completed", createdAt: "2026-09-27T10:00:00.0000000Z"
  };
  const assistantMessage = {
    id: "answer-live", sessionId: "session-a", role: "assistant", content: "", status: "streaming",
    createdAt: "2026-09-27T10:00:00.0000001Z", toolSteps: []
  };

  emit("chat.started", {
    sessionId: "session-a", userMessage, message: assistantMessage, runId: "run-live",
    runStatus: "Denkt nach", model: "local-model"
  });
  assert.deepEqual(Array.from(state.messages, message => message.id), ["question-live", "answer-live"],
    "the host-authored user bubble and assistant placeholder must appear immediately");
  assert.equal(state.isRunning, true);
  emit("chat.started", {
    sessionId: "session-a", userMessage, message: assistantMessage, runId: "run-live",
    runStatus: "Denkt nach", model: "local-model"
  });
  assert.deepEqual(Array.from(state.messages, message => message.id), ["question-live", "answer-live"],
    "a repeated lifecycle packet must update by message ID instead of duplicating bubbles");

  const toolSteps = [{ id: "tool-1", tool: "fs.readText", status: "running", detail: "app.js" }];
  emit("chat.delta", {
    sessionId: "session-a", messageId: "answer-live", content: "Ich prüfe die Datei", toolSteps
  });
  assert.equal(state.messages[1].content, "Ich prüfe die Datei");
  assert.deepEqual(JSON.parse(JSON.stringify(state.messages[1].toolSteps)), toolSteps,
    "chat.delta carries the complete live tool timeline as well as text");

  const toolStep = { id: "tool-1", tool: "fs.readText", status: "completed", detail: "app.js gelesen" };
  emit("status.changed", {
    sessionId: "session-a", messageId: "answer-live", runStatus: "Werkzeug abgeschlossen", toolStep
  });
  assert.deepEqual(JSON.parse(JSON.stringify(codingUpdates.at(-1).toolStep)), toolStep);

  const finalMessage = {
    ...assistantMessage,
    content: "Die Prüfung ist abgeschlossen.",
    status: "completed",
    toolSteps: [{ ...toolStep }]
  };
  emit("chat.completed", {
    sessionId: "session-a", message: finalMessage, runStatus: "Abgeschlossen",
    session: { id: "session-a", title: "Host-Lauf" }
  });
  assert.equal(state.messages.length, 2);
  assert.equal(state.messages[1].content, "Die Prüfung ist abgeschlossen.");
  assert.equal(state.messages[1].status, "completed");
  assert.equal(state.isRunning, false);
  assert.equal(state.messageRunStatus.size, 0);
});

test("late conversation snapshots cannot roll back a newer live revision", () => {
  const { context, state, posts } = harness();
  const current = {
    id: "answer-current", sessionId: "session-a", role: "assistant",
    content: "Neuer Live-Stand", status: "streaming", revision: 4
  };
  context.applySnapshot(snapshot({ messages: [current], conversationRevision: 4, isRunning: true }));
  state.conversationRefreshPending = true;

  context.applyConversationSnapshot({
    activeSessionId: "session-a",
    conversationRevision: 3,
    messages: [{ ...current, content: "Veralteter Snapshot", revision: 3 }]
  });

  assert.equal(state.messages[0].content, "Neuer Live-Stand");
  assert.equal(state.conversationRevision, 4);
  assert.equal(posts.at(-1).type, "conversation.refresh");
  assert.equal(state.conversationRefreshPending, true,
    "the stale response is replaced by one fresh in-flight snapshot request");
});

test("chat start accepts its two messages but refreshes an unexplained revision gap", () => {
  const { context, state, posts, emit } = harness();
  context.applySnapshot(snapshot({ messages: [], conversationRevision: 3, isRunning: false }));

  emit("chat.started", {
    sessionId: "session-a",
    conversationRevision: 8,
    userMessage: { id: "question-gap", sessionId: "session-a", role: "user", content: "Neu",
      createdAt: "2026-09-27T10:00:00.0000000Z" },
    message: { id: "answer-gap", sessionId: "session-a", role: "assistant", content: "", status: "streaming",
      createdAt: "2026-09-27T10:00:00.0000001Z" },
    runStatus: "Denkt nach"
  });

  assert.deepEqual(Array.from(state.messages, message => message.id), ["question-gap", "answer-gap"]);
  assert.equal(state.conversationRevision, 8);
  assert.equal(posts.at(-1).type, "conversation.refresh");
  assert.equal(state.conversationRefreshPending, true);
});

test("conversation lifecycle events from another session never alter the locally open dialog", () => {
  const { context, state, emit } = harness();
  context.applySnapshot(snapshot({ messages: [
    { id: "local-answer", sessionId: "session-a", role: "assistant", content: "Lokaler Dialog", status: "completed" }
  ] }));
  const before = JSON.stringify(state.messages);

  emit("chat.started", {
    sessionId: "session-b",
    userMessage: { id: "foreign-question", sessionId: "session-b", role: "user", content: "Fremd" },
    message: { id: "foreign-answer", sessionId: "session-b", role: "assistant", content: "", status: "streaming" }
  });
  emit("chat.delta", {
    sessionId: "session-b", messageId: "foreign-answer", content: "Fremder Stream", toolSteps: []
  });
  emit("chat.completed", {
    sessionId: "session-b",
    message: { id: "foreign-answer", sessionId: "session-b", role: "assistant", content: "Fertig", status: "completed" }
  });

  assert.equal(JSON.stringify(state.messages), before);
  assert.equal(state.activeSessionId, "session-a");
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
