(function () {
  "use strict";

  const state = {
    sessions: [],
    messages: [],
    workflows: [],
    documents: [],
    activeSessionId: null,
    selectedWorkflowEditorId: null,
    isWorkflowEditing: false,
    pendingWorkflowTitle: null,
    isRunning: false,
    model: null,
    contextUsed: 0,
    contextLimit: 8192,
    contextWasTruncated: false,
    contextNotice: null
  };

  const byId = id => document.getElementById(id);
  const elements = {
    appShell: byId("app-shell"),
    sessionList: byId("session-list"),
    sessionSearch: byId("session-search"),
    toggleSessions: byId("toggle-sessions"),
    clearSessions: byId("clear-sessions"),
    newSession: byId("new-session"),
    messageList: byId("message-list"),
    messageScroll: byId("message-scroll"),
    prompt: byId("prompt"),
    send: byId("send"),
    stop: byId("stop"),
    reasoning: byId("reasoning"),
    toolsButton: byId("tools-button"),
    toolsMenu: byId("tools-menu"),
    context: byId("context-meter"),
    contextLabel: byId("context-label"),
    contextStrip: byId("context-strip"),
    documents: byId("document-chips"),
    overlay: byId("workflow-overlay"),
    workflowList: byId("workflow-list"),
    workflowSearch: byId("workflow-search"),
    workflowEmpty: byId("workflow-empty"),
    workflowPreview: byId("workflow-preview"),
    workflowPreviewTitle: byId("workflow-preview-title"),
    workflowPreviewId: byId("workflow-preview-id"),
    workflowPreviewBadge: byId("workflow-preview-badge"),
    workflowPreviewTags: byId("workflow-preview-tags"),
    workflowPreviewDescription: byId("workflow-preview-description"),
    workflowPreviewSummary: byId("workflow-preview-summary"),
    workflowPreviewContent: byId("workflow-preview-content"),
    workflowEditor: byId("workflow-editor"),
    workflowEditorTitle: byId("workflow-editor-title"),
    workflowId: byId("workflow-id"),
    workflowRevision: byId("workflow-revision"),
    workflowName: byId("workflow-name"),
    workflowDomain: byId("workflow-domain"),
    workflowTags: byId("workflow-tags"),
    workflowDescription: byId("workflow-description"),
    workflowSummary: byId("workflow-summary"),
    workflowContent: byId("workflow-content"),
    workflowLock: byId("workflow-lock-note"),
    deleteWorkflow: byId("delete-workflow"),
    editWorkflow: byId("edit-workflow"),
    cancelWorkflowEdit: byId("cancel-workflow-edit"),
    selectWorkflow: byId("select-workflow"),
    saveWorkflow: byId("save-workflow")
  };

  const sidebarStorageKey = "go.assistant.sessions-collapsed";
  let draftTimer = 0;
  let pendingDraft = null;

  function post(type, payload) {
    try {
      return globalThis.goBridge.post(type, payload);
    } catch (error) {
      showToast(error instanceof Error ? error.message : String(error), true);
      return null;
    }
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
    return new Intl.DateTimeFormat(document.documentElement.lang || "de", {
      dateStyle: "short",
      timeStyle: "short"
    }).format(date);
  }

  function timeLabel(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "";
    const valueText = new Intl.DateTimeFormat(document.documentElement.lang || "de", {
      hour: "2-digit",
      minute: "2-digit"
    }).format(date);
    return `${valueText} Uhr`;
  }

  function sessionShortLabel(title) {
    const words = String(title || "GO").trim().split(/\s+/).filter(Boolean);
    return words.slice(0, 2).map(word => word[0]).join("").slice(0, 2) || "GO";
  }

  function setSessionsCollapsed(collapsed, persist) {
    elements.appShell.classList.toggle("sessions-collapsed", collapsed);
    elements.toggleSessions.setAttribute("aria-expanded", String(!collapsed));
    elements.toggleSessions.title = collapsed ? "Sitzungsleiste ausklappen" : "Sitzungsleiste einklappen";
    if (persist) {
      try { globalThis.localStorage.setItem(sidebarStorageKey, collapsed ? "1" : "0"); }
      catch { /* WebView storage is optional. */ }
    }
  }

  function restoreSessionsCollapsed() {
    try { setSessionsCollapsed(globalThis.localStorage.getItem(sidebarStorageKey) === "1", false); }
    catch { setSessionsCollapsed(false, false); }
  }

  function renderSessions() {
    const query = elements.sessionSearch.value.trim().toLocaleLowerCase();
    elements.sessionList.replaceChildren();
    const sessions = state.sessions.filter(item => !query || String(item.title || "").toLocaleLowerCase().includes(query));

    for (const session of sessions) {
      const item = document.createElement("div");
      item.className = `session-item${session.id === state.activeSessionId ? " active" : ""}`;
      item.setAttribute("role", "listitem");

      const open = document.createElement("button");
      open.type = "button";
      open.className = "session-item__open";
      open.title = `${session.title || "Neue Sitzung"}\nRechtsklick zum Umbenennen`;
      const main = document.createElement("span");
      main.className = "session-item__main";
      const title = document.createElement("span");
      title.className = "session-item__title";
      title.textContent = session.title || "Neue Sitzung";
      const date = document.createElement("span");
      date.className = "session-item__date";
      date.textContent = dateLabel(session.updatedAt);
      main.append(title, date);
      const short = document.createElement("span");
      short.className = "session-item__short";
      short.textContent = sessionShortLabel(session.title);
      open.append(main, short);
      open.addEventListener("click", () => {
        flushDraft();
        post("session.open", { sessionId: session.id });
        document.body.classList.remove("sessions-open");
      });
      open.addEventListener("contextmenu", event => {
        event.preventDefault();
        const nextTitle = globalThis.prompt("Sitzung umbenennen", session.title || "Neue Sitzung");
        if (nextTitle?.trim()) post("session.rename", { sessionId: session.id, title: nextTitle.trim() });
      });

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "session-delete";
      remove.disabled = state.isRunning;
      remove.setAttribute("aria-label", `${session.title || "Sitzung"} löschen`);
      remove.title = "Sitzung löschen";
      remove.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m7 7 10 10M17 7 7 17"/></svg>';
      remove.addEventListener("click", () => {
        if (globalThis.confirm(`„${session.title || "Neue Sitzung"}“ endgültig löschen?`)) {
          post("session.delete", { sessionId: session.id });
        }
      });

      item.append(open, remove);
      elements.sessionList.append(item);
    }
  }

  function introMessage() {
    const session = state.sessions.find(item => item.id === state.activeSessionId);
    if (!session) return null;
    return {
      id: `intro-${session.id}`,
      sessionId: session.id,
      role: "assistant",
      content: "Wobei kann ich dich in der TGA-Planung unterstützen?",
      status: "completed",
      createdAt: session.createdAt || session.updatedAt,
      isIntro: true
    };
  }

  function renderMessages(scrollToEnd) {
    elements.messageList.replaceChildren();
    const intro = introMessage();
    if (intro) elements.messageList.append(createMessage(intro));
    for (const message of state.messages) {
      elements.messageList.append(createMessage(message));
    }
    if (scrollToEnd) {
      requestAnimationFrame(() => { elements.messageScroll.scrollTop = elements.messageScroll.scrollHeight; });
    }
  }

  const messageCopyIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>';
  const messagePdfIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V4"/><path d="m7 9 5-5 5 5"/><path d="M20 16v3a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-3"/></svg>';
  const messageDoneIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg>';

  function flashMessageAction(button, originalIcon, label) {
    button.classList.add("copied");
    button.innerHTML = messageDoneIcon;
    setTimeout(() => {
      button.classList.remove("copied");
      button.innerHTML = originalIcon;
      button.title = label;
      button.setAttribute("aria-label", label);
    }, 1200);
  }

  function createMessageIconAction(label, icon, handler) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "message-action";
    button.title = label;
    button.setAttribute("aria-label", label);
    button.innerHTML = icon;
    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      handler(button);
    });
    return button;
  }

  function createMessageFooterLink(label, handler) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "message-footer-link";
    button.textContent = label;
    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      handler();
    });
    return button;
  }

  function scrollMessageToTop(article) {
    const scrollerBounds = elements.messageScroll.getBoundingClientRect();
    const messageBounds = article.getBoundingClientRect();
    const top = elements.messageScroll.scrollTop + messageBounds.top - scrollerBounds.top - 8;
    elements.messageScroll.scrollTo({ top: Math.max(0, top), behavior: "smooth" });
  }

  function createMessageFooter(message, article) {
    const showWorkflowActions = String(message.role).toLowerCase() === "assistant" && !message.isIntro;
    const footer = document.createElement("div");
    footer.className = `message-footer${showWorkflowActions ? " has-workflow-actions" : ""}`;
    const messageText = message.content || "";

    footer.append(createMessageIconAction("Nachricht kopieren", messageCopyIcon, button => {
      post("message.copy", { text: messageText });
      flashMessageAction(button, messageCopyIcon, "Nachricht kopieren");
    }));
    footer.append(createMessageIconAction("Nachricht als PDF exportieren", messagePdfIcon, button => {
      post("message.exportPdf", { messageId: String(message.id) });
      flashMessageAction(button, messagePdfIcon, "Nachricht als PDF exportieren");
    }));

    if (showWorkflowActions) {
      footer.append(createMessageFooterLink("Als Workflow speichern", () => {
        post("workflow.createFromMessage", { messageId: message.id });
      }));
      footer.append(createMessageFooterLink("Zum Anfang springen", () => scrollMessageToTop(article)));
    }

    if (String(message.role).toLowerCase() === "assistant"
      && ["failed", "interrupted", "cancelled"].includes(String(message.status).toLowerCase())) {
      footer.append(createMessageFooterLink("Erneut senden", () => retryMessage(message)));
      if (messageText) footer.append(createMessageFooterLink("Fortsetzen", continueMessage));
    }

    return footer;
  }

  function createMessage(message) {
    const role = String(message.role).toLowerCase();
    const article = document.createElement("article");
    article.className = `message ${role}${message.isIntro ? " intro" : ""}`;
    article.dataset.messageId = message.id;

    if (role === "assistant") {
      const avatar = document.createElement("div");
      avatar.className = "avatar";
      avatar.textContent = "AI";
      article.append(avatar);
    }

    const body = document.createElement("div");
    body.className = "message-body";
    if (role === "assistant") {
      const meta = document.createElement("div");
      meta.className = "message-meta";
      const messageTime = timeLabel(message.createdAt || message.updatedAt);
      meta.textContent = messageTime ? `AI - ${messageTime}` : "AI";
      if (message.status && !["completed", "Completed"].includes(message.status)) {
        const status = document.createElement("span");
        status.className = `message-status ${String(message.status).toLowerCase()}`;
        status.textContent = statusLabel(message.status);
        meta.append(" · ", status);
      }
      body.append(meta);
    }

    const content = document.createElement("div");
    content.className = "message-content";
    if (["streaming", "Streaming"].includes(message.status)) content.classList.add("stream-cursor");
    content.append(globalThis.goMarkdown.render(message.content || ""));
    body.append(content);
    body.append(createMessageFooter(message, article));

    article.append(body);
    return article;
  }

  function statusLabel(status) {
    return ({
      pending: "Wartet",
      streaming: "Denkt nach",
      cancelled: "Abgebrochen",
      failed: "Fehlgeschlagen",
      interrupted: "Unterbrochen"
    })[String(status).toLowerCase()] || String(status);
  }

  function renderContext() {
    elements.documents.replaceChildren();

    for (const documentItem of state.documents) {
      const chip = document.createElement("div");
      chip.className = "context-chip";
      const icon = document.createElement("span");
      icon.textContent = "▤";
      const name = document.createElement("span");
      name.textContent = documentItem.fileName;
      name.title = documentItem.fileName;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "×";
      remove.setAttribute("aria-label", `${documentItem.fileName} entfernen`);
      remove.addEventListener("click", () => post("document.remove", { documentId: documentItem.id }));
      chip.append(icon, name, remove);
      elements.documents.append(chip);
    }
    elements.contextStrip.hidden = state.documents.length === 0;
  }

  function renderStatus() {
    elements.send.hidden = state.isRunning;
    elements.stop.hidden = !state.isRunning;
    elements.prompt.disabled = state.isRunning;
    elements.newSession.disabled = state.isRunning;
    elements.clearSessions.disabled = state.isRunning;

    const usedValue = Math.max(0, Number(state.contextUsed) || 0);
    const limitValue = Math.max(1, Number(state.contextLimit) || 8192);
    const ratio = Math.max(0, Math.min(1, usedValue / limitValue));
    const percentage = Math.max(0, Math.min(100, Math.round(ratio * 100)));
    elements.context.style.setProperty("--context-fill", `${ratio * 360}deg`);
    elements.contextLabel.textContent = `${percentage}%`;
    elements.context.classList.toggle("warning", percentage >= 80 || Boolean(state.contextWasTruncated));
    elements.context.classList.toggle("full", percentage >= 100 || Boolean(state.contextWasTruncated));
    const used = usedValue.toLocaleString("de-DE");
    const limit = limitValue.toLocaleString("de-DE");
    elements.context.title = state.contextNotice || `Kontext: ${used} von ${limit} Tokens`;
    elements.context.setAttribute("aria-label", `Kontext zu ${percentage} Prozent belegt`);
  }

  function setReasoning(value) {
    const validValue = ["low", "medium", "high"].includes(value) ? value : "medium";
    elements.reasoning.value = validValue;
    for (const option of document.querySelectorAll(".reasoning-option")) {
      const active = option.dataset.reasoning === validValue;
      option.classList.toggle("active", active);
      option.setAttribute("aria-checked", String(active));
    }
  }

  function setToolsMenuOpen(open) {
    elements.toolsMenu.hidden = !open;
    elements.toolsButton.setAttribute("aria-expanded", String(open));
  }

  function renderWorkflows() {
    const query = elements.workflowSearch.value.trim().toLocaleLowerCase();
    const workflows = state.workflows.filter(item => !query ||
      `${item.title} ${item.description} ${item.domain} ${item.contextSummary} ${(item.tags || []).join(" ")}`
        .toLocaleLowerCase().includes(query));
    elements.workflowList.replaceChildren();

    if (workflows.length === 0) {
      const empty = document.createElement("p");
      empty.className = "workflow-list-empty";
      empty.textContent = query ? "Keine passenden Workflows." : "Noch keine Workflows vorhanden.";
      elements.workflowList.append(empty);
      return;
    }

    for (const workflow of workflows) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `workflow-item${workflow.id === state.selectedWorkflowEditorId ? " active" : ""}`;
      const title = document.createElement("strong");
      title.textContent = workflow.title;
      const description = document.createElement("span");
      description.textContent = workflow.description || workflow.domain || "Ohne Beschreibung";
      button.append(title, description);
      if (workflow.isBuiltIn) {
        const badge = document.createElement("span");
        badge.className = "built-in-badge";
        badge.textContent = "Integriert";
        button.append(badge);
      }
      button.addEventListener("click", () => showWorkflowPreview(workflow));
      elements.workflowList.append(button);
    }
  }

  function readableWorkflowContent(contentJson) {
    try { return JSON.stringify(JSON.parse(contentJson || "{}"), null, 2); }
    catch { return contentJson || ""; }
  }

  function setWorkflowFooterMode(mode, workflow) {
    const persisted = Boolean(workflow?.id);
    const locked = Boolean(workflow?.isBuiltIn);
    const editing = mode === "edit";
    elements.deleteWorkflow.hidden = editing || !persisted || locked;
    elements.editWorkflow.hidden = editing || !persisted || locked;
    elements.cancelWorkflowEdit.hidden = !editing;
    elements.selectWorkflow.hidden = editing || !persisted;
    elements.saveWorkflow.hidden = !editing || locked;
  }

  function setWorkflowIdentity(workflow) {
    elements.workflowId.value = workflow?.id || "";
    elements.workflowRevision.value = String(workflow?.revision || 0);
  }

  function showWorkflowPreview(workflow) {
    state.isWorkflowEditing = false;
    state.selectedWorkflowEditorId = workflow?.id || null;
    setWorkflowIdentity(workflow);
    elements.workflowEditor.hidden = true;
    elements.workflowLock.hidden = true;
    elements.workflowEmpty.hidden = Boolean(workflow);
    elements.workflowPreview.hidden = !workflow;

    if (workflow) {
      elements.workflowPreviewTitle.textContent = workflow.title || "Unbenannter Workflow";
      elements.workflowPreviewId.textContent = [workflow.domain, workflow.slug || workflow.id].filter(Boolean).join(" · ");
      elements.workflowPreviewBadge.hidden = !workflow.isBuiltIn;
      elements.workflowPreviewTags.replaceChildren();
      for (const tag of workflow.tags || []) {
        const chip = document.createElement("span");
        chip.className = "workflow-tag";
        chip.textContent = tag;
        elements.workflowPreviewTags.append(chip);
      }
      elements.workflowPreviewTags.hidden = (workflow.tags || []).length === 0;
      elements.workflowPreviewDescription.textContent = workflow.description || "Keine Beschreibung hinterlegt.";
      elements.workflowPreviewSummary.textContent = workflow.contextSummary || "Keine Kontextzusammenfassung hinterlegt.";
      elements.workflowPreviewContent.textContent = readableWorkflowContent(workflow.contentJson);
    }

    setWorkflowFooterMode("preview", workflow);
    renderWorkflows();
  }

  function showWorkflowEditor(workflow) {
    state.isWorkflowEditing = true;
    state.selectedWorkflowEditorId = workflow?.id || null;
    setWorkflowIdentity(workflow);
    elements.workflowEmpty.hidden = true;
    elements.workflowPreview.hidden = true;
    elements.workflowEditor.hidden = false;
    elements.workflowEditorTitle.textContent = workflow?.id ? "Workflow bearbeiten" : "Neuen Workflow erstellen";
    elements.workflowName.value = workflow?.title || "";
    elements.workflowDomain.value = workflow?.domain || "";
    elements.workflowTags.value = (workflow?.tags || []).join(", ");
    elements.workflowDescription.value = workflow?.description || "";
    elements.workflowSummary.value = workflow?.contextSummary || "";
    elements.workflowContent.value = readableWorkflowContent(workflow?.contentJson || '{"schema":"go.general.workflow.v1","blocks":[]}');
    const locked = Boolean(workflow?.isBuiltIn);
    for (const control of elements.workflowEditor.querySelectorAll("input:not([type=hidden]), textarea")) {
      control.disabled = locked;
    }
    elements.workflowLock.hidden = !locked;
    setWorkflowFooterMode("edit", workflow);
    renderWorkflows();
    requestAnimationFrame(() => elements.workflowName.focus());
  }

  function selectedWorkflowForDialog() {
    return state.workflows.find(item => item.id === state.selectedWorkflowEditorId)
      || state.workflows[0]
      || null;
  }

  function openWorkflows() {
    elements.overlay.hidden = false;
    post("workflow.list", { search: elements.workflowSearch.value });
    showWorkflowPreview(selectedWorkflowForDialog());
    requestAnimationFrame(() => elements.workflowSearch.focus());
  }

  function closeWorkflows() {
    elements.overlay.hidden = true;
    state.isWorkflowEditing = false;
  }

  function submitPrompt() {
    const prompt = elements.prompt.value.trim();
    if (!prompt || state.isRunning) return;
    clearTimeout(draftTimer);
    draftTimer = 0;
    pendingDraft = null;
    post("chat.send", {
      sessionId: state.activeSessionId,
      prompt,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: elements.reasoning.value
    });
    elements.prompt.value = "";
  }

  function retryMessage(message) {
    const index = state.messages.findIndex(item => item.id === message.id);
    const source = state.messages.slice(0, index).reverse().find(item => String(item.role).toLowerCase() === "user");
    if (!source?.content || state.isRunning) return;
    post("chat.send", {
      sessionId: state.activeSessionId,
      prompt: source.content,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: elements.reasoning.value
    });
  }

  function continueMessage() {
    if (state.isRunning || !state.activeSessionId) return;
    post("chat.send", {
      sessionId: state.activeSessionId,
      prompt: "Setze deine unmittelbar vorherige, unterbrochene Antwort direkt an der Abbruchstelle fort. Wiederhole den bereits vorhandenen Text nicht.",
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: elements.reasoning.value
    });
  }

  function applySnapshot(payload) {
    const dialogWasOpen = !elements.overlay.hidden;
    const editorSelection = state.selectedWorkflowEditorId;
    const wasEditing = state.isWorkflowEditing;
    state.sessions = Array.isArray(payload.sessions) ? payload.sessions : [];
    state.messages = Array.isArray(payload.messages) ? payload.messages : [];
    state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
    state.documents = Array.isArray(payload.documents) ? payload.documents : [];
    state.activeSessionId = payload.activeSessionId || null;
    state.isRunning = Boolean(payload.isRunning);
    state.model = payload.model || null;
    state.contextUsed = payload.contextUsed || 0;
    state.contextLimit = payload.contextLimit || 8192;
    state.contextWasTruncated = Boolean(payload.contextWasTruncated);
    state.contextNotice = payload.contextNotice || null;
    if (typeof payload.isSessionPaneOpen === "boolean") {
      const collapsed = !payload.isSessionPaneOpen;
      setSessionsCollapsed(collapsed, false);
      try { globalThis.localStorage.setItem(sidebarStorageKey, collapsed ? "1" : "0"); }
      catch { /* WebView storage is optional. */ }
    }
    elements.prompt.value = payload.draft || "";
    setReasoning(payload.reasoningEffort || "medium");
    renderSessions();
    renderMessages(true);
    renderContext();
    renderStatus();

    if (dialogWasOpen && !wasEditing) {
      const pending = state.pendingWorkflowTitle
        ? state.workflows.find(item => item.title === state.pendingWorkflowTitle)
        : null;
      state.pendingWorkflowTitle = null;
      const selected = pending
        || state.workflows.find(item => item.id === editorSelection)
        || selectedWorkflowForDialog();
      showWorkflowPreview(selected || null);
    } else {
      renderWorkflows();
    }
  }

  function handleHostMessage(event) {
    const { type, payload } = event.detail;
    switch (type) {
      case "state.snapshot":
        applySnapshot(payload);
        break;
      case "chat.started":
        state.isRunning = true;
        if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
        if (Number.isFinite(payload.contextLimit)) state.contextLimit = payload.contextLimit;
        state.contextWasTruncated = Boolean(payload.contextWasTruncated);
        state.contextNotice = payload.contextNotice || null;
        if (payload.message && payload.message.sessionId === state.activeSessionId) {
          const index = state.messages.findIndex(item => item.id === payload.message.id);
          if (index >= 0) state.messages[index] = payload.message;
          else state.messages.push(payload.message);
        }
        renderSessions();
        renderMessages(true);
        renderStatus();
        if (state.contextWasTruncated) showToast(state.contextNotice || "Der Modellkontext wurde gekürzt.");
        break;
      case "chat.delta": {
        if (payload.sessionId !== state.activeSessionId) break;
        const message = state.messages.find(item => item.id === payload.messageId);
        if (message) {
          message.content = payload.content || "";
          message.status = "streaming";
          renderMessages(true);
        }
        break;
      }
      case "chat.completed":
      case "chat.cancelled":
      case "chat.failed":
        state.isRunning = false;
        if (payload.session) {
          const sessionIndex = state.sessions.findIndex(item => item.id === payload.session.id);
          if (sessionIndex >= 0) state.sessions[sessionIndex] = payload.session;
          else state.sessions.push(payload.session);
        }
        if (payload.message && payload.message.sessionId === state.activeSessionId) {
          const index = state.messages.findIndex(item => item.id === payload.message.id);
          if (index >= 0) state.messages[index] = payload.message;
          else state.messages.push(payload.message);
        }
        renderSessions();
        renderMessages(true);
        renderStatus();
        if (type === "chat.failed") showToast(payload.error || "Die Antwort ist fehlgeschlagen.", true);
        break;
      case "session.changed":
      case "workflow.changed":
      case "document.changed":
        applySnapshot(payload);
        break;
      case "workflow.snapshot": {
        state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
        if (!state.isWorkflowEditing) {
          const current = state.workflows.find(item => item.id === state.selectedWorkflowEditorId) || selectedWorkflowForDialog();
          showWorkflowPreview(current || null);
        } else {
          renderWorkflows();
        }
        break;
      }
      case "workflow.draft":
        elements.overlay.hidden = false;
        showWorkflowEditor(payload.workflow || null);
        break;
      case "status.changed":
        Object.assign(state, payload);
        renderContext();
        renderStatus();
        break;
      case "theme.changed":
        document.documentElement.dataset.theme = payload.highContrast ? "high-contrast" : payload.theme || "system";
        if (payload.highContrast) {
          document.documentElement.style.removeProperty("--accent");
          document.documentElement.style.removeProperty("--background-accent");
        } else {
          if (payload.accent) {
            document.documentElement.style.setProperty("--accent", payload.accent);
          }
          if (payload.backgroundAccent) {
            document.documentElement.style.setProperty("--background-accent", payload.backgroundAccent);
          }
        }
        break;
      case "host.error":
        showToast(payload.message || "Unbekannter Fehler", true);
        break;
      default:
        break;
    }
  }

  restoreSessionsCollapsed();
  setReasoning("medium");

  elements.toggleSessions.addEventListener("click", () => {
    const collapsed = !elements.appShell.classList.contains("sessions-collapsed");
    setSessionsCollapsed(collapsed, true);
    post("ui.sessionPane", { isOpen: !collapsed });
  });
  elements.newSession.addEventListener("click", () => {
    flushDraft();
    post("session.create", {});
    document.body.classList.remove("sessions-open");
  });
  elements.clearSessions.addEventListener("click", () => {
    if (state.sessions.length > 0 && globalThis.confirm("Alle Sitzungen und ihre Nachrichten endgültig löschen?")) {
      flushDraft();
      post("session.clear", {});
    }
  });
  elements.sessionSearch.addEventListener("input", renderSessions);
  byId("open-sessions").addEventListener("click", () => document.body.classList.add("sessions-open"));
  byId("collapse-sessions").addEventListener("click", () => document.body.classList.remove("sessions-open"));

  elements.prompt.addEventListener("input", () => {
    scheduleDraftSave();
  });
  elements.prompt.addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      submitPrompt();
    }
  });
  elements.send.addEventListener("click", submitPrompt);
  elements.stop.addEventListener("click", () => post("chat.cancel", {}));
  byId("pick-document").addEventListener("click", () => post("document.pick", { sessionId: state.activeSessionId }));
  byId("export-pdf").addEventListener("click", () => post("chat.exportPdf", { sessionId: state.activeSessionId }));

  elements.toolsButton.addEventListener("click", event => {
    event.stopPropagation();
    setToolsMenuOpen(elements.toolsMenu.hidden);
  });
  elements.toolsMenu.addEventListener("click", event => event.stopPropagation());
  for (const option of document.querySelectorAll(".reasoning-option")) {
    option.addEventListener("click", () => {
      setReasoning(option.dataset.reasoning);
      setToolsMenuOpen(false);
    });
  }
  document.addEventListener("click", () => setToolsMenuOpen(false));

  byId("open-workflows").addEventListener("click", openWorkflows);
  byId("close-workflows").addEventListener("click", closeWorkflows);
  elements.overlay.addEventListener("click", event => {
    if (event.target === elements.overlay) closeWorkflows();
  });
  elements.workflowSearch.addEventListener("input", renderWorkflows);
  byId("new-workflow").addEventListener("click", () => showWorkflowEditor(null));
  elements.selectWorkflow.addEventListener("click", () => {
    if (state.selectedWorkflowEditorId) {
      post("workflow.insert", { workflowId: state.selectedWorkflowEditorId });
      closeWorkflows();
    }
  });
  elements.editWorkflow.addEventListener("click", () => {
    const workflow = state.workflows.find(item => item.id === state.selectedWorkflowEditorId);
    if (workflow && !workflow.isBuiltIn) showWorkflowEditor(workflow);
  });
  elements.cancelWorkflowEdit.addEventListener("click", () => showWorkflowPreview(selectedWorkflowForDialog()));
  elements.deleteWorkflow.addEventListener("click", () => {
    const workflow = state.workflows.find(item => item.id === state.selectedWorkflowEditorId);
    if (workflow && !workflow.isBuiltIn && globalThis.confirm("Diesen Workflow endgültig löschen?")) {
      post("workflow.delete", { workflowId: workflow.id, revision: Number(workflow.revision || 0) });
    }
  });
  elements.workflowEditor.addEventListener("submit", event => {
    event.preventDefault();
    let normalizedContent;
    try { normalizedContent = JSON.stringify(JSON.parse(elements.workflowContent.value || "{}")); }
    catch {
      showToast("Der Workflow-Inhalt ist kein gültiges JSON.", true);
      return;
    }
    const title = elements.workflowName.value.trim();
    if (!title) {
      elements.workflowName.focus();
      return;
    }
    const payload = {
      workflowId: elements.workflowId.value || null,
      revision: Number(elements.workflowRevision.value || 0),
      title,
      domain: elements.workflowDomain.value.trim(),
      tags: elements.workflowTags.value.split(",").map(tag => tag.trim()).filter(Boolean),
      description: elements.workflowDescription.value.trim(),
      contextSummary: elements.workflowSummary.value.trim(),
      contentJson: normalizedContent
    };
    state.pendingWorkflowTitle = title;
    state.isWorkflowEditing = false;
    post(payload.workflowId ? "workflow.update" : "workflow.create", payload);
  });

  globalThis.addEventListener("go:host-message", handleHostMessage);
  globalThis.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    if (!elements.toolsMenu.hidden) setToolsMenuOpen(false);
    else if (!elements.overlay.hidden) closeWorkflows();
  });
  globalThis.goCaptureDraft = () => ({ sessionId: state.activeSessionId, draft: elements.prompt.value });
  globalThis.goFlushDraft = flushDraft;
  let preparedPdfMessage = null;
  globalThis.goPrepareMessagePdf = messageId => {
    globalThis.goFinishMessagePdf();
    const normalizedId = String(messageId || "");
    const target = [...elements.messageList.querySelectorAll(".message")]
      .find(message => message.dataset.messageId === normalizedId);
    if (!target) return false;
    preparedPdfMessage = target;
    document.documentElement.classList.add("pdf-exporting-message");
    document.body.classList.add("pdf-exporting-message");
    target.classList.add("pdf-export-target");
    return true;
  };
  globalThis.goFinishMessagePdf = () => {
    preparedPdfMessage?.classList.remove("pdf-export-target");
    preparedPdfMessage = null;
    document.documentElement.classList.remove("pdf-exporting-message");
    document.body.classList.remove("pdf-exporting-message");
  };
  globalThis.addEventListener("pagehide", flushDraft);
  globalThis.addEventListener("beforeunload", flushDraft);
  post("app.ready", {});
})();
