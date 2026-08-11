(function () {
  "use strict";

  const state = {
    sessions: [], messages: [], workflows: [], documents: [],
    activeSessionId: null, selectedWorkflowId: null, selectedWorkflowEditorId: null,
    isRunning: false, model: null, contextUsed: 0, contextLimit: 8192,
    contextWasTruncated: false, contextNotice: null
  };
  const byId = id => document.getElementById(id);
  const elements = {
    sessionList: byId("session-list"), sessionSearch: byId("session-search"),
    welcome: byId("welcome"), messageList: byId("message-list"), messageScroll: byId("message-scroll"),
    prompt: byId("prompt"), send: byId("send"), stop: byId("stop"),
    connection: byId("connection-pill"), context: byId("context-meter"),
    contextStrip: byId("context-strip"), workflowChip: byId("workflow-chip"),
    workflowChipName: byId("workflow-chip-name"), documents: byId("document-chips"),
    overlay: byId("workflow-overlay"), workflowList: byId("workflow-list"),
    workflowSearch: byId("workflow-search"), workflowEditor: byId("workflow-editor"),
    workflowId: byId("workflow-id"), workflowRevision: byId("workflow-revision"),
    workflowName: byId("workflow-name"), workflowDomain: byId("workflow-domain"),
    workflowTags: byId("workflow-tags"), workflowDescription: byId("workflow-description"),
    workflowSummary: byId("workflow-summary"), workflowContent: byId("workflow-content"),
    workflowLock: byId("workflow-lock-note"), deleteWorkflow: byId("delete-workflow"),
    cloneWorkflow: byId("clone-workflow"), saveWorkflow: byId("save-workflow"),
    chatTitle: byId("chat-title")
  };
  let draftTimer = 0;
  let pendingDraft = null;

  function post(type, payload) {
    try { return globalThis.goBridge.post(type, payload); }
    catch (error) { showToast(error instanceof Error ? error.message : String(error), true); return null; }
  }

  function showToast(message, isError) {
    const toast = document.createElement("div");
    toast.className = `toast${isError ? " error" : ""}`;
    toast.textContent = message;
    byId("toast-region").append(toast);
    setTimeout(() => toast.remove(), 4500);
  }

  function scheduleDraftSave() {
    clearTimeout(draftTimer);
    const scheduled = { sessionId: state.activeSessionId, draft: elements.prompt.value };
    pendingDraft = scheduled;
    draftTimer = setTimeout(() => {
      if (pendingDraft !== scheduled) return;
      pendingDraft = null;
      if (scheduled.sessionId) post("session.draft", scheduled);
    }, 500);
  }

  function flushDraft() {
    clearTimeout(draftTimer);
    draftTimer = 0;
    const draft = pendingDraft;
    pendingDraft = null;
    if (draft?.sessionId) post("session.draft", draft);
  }

  function dateLabel(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "";
    return new Intl.DateTimeFormat(document.documentElement.lang || "de", { dateStyle: "short", timeStyle: "short" }).format(date);
  }

  function renderSessions() {
    const query = elements.sessionSearch.value.trim().toLocaleLowerCase();
    elements.sessionList.replaceChildren();
    for (const session of state.sessions.filter(item => !query || item.title.toLocaleLowerCase().includes(query))) {
      const item = document.createElement("div");
      item.className = `session-item${session.id === state.activeSessionId ? " active" : ""}`;
      item.setAttribute("role", "listitem");
      const main = document.createElement("button");
      main.type = "button";
      main.className = "session-item__main";
      main.style.cssText = "border:0;background:transparent;text-align:left;min-width:0;padding:0;color:inherit";
      const title = document.createElement("span"); title.className = "session-item__title"; title.textContent = session.title || "Neuer Chat";
      const date = document.createElement("span"); date.className = "session-item__date"; date.textContent = dateLabel(session.updatedAt);
      main.append(title, date);
      main.addEventListener("click", () => { flushDraft(); post("session.open", { sessionId: session.id }); });
      const menu = document.createElement("button");
      menu.type = "button"; menu.className = "session-actions"; menu.textContent = "•••"; menu.setAttribute("aria-label", `Aktionen für ${session.title}`);
      menu.addEventListener("click", () => sessionActions(session));
      item.append(main, menu); elements.sessionList.append(item);
    }
  }

  function sessionActions(session) {
    const nextTitle = globalThis.prompt("Chat umbenennen", session.title || "Neuer Chat");
    if (nextTitle === null) return;
    const title = nextTitle.trim();
    if (title) { post("session.rename", { sessionId: session.id, title }); return; }
    if (globalThis.confirm(`„${session.title}“ endgültig löschen?`)) post("session.delete", { sessionId: session.id });
  }

  function renderMessages(scrollToEnd) {
    elements.messageList.replaceChildren();
    elements.welcome.hidden = state.messages.length > 0;
    for (const message of state.messages) elements.messageList.append(createMessage(message));
    if (scrollToEnd) requestAnimationFrame(() => { elements.messageScroll.scrollTop = elements.messageScroll.scrollHeight; });
  }

  function createMessage(message) {
    const article = document.createElement("article");
    article.className = `message ${String(message.role).toLowerCase()}`;
    article.dataset.messageId = message.id;
    if (String(message.role).toLowerCase() === "assistant") {
      const avatar = document.createElement("div"); avatar.className = "avatar"; avatar.textContent = "GO"; article.append(avatar);
    }
    const body = document.createElement("div"); body.className = "message-body";
    if (String(message.role).toLowerCase() === "assistant") {
      const meta = document.createElement("div"); meta.className = "message-meta"; meta.textContent = "GO";
      if (message.status && !["completed", "Completed"].includes(message.status)) {
        const status = document.createElement("span");
        status.className = `message-status ${String(message.status).toLowerCase()}`;
        status.textContent = statusLabel(message.status); meta.append(" · ", status);
      }
      body.append(meta);
    }
    const content = document.createElement("div"); content.className = "message-content";
    if (["streaming", "Streaming"].includes(message.status)) content.classList.add("stream-cursor");
    content.append(globalThis.goMarkdown.render(message.content || "")); body.append(content);
    const actions = document.createElement("div"); actions.className = "message-actions";
    const copy = document.createElement("button"); copy.type = "button"; copy.textContent = "Kopieren";
    copy.addEventListener("click", () => post("message.copy", { text: message.content || "" })); actions.append(copy);
    if (String(message.role).toLowerCase() === "assistant" && message.content) {
      const workflow = document.createElement("button"); workflow.type = "button"; workflow.textContent = "Als Workflow";
      workflow.addEventListener("click", () => post("workflow.createFromMessage", { messageId: message.id })); actions.append(workflow);
    }
    if (String(message.role).toLowerCase() === "assistant"
      && ["failed", "interrupted", "cancelled"].includes(String(message.status).toLowerCase())) {
      const retry = document.createElement("button"); retry.type = "button"; retry.textContent = "Erneut senden";
      retry.addEventListener("click", () => retryMessage(message)); actions.append(retry);
      if (message.content) {
        const resume = document.createElement("button"); resume.type = "button"; resume.textContent = "Fortsetzen";
        resume.addEventListener("click", () => continueMessage()); actions.append(resume);
      }
    }
    body.append(actions); article.append(body); return article;
  }

  function statusLabel(status) {
    return ({ pending: "Wartet", streaming: "Antwortet …", cancelled: "Abgebrochen", failed: "Fehlgeschlagen", interrupted: "Unterbrochen" })[String(status).toLowerCase()] || String(status);
  }

  function renderContext() {
    const workflow = state.workflows.find(item => item.id === state.selectedWorkflowId);
    elements.workflowChip.hidden = !workflow;
    elements.workflowChipName.textContent = workflow?.title || "";
    elements.documents.replaceChildren();
    for (const documentItem of state.documents) {
      const chip = document.createElement("div"); chip.className = "context-chip";
      const icon = document.createElement("span"); icon.textContent = "▤";
      const name = document.createElement("span"); name.textContent = documentItem.fileName; name.title = documentItem.fileName;
      const remove = document.createElement("button"); remove.type = "button"; remove.textContent = "×"; remove.setAttribute("aria-label", `${documentItem.fileName} entfernen`);
      remove.addEventListener("click", () => post("document.remove", { documentId: documentItem.id }));
      chip.append(icon, name, remove); elements.documents.append(chip);
    }
    elements.contextStrip.hidden = !workflow && state.documents.length === 0;
  }

  function renderStatus() {
    elements.send.hidden = state.isRunning;
    elements.stop.hidden = !state.isRunning;
    elements.prompt.disabled = state.isRunning;
    elements.connection.className = `status-pill ${state.isRunning ? "busy" : state.model ? "online" : "offline"}`;
    elements.connection.lastElementChild.textContent = state.isRunning ? "LM Studio antwortet" : state.model ? state.model : "LM Studio nicht verbunden";
    const used = Number(state.contextUsed || 0).toLocaleString("de-DE");
    const limit = Number(state.contextLimit || 8192).toLocaleString("de-DE");
    elements.context.textContent = `${used} / ${limit}`;
    elements.context.classList.toggle("warning", Boolean(state.contextWasTruncated));
    elements.context.title = state.contextNotice || "Geschätzte Belegung des Modellkontexts";
  }

  function renderWorkflows() {
    const query = elements.workflowSearch.value.trim().toLocaleLowerCase();
    elements.workflowList.replaceChildren();
    for (const workflow of state.workflows.filter(item => !query ||
      `${item.title} ${item.description} ${item.domain} ${item.contextSummary} ${(item.tags || []).join(" ")}`
        .toLocaleLowerCase().includes(query))) {
      const button = document.createElement("button"); button.type = "button";
      button.className = `workflow-item${workflow.id === state.selectedWorkflowEditorId ? " active" : ""}`;
      const title = document.createElement("strong"); title.textContent = workflow.title;
      const description = document.createElement("span"); description.textContent = workflow.description || workflow.domain || "Ohne Beschreibung";
      button.append(title, description);
      if (workflow.isBuiltIn) { const badge = document.createElement("span"); badge.className = "built-in-badge"; badge.textContent = "Integriert"; button.append(badge); }
      button.addEventListener("click", () => editWorkflow(workflow)); elements.workflowList.append(button);
    }
  }

  function editWorkflow(workflow) {
    state.selectedWorkflowEditorId = workflow?.id || null;
    elements.workflowId.value = workflow?.id || "";
    elements.workflowRevision.value = String(workflow?.revision || 0);
    elements.workflowName.value = workflow?.title || "";
    elements.workflowDomain.value = workflow?.domain || "";
    elements.workflowTags.value = (workflow?.tags || []).join(", ");
    elements.workflowDescription.value = workflow?.description || "";
    elements.workflowSummary.value = workflow?.contextSummary || "";
    elements.workflowContent.value = workflow?.contentJson || '{"schema":"go.general.workflow.v1","blocks":[]}';
    const locked = Boolean(workflow?.isBuiltIn);
    const persisted = Boolean(workflow?.id);
    for (const control of elements.workflowEditor.querySelectorAll("input:not([type=hidden]), textarea")) control.disabled = locked;
    elements.workflowLock.hidden = !locked;
    elements.saveWorkflow.hidden = locked;
    elements.deleteWorkflow.hidden = !persisted || locked;
    elements.cloneWorkflow.hidden = !persisted;
    byId("select-workflow").hidden = !persisted;
    renderWorkflows();
  }

  function openWorkflows() {
    elements.overlay.hidden = false;
    post("workflow.list", { search: elements.workflowSearch.value });
    const selected = state.workflows.find(item => item.id === state.selectedWorkflowId) || state.workflows[0];
    editWorkflow(selected || null);
  }

  function closeWorkflows() { elements.overlay.hidden = true; }

  function submitPrompt() {
    const prompt = elements.prompt.value.trim();
    if (!prompt || state.isRunning) return;
    clearTimeout(draftTimer);
    draftTimer = 0;
    pendingDraft = null;
    post("chat.send", {
      sessionId: state.activeSessionId,
      prompt,
      workflowId: state.selectedWorkflowId,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: byId("reasoning").value
    });
    elements.prompt.value = ""; resizePrompt();
  }

  function retryMessage(message) {
    const index = state.messages.findIndex(item => item.id === message.id);
    const source = state.messages.slice(0, index).reverse().find(item => String(item.role).toLowerCase() === "user");
    if (!source?.content || state.isRunning) return;
    post("chat.send", {
      sessionId: state.activeSessionId,
      prompt: source.content,
      workflowId: state.selectedWorkflowId,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: byId("reasoning").value
    });
  }

  function continueMessage() {
    if (state.isRunning || !state.activeSessionId) return;
    post("chat.send", {
      sessionId: state.activeSessionId,
      prompt: "Setze deine unmittelbar vorherige, unterbrochene Antwort direkt an der Abbruchstelle fort. Wiederhole den bereits vorhandenen Text nicht.",
      workflowId: state.selectedWorkflowId,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: byId("reasoning").value
    });
  }

  function resizePrompt() {
    elements.prompt.style.height = "auto";
    elements.prompt.style.height = `${Math.min(elements.prompt.scrollHeight, 210)}px`;
  }

  function applySnapshot(payload) {
    state.sessions = Array.isArray(payload.sessions) ? payload.sessions : [];
    state.messages = Array.isArray(payload.messages) ? payload.messages : [];
    state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
    state.documents = Array.isArray(payload.documents) ? payload.documents : [];
    state.activeSessionId = payload.activeSessionId || null;
    state.selectedWorkflowId = payload.selectedWorkflowId || null;
    state.isRunning = Boolean(payload.isRunning);
    state.model = payload.model || null;
    state.contextUsed = payload.contextUsed || 0;
    state.contextLimit = payload.contextLimit || 8192;
    state.contextWasTruncated = Boolean(payload.contextWasTruncated);
    state.contextNotice = payload.contextNotice || null;
    elements.prompt.value = payload.draft || "";
    byId("reasoning").value = payload.reasoningEffort || "medium";
    const active = state.sessions.find(item => item.id === state.activeSessionId);
    elements.chatTitle.textContent = active?.title || "AI Assistent";
    renderSessions(); renderMessages(true); renderContext(); renderWorkflows(); renderStatus(); resizePrompt();
  }

  function handleHostMessage(event) {
    const { type, payload } = event.detail;
    switch (type) {
      case "state.snapshot": applySnapshot(payload); break;
      case "chat.started":
        state.isRunning = true;
        if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
        if (Number.isFinite(payload.contextLimit)) state.contextLimit = payload.contextLimit;
        state.contextWasTruncated = Boolean(payload.contextWasTruncated);
        state.contextNotice = payload.contextNotice || null;
        if (payload.message && payload.message.sessionId === state.activeSessionId) {
          const index = state.messages.findIndex(item => item.id === payload.message.id);
          if (index >= 0) state.messages[index] = payload.message; else state.messages.push(payload.message);
        }
        renderMessages(true); renderStatus();
        if (state.contextWasTruncated) showToast(state.contextNotice || "Der Modellkontext wurde gekürzt.");
        break;
      case "chat.delta": {
        if (payload.sessionId !== state.activeSessionId) break;
        const message = state.messages.find(item => item.id === payload.messageId);
        if (message) { message.content = payload.content || ""; message.status = "streaming"; renderMessages(true); }
        break;
      }
      case "chat.completed": case "chat.cancelled": case "chat.failed":
        state.isRunning = false;
        if (payload.message && payload.message.sessionId === state.activeSessionId) {
          const index = state.messages.findIndex(item => item.id === payload.message.id);
          if (index >= 0) state.messages[index] = payload.message; else state.messages.push(payload.message);
        }
        renderMessages(true); renderStatus();
        if (type === "chat.failed") showToast(payload.error || "Die Antwort ist fehlgeschlagen.", true);
        break;
      case "session.changed": case "workflow.changed": case "document.changed": applySnapshot(payload); break;
      case "workflow.snapshot": state.workflows = payload.workflows || []; renderWorkflows(); break;
      case "workflow.draft":
        elements.overlay.hidden = false;
        editWorkflow(payload.workflow || null);
        elements.workflowName.focus();
        break;
      case "status.changed":
        Object.assign(state, payload); renderContext(); renderStatus(); break;
      case "theme.changed":
        document.documentElement.dataset.theme = payload.highContrast ? "high-contrast" : payload.theme || "system";
        if (payload.accent) document.documentElement.style.setProperty("--accent", payload.accent);
        break;
      case "host.error": showToast(payload.message || "Unbekannter Fehler", true); break;
      default: break;
    }
  }

  byId("new-session").addEventListener("click", () => { flushDraft(); post("session.create", {}); });
  elements.sessionSearch.addEventListener("input", renderSessions);
  byId("open-sessions").addEventListener("click", () => document.body.classList.add("sessions-open"));
  byId("collapse-sessions").addEventListener("click", () => document.body.classList.remove("sessions-open"));
  elements.prompt.addEventListener("input", () => {
    resizePrompt();
    scheduleDraftSave();
  });
  elements.prompt.addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); submitPrompt(); }
  });
  elements.send.addEventListener("click", submitPrompt);
  elements.stop.addEventListener("click", () => post("chat.cancel", {}));
  byId("pick-document").addEventListener("click", () => post("document.pick", { sessionId: state.activeSessionId }));
  byId("export-pdf").addEventListener("click", () => post("chat.exportPdf", { sessionId: state.activeSessionId }));
  byId("open-workflows").addEventListener("click", openWorkflows);
  byId("close-workflows").addEventListener("click", closeWorkflows);
  elements.overlay.addEventListener("click", event => { if (event.target === elements.overlay) closeWorkflows(); });
  elements.workflowSearch.addEventListener("input", renderWorkflows);
  byId("new-workflow").addEventListener("click", () => editWorkflow(null));
  byId("remove-workflow").addEventListener("click", () => post("workflow.select", { workflowId: null }));
  byId("select-workflow").addEventListener("click", () => { post("workflow.select", { workflowId: elements.workflowId.value || null }); closeWorkflows(); });
  elements.deleteWorkflow.addEventListener("click", () => {
    if (elements.workflowId.value && globalThis.confirm("Diesen Workflow endgültig löschen?")) post("workflow.delete", { workflowId: elements.workflowId.value, revision: Number(elements.workflowRevision.value) });
  });
  elements.cloneWorkflow.addEventListener("click", () => {
    const workflow = state.workflows.find(item => item.id === elements.workflowId.value);
    if (workflow) post("workflow.clone", { workflowId: workflow.id, title: `${workflow.title} – Kopie` });
  });
  elements.workflowEditor.addEventListener("submit", event => {
    event.preventDefault();
    try { JSON.parse(elements.workflowContent.value || "{}"); }
    catch { showToast("Der Workflow-Inhalt ist kein gültiges JSON.", true); return; }
    const payload = {
      workflowId: elements.workflowId.value || null,
      revision: Number(elements.workflowRevision.value || 0), title: elements.workflowName.value.trim(),
      domain: elements.workflowDomain.value.trim(), tags: elements.workflowTags.value.split(",").map(tag => tag.trim()).filter(Boolean),
      description: elements.workflowDescription.value.trim(), contextSummary: elements.workflowSummary.value.trim(),
      contentJson: elements.workflowContent.value
    };
    post(payload.workflowId ? "workflow.update" : "workflow.create", payload);
  });
  for (const suggestion of document.querySelectorAll("[data-prompt]")) {
    suggestion.addEventListener("click", () => { elements.prompt.value = suggestion.dataset.prompt || ""; resizePrompt(); elements.prompt.focus(); });
  }
  globalThis.addEventListener("go:host-message", handleHostMessage);
  globalThis.addEventListener("keydown", event => { if (event.key === "Escape" && !elements.overlay.hidden) closeWorkflows(); });
  globalThis.goCaptureDraft = () => ({ sessionId: state.activeSessionId, draft: elements.prompt.value });
  globalThis.goFlushDraft = flushDraft;
  globalThis.addEventListener("pagehide", flushDraft);
  globalThis.addEventListener("beforeunload", flushDraft);
  post("app.ready", {});
})();
