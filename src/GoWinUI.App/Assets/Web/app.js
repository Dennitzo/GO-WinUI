(function () {
  "use strict";

  const state = {
    sessions: [],
    sessionGroups: [],
    messages: [],
    workflows: [],
    conversationRevision: 0,
    conversationRefreshPending: false,
    documents: [],
    attachments: [],
    documentGroupStatus: { total: 0, ready: 0, processing: 0, failed: 0, status: "ready" },
    pendingDocumentImports: [],
    activeSessionId: null,
    chatMode: "general",
    scienceView: "overview",
    workspacePath: null,
    codingWorkspacePath: null,
    actionDescriptors: [],
    activeActionIds: new Set(),
    assistantTitle: "AI Assistent",
    projectMemory: {
      available: false,
      projectKey: null,
      projectId: null,
      projectLabel: "Projektgedächtnis",
      autoCaptureEnabled: true,
      entries: [],
      selectedEntryId: null,
      isEditing: false,
      disabledReason: null
    },
    scientificResearch: { available: true, projects: [], selectedProjectId: null, detail: null, disabledReason: null, revision: 0 },
    scienceCapabilities: {},
    scienceChatVisible: true,
    scienceExporting: false,
    scienceRequestedProjectId: null,
    selectedWorkflowEditorId: null,
    isWorkflowEditing: false,
    pendingWorkflowTitle: null,
    isRunning: false,
    isAiBusy: false,
    activeRunId: null,
    activeRunSessionId: null,
    activeRunMessageId: null,
    runQueue: { active: null, pending: [], queueDepth: 0, isIdle: true },
    pendingChatSend: null,
    pendingPlanImplementation: null,
    contextSource: "estimated",
    contextProfile: null,
    contextMeasurementMessageId: null,
    model: null,
    contextUsed: 0,
    contextLimit: 8192,
    contextWasTruncated: false,
    contextNotice: null,
    runStatus: null,
    runDetail: null,
    speechStatus: { active: false, status: null, detail: null, model: null, directionModel: null, error: null, cacheHit: false },
    speechProgress: {
      sessionId: null,
      sourceMessageId: null,
      sourceKind: null,
      playbackId: null,
      eventSequence: 0,
      sourceUnits: [],
      activeSourceUnitIds: [],
      state: null
    },
    readFromContextTarget: null,
    messageRunStatus: new Map(),
    codingActivity: new Map(),
    codingToolStepsExpanded: false,
    changesSummary: null,
    codingPreviewDialog: null,
    artifactPreviewUrls: new Map(),
    artifactPreviewPending: new Set(),
    selectedExtensionActionId: null,
    persistentExtensionActionId: null,
    selectedToolAction: null,
    persistentToolAction: null,
    deepResearch: false,
    deepResearchProfile: "auto",
    pendingCaptureRequest: null,
    waitingForCapture: false,
    captureStopRequested: false,
    audioCaptureStopRequested: false,
    liveCaption: {
      isActive: false,
      mode: "transcribe",
      status: "Inaktiv",
      transcript: "",
      provider: null,
      error: null
    },
    microphone: {
      isRecording: false,
      isBusy: false,
      isSpeaking: false,
      status: "Inaktiv",
      partialTranscript: "",
      error: null
    },
    voiceTurn: null,
    voiceStarting: false,
    voicePlaybackPending: false,
    voiceCaptureFeedbackAction: null,
    voiceCaptureFeedbackStarted: false,
    voiceLevel: 0,
    voiceFrequency: 0,
    voiceDominantHz: 0,
    screenClip: {
      isRecording: false,
      isBusy: false,
      status: "Inaktiv",
      elapsedSeconds: 0,
      maximumSeconds: 30,
      sourceLabel: null,
      error: null
    },
    audioCapture: {
      isRecording: false,
      isBusy: false,
      status: "Inaktiv",
      elapsedSeconds: 0,
      maximumSeconds: 600,
      sourceLabel: null,
      error: null
    }
  };

  const byId = id => document.getElementById(id);
  const persistentToolActions = new Set(["audiobook", "planMode"]);
  const persistentExtensionActionIds = new Set(["builtin.audiobook/create", "builtin.coding/plan-mode"]);
  const toolVisuals = Object.freeze({
    coding: ["Coding", "M8 6l-6 6 6 6M16 6l6 6-6 6M14 3l-4 18"],
    audioAnalysis: ["Audio analysieren", "M4 12h2m2-5 4 10 3-7 2 4h3"],
    documentCreate: ["Dokument erstellen", "M6 3h8l4 4v14H6zM14 3v5h4M9 12h6M9 16h6"],
    imageAnalysis: ["Bild analysieren", "M4 5h16v14H4zM7 15l3-3 3 3 2-2 2 2"],
    imageGeneration: ["Bild erstellen", "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"],
    audiobook: ["Hörbuch erstellen", "M4 5c3-1 5-1 8 1v14c-3-2-5-2-8-1zM20 5c-3-1-5-1-8 1v14c3-2 5-2 8-1z"],
    translation: ["Übersetzen", "M4 5h10M9 3v2c0 5-2 8-5 10M6 9c2 3 4 5 8 7M15 9l5 12M18 9l-5 12M14 18h7"],
    videoAnalysis: ["Video analysieren", "M3 6h13v12H3zM16 10l5-3v10l-5-3z"],
    textToSpeech: ["Vorlesen", "M5 9v6h4l5 4V5L9 9zM17 9c1 1 1 5 0 6M19 6c3 3 3 9 0 12"],
    webSearch: ["Websuche", "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM16 16l5 5M4 11h14M11 4c3 3 3 11 0 14M11 4c-3 3-3 11 0 14"],
    planMode: ["Planmodus", "M5 4h14v16H5zM8 8h8M8 12h8M8 16h5"],
    "screen.capture": ["Bild aufnehmen", "M4 7h4l2-2h4l2 2h4v12H4zM12 10a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"],
    "screenClip.toggle": ["Video aufnehmen", "M3 6h13v12H3zM16 10l5-3v10l-5-3z"],
    "liveCaption.start": ["Live-Untertitel", "M4 8h2M4 12h4M4 16h2M10 7v10M14 9v6M18 6v12M22 9v6"]
  });

  const actionIcons = Object.freeze({
    attachment: "M12 5v14M5 12h14",
    web: toolVisuals.webSearch[1],
    research: "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM16 16l5 5M8 11h6M11 8v6",
    audio: toolVisuals.audioAnalysis[1],
    image: toolVisuals.imageAnalysis[1],
    video: toolVisuals.videoAnalysis[1],
    document: toolVisuals.documentCreate[1],
    generateImage: toolVisuals.imageGeneration[1],
    audiobook: toolVisuals.audiobook[1],
    pdf: "M6 3h8l4 4v14H6zM14 3v5h4M9 13h6M9 17h4",
    translate: toolVisuals.translation[1],
    speech: toolVisuals.textToSpeech[1],
    captions: toolVisuals["liveCaption.start"][1],
    workflow: "M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01",
    memory: "M7 4h10a3 3 0 0 1 3 3v10a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3zM9 9h6M9 13h6M9 17h4",
    plan: toolVisuals.planMode[1],
    extension: "M8 3v4M16 3v4M5 7h14v6a7 7 0 0 1-14 0zM9 20h6"
  });

  // Read-only aliases keep old snapshots and native capture events readable.
  // They never populate the menu; the current snapshot catalog is its sole source.
  const legacyExtensionActionIds = Object.freeze({
    audioAnalysis: "builtin.media/audio-analysis",
    imageAnalysis: "builtin.media/image-analysis",
    imageGeneration: "builtin.image/generate",
    audiobook: "builtin.audiobook/create",
    documentCreate: "builtin.documents/create",
    textToSpeech: "builtin.speech/read-aloud",
    translation: "builtin.speech/translate",
    videoAnalysis: "builtin.media/video-analysis",
    webSearch: "builtin.web/web-search",
    deepResearch: "builtin.web/deep-research",
    planMode: "builtin.coding/plan-mode"
  });

  function normalizeToolAction(action) {
    const value = action || null;
    return value && Object.prototype.hasOwnProperty.call(toolVisuals, value) ? value : null;
  }

  function normalizeExtensionActionId(actionId) {
    const value = String(actionId || "").trim();
    return value || null;
  }

  function extensionActionIdForToolAction(toolAction) {
    const alias = String(toolAction || "").trim();
    if (!alias) return null;
    const descriptor = (Array.isArray(state.actionDescriptors) ? state.actionDescriptors : [])
      .find(item => [item?.toolAction, item?.toolToggle, item?.captureAction].includes(alias));
    return normalizeExtensionActionId(descriptor?.actionId || legacyExtensionActionIds[alias]);
  }

  function toolActionForExtensionActionId(actionId) {
    const normalized = normalizeExtensionActionId(actionId);
    if (!normalized) return null;
    const descriptor = (Array.isArray(state.actionDescriptors) ? state.actionDescriptors : [])
      .find(item => String(item?.actionId || "").trim() === normalized);
    const descriptorAlias = normalizeToolAction(descriptor?.toolAction);
    if (descriptorAlias) return descriptorAlias;
    const legacyAlias = Object.entries(legacyExtensionActionIds)
      .find(([, candidate]) => candidate === normalized)?.[0];
    return normalizeToolAction(legacyAlias);
  }

  function normalizeScheduledRun(run, fallbackPosition = 0) {
    if (!run || !run.sessionId) return null;
    const position = Number(run.position);
    return {
      ...run,
      sessionId: String(run.sessionId),
      ticketId: run.ticketId ? String(run.ticketId) : null,
      requestId: run.requestId ? String(run.requestId) : null,
      state: String(run.state || (fallbackPosition > 0 ? "queued" : "running")).toLocaleLowerCase(),
      position: Number.isFinite(position) ? position : fallbackPosition
    };
  }

  function normalizeRunQueue(value) {
    const raw = value?.runQueue || value || {};
    const active = normalizeScheduledRun(raw.active, 0);
    const pending = (Array.isArray(raw.pending) ? raw.pending : [])
      .map((run, index) => normalizeScheduledRun(run, index + 1))
      .filter(Boolean);
    const queueDepth = Number(raw.queueDepth);
    return {
      active,
      pending,
      queueDepth: Number.isFinite(queueDepth) && queueDepth >= 0 ? queueDepth : pending.length,
      isIdle: typeof raw.isIdle === "boolean" ? raw.isIdle : !active && pending.length === 0
    };
  }

  function scheduledRunForSession(sessionId) {
    const id = String(sessionId || "");
    if (!id) return null;
    const queue = state.runQueue || {};
    if (String(queue.active?.sessionId || "") === id) return { ...queue.active, queueState: "active" };
    const pending = (queue.pending || []).find(run => String(run.sessionId || "") === id);
    return pending ? { ...pending, queueState: "queued" } : null;
  }

  function applyRunQueue(value) {
    state.runQueue = normalizeRunQueue(value);
    const active = state.runQueue.active;
    state.isAiBusy = !state.runQueue.isIdle || Boolean(state.isRunning);
    if (active) {
      state.activeRunSessionId = active.sessionId;
    } else if (!state.isRunning) {
      state.activeRunSessionId = null;
      state.activeRunId = null;
      state.activeRunMessageId = null;
    }
  }

  function removeScheduledRun(sessionId, requestId = null, includeActive = false) {
    const id = String(sessionId || "");
    const request = String(requestId || "");
    const matches = run => String(run?.sessionId || "") === id
      && (!request || !run?.requestId || String(run.requestId) === request);
    const queue = normalizeRunQueue(state.runQueue);
    if (matches(queue.active) && (includeActive || !state.isRunning)) queue.active = null;
    queue.pending = queue.pending.filter(run => !matches(run));
    queue.pending.forEach((run, index) => { run.position = index + 1; });
    queue.queueDepth = queue.pending.length;
    queue.isIdle = !queue.active && queue.pending.length === 0;
    applyRunQueue(queue);
  }

  function createToolIcon(pathData) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS(svg.namespaceURI, "path");
    path.setAttribute("d", pathData);
    svg.append(path);
    return svg;
  }

  const elements = {
    appShell: byId("app-shell"),
    sessionList: byId("session-list"),
    sessionSearch: byId("session-search"),
    toggleSessions: byId("toggle-sessions"),
    clearSessions: byId("clear-sessions"),
    modeSwitcherButton: byId("mode-switcher-button"),
    modeSwitcherMenu: byId("mode-switcher-menu"),
    modeSwitcherLabel: byId("mode-switcher-label"),
    modeSwitcherInitial: byId("mode-switcher-initial"),
    newSession: byId("new-session"),
    messageList: byId("message-list"),
    messageScroll: byId("message-scroll"),
    codingChanges: byId("coding-changes"),
    prompt: byId("prompt"),
    chatPane: document.querySelector(".chat-pane"),
    composerRegion: document.querySelector(".composer-region"),
    composerSpeechStatus: byId("composer-speech-status"),
    composerSpeechDetail: byId("composer-speech-detail"),
    composerSpeechPause: byId("composer-speech-pause"),
    composerSpeechPauseIcon: byId("composer-speech-pause-icon"),
    composerSpeechStop: byId("composer-speech-stop"),
    send: byId("send"),
    toolsButton: byId("tools-button"),
    toolsMenu: byId("tools-menu"),
    toolsMenuContent: byId("tools-menu-content"),
    context: byId("context-meter"),
    contextLabel: byId("context-label"),
    contextStrip: byId("context-strip"),
    activeTools: byId("active-tool-chips"),
    documents: byId("document-chips"),
    workspaceOverlay: byId("workspace-overlay"),
    workspacePickerForm: byId("workspace-picker-form"),
    workspacePath: byId("workspace-path"),
    workspacePathOptions: byId("workspace-path-options"),
    microphone: byId("microphone"),
    screenClip: document.querySelector('[data-tool-immediate="screenClip.toggle"]'),
    overlay: byId("workflow-overlay"),
    workflowDialogTitle: byId("workflow-dialog-title"),
    workflowDialogSubtitle: byId("workflow-dialog-subtitle"),
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
    saveWorkflow: byId("save-workflow"),
    newWorkflow: byId("new-workflow"),
    memoryOverlay: byId("memory-overlay"),
    memoryProjectLabel: byId("memory-project-label"),
    memoryAutoCapture: byId("memory-auto-capture"),
    memoryDisabledReason: byId("memory-disabled-reason"),
    memoryList: byId("memory-list"),
    memoryEmpty: byId("memory-empty"),
    memoryPreview: byId("memory-preview"),
    memoryPreviewKind: byId("memory-preview-kind"),
    memoryPreviewTitle: byId("memory-preview-title"),
    memoryPreviewContent: byId("memory-preview-content"),
    memoryPreviewSource: byId("memory-preview-source"),
    memoryPreviewUpdated: byId("memory-preview-updated"),
    memoryPreviewConfirmed: byId("memory-preview-confirmed"),
    memoryEditor: byId("memory-editor"),
    memoryEntryId: byId("memory-entry-id"),
    memoryEntryVersion: byId("memory-entry-version"),
    memoryEntryKind: byId("memory-entry-kind"),
    memoryEntryContent: byId("memory-entry-content"),
    pinMemoryEntry: byId("pin-memory-entry"),
    deleteMemoryEntry: byId("delete-memory-entry"),
    confirmMemoryEntry: byId("confirm-memory-entry"),
    editMemoryEntry: byId("edit-memory-entry"),
    cancelMemoryEdit: byId("cancel-memory-edit"),
    saveMemoryEntry: byId("save-memory-entry"),
    newMemoryEntry: byId("new-memory-entry"),
    scienceWorkbench: byId("science-workbench"),
    scienceWorkbenchStatus: byId("science-workbench-status"),
    scienceWorkbenchContent: byId("science-workbench-content"),
    scienceProjectTitle: byId("science-project-title"),
    scienceProfile: byId("science-profile"),
    scienceCheckpoint: byId("science-checkpoint"),
    scienceSandbox: byId("science-sandbox"),
    scienceAlert: byId("science-alert"),
    scienceExport: byId("science-export"),
    scienceChatToggle: byId("science-chat-toggle"),
    conversationPane: byId("conversation-pane")
  };

  const sidebarStorageKey = "assistant.ui.sessions-collapsed";
  const legacySidebarStorageKey = "go.assistant.sessions-collapsed";
  const chatScroll = globalThis.createGoChatScroll({
    scroller: elements.messageScroll, content: elements.messageList, button: byId("scroll-to-latest")
  });
  const sessionScrollStoragePrefix = "assistant.ui.session-scroll.v1:";
  const legacySessionScrollStoragePrefix = "go.assistant.session-scroll.v1:";
  const ungroupedSessionStorageKey = "assistant.ui.ungrouped-sessions-collapsed";
  const legacyUngroupedSessionStorageKey = "go-session-ungrouped-collapsed";
  let draftTimer = 0;
  let pendingDraft = null;
  let promptResizeFrame = 0;

  function readMigratedStorage(canonicalKey, ...legacyKeys) {
    const storage = globalThis.localStorage;
    const canonicalValue = storage.getItem(canonicalKey);
    if (canonicalValue !== null) {
      for (const legacyKey of legacyKeys) storage.removeItem(legacyKey);
      return canonicalValue;
    }
    for (const legacyKey of legacyKeys) {
      const legacyValue = storage.getItem(legacyKey);
      if (legacyValue === null) continue;
      storage.setItem(canonicalKey, legacyValue);
      storage.removeItem(legacyKey);
      return legacyValue;
    }
    return null;
  }

  function resizePrompt() {
    const prompt = elements.prompt;
    const pane = elements.chatPane.getBoundingClientRect();
    if (pane.height <= 0 || prompt.clientWidth <= 0) return;
    const viewport = globalThis.visualViewport;
    const bottom = Math.min(pane.bottom, viewport ? viewport.offsetTop + viewport.height : globalThis.innerHeight);
    const overhead = elements.composerRegion.getBoundingClientRect().height - prompt.getBoundingClientRect().height;
    const minimum = parseFloat(globalThis.getComputedStyle(prompt).minHeight) || 58;
    // Reserve the actual toolbar and attachments; the conversation can
    // yield its space to the draft until the available window height is filled.
    const maximum = Math.max(minimum, Math.floor(bottom - pane.top - overhead - 12));
    const previousScroll = prompt.scrollTop;
    prompt.style.maxHeight = `${maximum}px`;
    prompt.style.overflowY = "hidden";
    prompt.style.height = "0px";
    const contentHeight = prompt.scrollHeight;
    prompt.style.height = `${Math.min(maximum, Math.max(minimum, contentHeight))}px`;
    prompt.style.overflowY = contentHeight > maximum ? "auto" : "hidden";
    prompt.scrollTop = contentHeight > maximum ? previousScroll : 0;
  }

  function schedulePromptResize() {
    if (promptResizeFrame) return;
    promptResizeFrame = requestAnimationFrame(() => {
      promptResizeFrame = 0;
      resizePrompt();
    });
  }

  function setPromptValue(value) {
    elements.prompt.value = value;
    schedulePromptResize();
    renderComposerAction();
  }

  function post(type, payload, requestId) {
    try {
      return globalThis.goBridge.post(type, payload, requestId);
    } catch (error) {
      showToast(error instanceof Error ? error.message : String(error), true);
      return null;
    }
  }

  function resourceUrl(value) {
    return globalThis.goBridge?.resourceUrl?.(value) || String(value || "");
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

  function timeLabel(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "";
    const dateText = new Intl.DateTimeFormat(document.documentElement.lang || "de", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit"
    }).format(date);
    const valueText = new Intl.DateTimeFormat(document.documentElement.lang || "de", {
      hour: "2-digit",
      minute: "2-digit"
    }).format(date);
    return `${dateText} · ${valueText} Uhr`;
  }

  function sessionShortLabel(title) {
    const words = String(title || state.assistantTitle || "AI").trim().split(/\s+/).filter(Boolean);
    return words.slice(0, 2).map(word => word[0]).join("").slice(0, 2) || "AI";
  }

  function normalizeChatMode(value) {
    const mode = String(value || "").trim().toLocaleLowerCase();
    if (["claudescience", "claude-science", "science"].includes(mode)) return "claudescience";
    if (["coding", "code", "codex"].includes(mode)) return "coding";
    if (["general", "chat", "chatgpt"].includes(mode)) return "general";
    return null;
  }

  function sessionMatchesMode(session) {
    const sessionMode = normalizeChatMode(session?.chatMode);
    // Older hosts already return a mode-scoped list without annotating every
    // row. Keep those rows visible during the protocol migration.
    return !sessionMode || sessionMode === state.chatMode;
  }

  function sessionBelongsToGroup(session, group) {
    if (!session || !group) return false;
    if (Object.prototype.hasOwnProperty.call(session, "sessionGroupId")) {
      return String(session.sessionGroupId || "") === String(group.id || "");
    }
    return (Array.isArray(group.sessionIds) ? group.sessionIds : [])
      .some(sessionId => String(sessionId) === String(session.id));
  }

  function setModeMenuOpen(open) {
    elements.modeSwitcherMenu.hidden = !open;
    elements.modeSwitcherButton.setAttribute("aria-expanded", String(open));
  }

  function renderModeSwitcher() {
    const coding = state.chatMode === "coding";
    const label = state.chatMode === "claudescience" ? "Claude Science" : coding ? "Codex" : "ChatGPT";
    elements.modeSwitcherLabel.textContent = label;
    elements.modeSwitcherInitial.textContent = label[0];
    elements.modeSwitcherButton.title = `${label}-Ansicht wechseln`;
    elements.appShell.dataset.chatMode = state.chatMode;
    elements.newSession.title = state.chatMode === "claudescience" ? "Neues Forschungsvorhaben starten" : "Projektordner auswählen";
    elements.newSession.setAttribute("aria-label", state.chatMode === "claudescience" ? "Neues Forschungsvorhaben starten" : "Neues Projekt: Workspace auswählen");
    const projectsHeading = document.querySelector(".sidebar-projects-heading .sidebar-title");
    if (projectsHeading) projectsHeading.textContent = state.chatMode === "claudescience" ? "Forschung" : "Projekte";
    for (const option of elements.modeSwitcherMenu.querySelectorAll("[data-chat-mode]")) {
      const selected = normalizeChatMode(option.dataset.chatMode) === state.chatMode;
      option.setAttribute("aria-checked", String(selected));
      option.classList.toggle("active", selected);
    }
  }

  function switchChatMode(value) {
    const chatMode = normalizeChatMode(value);
    if (!chatMode || chatMode === state.chatMode) { setModeMenuOpen(false); return; }
    flushDraft();
    setModeMenuOpen(false);
    post("mode.switch", { chatMode });
    document.body.classList.remove("sessions-open");
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
    try { setSessionsCollapsed(readMigratedStorage(sidebarStorageKey, legacySidebarStorageKey) === "1", false); }
    catch { setSessionsCollapsed(false, false); }
  }

  function sessionScrollStorageKey(sessionId) {
    return `${sessionScrollStoragePrefix}${String(sessionId || "")}`;
  }

  function legacySessionScrollStorageKey(sessionId) {
    return `${legacySessionScrollStoragePrefix}${String(sessionId || "")}`;
  }

  function readSessionScrollPosition(sessionId) {
    if (!sessionId) return null;
    try {
      const value = JSON.parse(readMigratedStorage(
        sessionScrollStorageKey(sessionId),
        legacySessionScrollStorageKey(sessionId)) || "null");
      if (!value || !Number.isFinite(value.top) || !Number.isFinite(value.left)) return null;
      return value;
    } catch {
      return null;
    }
  }

  function persistSessionScrollPosition(sessionId = state.activeSessionId) {
    if (!sessionId || !elements.messageScroll) return;
    const scroller = elements.messageScroll;
    const maximumTop = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
    const scrollerBounds = scroller.getBoundingClientRect();
    const anchor = [...elements.messageList.querySelectorAll(":scope > .message[data-message-id]")]
      .find(message => message.getBoundingClientRect().bottom > scrollerBounds.top + 1);
    const anchorBounds = anchor?.getBoundingClientRect();
    const value = {
      top: Math.max(0, scroller.scrollTop),
      left: Math.max(0, scroller.scrollLeft),
      progress: maximumTop > 0 ? Math.max(0, Math.min(1, scroller.scrollTop / maximumTop)) : 0,
      atEnd: maximumTop - scroller.scrollTop <= 4,
      anchorMessageId: anchor?.dataset.messageId || null,
      anchorOffset: anchorBounds ? anchorBounds.top - scrollerBounds.top : null
    };
    try { globalThis.localStorage.setItem(sessionScrollStorageKey(sessionId), JSON.stringify(value)); }
    catch { /* WebView storage is optional. */ }
  }

  function restoreSessionScrollPosition(sessionId) {
    const saved = readSessionScrollPosition(sessionId);
    chatScroll.restore(!saved || saved.atEnd);
    requestAnimationFrame(() => {
      if (String(sessionId || "") !== String(state.activeSessionId || "")) return;
      const scroller = elements.messageScroll;
      const maximumTop = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
      if (!saved) {
        scroller.scrollTop = maximumTop;
        scroller.scrollLeft = 0;
        chatScroll.refresh();
        return;
      }
      if (saved.atEnd) {
        scroller.scrollTop = maximumTop;
      } else {
        const anchor = saved.anchorMessageId
          ? [...elements.messageList.querySelectorAll(":scope > .message[data-message-id]")]
            .find(message => message.dataset.messageId === String(saved.anchorMessageId))
          : null;
        if (anchor && Number.isFinite(saved.anchorOffset)) {
          const scrollerBounds = scroller.getBoundingClientRect();
          const currentOffset = anchor.getBoundingClientRect().top - scrollerBounds.top;
          scroller.scrollTop = Math.max(0, Math.min(maximumTop,
            scroller.scrollTop + currentOffset - saved.anchorOffset));
        } else {
          const proportionalTop = Number.isFinite(saved.progress) ? maximumTop * saved.progress : saved.top;
          scroller.scrollTop = Math.max(0, Math.min(maximumTop, proportionalTop));
        }
      }
      scroller.scrollLeft = Math.max(0, saved.left);
      chatScroll.refresh();
    });
  }

  function readUngroupedCollapsed() {
    try { return readMigratedStorage(ungroupedSessionStorageKey, legacyUngroupedSessionStorageKey) === "1"; }
    catch { return false; }
  }

  function persistUngroupedCollapsed(collapsed) {
    try { globalThis.localStorage.setItem(ungroupedSessionStorageKey, collapsed ? "1" : "0"); }
    catch { /* WebView storage is optional. */ }
  }

  function createSessionItem(session) {
    const item = document.createElement("div");
    item.className = `session-item${session.id === state.activeSessionId ? " active" : ""}`;
    item.setAttribute("role", "listitem");

    const scheduledRun = scheduledRunForSession(session.id);
    if (scheduledRun) {
      item.dataset.queueState = scheduledRun.queueState;
      if (scheduledRun.queueState === "queued") item.dataset.queuePosition = String(scheduledRun.position);
    }

    const open = document.createElement("button");
    open.type = "button";
    open.className = "session-item__open";
    const queueLabel = scheduledRun?.queueState === "active"
      ? "Läuft"
      : scheduledRun ? `Warteschlange · Platz ${scheduledRun.position}` : null;
    open.title = [session.title || "Neue Sitzung", queueLabel, "Rechtsklick zum Umbenennen"].filter(Boolean).join("\n");
    const main = document.createElement("span");
    main.className = "session-item__main";
    const title = document.createElement("span");
    title.className = "session-item__title";
    title.textContent = session.title || "Neue Sitzung";
    main.append(title);
    if (queueLabel) {
      const status = document.createElement("span");
      status.className = `message-status${scheduledRun.queueState === "active" ? " streaming" : ""}`;
      status.textContent = queueLabel;
      main.append(status);
    }
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
    remove.setAttribute("aria-label", `${session.title || "Sitzung"} löschen`);
    remove.title = "Sitzung löschen";
    remove.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m7 7 10 10M17 7 7 17"/></svg>';
    remove.addEventListener("click", () => {
      if (globalThis.confirm(`„${session.title || "Neue Sitzung"}“ endgültig löschen?`)) {
        post("session.delete", { sessionId: session.id });
      }
    });

    item.append(open, remove);
    return item;
  }

  function createProjectRow({ id, name, workspacePath, addable = true, collapsed, sessions, onToggle, onNewSession }) {
    const section = document.createElement("section");
    section.className = "session-group";

    const head = document.createElement("button");
    head.type = "button";
    head.className = collapsed ? "session-group__head" : "session-group__head is-open";
    head.setAttribute("aria-expanded", String(!collapsed));
    head.title = [workspacePath, collapsed ? `${name} ausklappen` : `${name} einklappen`].filter(Boolean).join("\n");

    const chevron = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    chevron.setAttribute("viewBox", "0 0 24 24");
    chevron.classList.add("session-group__chevron");
    chevron.setAttribute("aria-hidden", "true");
    const chevronPath = document.createElementNS(chevron.namespaceURI, "path");
    chevronPath.setAttribute("d", "m9 6 6 6-6 6");
    chevron.append(chevronPath);

    const nameLabel = document.createElement("span");
    nameLabel.className = "session-group__name";
    nameLabel.textContent = name;

    const count = document.createElement("span");
    count.className = "session-group__count";
    count.textContent = String(sessions.length);

    const folder = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    folder.setAttribute("viewBox", "0 0 24 24");
    folder.setAttribute("aria-hidden", "true");
    folder.classList.add("session-group__folder");
    const folderPath = document.createElementNS(folder.namespaceURI, "path");
    folderPath.setAttribute("d", "M3 7V5h6l2 2h10v13H3Z");
    folder.append(folderPath);
    head.append(chevron, folder, nameLabel, count);
    head.addEventListener("click", () => onToggle(!collapsed));

    if (addable && onNewSession) {
      const add = document.createElement("button");
      add.type = "button";
      add.className = "session-group__add";
      add.disabled = state.isAiBusy || state.isRunning || Boolean(state.pendingChatSend);
      add.setAttribute("aria-label", `Neue Sitzung im Projekt ${name}`);
      add.title = `Neue Sitzung im Projekt ${name} starten`;
      const compose = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      compose.setAttribute("viewBox", "0 0 24 24");
      compose.setAttribute("aria-hidden", "true");
      compose.classList.add("session-group__compose");
      for (const shape of ["M12 5v14M5 12h14"]) {
        const path = document.createElementNS(compose.namespaceURI, "path");
        path.setAttribute("d", shape);
        compose.append(path);
      }
      add.append(compose);
      add.addEventListener("click", event => {
        event.stopPropagation();
        onNewSession();
      });
      const headRow = document.createElement("div");
      headRow.className = "session-group__headrow";
      headRow.append(head, add);
      section.append(headRow);
    } else {
      section.append(head);
    }

    if (!collapsed) {
      const body = document.createElement("div");
      body.className = "session-group__body";
      for (const session of sessions) body.append(createSessionItem(session));
      section.append(body);
    }
    return section;
  }

  function createWorkspaceProject() {
    if (state.isRunning || state.isAiBusy || state.pendingChatSend) return;
    flushDraft();
    if (state.chatMode === "claudescience") {
      post("session.create", { chatMode: state.chatMode });
      document.body.classList.remove("sessions-open");
      return;
    }
    if (globalThis.goBridge?.isLanBrowser) {
      openWorkspacePicker();
      document.body.classList.remove("sessions-open");
      return;
    }
    post("session.workspaceCreate", { chatMode: state.chatMode });
    document.body.classList.remove("sessions-open");
  }

  function openWorkspacePicker() {
    closeWorkflows();
    closeProjectMemory();
    elements.workspacePathOptions.replaceChildren();
    const paths = [...new Set((state.sessionGroups || [])
      .map(group => String(group.workspacePath || "").trim()).filter(Boolean))];
    for (const path of paths) {
      const option = document.createElement("option");
      option.value = path;
      elements.workspacePathOptions.append(option);
    }
    elements.workspacePath.value = paths[0] || state.workspacePath || "";
    elements.workspaceOverlay.hidden = false;
    requestAnimationFrame(() => elements.workspacePath.focus());
  }

  function closeWorkspacePicker() {
    elements.workspaceOverlay.hidden = true;
  }

  function renderSessions() {
    const bySessionActivity = (a, b) => {
      const left = Date.parse(a.updatedAt || a.createdAt || "");
      const right = Date.parse(b.updatedAt || b.createdAt || "");
      if (Number.isFinite(left) && Number.isFinite(right) && left !== right) return right - left;
      const leftRaw = String(a.updatedAt || a.createdAt || "");
      const rightRaw = String(b.updatedAt || b.createdAt || "");
      return leftRaw < rightRaw ? 1 : leftRaw > rightRaw ? -1 : 0;
    };
    const query = elements.sessionSearch.value.trim().toLocaleLowerCase();
    const matchesQuery = item => !query || String(item.title || "").toLocaleLowerCase().includes(query);
    const visibleSessions = state.sessions.filter(sessionMatchesMode);
    elements.sessionList.replaceChildren();

    const groups = (Array.isArray(state.sessionGroups) ? state.sessionGroups : [])
      .filter(group => visibleSessions.some(session => sessionBelongsToGroup(session, group)))
      .slice().sort((left, right) => {
      const latestActivity = group => {
        const memberDates = visibleSessions
          .filter(session => sessionBelongsToGroup(session, group))
          .map(session => Date.parse(session.updatedAt || session.createdAt || ""))
          .filter(Number.isFinite);
        const created = Date.parse(group.createdAt || "");
        return Math.max(Number.isFinite(created) ? created : 0, ...memberDates, 0);
      };
      return latestActivity(right) - latestActivity(left);
    });
    const assignedIds = new Set();

    for (const group of groups) {
      const members = visibleSessions.filter(session => sessionBelongsToGroup(session, group));
      for (const session of members) assignedIds.add(String(session.id));
      const matchesProject = query && String(group.name || "").toLocaleLowerCase().includes(query);
      const groupSessions = members.filter(session => matchesProject || matchesQuery(session))
        .sort(bySessionActivity);
      const collapsed = query ? false : Boolean(group.isCollapsed);
      if (!groupSessions.length && query && !matchesProject) continue;
      elements.sessionList.append(createProjectRow({
        id: group.id,
        name: group.name || "Projekt",
        workspacePath: group.workspacePath || null,
        addable: Boolean(group.workspacePath),
        collapsed,
        sessions: groupSessions,
        onToggle: nextCollapsed => {
          group.isCollapsed = nextCollapsed;
          renderSessions();
          post("session.groupCollapse", { groupId: group.id, collapsed: nextCollapsed });
        },
        onNewSession: () => {
          if (!group.workspacePath) { showToast("Für dieses Projekt ist kein Projektordner hinterlegt.", true); return; }
          flushDraft();
          // Do not visually carry the previous session's persistent chip while
          // the new General session is being created by the backend.
          state.selectedToolAction = null;
          state.persistentToolAction = null;
          state.selectedExtensionActionId = null;
          state.persistentExtensionActionId = null;
          renderContext();
          post("session.projectCreate", { workspacePath: group.workspacePath, chatMode: state.chatMode });
          document.body.classList.remove("sessions-open");
        }
      }));
    }

    const ungroupedSessions = visibleSessions.filter(session => !assignedIds.has(String(session.id)) && matchesQuery(session))
      .sort(bySessionActivity);
    if (ungroupedSessions.length) {
      const collapsed = query ? false : readUngroupedCollapsed();
      elements.sessionList.append(createProjectRow({
        id: "ungrouped",
        name: "Allgemeine Sitzungen",
        workspacePath: null,
        addable: true,
        collapsed,
        sessions: ungroupedSessions,
        onNewSession: () => {
          flushDraft();
          state.selectedToolAction = null;
          state.persistentToolAction = null;
          state.selectedExtensionActionId = null;
          state.persistentExtensionActionId = null;
          renderContext();
          post("session.create", { chatMode: state.chatMode });
        },
        onToggle: nextCollapsed => {
          persistUngroupedCollapsed(nextCollapsed);
          renderSessions();
        }
      }));
    }
  }

  function renderMessages(scrollToEnd) {
    renderCodingChanges();
    if ((state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction))) || state.messages.some(message => message.toolSteps?.length
      || state.codingActivity.get(String(message.id))?.some(step => step.kind === "tool"))) {
      renderCodingMessages(scrollToEnd);
      return;
    }
    const previousScrollTop = elements.messageScroll.scrollTop;
    const previousScrollLeft = elements.messageScroll.scrollLeft;
    const existing = new Map([...elements.messageList.children]
      .filter(item => item.dataset?.messageId)
      .map(item => [String(item.dataset.messageId), item]));
    let index = 0;
    for (const message of state.messages) {
      const old = existing.get(String(message.id));
      const next = createMessage(message, old);
      const article = old ? globalThis.goCodingTimeline.reconcile(old, next) : next;
      const at = elements.messageList.children[index];
      if (article !== at) elements.messageList.insertBefore(article, at || null);
      index++;
    }
    while (elements.messageList.children.length > index) elements.messageList.lastChild.remove();
    if (!state.messages.length && state.activeSessionId) {
      const empty = document.createElement("div");
      empty.className = "chat-empty-state";
      if (state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction))) {
        const heading = document.createElement("h2");
        heading.textContent = "Woran arbeiten wir?";
        const description = document.createElement("p");
        description.textContent = state.codingWorkspacePath
          ? "Beschreibe eine Änderung, einen Fehler oder eine Frage zum Projekt."
          : "Wähle über Projekte in der Sidebar deinen Projektordner und beschreibe die gewünschte Codeänderung.";
        empty.append(createToolIcon(toolVisuals.coding[1]), heading, description);
      } else {
        empty.textContent = "Wobei kann ich dich unterstützen?";
      }
      elements.messageList.append(empty);
    }
    applySpeechHighlight();
    if (!chatScroll.following) {
      elements.messageScroll.scrollTop = previousScrollTop;
      elements.messageScroll.scrollLeft = previousScrollLeft;
    }
    if (scrollToEnd === "force") chatScroll.jump(false);
    else chatScroll.refresh();
  }

  function renderCodingMessages(scrollToEnd) {
    const scroller = elements.messageScroll;
    const previousTop = scroller.scrollTop;
    const previousLeft = scroller.scrollLeft;
    const follow = scrollToEnd === "force" || chatScroll.following;
    const viewportTop = scroller.getBoundingClientRect().top;
    const visibleAnchor = selector => [...elements.messageList.querySelectorAll(selector)]
      .find(item => item.getBoundingClientRect().bottom > viewportTop + 2);
    const anchor = visibleAnchor(".coding-diff__line, .coding-output, .coding-result, .coding-narration, .coding-reasoning__body > *")
      || visibleAnchor(".coding-step") || visibleAnchor(".message");
    const anchorTop = anchor?.getBoundingClientRect().top;
    const existing = new Map([...elements.messageList.children].map(item => [String(item.dataset.messageId), item]));
    let index = 0;
    for (const message of state.messages) {
      const old = existing.get(String(message.id));
      const next = createMessage(message, old);
      const article = old ? globalThis.goCodingTimeline.reconcile(old, next) : next;
      const at = elements.messageList.children[index];
      if (article !== at) elements.messageList.insertBefore(article, at || null);
      index++;
    }
    while (elements.messageList.children.length > index) elements.messageList.lastChild.remove();
    if (!state.messages.length && state.activeSessionId) {
      const empty = document.createElement("div");
      empty.className = "chat-empty-state";
      const heading = document.createElement("h2");
      heading.textContent = "Woran arbeiten wir?";
      const description = document.createElement("p");
      description.textContent = state.codingWorkspacePath
        ? "Beschreibe eine Änderung, einen Fehler oder eine Frage zum Projekt."
        : "Wähle über Projekte in der Sidebar deinen Projektordner und beschreibe die gewünschte Codeänderung.";
      empty.append(createToolIcon(toolVisuals.coding[1]), heading, description);
      elements.messageList.append(empty);
    }
    applySpeechHighlight();
    scroller.scrollLeft = previousLeft;
    if (!follow) {
      scroller.scrollTop = anchor?.isConnected ? previousTop + anchor.getBoundingClientRect().top - anchorTop : previousTop;
    }
    if (scrollToEnd === "force") chatScroll.jump(false);
    else chatScroll.refresh();
  }

  const speechHighlightName = "go-speech-current";

  function clearSpeechHighlight() {
    if (globalThis.CSS?.highlights) globalThis.CSS.highlights.delete(speechHighlightName);
    for (const node of elements.messageList.querySelectorAll("[data-speech-source-active]")) {
      node.removeAttribute("data-speech-source-active");
      node.removeAttribute("aria-current");
      node.classList.remove("speech-source-active--block");
    }
  }

  function speechBlockCandidates(content, kind) {
    // Reasoning remains readable on screen but is never an app speech source,
    // even if this helper is called with its Markdown body directly.
    for (let ancestor = content; ancestor; ancestor = ancestor.parentElement) {
      if (ancestor.hasAttribute("data-speech-exclude")) return [];
    }
    if (content.classList.contains("coding-timeline")) {
      return [...content.querySelectorAll(".coding-narration")].flatMap(part => speechBlockCandidates(part, kind));
    }
    const selectors = {
      heading: ":scope > h1, :scope > h2, :scope > h3, :scope > h4, :scope > h5, :scope > h6",
      paragraph: ":scope > p",
      listItem: ":scope > ul > li, :scope > ol > li",
      tableRow: ":scope > .table-wrap tr",
      quote: ":scope > blockquote",
      math: ":scope > .math-selectable.display",
      code: ":scope > .code-block"
    };
    const selector = selectors[String(kind || "")];
    return selector ? Array.from(content.querySelectorAll(selector)) : [];
  }

  const readableSpeechMessageStatuses = new Set(["completed", "cancelled", "interrupted", "failed"]);
  const readableSpeechBlockKinds = ["heading", "paragraph", "listItem", "tableRow", "quote", "math", "code"];

  function annotateReadableSpeechBlocks(message, article, content) {
    if (String(message?.role || "").toLowerCase() !== "assistant"
      || !String(message?.content || "").trim()
      || !readableSpeechMessageStatuses.has(String(message?.status || "").toLowerCase())
      || !message?.updatedAt) {
      return;
    }

    article.dataset.messageUpdatedAt = String(message.updatedAt);
    for (const kind of readableSpeechBlockKinds) {
      speechBlockCandidates(content, kind).forEach((block, blockIndex) => {
        block.dataset.speechBlockKind = kind;
        block.dataset.speechBlockIndex = String(blockIndex);
      });
    }
  }

  function captureReadFromContextTarget(event) {
    state.readFromContextTarget = null;
    const origin = event?.target instanceof Element ? event.target : null;
    if (origin?.closest("[data-speech-exclude]")) return;
    const block = origin?.closest("[data-speech-block-kind][data-speech-block-index]");
    const article = block?.closest("article[data-message-id]");
    if (!block || !article || !elements.messageList.contains(article)) return;

    const message = state.messages.find(item => String(item.id) === String(article.dataset.messageId));
    if (!message
      || String(message.role || "").toLowerCase() !== "assistant"
      || !readableSpeechMessageStatuses.has(String(message.status || "").toLowerCase())
      || !String(message.content || "").trim()
      || String(message.sessionId || "") !== String(state.activeSessionId || "")
      || !message.updatedAt) {
      return;
    }

    const blockIndex = Number(block.dataset.speechBlockIndex);
    if (!Number.isSafeInteger(blockIndex) || blockIndex < 0) return;
    state.readFromContextTarget = {
      sessionId: message.sessionId,
      messageId: message.id,
      messageUpdatedAt: message.updatedAt,
      kind: block.dataset.speechBlockKind,
      blockIndex
    };
  }

  function isSpeechTextNode(node) {
    const parent = node.parentElement;
    if (!parent || !node.nodeValue) return false;
    return !parent.closest("button, [aria-hidden='true'], .math-render, .code-header");
  }

  function searchableSpeechText(root) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: node => isSpeechTextNode(node) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT
    });
    const positions = [];
    let text = "";
    let node = walker.nextNode();
    while (node) {
      const value = node.nodeValue || "";
      for (let offset = 0; offset < value.length; offset += 1) {
        const character = /\s/u.test(value[offset]) ? " " : value[offset];
        if (character === " " && (!text.length || text.endsWith(" "))) continue;
        text += character;
        positions.push({ node, offset });
      }
      node = walker.nextNode();
    }
    return { text: text.trimEnd(), positions };
  }

  function normalizedSpeechNeedle(value) {
    return String(value || "")
      .replace(/\u00a0/g, " ")
      .replace(/\s+/gu, " ")
      .trim();
  }

  function findSpeechText(text, needle, start, allowRestart = true) {
    let found = text.indexOf(needle, start);
    if (found >= 0) return found;
    found = text.toLocaleLowerCase("de-DE").indexOf(needle.toLocaleLowerCase("de-DE"), start);
    if (found >= 0) return found;
    if (!allowRestart) return -1;
    found = text.indexOf(needle);
    if (found >= 0) return found;
    return text.toLocaleLowerCase("de-DE").indexOf(needle.toLocaleLowerCase("de-DE"));
  }

  function rangeForSpeechOffsets(searchable, start, end) {
    if (start < 0 || end <= start || end > searchable.positions.length) return null;
    const first = searchable.positions[start];
    const last = searchable.positions[end - 1];
    if (!first || !last) return null;
    const range = document.createRange();
    range.setStart(first.node, first.offset);
    range.setEnd(last.node, last.offset + 1);
    return { range, next: end };
  }

  function rangeForSpeechUnit(searchable, unit, startAt, allowRestart = true) {
    const needle = normalizedSpeechNeedle(unit?.text);
    if (!needle || !searchable.positions.length) return null;
    const index = findSpeechText(searchable.text, needle, startAt, allowRestart);
    if (index < 0 || index + needle.length > searchable.positions.length) return null;
    return rangeForSpeechOffsets(searchable, index, index + needle.length);
  }

  function speechSentenceOffsets(text) {
    const output = [];
    const maximum = 3000;

    const addPart = (rawStart, rawEnd) => {
      let start = rawStart;
      let end = rawEnd;
      while (start < end && /\s/u.test(text[start])) start += 1;
      while (end > start && /\s/u.test(text[end - 1])) end -= 1;
      while (end - start > maximum) {
        const minimum = start + Math.floor(maximum / 2);
        let boundary = Math.min(start + maximum, end - 1);
        while (boundary >= minimum
          && !/[\s,;:]/u.test(text[boundary])) boundary -= 1;
        if (boundary < minimum) boundary = Math.min(start + maximum, end);
        else if (/[,;:]/u.test(text[boundary])) boundary += 1;
        output.push({ start, end: boundary });
        start = boundary;
        while (start < end && /\s/u.test(text[start])) start += 1;
      }
      if (end > start) output.push({ start, end });
    };

    let start = 0;
    for (let index = 0; index < text.length; index += 1) {
      if (![".", "!", "?"].includes(text[index])) continue;
      let end = index + 1;
      while (end < text.length && [".", "!", "?"].includes(text[end])) end += 1;
      while (end < text.length && /["'‘’‚‛“”„‟«»‹›]/u.test(text[end])) end += 1;
      if (end < text.length && !/\s/u.test(text[end])) continue;
      addPart(start, end);
      start = end;
      while (start < text.length && /\s/u.test(text[start])) start += 1;
      index = start - 1;
    }
    if (start < text.length) addPart(start, text.length);
    return output;
  }

  function rangeForSpeechOrdinal(searchable, ordinal) {
    const offsets = speechSentenceOffsets(searchable.text);
    const selected = offsets[Number(ordinal) || 0];
    return selected
      ? rangeForSpeechOffsets(searchable, selected.start, selected.end)
      : null;
  }

  function speechSourceRangeMap(content, sourceUnits) {
    const output = new Map();
    const groups = new Map();
    for (const unit of sourceUnits) {
      if (["tableRow", "math", "code"].includes(String(unit?.kind))) continue;
      const key = `${unit?.kind}:${Number(unit?.blockIndex) || 0}`;
      const group = groups.get(key) || [];
      group.push(unit);
      groups.set(key, group);
    }

    for (const group of groups.values()) {
      group.sort((left, right) => (Number(left?.ordinalInBlock) || 0) - (Number(right?.ordinalInBlock) || 0));
      const firstUnit = group[0];
      const candidates = speechBlockCandidates(content, firstUnit?.kind);
      const block = candidates[Number(firstUnit?.blockIndex) || 0];
      if (!block) continue;
      const searchable = searchableSpeechText(block);
      let cursor = 0;
      for (const unit of group) {
        // Resolve all preceding sentences as well, so repeated wording can
        // never make a later progress event jump back to the first match.
        const exact = rangeForSpeechUnit(searchable, unit, cursor, false);
        const match = exact || rangeForSpeechOrdinal(searchable, unit?.ordinalInBlock);
        if (!match) continue;
        output.set(String(unit.id), { block, range: match.range });
        cursor = Math.max(cursor, match.next);
      }
    }
    return output;
  }

  function activeSpeechArticle(messageId) {
    if (!messageId) return null;
    return Array.from(elements.messageList.querySelectorAll("article[data-message-id]"))
      .find(article => article.dataset.messageId === String(messageId)) || null;
  }

  function applySpeechHighlight() {
    clearSpeechHighlight();
    const progress = state.speechProgress || {};
    if (String(progress.sessionId || "") !== String(state.activeSessionId || "")) return;
    if (!Array.isArray(progress.activeSourceUnitIds) || progress.activeSourceUnitIds.length === 0) return;
    const article = activeSpeechArticle(progress.sourceMessageId);
    const content = article?.querySelector(":scope > .message-body > .message-content");
    if (!content) return;

    // One playback segment maps to one visible sentence. Keeping this as one
    // ID also prevents a delayed multi-part event from lighting two places.
    const activeIds = new Set(progress.activeSourceUnitIds.slice(0, 1).map(String));
    const sourceUnits = Array.isArray(progress.sourceUnits) ? progress.sourceUnits : [];
    const units = sourceUnits
      .filter(unit => activeIds.has(String(unit.id)));
    const ranges = [];
    const sourceRangeMap = speechSourceRangeMap(content, sourceUnits);
    for (const unit of units) {
      const candidates = speechBlockCandidates(content, unit.kind);
      const block = candidates[Number(unit.blockIndex) || 0];
      if (!block) continue;

      if (["tableRow", "math", "code"].includes(String(unit.kind))) {
        block.dataset.speechSourceActive = "true";
        block.setAttribute("aria-current", "true");
        block.classList.add("speech-source-active--block");
        continue;
      }

      const match = sourceRangeMap.get(String(unit.id));
      if (!match) continue;
      match.block.dataset.speechSourceActive = "true";
      match.block.setAttribute("aria-current", "true");
      ranges.push(match.range);
    }

    let customHighlightApplied = false;
    if (ranges.length && globalThis.CSS?.highlights && typeof globalThis.Highlight === "function") {
      try {
        globalThis.CSS.highlights.set(speechHighlightName, new globalThis.Highlight(...ranges));
        customHighlightApplied = true;
      } catch {
        customHighlightApplied = false;
      }
    }
    // Current WebView2 versions support CSS Custom Highlight. If unavailable,
    // leave prose unmarked instead of incorrectly flashing the whole paragraph.
  }

  function updateSpeechProgress(payload) {
    const playbackState = String(payload?.state || "").toLowerCase();
    const incomingPlaybackId = String(payload?.playbackId || "");
    const incomingSequence = Number(payload?.eventSequence) || 0;
    const current = state.speechProgress || {};
    const currentPlaybackId = String(current.playbackId || "");
    const isNewPlayback = playbackState === "buffering"
      && incomingPlaybackId
      && incomingPlaybackId !== currentPlaybackId;

    if (!isNewPlayback && incomingPlaybackId && currentPlaybackId
      && incomingPlaybackId !== currentPlaybackId) return;
    if (!isNewPlayback && incomingSequence > 0
      && incomingSequence <= (Number(current.eventSequence) || 0)) return;

    if (["completed", "cancelled"].includes(playbackState)) {
      state.speechProgress = {
        sessionId: null,
        sourceMessageId: null,
        sourceKind: null,
        playbackId: incomingPlaybackId || currentPlaybackId || null,
        eventSequence: incomingSequence,
        sourceUnits: [],
        activeSourceUnitIds: [],
        state: playbackState
      };
      clearSpeechHighlight();
      return;
    }

    const sourceChanged = String(current.sourceMessageId || "") !== String(payload?.sourceMessageId || "")
      || String(current.sessionId || "") !== String(payload?.sessionId || "")
      || isNewPlayback;
    const incomingUnits = Array.isArray(payload?.sourceUnits) ? payload.sourceUnits : null;
    const shouldAdvanceHighlight = playbackState === "playing" || playbackState === "paused";
    state.speechProgress = {
      sessionId: payload?.sessionId || current.sessionId || null,
      sourceMessageId: payload?.sourceMessageId || current.sourceMessageId || null,
      sourceKind: payload?.sourceKind || current.sourceKind || null,
      playbackId: incomingPlaybackId || current.playbackId || null,
      eventSequence: incomingSequence || current.eventSequence || 0,
      sourceUnits: incomingUnits || (sourceChanged ? [] : current.sourceUnits || []),
      activeSourceUnitIds: shouldAdvanceHighlight
        ? (Array.isArray(payload?.sourceUnitIds) ? payload.sourceUnitIds.slice(0, 1) : [])
        : [],
      state: playbackState || current.state || null
    };
    applySpeechHighlight();
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
    article = [...elements.messageList.querySelectorAll("article")]
      .find(item => item.dataset.messageId === article.dataset.messageId) || article;
    const scrollerBounds = elements.messageScroll.getBoundingClientRect();
    const messageBounds = article.getBoundingClientRect();
    const top = elements.messageScroll.scrollTop + messageBounds.top - scrollerBounds.top - 8;
    elements.messageScroll.scrollTo({ top: Math.max(0, top), behavior: "smooth" });
  }

  function createMessageFooter(message, article) {
    const showWorkflowActions = String(message.role).toLowerCase() === "assistant";
    const footer = document.createElement("div");
    footer.className = `message-footer${showWorkflowActions ? " has-workflow-actions" : ""}`;
    const messageText = message.content || "";
    const canReadAloud = showWorkflowActions
      && ["completed", "cancelled", "interrupted", "failed"]
        .includes(String(message.status || "").toLowerCase())
      && messageText.trim().length > 0;

    footer.append(createMessageIconAction("Nachricht kopieren", messageCopyIcon, button => {
      post("message.copy", { text: messageText });
      flashMessageAction(button, messageCopyIcon, "Nachricht kopieren");
    }));
    footer.append(createMessageIconAction("Nachricht als PDF exportieren", messagePdfIcon, button => {
      post("message.exportPdf", { messageId: String(message.id) });
      flashMessageAction(button, messagePdfIcon, "Nachricht als PDF exportieren");
    }));

    if (canReadAloud) {
      footer.append(createMessageFooterLink("Vorlesen", () => {
        post("microphone.speak", {
          sessionId: state.activeSessionId,
          messageId: String(message.id),
          text: messageText
        });
      }));
    }
    if (showWorkflowActions) {
      footer.append(createMessageFooterLink("Als Workflow speichern", () => {
        post("workflow.createFromMessage", { messageId: message.id });
      }));
      footer.append(createMessageFooterLink("Zum Anfang springen", () => scrollMessageToTop(article)));
    }

    return footer;
  }

  function sanitizeVisibleMessageContent(value) {
    const marker = /GO(?:\\?_)?SESSION(?:\\?_)?TITLE\s*:\s*/ig;
    return String(value || "")
      .replace(/```assistant-plan\s*[\s\S]*?```/gi, "")
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .filter(line => !/^\s*(?:#{1,6}\s*)?(?:(?:\*\*|__|`)+\s*)?GO_SESSION_TITLE\s*:/i.test(
        line.replace(/\u00a0/g, " ").replace(/\\_/g, "_")))
      .map(line => line.replace(marker, ""))
      .join("\n")
      .trim();
  }

  function parsePlanInteraction(value) {
    const match = /```assistant-plan\s*([\s\S]*?)```/i.exec(String(value || ""));
    if (!match) return null;
    try {
      const parsed = JSON.parse(match[1]);
      return parsed && ["questions", "plan"].includes(parsed.kind) ? parsed : null;
    } catch { return null; }
  }

  function createPlanInteraction(value) {
    const plan = parsePlanInteraction(value);
    if (!plan) return null;
    const card = document.createElement("section");
    card.className = "plan-interaction";
    const title = document.createElement("strong");
    title.textContent = plan.kind === "questions" ? "Entscheidungen für den Plan" : String(plan.title || "Plan ist bereit");
    card.append(title);
    if (plan.kind === "questions") {
      const form = document.createElement("form");
      form.className = "plan-questions";
      for (const [index, question] of (Array.isArray(plan.questions) ? plan.questions : []).slice(0, 3).entries()) {
        const field = document.createElement("fieldset");
        const legend = document.createElement("legend");
        legend.textContent = String(question?.text || `Frage ${index + 1}`);
        field.append(legend);
        const name = `plan-${Date.now()}-${index}`;
        for (const option of (Array.isArray(question?.options) ? question.options : []).slice(0, 6)) {
          const label = document.createElement("label");
          const input = document.createElement("input");
          input.type = "radio"; input.name = name; input.value = String(option?.label || option?.id || "Option");
          const copy = document.createElement("span");
          copy.textContent = [option?.label, option?.description].filter(Boolean).join(" · ");
          label.append(input, copy); field.append(label);
        }
        if (question?.allowFreeText !== false) {
          const manual = document.createElement("input");
          manual.type = "text"; manual.className = "plan-manual-answer";
          manual.placeholder = "Eigene Antwort oder Änderung …";
          field.append(manual);
        }
        form.append(field);
      }
      const submit = document.createElement("button");
      submit.type = "submit"; submit.className = "primary"; submit.textContent = "Antworten übernehmen";
      form.append(submit);
      form.addEventListener("submit", event => {
        event.preventDefault();
        const answers = [...form.querySelectorAll("fieldset")].map(field => {
          const question = field.querySelector("legend")?.textContent || "Frage";
          const selected = field.querySelector('input[type="radio"]:checked')?.value;
          const manual = field.querySelector(".plan-manual-answer")?.value?.trim();
          return `${question}: ${manual || selected || "Keine Auswahl"}`;
        });
        setPromptValue("Antworten auf die Planungsfragen:\n" + answers.join("\n"));
        void submitPrompt();
      });
      card.append(form);
    } else {
      const actions = document.createElement("div");
      actions.className = "plan-actions";
      const change = document.createElement("button");
      change.type = "button"; change.className = "secondary"; change.textContent = "Änderungen eingeben";
      change.addEventListener("click", () => { elements.prompt.focus(); setPromptValue("Ändere den Plan bitte wie folgt: "); });
      const implement = document.createElement("button");
      implement.type = "button"; implement.className = "primary"; implement.textContent = "Plan implementieren";
      implement.addEventListener("click", () => {
        if (!ensureEditableContext()) return;
        state.pendingPlanImplementation = "Implementiere jetzt den zuletzt abgestimmten Plan vollständig. Prüfe die Änderungen mit den passenden Builds und Tests.";
        post("action.invoke", { actionId: "builtin.coding/plan-mode", sessionId: state.activeSessionId, selectionBehavior: "toggle", enabled: false });
      });
      actions.append(change, implement); card.append(actions);
    }
    return card;
  }

  function createMessage(message, previousArticle = null) {
    const role = String(message.role).toLowerCase();
    const hasTimeline = role === "assistant" && ((state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction))) || message.toolSteps?.length
      || state.codingActivity.get(String(message.id))?.some(step => step.kind === "tool"));
    // Failed runs without generated text store their error as a content fallback.
    // Display it once; retain the original message for copy/export and persistence.
    const contentMessage = role === "assistant" && message.error
      && String(message.content || "").trim() === String(message.error).trim()
      ? { ...message, content: "" } : message;
    const article = document.createElement("article");
    article.className = `message ${role}`;
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
        const assistantLabel = state.chatMode === "claudescience" ? "Claude Science"
          : state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction)) ? "Codex" : "AI";
      const displayLabel = message.isLiveCaption ? "Live-Untertitel" : assistantLabel;
      const identity = document.createElement("span");
      identity.className = "message-meta__identity";
      identity.textContent = messageTime ? `${displayLabel} - ${messageTime}` : displayLabel;
      meta.append(identity);
      const liveStatus = state.messageRunStatus.get(String(message.id));
      const appendActivity = (label, detail, spinning = false, failed = false) => {
        const activity = document.createElement("span");
        activity.className = "message-meta__activity";
        if (spinning) {
          const spinner = document.createElement("span");
          spinner.className = "message-status-spinner";
          spinner.setAttribute("aria-hidden", "true");
          activity.append(spinner);
        }
        const status = document.createElement("span");
        status.className = `message-status${spinning ? " streaming" : ""}${failed ? " failed" : ""}`;
        status.textContent = label;
        activity.append(status);
        if (detail) {
          const information = document.createElement("span");
          information.className = "message-meta__detail";
          information.textContent = detail;
          activity.append(information);
        }
        meta.append(activity);
      };
      if (message.isLiveCaption) {
        appendActivity(message.liveCaptionStatus || "Sprache wird erkannt", message.liveCaptionProvider, true);
      } else if (message.status && !message.tool && !liveStatus && !["completed", "Completed"].includes(message.status)) {
        const normalizedStatus = String(message.status).toLowerCase();
        appendActivity(statusLabel(message.status), null,
          ["pending", "streaming"].includes(normalizedStatus), normalizedStatus === "failed");
      } else if (liveStatus?.status && !hasTimeline) {
        appendActivity(liveStatus.status, runStatusText(liveStatus), true);
      }
      body.append(meta);
    }

    const timeline = hasTimeline
      ? createCodingActivity(contentMessage, previousArticle?.querySelector(".coding-timeline")) : null;
    if (timeline) {
      body.append(timeline);
      annotateReadableSpeechBlocks(contentMessage, article, timeline);
    } else {
    const content = document.createElement("div");
    content.className = "message-content";
    if (["streaming", "Streaming"].includes(message.status)) content.classList.add("stream-cursor");
    content.append(globalThis.goMarkdown.render(sanitizeVisibleMessageContent(contentMessage.content)));
    if (state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction))) enhanceCodingCodeBlocks(content);
    annotateReadableSpeechBlocks(contentMessage, article, content);
    body.append(content);
    }
    if (message.tool) {
      const toolBox = document.createElement("div");
      toolBox.className = `message-tool-box message-tool-box--${String(message.tool.status || "").toLowerCase()}`;
      toolBox.textContent = [message.tool.tool, message.tool.context, message.tool.detail, message.tool.status]
        .filter(Boolean).join(" · ");
      body.append(toolBox);
    }
    if (role === "assistant") {
      const planInteraction = typeof createPlanInteraction === "function"
        ? createPlanInteraction(contentMessage.content)
        : null;
      if (planInteraction) body.append(planInteraction);
    }
    // Media artifacts bound to a tool step render inside the coding timeline.
    // Only unanchored captures/documents stay at the message end.
    const artifactItems = Array.isArray(message.artifacts) ? message.artifacts : [];
    const anchoredStepIds = new Set(timeline ? mergeCodingToolSteps(contentMessage).map(step => String(step.id)) : []);
    const trailingArtifacts = artifactItems.filter(item => !item?.stepId || !anchoredStepIds.has(String(item.stepId)));
    if (trailingArtifacts.length) body.append(createArtifactList(trailingArtifacts));
    if (role === "assistant" && message.error) {
      const error = document.createElement("div");
      error.className = "message-error";
      error.setAttribute("role", "note");
      const heading = document.createElement("strong");
      heading.textContent = "Lauf beendet";
      const detail = document.createElement("p");
      detail.textContent = String(message.error);
      error.append(heading, detail);
      body.append(error);
    }
    if (!message.isLiveCaption) {
      body.append(createMessageFooter(message, article));
    }

    article.append(body);
    return article;
  }

  function createArtifactList(items) {
    const list = document.createElement("div");
    list.className = "message-artifacts";
    for (const artifact of items.filter(isVisibleArtifact)) {
      const card = document.createElement("section");
      card.className = "artifact-card";
      card.dataset.artifactId = artifact.id;
      const isCapturedMedia = artifact.provider === "screen-capture";
      if (isCapturedMedia) {
        list.classList.add("message-artifacts--captures");
        card.classList.add("artifact-card--capture");
      }
      const mediaType = String(artifact.contentType || "application/octet-stream").toLowerCase();
      if (mediaType.startsWith("image/")) {
        const open = document.createElement("button");
        open.type = "button";
        open.className = "artifact-card__open-image";
        open.title = `${artifact.fileName || "Bild"} im Standardprogramm öffnen`;
        open.setAttribute("aria-label", open.title);
        const image = document.createElement("img");
        image.alt = artifact.fileName || "Erzeugtes Bild";
        image.loading = "lazy";
        image.decoding = "async";
        image.addEventListener("error", () => {
          card.classList.add("artifact-card--failed");
          image.alt = `${artifact.fileName || "Bild"} konnte nicht als Vorschau geladen werden`;
        }, { once: true });
        open.append(image);
        open.addEventListener("click", () => post("artifact.open", { artifactId: artifact.id }));
        card.append(open);
      } else if (mediaType.startsWith("audio/")) {
        const audio = document.createElement("audio");
        audio.controls = true;
        audio.preload = "metadata";
        audio.addEventListener("error", () => card.classList.add("artifact-card--failed"));
        audio.addEventListener("loadedmetadata", () => card.classList.remove("artifact-card--failed"));
        card.append(audio);
      } else if (mediaType.startsWith("video/")) {
        const video = document.createElement("video");
        video.controls = true;
        video.preload = "metadata";
        video.addEventListener("error", () => card.classList.add("artifact-card--failed"));
        video.addEventListener("loadedmetadata", () => card.classList.remove("artifact-card--failed"));
        card.append(video);
      }
      const footer = document.createElement("div");
      footer.className = "artifact-card__footer";
      const info = document.createElement("span");
      info.textContent = `${artifact.fileName || "Artefakt"} · ${formatBytes(artifact.length)}`;
      const open = document.createElement("button");
      open.type = "button";
      open.className = "artifact-card__open";
      open.textContent = "Öffnen";
      open.title = `${artifact.fileName || "Artefakt"} im Standardprogramm öffnen`;
      open.addEventListener("click", () => post("artifact.open", { artifactId: artifact.id }));
      const save = document.createElement("button");
      save.type = "button";
      save.textContent = isCapturedMedia
        ? (mediaType.startsWith("video/")
          ? "Video speichern"
          : mediaType.startsWith("audio/")
            ? "Audio speichern"
            : "Screenshot speichern")
        : "Speichern unter";
      save.addEventListener("click", () => post("artifact.save", { artifactId: artifact.id }));
      footer.append(info, open, save);
      card.append(footer);
      list.append(card);
      if (mediaType.startsWith("image/") || mediaType.startsWith("audio/") || mediaType.startsWith("video/")) {
        const cached = state.artifactPreviewUrls.get(String(artifact.id));
        if (cached) {
          const image = card.querySelector("img");
          const audio = card.querySelector("audio");
          const video = card.querySelector("video");
          if (image && cached.url) image.src = resourceUrl(cached.url);
          if (audio && cached.url) {
            audio.src = resourceUrl(cached.url);
            audio.load();
          }
          if (video && cached.url) {
            video.src = resourceUrl(cached.url);
            if (cached.posterUrl) video.poster = resourceUrl(cached.posterUrl);
            video.load();
          }
        } else if (!state.artifactPreviewPending.has(String(artifact.id))) {
          state.artifactPreviewPending.add(String(artifact.id));
          post("artifact.preview", { artifactId: artifact.id });
        }
      }
    }
    return list;
  }

  function isVisibleArtifact(artifact) {
    return String(artifact?.metadata?.role || "").toLowerCase() !== "vision_input";
  }

  function formatBytes(value) {
    const bytes = Math.max(0, Number(value) || 0);
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
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

  function isTerminalMessageStatus(status) {
    return ["completed", "cancelled", "failed", "interrupted"]
      .includes(String(status || "").toLowerCase());
  }

  function pruneTerminalMessageRunStatuses() {
    for (const message of state.messages) {
      if (isTerminalMessageStatus(message?.status)) {
        state.messageRunStatus.delete(String(message.id));
      }
    }
  }

  function visibleModelLabel(value) {
    const model = String(value || "").trim();
    return /gpt-oss-120b/i.test(model)
      ? model.replace(/\s*·\s*MXFP4\b/ig, "").trim()
      : model;
  }

  function codingToolLabel(value, inputJson) {
    const name = String(value || "");
    return ({
      "assistant.reasoning": "Denkprozess",
      "document.agent": "Historischer Dokumentauftrag",
      "document.create": "Dokument erstellen oder bearbeiten",
      "document.read": "Dokument lesen",
      "image.input": "Bild oder Screenshot laden",
      "media.analyze": "Bild analysieren",
      "media.inspect": "Medien prüfen",
      "workspace.open": "Projektanwendung öffnen",
      "speech.synthesize": "Audio erstellen",
      "coding.list": "Projekt erkunden",
      "coding.read": "Datei lesen",
      "coding.readOutput": "Ausgabe nachlesen",
      "coding.searchRunEvidence": "Laufbelege durchsuchen",
      "coding.updatePlan": "Arbeitsstand aktualisieren",
      "coding.search": "Code durchsuchen",
      "coding.write": "Datei schreiben",
      "coding.edit": "Datei bearbeiten",
      "coding.command": "Befehl ausführen",
      "coding.gitDiff": "Änderungen prüfen",
      "coding.undo": "Änderungen zurücknehmen",
      "coding.patch": "Änderung anwenden",
      "coding.applyPatch": "Änderung anwenden",
      "coding.run": "Befehl ausführen",
      "coding.exec": "Befehl ausführen",
      "coding.searchHistory": "Chatverlauf durchsuchen",
      "coding.searchKnowledge": "Dokumentwissen durchsuchen",
      "coding.renderHtml": "HTML-Vorschau erstellen",
      "web.search": "Im Web suchen",
      "web.fetch": "Quelle lesen",
      "web.deepResearch": "Quellen recherchieren"
    })[name] || name.replace(/^(?:coding|web)\./, "").replace(/([a-z])([A-Z])/g, "$1 $2") || "Werkzeug";
  }

  function codingStepState(value) {
    const status = String(value || "running").toLowerCase();
    return ["completed", "failed", "cancelled", "interrupted", "denied", "running", "pending"].includes(status)
      ? status : "running";
  }

  function normalizeCodingStep(tool) {
    return { ...tool, id: String(tool.id), kind: "tool", tool: String(tool.tool),
      label: codingToolLabel(tool.tool, tool.inputJson), status: codingStepState(tool.status),
      detail: String(tool.detail || ""), previewHtml: codingPreviewHtml(tool) };
  }

  function compareReasoningStepUpdates(previous, next) {
    // The server cursor is authoritative across provider retries and reconnects.
    // A timestamp is the compatibility fallback for older display receipts.
    const cursor = step => {
      try {
        const data = typeof step.outputJson === "string" ? JSON.parse(step.outputJson) : step.outputJson;
        return Number.isSafeInteger(data?.lastEventId) && data.lastEventId >= 0 ? data.lastEventId : null;
      } catch { return null; }
    };
    const priorCursor = cursor(previous), nextCursor = cursor(next);
    if (priorCursor !== null && nextCursor !== null) return Math.sign(nextCursor - priorCursor);
    const priorTime = Date.parse(previous.updatedAt), nextTime = Date.parse(next.updatedAt);
    return Number.isFinite(priorTime) && Number.isFinite(nextTime) ? Math.sign(nextTime - priorTime) : null;
  }

  function recordCodingActivity(payload) {
    if (!payload?.messageId
      || payload.sessionId && String(payload.sessionId) !== String(state.activeSessionId)) return;
    const messageId = String(payload.messageId);
    let steps = state.codingActivity.get(messageId);
    if (!steps) {
      steps = [];
      state.codingActivity.set(messageId, steps);
      if (state.codingActivity.size > 100) state.codingActivity.delete(state.codingActivity.keys().next().value);
    }
    const tool = payload.toolStep;
    if (tool?.id && tool.tool) {
      const existing = steps.find(item => item.id === String(tool.id) && item.kind === "tool");
      const next = normalizeCodingStep(tool);
      if (existing) {
        const reasoningOrder = existing.tool === "assistant.reasoning" && next.tool === "assistant.reasoning"
          ? compareReasoningStepUpdates(existing, next) : null;
        const newer = reasoningOrder !== null ? reasoningOrder > 0
          : !existing.updatedAt || !next.updatedAt || Date.parse(next.updatedAt) >= Date.parse(existing.updatedAt);
        const regressesTerminal = !["running", "pending"].includes(existing.status) && ["running", "pending"].includes(next.status)
          && !(reasoningOrder > 0);
        if (newer && !regressesTerminal) Object.assign(existing, next);
      } else steps.push(next);
    } else if (payload.runStatus) {
      const previous = steps.at(-1);
      const phase = { id: "current-phase", kind: "phase", label: String(payload.runStatus), status: "running",
        detail: cleanStatusMetadata(payload.runDetail) };
      if (previous?.kind === "phase") Object.assign(previous, phase);
      else steps.push(phase);
    }
  }

  function createCodingActivity(message, previousTimeline = null) {
    const allSteps = mergeCodingToolSteps(message).filter(step => step.kind === "tool");
    const steps = allSteps;
    const live = isTerminalMessageStatus(message.status) ? null : state.messageRunStatus.get(String(message.id));
    if (!steps.length && !live?.status) return null;
    const sessionId = state.activeSessionId;
    return globalThis.goCodingTimeline.render(message, steps, {
      codingToolStepsExpanded: state.codingToolStepsExpanded,
      previousTimeline,
      renderMarkdown: text => globalThis.goMarkdown.render(text),
      enhanceCodeBlocks: enhanceCodingCodeBlocks,
      sanitizeText: sanitizeVisibleMessageContent,
      liveStatus: live ? { status: live.status, detail: cleanStatusMetadata(live.detail) } : null,
      onPreview: (messageId, stepId) => { if (state.activeSessionId === sessionId) openCodingPreview(messageId, stepId); },
      createArtifacts: typeof createArtifactList === "function" ? createArtifactList : null
    });
  }

  function mergeCodingToolSteps(message) {
    // Stored order survives reconnects where only the newest live step is known.
    const steps = (Array.isArray(message.toolSteps) ? message.toolSteps : [])
      .filter(tool => tool?.id && tool.tool).map(normalizeCodingStep);
    for (const live of state.codingActivity.get(String(message.id)) || []) {
      if (live.kind !== "tool") continue;
      const existing = steps.find(step => step.id === live.id);
      if (!existing) { steps.push({ ...live }); continue; }
      if (existing.tool === "assistant.reasoning" && live.tool === "assistant.reasoning") {
        const order = compareReasoningStepUpdates(existing, live);
        if (order !== null) {
          // A newer persisted running snapshot must also beat an older cached
          // terminal receipt when checkpoint recovery restarts the same round.
          if (order > 0) Object.assign(existing, live);
          continue;
        }
      }
      const storedTerminal = !["running", "pending"].includes(existing.status);
      const liveTerminal = !["running", "pending"].includes(live.status);
      if (storedTerminal && !liveTerminal) continue;
      if (existing.updatedAt && live.updatedAt && Date.parse(existing.updatedAt) > Date.parse(live.updatedAt)) continue;
      if (!storedTerminal || liveTerminal && (!existing.updatedAt || !live.updatedAt || Date.parse(live.updatedAt) >= Date.parse(existing.updatedAt))) {
        // Legacy terminal snapshots are authoritative when neither event has a clock.
        if (!(storedTerminal && liveTerminal && !existing.updatedAt && !live.updatedAt)) Object.assign(existing, live);
      }
    }
    return steps;
  }
  function codingPreviewHtml(step) {
    return step?.tool === "coding.renderHtml" && step.status === "completed"
      && typeof step.previewHtml === "string" && step.previewHtml.trim() && step.previewHtml.length <= 16000
      ? step.previewHtml : null;
  }

  function closeCodingPreview() {
    const dialog = state.codingPreviewDialog;
    state.codingPreviewDialog = null;
    if (dialog) {
      dialog.close();
      dialog.remove();
    }
  }

  function openCodingPreview(messageId, stepId) {
    closeCodingPreview();
    const dialog = document.createElement("dialog");
    dialog.className = "coding-preview-dialog";
    dialog.setAttribute("aria-label", "HTML-Vorschau");
    const header = document.createElement("div");
    header.className = "coding-preview-dialog__header";
    const title = document.createElement("strong");
    title.textContent = "HTML-Vorschau";
    const close = document.createElement("button");
    close.type = "button";
    close.textContent = "Schließen";
    close.addEventListener("click", closeCodingPreview);
    header.append(title, close);
    const frame = document.createElement("iframe");
    frame.title = "Isolierte HTML-Vorschau ohne Netzwerkzugriff";
    frame.setAttribute("sandbox", "allow-scripts");
    frame.setAttribute("referrerpolicy", "no-referrer");
    // The native resource handler loads the persisted result. Never inject model HTML into the assistant document.
    const previewUrl = `https://assistant-coding-preview.local/coding/${encodeURIComponent(messageId)}/${encodeURIComponent(stepId)}`;
    frame.src = globalThis.goBridge?.resourceUrl?.(previewUrl) || previewUrl;
    dialog.append(header, frame);
    dialog.addEventListener("close", () => {
      if (state.codingPreviewDialog === dialog) state.codingPreviewDialog = null;
      dialog.remove();
    });
    state.codingPreviewDialog = dialog;
    // Kept outside messageList so streamed tokens cannot reload the interactive frame.
    document.body.append(dialog);
    dialog.showModal();
  }

  function enhanceCodingCodeBlocks(content) {
    for (const block of content.querySelectorAll(".code-block")) {
      const header = block.querySelector(".code-header");
      const code = block.querySelector("pre code");
      if (!header || !code || !/^(diff|patch)$/i.test(header.firstChild?.textContent.trim() || "")) continue;
      const source = code.textContent;
      const lines = source.split("\n");
      let added = 0;
      let removed = 0;
      code.replaceChildren();
      lines.forEach((line, index) => {
        const row = document.createElement("span");
        let kind = "context";
        if (/^(?:diff |index |--- |\+\+\+ |@@)/.test(line)) kind = "header";
        else if (line.startsWith("+")) { kind = "added"; added += 1; }
        else if (line.startsWith("-")) { kind = "removed"; removed += 1; }
        row.className = `diff-line diff-line--${kind}`;
        row.textContent = line;
        code.append(row);
        if (index < lines.length - 1) code.append(document.createTextNode("\n"));
      });
      block.classList.add("code-block--diff");
      const counts = document.createElement("span");
      counts.className = "code-diff-counts";
      counts.textContent = `+${added} −${removed}`;
      counts.setAttribute("aria-label", `${added} hinzugefügte und ${removed} entfernte Zeilen`);
      header.insertBefore(counts, header.lastChild);
    }
  }

  function renderCodingWorkspace() {
    const coding = state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction));
    elements.appShell.classList.toggle("coding-mode", coding);
    elements.appShell.classList.toggle("science-mode", state.chatMode === "claudescience");
    elements.prompt.placeholder = state.chatMode === "claudescience"
      ? "Forschungsfrage, Analyse oder Manuskriptauftrag eingeben …"
      : coding ? "Änderung beschreiben oder Frage zum Projekt stellen …" : "Nachricht eingeben …";
    renderScienceWorkbench();
    renderCodingChanges();
  }

  function renderScienceWorkbench() {
    const visible = state.chatMode === "claudescience";
    elements.scienceWorkbench.hidden = !visible;
    elements.conversationPane.classList.toggle("science-conversation", visible);
    elements.appShell.classList.toggle("science-chat-hidden", visible && !state.scienceChatVisible);
    if (!visible) return;
    const science = state.scientificResearch || {};
    const projects = Array.isArray(science.projects) ? science.projects : [];
    const detail = science.detail;
    const project = detail?.project || projects.find(item => item.id === science.selectedProjectId) || projects[0] || null;
    elements.scienceProjectTitle.textContent = project?.interpretedQuestion || project?.originalQuestion || "Neues Forschungsvorhaben";
    elements.scienceWorkbenchStatus.textContent = project
      ? `${project.status || "active"} · aktualisiert ${timeLabel(project.updatedAt)}`
      : science.disabledReason || "Beschreibe rechts eine Forschungsfrage. Deep Research legt daraus ein nachvollziehbares Vorhaben an.";
    elements.scienceProfile.textContent = project?.profile || "Automatisch";
    elements.scienceCheckpoint.textContent = detail?.checkpoint
      ? `${detail.checkpoint.stage || "Checkpoint"} · R${detail.checkpoint.revision || 0}` : "Kein Checkpoint";
    elements.scienceSandbox.textContent = state.scienceCapabilities?.sandbox === false ? "Sandbox nicht bereit" : "Lokale Sandbox";
    elements.scienceExport.disabled = !project || state.scienceExporting || !state.scienceCapabilities?.export;
    elements.scienceExport.textContent = state.scienceExporting ? "Export läuft …" : "Exportieren";
    elements.scienceChatToggle.setAttribute("aria-pressed", String(state.scienceChatVisible));
    elements.scienceChatToggle.setAttribute("aria-label", state.scienceChatVisible ? "Chatpanel ausblenden" : "Chatpanel einblenden");
    const warning = science.disabledReason || (project?.status === "conflictingEvidence" ? "Die Evidenz ist widersprüchlich. Prüfe die markierten Aussagen." : "");
    elements.scienceAlert.hidden = !warning;
    elements.scienceAlert.textContent = warning;
    elements.scienceWorkbenchContent.replaceChildren();
    if (!detail) renderScienceLanding(projects);
    else renderScienceView(detail, project);
    for (const tab of elements.scienceWorkbench.querySelectorAll("[data-science-view]")) {
      const selected = tab.dataset.scienceView === state.scienceView;
      tab.setAttribute("aria-selected", String(selected));
      tab.classList.toggle("active", selected);
    }
  }

  function scienceElement(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function scienceSection(title, description) {
    const section = scienceElement("section", "science-card");
    const header = scienceElement("header", "science-card__header");
    header.append(scienceElement("h2", "", title));
    if (description) header.append(scienceElement("p", "", description));
    section.append(header);
    return section;
  }

  function scienceEmpty(text) {
    return scienceElement("p", "science-empty", text);
  }

  function renderScienceLanding(projects) {
    const landing = scienceElement("div", "science-landing");
    const copy = scienceElement("div", "science-landing__copy");
    copy.append(scienceElement("span", "science-eyebrow", "Lokale wissenschaftliche Arbeitsumgebung"),
      scienceElement("h2", "", "Von der Forschungsfrage bis zum reproduzierbaren Ergebnis"),
      scienceElement("p", "", "DeepSeek recherchiert, plant, berechnet und prüft in einer isolierten Sandbox. Quellen, Code, Umgebungen und Ergebnisse bleiben gemeinsam nachvollziehbar."));
    landing.append(copy);
    if (projects.length) {
      const list = scienceSection("Vorhaben dieser Sitzung", "Wähle ein Vorhaben, um seinen letzten belastbaren Stand zu öffnen.");
      const grid = scienceElement("div", "science-project-grid");
      for (const item of projects) {
        const button = scienceElement("button", "science-project-card");
        button.type = "button";
        button.append(scienceElement("strong", "", item.interpretedQuestion || item.originalQuestion || item.id),
          scienceElement("span", "", `${item.profile || "Automatisch"} · ${item.status || "unresolved"}`),
          scienceElement("small", "", `Aktualisiert ${timeLabel(item.updatedAt)}`));
        button.addEventListener("click", () => openScienceProject(item.id));
        grid.append(button);
      }
      list.append(grid); landing.append(list);
    } else {
      landing.append(scienceEmpty("Noch kein Vorhaben gespeichert. Stelle rechts eine Forschungsfrage oder füge Daten und Literatur hinzu."));
    }
    elements.scienceWorkbenchContent.append(landing);
  }

  function appendScienceRows(section, items, emptyText, render) {
    const list = scienceElement("div", "science-rows");
    for (const item of Array.isArray(items) ? items : []) {
      const value = render(item);
      const row = scienceElement(value.href ? "a" : "article", "science-row");
      if (value.href) {
        row.href = value.href;
        row.target = "_blank";
        row.rel = "noopener noreferrer";
      }
      row.append(scienceElement("strong", "", value.title), scienceElement("span", "", value.meta || ""));
      if (value.detail) row.append(scienceElement("p", "", value.detail));
      list.append(row);
    }
    if (!list.children.length) list.append(scienceEmpty(emptyText));
    section.append(list);
  }

  function renderScienceView(detail, project) {
    const view = state.scienceView || "overview";
    const root = scienceElement("div", `science-view science-view--${view}`);
    if (view === "overview") {
      const metrics = scienceElement("div", "science-metrics");
      for (const [label, value] of [["Planschritte", detail.nodes?.length || 0], ["Quellen", detail.works?.length || 0],
        ["Experimente", detail.experiments?.length || 0], ["Prüfungen", detail.verifications?.length || 0]]) {
        const metric = scienceElement("div", "science-metric"); metric.append(scienceElement("strong", "", value), scienceElement("span", "", label)); metrics.append(metric);
      }
      root.append(metrics);
      const question = scienceSection("Forschungsfrage", "Original und präzisierte Interpretation");
      question.append(scienceElement("p", "science-question", project?.interpretedQuestion || project?.originalQuestion || ""));
      if (project?.interpretedQuestion && project?.originalQuestion !== project.interpretedQuestion)
        question.append(scienceElement("blockquote", "", project.originalQuestion));
      root.append(question);
      const plan = scienceSection("Forschungsplan", "Aktive, belegte und noch offene Schritte");
      appendScienceRows(plan, detail.nodes, "Noch kein Forschungsgraph gespeichert.", item => ({ title: item.title || item.id,
        meta: `${item.nodeType || "Question"} · ${item.status || "planned"} · ${Math.round(Number(item.confidence || 0) * 100)} %` }));
      root.append(plan);
    } else if (view === "research") {
      const works = scienceSection("Literaturmatrix", `${detail.works?.length || 0} aufgelöste Arbeiten`);
      appendScienceRows(works, detail.works, "Noch keine geprüften Arbeiten gespeichert.", item => ({ title: item.title || item.workId,
        meta: `${item.versionKind || "work"} · ${item.screeningStatus || "pending"} · ${item.evidenceLevel || "ohne Evidenzklasse"}`, href: item.canonicalUrl }));
      const evidence = scienceSection("Geprüfte Fundstellen", "Nur geöffnete und verortete Belege gelten als zitierfähig.");
      appendScienceRows(evidence, detail.evidence, "Noch keine zitierfähigen Fundstellen gespeichert.", item => ({ title: item.normalizedStatement || item.id,
        meta: [item.evidenceLevel, item.page && `Seite ${item.page}`, item.section].filter(Boolean).join(" · "), detail: item.exactExcerpt }));
      root.append(works, evidence);
    } else if (view === "data") {
      const inputs = scienceSection("Eingaben und Datensätze", "Anhänge werden gehasht und als unveränderte Eingaben der lokalen Sandbox bereitgestellt.");
      appendScienceRows(inputs, [...(state.documents || []), ...(state.attachments || [])], "Noch keine Daten oder Dokumente angehängt.", item => ({
        title: item.fileName || item.title || item.name || item.id, meta: item.status || item.contentType || "Eingabe", detail: item.error || "" }));
      root.append(inputs);
    } else if (view === "analysis") {
      const experiments = scienceSection("Reproduzierbare Analysen", "Befehl, Umgebung, Ergebnis und fachliche Prüfung bleiben getrennt sichtbar.");
      appendScienceRows(experiments, detail.experiments, "Noch keine ausführbaren Experimente gespeichert.", item => ({
        title: item.commandText || item.id, meta: `${item.verificationStatus || "unresolved"} · ${timeLabel(item.updatedAt)}`, detail: item.resultArtifactsJson || "" }));
      root.append(experiments);
    } else if (view === "figures") {
      const figures = scienceSection("Abbildungen und Ergebnisartefakte", "Versionierte Ergebnisse werden mit erzeugendem Lauf und Prüfstatus verknüpft.");
      const artifacts = state.messages.flatMap(message => Array.isArray(message.artifacts) ? message.artifacts : []);
      appendScienceRows(figures, artifacts, "Noch keine Abbildungen oder Ergebnisartefakte gespeichert.", item => ({
        title: item.fileName || item.name || item.id, meta: item.contentType || "Artefakt", detail: "Mit der erzeugenden Unterhaltung und dem Toolschritt verknüpft." }));
      root.append(figures);
    } else if (view === "manuscript") {
      const manuscript = scienceSection("Manuskript", detail.report ? `${detail.report.reportKind || "Bericht"} · ${detail.report.conclusionStatus || "unresolved"}` : "Markdown- und LaTeX-Vorschau");
      const content = scienceElement("div", "science-manuscript markdown-body");
      content.append(globalThis.goMarkdown.render(detail.report?.contentMarkdown || "Noch kein Manuskript gespeichert. Deep Research erzeugt den Entwurf aus verifizierten Befunden."));
      manuscript.append(content); root.append(manuscript);
    } else if (view === "review") {
      const verification = scienceSection("Prüfmatrix", "Quellen, Methode, Mathematik, Code, Daten und Robustheit werden unabhängig bewertet.");
      appendScienceRows(verification, detail.verifications, "Noch keine Prüfungen gespeichert.", item => ({
        title: item.method || item.targetId || item.id, meta: `${item.dimension || "robustness"} · ${item.status || "planned"}`, detail: `${item.targetType || "result"}: ${item.targetId || ""}` }));
      const claims = scienceSection("Aussagen und Evidenzstatus", "Modellschluss, Berechnung und formale Verifikation bleiben unterscheidbar.");
      appendScienceRows(claims, detail.claims, "Noch keine klassifizierten Aussagen gespeichert.", item => ({
        title: item.statement || item.id, meta: `${item.claimClass || "modelInference"} · ${item.conclusionStatus || "unresolved"} · ${Math.round(Number(item.confidence || 0) * 100)} %` }));
      root.append(verification, claims);
    } else {
      const provenance = scienceSection("Provenienz", "Unveränderliche Verbindung zwischen Plan, Lauf, Evidenz, Artefakt und Bericht.");
      const checkpoint = detail.checkpoint;
      appendScienceRows(provenance, [
        ...(checkpoint ? [{ title: `${checkpoint.stage || "Checkpoint"} · Revision ${checkpoint.revision || 0}`, meta: `Run ${checkpoint.runId || "unbekannt"}`, detail: checkpoint.stateJson }] : []),
        ...(detail.nodes || []).map(item => ({ title: item.title || item.id, meta: `${item.nodeType || "Node"} · ${item.status || "planned"}`, detail: item.checkpointId ? `Checkpoint ${item.checkpointId}` : "" })),
        ...(detail.edges || []).map(item => ({ title: `${item.fromNodeId} → ${item.toNodeId}`, meta: item.edgeType || "dependsOn" }))
      ], "Noch keine Provenienzereignisse gespeichert.", item => item);
      root.append(provenance);
    }
    elements.scienceWorkbenchContent.append(root);
  }

  function openScienceProject(projectId) {
    if (!projectId || state.scienceRequestedProjectId === projectId) return;
    state.scienceRequestedProjectId = projectId;
    post("research.open", { sessionId: state.activeSessionId, projectId, expectedRevision: state.scientificResearch?.revision || 0 });
  }

  function renderCodingChanges() {
    if (!elements.codingChanges || !globalThis.goCodingChanges) return;
    const latest = state.messages.filter(message => message.role === "assistant").at(-1);
    const view = elements.codingChanges._view ||= globalThis.goCodingChanges.create({
      host: elements.codingChanges, onLayout: schedulePromptResize
    });
    view.update(state.changesSummary, { sessionId: state.activeSessionId, messageId: latest?.id,
      workspacePath: state.codingWorkspacePath, isCoding: state.chatMode === "coding" || (!state.chatMode && ["coding"].includes(state.selectedToolAction)), toolSteps: latest?.toolSteps });
    updateContextStripVisibility();
  }

  function applyCodingChanges(summary) {
    if (!summary || String(summary.sessionId || "") !== String(state.activeSessionId || "")) return;
    const latest = state.messages.filter(message => message.role === "assistant").at(-1);
    if (!latest || String(summary.messageId || "") !== String(latest.id)) return;
    const previous = state.changesSummary;
    if (previous && previous.messageId === summary.messageId
      && Number.isFinite(previous.revision) && Number.isFinite(summary.revision)
      && summary.revision <= previous.revision) return;
    state.changesSummary = summary;
    renderCodingChanges();
  }

  function runStatusText(liveStatus) {
    const detail = cleanStatusMetadata(liveStatus?.detail);
    return uniqueStatusParts(detail).join(" · ");
  }

  function cleanStatusMetadata(value) {
    return String(value || "")
      .split("·")
      .map(part => part.trim())
      .filter(part => part && !/^(?:[\d.,]+)\s*kontexttoken(?:s)?$/i.test(part))
      .join(" · ");
  }

  function uniqueStatusParts(...values) {
    const output = [];
    const normalized = [];
    values
      .flatMap(value => cleanStatusMetadata(value).split("·"))
      .map(part => part.trim())
      .filter(Boolean)
      .forEach(part => {
        const key = part.toLocaleLowerCase();
        if (normalized.some(existing => existing === key
          || existing.endsWith(`: ${key}`)
          || key.endsWith(`: ${existing}`))) return;
        normalized.push(key);
        output.push(part);
      });
    return output;
  }

  function hasMediaAnalysisContext(action) {
    if (state.documents.length > 0) return true;
    const prefix = action === "imageAnalysis"
      ? "image/"
      : action === "videoAnalysis"
        ? "video/"
        : action === "audioAnalysis"
          ? "audio/"
          : null;
    return Boolean(prefix && state.attachments.some(item => String(item.contentType || "").toLowerCase().startsWith(prefix)));
  }

  function isAudioCaptureActive() {
    return Boolean(state.audioCapture?.isRecording || state.audioCapture?.isBusy);
  }

  function isVoiceControlActive() {
    return Boolean(
      globalThis.goVoiceCapture?.isActive
      || globalThis.goVoiceCapture?.isStarting
      || state.voiceStarting);
  }

  function mediaCaptureFeedback(action) {
    if (action === "audioAnalysis") {
      return "Systemaudio wird aufgezeichnet. Sage Beenden, um die Aufnahme abzuschließen.";
    }
    if (action === "videoAnalysis") {
      return "Video wird aufgezeichnet. Sage Beenden, um die Aufnahme abzuschließen.";
    }
    return action === "imageAnalysis" ? "Die Bildaufnahme wird geöffnet." : null;
  }

  function isCaptureFinishCommand(text) {
    const command = String(text || "")
      .trim()
      .toLocaleLowerCase("de-DE")
      .replace(/[.!?,;:]+$/g, "")
      .replace(/\s+/g, " ");
    return ["beenden", "aufnahme beenden", "abschließen", "aufnahme abschließen"].includes(command);
  }

  function finishActiveMediaCaptureFromVoice(text) {
    if (!isCaptureFinishCommand(text)) return false;
    if (state.audioCapture?.isRecording) {
      post("audioCapture.stop", {});
      return true;
    }
    if (state.screenClip?.isRecording) {
      post("screenClip.stop", { sessionId: state.activeSessionId });
      return true;
    }
    return false;
  }

  async function beginMediaCapture(action, skipVoiceFeedback = false) {
    if (hasMediaAnalysisContext(action)) return;
    const feedback = mediaCaptureFeedback(action);
    if (!skipVoiceFeedback && feedback && isVoiceControlActive()) {
      state.voiceCaptureFeedbackAction = action;
      state.voiceCaptureFeedbackStarted = false;
      state.voicePlaybackPending = true;
      syncVoiceCaptureSuspension();
      post("microphone.speak", { text: feedback });
      return;
    }
    state.captureStopRequested = false;
    try {
      if (action === "imageAnalysis") {
        post("screen.capture", { sessionId: state.activeSessionId });
      } else if (action === "videoAnalysis") {
        if (!isScreenClipActive()) post("screenClip.start", { sessionId: state.activeSessionId });
      } else if (action === "audioAnalysis") {
        if (!isAudioCaptureActive()) {
          post("audioCapture.start", { sessionId: state.activeSessionId });
        }
      }
    } catch (error) {
      state.waitingForCapture = false;
      if (state.pendingCaptureRequest?.prompt) setPromptValue(state.pendingCaptureRequest.prompt);
      state.pendingCaptureRequest = null;
      if (state.selectedToolAction === action) selectToolAction(null, false);
      showToast(microphoneErrorMessage(error), true);
    }
  }

  function renderContext() {
    renderCodingWorkspace();
    elements.activeTools.replaceChildren();
    elements.documents.replaceChildren();
    renderDeepResearch();
    renderScienceWorkbench();

    if (state.workspacePath) {
      const workspace = String(state.workspacePath);
      const trimmed = workspace.replace(/[\\/]+$/, "");
      const label = trimmed.split(/[\\/]/).filter(Boolean).at(-1) || trimmed || workspace;
      const chip = document.createElement("div");
      chip.className = "active-tool-chip workspace-context-chip";
      chip.title = `Workspace: ${workspace}`;
      chip.setAttribute("aria-label", `Workspace ${label}`);
      const text = document.createElement("span");
      text.textContent = label;
      chip.append(createToolIcon("M3 7V5h6l2 2h10v13H3Z"), text);
      elements.activeTools.append(chip);
    }

    const selectedDescriptor = state.selectedExtensionActionId
      ? availableActionDescriptors().find(item => item.actionId === state.selectedExtensionActionId)
      : null;
    const displaysToolChip = selectedDescriptor
      && !["builtin.coding/run", "builtin.general/run"].includes(selectedDescriptor.actionId);
    if (displaysToolChip || state.selectedToolAction && !["coding", "general"].includes(state.selectedToolAction) && toolVisuals[state.selectedToolAction]) {
      const fallbackVisual = toolVisuals[state.selectedToolAction] || ["Erweiterungsaktion", actionIcons.extension];
      const label = selectedDescriptor?.displayName || fallbackVisual[0];
      const iconPath = actionIcons[selectedDescriptor?.iconKey] || fallbackVisual[1];
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "active-tool-chip";
      chip.title = `${label} abwählen`;
      chip.setAttribute("aria-label", `${label} abwählen`);
      const text = document.createElement("span");
      text.textContent = label;
      const remove = document.createElement("span");
      remove.className = "active-tool-chip__remove";
      remove.textContent = "×";
      chip.append(createToolIcon(iconPath), text, remove);
      chip.addEventListener("click", () => selectedDescriptor
        ? invokeActionDescriptor(selectedDescriptor)
        : selectToolAction(null));
      elements.activeTools.append(chip);
    }

    if (state.liveCaption?.isActive) {
      const [label, iconPath] = toolVisuals["liveCaption.start"];
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "active-tool-chip";
      chip.title = `${label} beenden`;
      chip.setAttribute("aria-label", `${label} beenden`);
      const text = document.createElement("span");
      text.textContent = label;
      const remove = document.createElement("span");
      remove.className = "active-tool-chip__remove";
      remove.textContent = "×";
      chip.append(createToolIcon(iconPath), text, remove);
      chip.addEventListener("click", () => post("liveCaption.stop", {}));
      elements.activeTools.append(chip);
    }

    const browserVoiceActive = Boolean(
      globalThis.goVoiceCapture?.isActive
      || globalThis.goVoiceCapture?.isStarting
      || state.voiceStarting);
    if (browserVoiceActive) {
      const chip = document.createElement("div");
      chip.className = "active-tool-chip voice-context-chip";
      const label = document.createElement("span");
      label.textContent = state.voiceStarting
        ? "Mikrofon wird geöffnet …"
        : "Ich höre zu · „Senden“ zum Absenden";
      chip.append(createToolIcon("M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3zM5 11a7 7 0 0 0 14 0M12 18v3M9 21h6"), label);
      elements.activeTools.append(chip);
    }

    if (isAudioCaptureActive()) {
      const audio = state.audioCapture || {};
      const elapsed = Math.max(0, Number(audio.elapsedSeconds) || 0);
      const maximum = Math.max(1, Number(audio.maximumSeconds) || 600);
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "active-tool-chip screen-clip-chip";
      chip.disabled = Boolean(audio.isBusy);
      chip.title = audio.isBusy ? "Audio wird vorbereitet" : "Audioaufnahme abschließen";
      chip.setAttribute("aria-label", chip.title);
      const label = document.createElement("span");
      label.textContent = audio.isBusy
        ? "Systemaudio wird vorbereitet"
        : `Systemaudio aufnehmen · ${formatClipTime(elapsed)} / ${formatClipTime(maximum)}`;
      const indicator = document.createElement("span");
      indicator.className = "screen-clip-chip__indicator";
      indicator.setAttribute("aria-hidden", "true");
      chip.append(createToolIcon(toolVisuals.audioAnalysis[1]), label, indicator);
      chip.addEventListener("click", () => post("audioCapture.stop", {}));
      elements.activeTools.append(chip);
      if (audio.isRecording && elapsed >= maximum && !state.audioCaptureStopRequested) {
        state.audioCaptureStopRequested = true;
        post("audioCapture.stop", {});
      }
    } else if (!state.audioCapture?.isBusy) {
      state.audioCaptureStopRequested = false;
    }

    if (isScreenClipActive()) {
      const clip = state.screenClip || {};
      const elapsed = Math.max(0, Number(clip.elapsedSeconds) || 0);
      const maximum = Math.max(1, Number(clip.maximumSeconds) || 30);
      const chip = document.createElement("div");
      chip.className = "active-tool-chip screen-clip-chip";
      chip.title = clip.isBusy ? "Video wird vorbereitet" : "Aufnahme übernehmen";
      const label = document.createElement("span");
      label.textContent = clip.isBusy
        ? "Video wird vorbereitet"
        : `Video aufnehmen · ${formatClipTime(elapsed)} / ${formatClipTime(maximum)}`;
      const indicator = document.createElement("span");
      indicator.className = "screen-clip-chip__indicator";
      indicator.setAttribute("aria-hidden", "true");
      chip.append(createToolIcon(toolVisuals["screenClip.toggle"][1]), label, indicator);
      if (!clip.isBusy) {
        const accept = document.createElement("button");
        accept.type = "button";
        accept.className = "screen-clip-chip__action";
        accept.title = "Aufnahme übernehmen";
        accept.setAttribute("aria-label", accept.title);
        accept.textContent = "✓";
        accept.addEventListener("click", () => post("screenClip.stop", { sessionId: state.activeSessionId }));
        const cancel = document.createElement("button");
        cancel.type = "button";
        cancel.className = "screen-clip-chip__action screen-clip-chip__action--cancel";
        cancel.title = "Aufnahme verwerfen";
        cancel.setAttribute("aria-label", cancel.title);
        cancel.textContent = "×";
        cancel.addEventListener("click", () => post("screenClip.cancel", {}));
        chip.append(accept, cancel);
      }
      elements.activeTools.append(chip);
    }

    const createFileChip = file => {
      const chip = document.createElement("div");
      chip.className = `context-chip${file.kind === "attachment" ? " attachment" : ""}`;
      const icon = document.createElement("span");
      icon.textContent = file.kind === "attachment"
        && String(file.item.contentType || "").startsWith("image/") ? "▧" : "◇";
      const name = document.createElement("span");
      name.textContent = file.item.fileName;
      name.title = file.item.fileName;
      const preparation = file.kind === "pending" ? "preparing" : file.kind === "document" ? String(file.item.preparationStatus || "ready") : "ready";
      const status = document.createElement("span");
      status.className = `document-preparation-status ${preparation}`;
      status.setAttribute("aria-hidden", "true");
      status.textContent = preparation === "failed" ? "!" : preparation === "ready" ? "✓" : "";
      if (preparation === "failed") {
        chip.title = file.item.preparationError || `${file.item.fileName} konnte nicht aufbereitet werden`;
      } else if (preparation !== "ready") {
        chip.title = `${file.item.fileName} wird aufbereitet`;
      } else if (file.item.cacheHit) {
        chip.title = `${file.item.fileName} wurde aus dem lokalen Dokumentindex geladen`;
      } else {
        chip.title = `${file.item.fileName} ist vollständig lokal indiziert`;
      }
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "×";
      remove.setAttribute("aria-label", `${file.item.fileName} entfernen`);
      remove.hidden = file.kind === "pending";
      remove.addEventListener("click", () => {
        if (!ensureEditableContext()) return;
        post(file.kind === "document" ? "document.remove" : "attachment.remove",
          file.kind === "document" ? { documentId: file.item.id } : { attachmentId: file.item.id });
      });
      chip.append(icon, name, status, remove);
      return chip;
    };
    const attachedFiles = [
      ...state.documents.map(item => ({ kind: "document", item })),
      ...state.attachments.map(item => ({ kind: "attachment", item })),
      ...state.pendingDocumentImports.map(item => ({ kind: "pending", item: { fileName: item } }))
    ];
    if (attachedFiles.length < 2) {
      for (const file of attachedFiles) elements.documents.append(createFileChip(file));
    } else {
      const anchor = document.createElement("div");
      anchor.className = "attachment-menu-anchor";
      const summary = document.createElement("div");
      summary.className = "active-tool-chip attachment-summary";
      const summaryToggle = document.createElement("button");
      summaryToggle.type = "button";
      summaryToggle.className = "attachment-summary__toggle";
      summaryToggle.setAttribute("aria-haspopup", "menu");
      summaryToggle.setAttribute("aria-expanded", "false");
      const icon = document.createElement("span");
      icon.textContent = "◇";
      const label = document.createElement("span");
      label.textContent = `${attachedFiles.length} Dateien`;
      const group = state.documentGroupStatus || {};
      const pendingCount = state.pendingDocumentImports.length;
      const groupState = pendingCount > 0 ? "processing" : String(group.status || "ready");
      const groupStatus = document.createElement("span");
      groupStatus.className = `document-preparation-status group ${groupState}`;
      groupStatus.setAttribute("aria-hidden", "true");
      groupStatus.textContent = groupState === "failed" ? "!" : groupState === "ready" ? "✓" : "";
      const distribution = `${Number(group.ready) || 0} bereit · ${(Number(group.processing) || 0) + pendingCount} wird verarbeitet · ${Number(group.failed) || 0} fehlgeschlagen`;
      summaryToggle.title = `${distribution}. Haken bedeutet: vollständig lokal indiziert.`;
      summaryToggle.setAttribute("aria-label", `${attachedFiles.length} Dateien. ${distribution}. Haken bedeutet vollständig lokal indiziert.`);
      const removeAll = document.createElement("button");
      removeAll.type = "button";
      removeAll.className = "attachment-summary__remove active-tool-chip__remove";
      removeAll.textContent = "×";
      removeAll.title = "Alle Dateianhänge entfernen";
      removeAll.setAttribute("aria-label", "Alle Dateianhänge entfernen");
      summaryToggle.append(icon, label, groupStatus);
      summary.append(summaryToggle, removeAll);

      const menu = document.createElement("div");
      menu.className = "attachment-menu";
      menu.setAttribute("role", "menu");
      menu.hidden = true;
      const title = document.createElement("div");
      title.className = "attachment-menu__title";
      title.textContent = "Dateianhänge";
      menu.append(title);
      for (const file of attachedFiles) {
        const row = createFileChip(file);
        row.classList.add("attachment-menu__item");
        menu.append(row);
      }
      summaryToggle.addEventListener("click", event => {
        event.stopPropagation();
        setToolsMenuOpen(false);
        const open = menu.hidden;
        closeAttachmentMenu();
        menu.hidden = !open;
        summaryToggle.setAttribute("aria-expanded", String(open));
      });
      removeAll.addEventListener("click", event => {
        event.stopPropagation();
        if (!ensureEditableContext()) return;
        closeAttachmentMenu();
        for (const file of attachedFiles) {
          post(
            file.kind === "document" ? "document.remove" : "attachment.remove",
            file.kind === "document" ? { documentId: file.item.id } : { attachmentId: file.item.id }
          );
        }
      });
      menu.addEventListener("click", event => event.stopPropagation());
      anchor.append(summary, menu);
      elements.documents.append(anchor);
    }
    updateContextStripVisibility();
  }

  function updateContextStripVisibility() {
    elements.contextStrip.hidden = state.documents.length === 0
      && state.attachments.length === 0
      && !state.selectedExtensionActionId
      && (!state.selectedToolAction || state.selectedToolAction === "coding")
      && !state.deepResearch
      && !isAudioCaptureActive()
      && !isScreenClipActive()
      && !globalThis.goVoiceCapture?.isActive
      && !globalThis.goVoiceCapture?.isStarting
      && !state.voiceStarting
      && (elements.codingChanges?.hidden ?? true)
      && !state.speechStatus?.active
      && !state.liveCaption?.isActive;
  }

  function renderComposerAction() {
    // Speech playback is an independent activity. The composer stop button only
    // cancels the current AI run; playback has its own chip
    // controls so sending/aborting a prompt cannot interrupt it.
    const canSteer = globalThis.goRunSteering?.canSteer(state) ?? false;
    const canRetrySteer = Boolean(globalThis.goRunSteering?.pendingRetry(state, elements.prompt.value));
    const preparing = Boolean(state.pendingChatSend);
    const hasText = Boolean(elements.prompt.value.trim());
    const scheduledRun = scheduledRunForSession(state.activeSessionId);
    const isQueued = scheduledRun?.queueState === "queued";
    const isStarting = scheduledRun?.queueState === "active" && !canSteer;
    const canCancelScheduled = Boolean(scheduledRun && !canSteer && !hasText && !preparing);
    const foreignRunActive = Boolean(state.runQueue?.active
      && String(state.runQueue.active.sessionId) !== String(state.activeSessionId || ""));
    const canStop = canSteer && !hasText && !preparing;
    elements.send.hidden = false;
    elements.send.classList.toggle("send-button--stop", canStop || canCancelScheduled);
    elements.send.disabled = preparing || (!hasText && !canStop && !canCancelScheduled)
      || Boolean(hasText && (isQueued || isStarting) && !canRetrySteer);
    elements.send.title = preparing ? "Der Auftrag wird vorbereitet. Deine weitere Eingabe bleibt erhalten."
      : canStop ? "Antwort stoppen"
      : canCancelScheduled ? isQueued ? `Auftrag von Platz ${scheduledRun.position} aus der Warteschlange entfernen` : "Startenden Auftrag abbrechen"
      : isQueued ? `Diese Sitzung wartet auf Platz ${scheduledRun.position}. Ein zweiter Auftrag wird nicht eingereiht.`
      : isStarting ? "Der Auftrag dieser Sitzung wird gestartet."
      : canRetrySteer ? "Umlenkung erneut bestätigen" : canSteer ? "Laufenden Auftrag umlenken" : "Nachricht senden";
    if (!canSteer && !canRetrySteer && !scheduledRun && foreignRunActive) {
      elements.send.title = "Nachricht zur Warteschlange hinzufügen";
    }
    elements.send.setAttribute("aria-label", preparing ? "Wird vorbereitet" : canStop ? "Antwort stoppen"
      : canCancelScheduled ? "Aus Warteschlange entfernen"
      : canSteer || canRetrySteer ? "Umlenken"
      : foreignRunActive ? "Einreihen" : "Senden");
    elements.prompt.placeholder = isQueued ? `Warteschlange · Platz ${scheduledRun.position}`
      : isStarting ? "Auftrag wird gestartet …"
      : foreignRunActive ? "Nachricht eingeben und einreihen …"
      : "Nachricht eingeben …";
  }

  function renderStatus() {
    renderCodingWorkspace();
    renderComposerAction();
    const preparing = Boolean(state.pendingChatSend);
    elements.prompt.disabled = false;
    elements.newSession.disabled = state.isRunning || state.isAiBusy || preparing;
    for (const button of elements.sessionList?.querySelectorAll(".session-group__add") || []) {
      button.disabled = state.isRunning || state.isAiBusy || preparing;
    }
    const hasVisibleSessions = state.sessions.some(session => !session.chatMode
      || String(session.chatMode).toLocaleLowerCase() === String(state.chatMode || "general").toLocaleLowerCase());
    elements.clearSessions.disabled = state.isRunning || state.isAiBusy || preparing || !hasVisibleSessions;
    elements.clearSessions.title = hasVisibleSessions
      ? "Alle Sitzungen in dieser Ansicht löschen"
      : "Keine Sitzungen in dieser Ansicht vorhanden";
    elements.clearSessions.setAttribute("aria-label", elements.clearSessions.title);
    if (typeof updateActionMenuState === "function") updateActionMenuState();

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

  function renderSpeechStatus() {
    const speech = state.speechStatus || {};
    const canPause = Boolean(speech.active && state.microphone?.canPauseSpeech);
    const isPaused = Boolean(canPause && state.microphone?.isSpeechPaused);
    elements.composerSpeechStatus.hidden = !speech.active;
    elements.composerSpeechStatus.classList.toggle("paused", isPaused);
    if (!speech.active) {
      elements.composerSpeechDetail.textContent = "";
      elements.composerSpeechPause.disabled = true;
      elements.composerSpeechStop.disabled = true;
      updateContextStripVisibility();
      return;
    }
    const liveStatus = isPaused
      ? "Pausiert"
      : canPause
        ? "Sprachausgabe wird wiedergegeben"
        : speech.status;
    const speechModel = String(speech.model || "").trim();
    const speechModelLabel = speechModel ? `Sprachausgabe: ${speechModel}` : null;
    elements.composerSpeechDetail.textContent = uniqueStatusParts(
      liveStatus,
      cleanStatusMetadata(speech.detail),
      speechModelLabel).join(" · ");
    const controlLabel = isPaused ? "Fortsetzen" : "Pausieren";
    elements.composerSpeechPause.disabled = !canPause;
    elements.composerSpeechPause.title = controlLabel;
    elements.composerSpeechPause.setAttribute("aria-label", controlLabel);
    elements.composerSpeechPause.setAttribute("aria-pressed", String(isPaused));
    elements.composerSpeechPauseIcon.setAttribute("d", isPaused ? "M8 5l11 7-11 7z" : "M8 5v14M16 5v14");
    elements.composerSpeechStop.disabled = false;
    updateContextStripVisibility();
  }

  function renderLiveCaption() {
    const caption = state.liveCaption || {};
    // Live captions use the same message stream as ordinary assistant output.
    state.messages = state.messages.filter(message => !message.isLiveCaption);
    if (caption.isActive) {
      const startedAt = caption.startedAt || new Date().toISOString();
      state.messages.push({
        id: `live-caption:${startedAt}`,
        sessionId: state.activeSessionId,
        role: "assistant",
        status: "streaming",
        content: (caption.transcript || "Warte auf Windows-Systemaudio …")
          .replace(/\r?\n/g, "\n\n"),
        error: caption.error || null,
        createdAt: startedAt,
        updatedAt: new Date().toISOString(),
        isLiveCaption: true,
        liveCaptionStatus: caption.status,
        liveCaptionProvider: caption.provider
      });
    }
    renderContext();
    renderMessages(Boolean(caption.isActive));
  }

  function renderMicrophone() {
    const browserActive = Boolean(globalThis.goVoiceCapture?.isActive || globalThis.goVoiceCapture?.isStarting);
    const active = Boolean(browserActive || state.voiceStarting);
    elements.microphone.classList.toggle("recording", active);
    elements.microphone.classList.remove("speaking");
    // Only a Chromium microphone capture explicitly started by the user owns
    // the button state. TTS and native transcription status must never make an
    // inactive voice-control button look active.
    elements.microphone.disabled = Boolean(state.voiceStarting);
    const label = active ? "Sprachsteuerung beenden" : "Sprachsteuerung starten";
    elements.microphone.title = label;
    elements.microphone.setAttribute("aria-label", label);
    renderVoiceMeter();
  }

  function renderVoiceMeter() {
    const level = Math.max(0, Math.min(1, Number(state.voiceLevel) || 0));
    const frequency = Math.max(0, Math.min(1, Number(state.voiceFrequency) || 0));
    elements.microphone.style.setProperty("--voice-level", String(level));
    elements.microphone.style.setProperty("--voice-frequency", String(frequency));
    const bars = elements.microphone.querySelectorAll(".microphone-frequency i");
    bars.forEach((bar, index) => {
      const wave = 0.35 + (Math.sin((index + 1) * 1.7 + frequency * 8) + 1) * 0.28;
      bar.style.height = `${3 + Math.round(level * (9 + frequency * 8) * wave)}px`;
    });
  }

  function syncVoiceCaptureSuspension() {
    if (!globalThis.goVoiceCapture?.isActive) return;
    // Speech playback and dictation have independent lifetimes. Chromium keeps
    // its microphone stream active and applies its normal browser audio
    // processing while Supertonic synthesizes or plays audio.
    globalThis.goVoiceCapture.setSuspended(!state.microphone?.isRecording);
  }

  async function stopVoiceControl(notifyHost = true) {
    state.voiceStarting = false;
    state.voicePlaybackPending = false;
    // Keep already recognized dictation in the editor when the user manually
    // turns the microphone off. Only explicit control commands are discarded.
    state.voiceTurn = null;
    scheduleDraftSave();
    state.voiceLevel = 0;
    state.voiceFrequency = 0;
    state.voiceDominantHz = 0;
    globalThis.goVoiceCapture?.setSuspended(true);
    // stop() marks capture inactive and closes all MediaStream tracks before
    // its asynchronous AudioContext shutdown. Reflect that off-state and ask
    // the native transcription session to stop immediately instead of waiting
    // for the context shutdown first.
    const captureStop = globalThis.goVoiceCapture?.stop(false);
    renderMessages(false);
    renderMicrophone();
    if (notifyHost) post("microphone.stop", {});
    await captureStop;
    renderMicrophone();
  }

  function microphoneErrorMessage(error) {
    const name = String(error?.name || "");
    if (name === "NotAllowedError" || name === "SecurityError") {
      return `Der Mikrofonzugriff wurde nicht erlaubt. Erlaube ${state.assistantTitle || "dem AI Assistenten"} den Zugriff im WebView2-Dialog und versuche es erneut.`;
    }
    if (name === "NotFoundError" || name === "DevicesNotFoundError") {
      return "Windows meldet kein verfügbares Mikrofon.";
    }
    if (name === "NotReadableError" || name === "TrackStartError") {
      return "Das Mikrofon wird bereits exklusiv von einer anderen Anwendung verwendet.";
    }
    return error instanceof Error ? error.message : String(error || "Das Mikrofon konnte nicht gestartet werden.");
  }

  function renderScreenClip() {
    const clip = state.screenClip || {};
    const seconds = Math.max(0, Number(clip.elapsedSeconds) || 0);
    const label = clip.isRecording
      ? `Bildschirmclip übernehmen · ${seconds} s`
      : "Bildschirmclip aufnehmen";
    if (elements.screenClip) {
      elements.screenClip.classList.toggle("recording", Boolean(clip.isRecording));
      elements.screenClip.disabled = Boolean(clip.isBusy);
      elements.screenClip.title = clip.status && clip.status !== "Inaktiv"
        ? `${label} · ${clip.status}`
        : label;
      elements.screenClip.setAttribute("aria-label", label);
      const menuLabel = elements.screenClip.querySelector("span");
      if (menuLabel) {
        menuLabel.textContent = clip.isBusy
          ? "Video wird vorbereitet"
          : clip.isRecording
            ? `Video aufnehmen · ${formatClipTime(seconds)}`
            : "Video aufnehmen";
      }
    }
    const maximum = Math.max(1, Number(clip.maximumSeconds) || 30);
    if (clip.isRecording && seconds >= maximum && !state.captureStopRequested) {
      state.captureStopRequested = true;
      post("screenClip.stop", { sessionId: state.activeSessionId });
    } else if (!clip.isRecording && !clip.isBusy) {
      state.captureStopRequested = false;
    }
    renderContext();
  }

  function isScreenClipActive() {
    return Boolean(state.screenClip?.isRecording || state.screenClip?.isBusy);
  }

  function formatClipTime(totalSeconds) {
    const value = Math.max(0, Math.floor(Number(totalSeconds) || 0));
    return `${String(Math.floor(value / 60)).padStart(2, "0")}:${String(value % 60).padStart(2, "0")}`;
  }

  function setToolsMenuOpen(open) {
    elements.toolsMenu.hidden = !open;
    elements.toolsButton.setAttribute("aria-expanded", String(open));
    if (open) {
      updateActionMenuState();
      elements.toolsMenu.querySelector(".service-option:not(:disabled)")?.focus();
    }
  }

  function isSelectableAction(descriptor) {
    const kind = String(descriptor?.actionKind ?? "").replace(/[_\s-]/g, "").toLocaleLowerCase();
    const behavior = String(descriptor?.selectionBehavior ?? "").toLocaleLowerCase();
    return Boolean(descriptor?.toolAction || descriptor?.toolToggle || behavior === "toggle"
      || kind === "selectabletool" || kind === "1");
  }

  function availableActionDescriptors() {
    const descriptors = new Map();
    for (const raw of Array.isArray(state.actionDescriptors) ? state.actionDescriptors : []) {
      const actionId = String(raw?.actionId || "").trim();
      const displayName = String(raw?.displayName || "").trim();
      if (!actionId || !displayName) continue;
      descriptors.set(actionId, {
        actionId,
        displayName,
        description: String(raw.description || ""),
        iconKey: String(raw.iconKey || "extension"),
        groupId: String(raw.groupId || "extensions"),
        groupLabel: String(raw.groupLabel || "Erweiterungen"),
        groupOrder: Number.isFinite(Number(raw.groupOrder)) ? Number(raw.groupOrder) : 100,
        itemOrder: Number.isFinite(Number(raw.itemOrder)) ? Number(raw.itemOrder) : 100,
        actionKind: String(raw.actionKind || "immediate"),
        selectionBehavior: String(raw.selectionBehavior || "none").toLocaleLowerCase(),
        toolAction: raw.toolAction ? String(raw.toolAction) : null,
        toolToggle: raw.toolToggle ? String(raw.toolToggle) : null,
        captureAction: raw.captureAction ? String(raw.captureAction) : null,
        supportedChatModes: Array.isArray(raw.supportedChatModes)
          ? raw.supportedChatModes.map(normalizeChatMode).filter(Boolean)
          : null,
        disabledReason: raw.disabledReason ? String(raw.disabledReason) : null
      });
    }
    return [...descriptors.values()]
      .filter(item => item.actionId !== "builtin.coding" && item.toolAction !== "coding")
      .filter(item => !item.supportedChatModes?.length || item.supportedChatModes.includes(state.chatMode))
      .sort((left, right) => Number(left.groupOrder) - Number(right.groupOrder)
        || Number(left.itemOrder) - Number(right.itemOrder)
        || left.displayName.localeCompare(right.displayName, "de"));
  }

  function isActionDescriptorActive(descriptor) {
    if (!descriptor) return false;
    const deepResearchActionId = extensionActionIdForToolAction("deepResearch");
    return descriptor.actionId === deepResearchActionId
      ? Boolean(state.deepResearch)
      : descriptor.actionId === state.selectedExtensionActionId
        || state.activeActionIds.has(descriptor.actionId)
        || Boolean(descriptor.toolAction && descriptor.toolAction === state.selectedToolAction);
  }

  function resolvedActionDisabledReason(descriptor) {
    if (!descriptor) return null;
    if (descriptor.actionId !== "builtin.documents/export-chat-pdf") {
      return descriptor.disabledReason || null;
    }
    if (!state.activeSessionId || state.messages.length === 0) {
      return "Der Chat enthält noch keine Nachrichten.";
    }
    return descriptor.disabledReason === "Der Chat enthält noch keine Nachrichten."
      ? null
      : descriptor.disabledReason || null;
  }

  function updateActionMenuState() {
    for (const button of elements.toolsMenuContent.querySelectorAll(".service-option")) {
      const descriptor = button._actionDescriptor;
      if (!descriptor) continue;
      const disabledReason = resolvedActionDisabledReason(descriptor);
      button.disabled = Boolean(disabledReason);
      button.title = disabledReason || descriptor.description || descriptor.displayName;
      const active = isActionDescriptorActive(descriptor);
      button.classList.toggle("active", active);
      if (isSelectableAction(descriptor)) {
        button.setAttribute("aria-checked", String(active));
      }
      const status = button.querySelector(".action-menu__status");
      if (status) status.hidden = !active;
    }
  }

  function invokeActionDescriptor(descriptor) {
    if (!descriptor || resolvedActionDisabledReason(descriptor)) return;
    const isExport = descriptor.actionId === "builtin.documents/export-chat-pdf";
    const opensLibrary = ["builtin.workflows/open-library", "builtin.memory/open-library"].includes(descriptor.actionId);
    if (isExport && (!state.activeSessionId || state.messages.length === 0)) return;
    if (!isExport && !opensLibrary && !ensureEditableContext()) return;

    if (descriptor.actionId === "builtin.workflows/open-library") {
      openWorkflows();
      post("workflow.list", {});
      setToolsMenuOpen(false);
      return;
    }
    if (descriptor.actionId === "builtin.memory/open-library") {
      openProjectMemory();
      setToolsMenuOpen(false);
      return;
    }

    const payload = { actionId: descriptor.actionId, sessionId: state.activeSessionId };
    if (isSelectableAction(descriptor)) {
      payload.selectionBehavior = "toggle";
      payload.enabled = !isActionDescriptorActive(descriptor);
    }
    post("action.invoke", payload);
    setToolsMenuOpen(false);
    elements.prompt.focus();
  }

  function renderActionMenu() {
    elements.toolsMenuContent.replaceChildren();
    const groups = new Map();
    for (const descriptor of availableActionDescriptors()) {
      const key = descriptor.groupId || "extensions";
      if (!groups.has(key)) groups.set(key, {
        id: key,
        label: descriptor.groupLabel || "Erweiterungen",
        order: Number(descriptor.groupOrder) || 100,
        items: []
      });
      groups.get(key).items.push(descriptor);
    }

    for (const group of [...groups.values()].sort((left, right) => left.order - right.order)) {
      const section = document.createElement("section");
      section.className = "action-menu__group";
      section.dataset.groupId = group.id;
      if (group.id !== "add") {
        const heading = document.createElement("div");
        heading.className = "action-menu__group-title";
        heading.textContent = group.label;
        section.append(heading);
      }
      for (const descriptor of group.items) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "service-option action-menu__item";
        const selectable = isSelectableAction(descriptor);
        button.setAttribute("role", selectable ? "menuitemcheckbox" : "menuitem");
        if (selectable) {
          if (descriptor.toolToggle) button.dataset.toolToggle = descriptor.toolToggle;
          button.setAttribute("aria-checked", "false");
        }
        if (descriptor.toolAction) button.dataset.toolAction = descriptor.toolAction;
        if (descriptor.toolImmediate) button.dataset.toolImmediate = descriptor.toolImmediate;
        button.dataset.actionId = descriptor.actionId;
        button._actionDescriptor = descriptor;
        button.append(createToolIcon(actionIcons[descriptor.iconKey] || actionIcons.extension));
        const copy = document.createElement("span");
        copy.className = "action-menu__copy";
        const title = document.createElement("strong");
        title.textContent = descriptor.displayName;
        const description = document.createElement("span");
        description.textContent = descriptor.description;
        copy.append(title, description);
        const status = document.createElement("span");
        status.className = "action-menu__status";
        status.textContent = "✓";
        status.hidden = true;
        button.append(copy, status);
        button.addEventListener("click", () => invokeActionDescriptor(descriptor));
        section.append(button);
      }
      elements.toolsMenuContent.append(section);
    }
    if (typeof updateActionMenuState === "function") updateActionMenuState();
  }

  function closeAttachmentMenu() {
    const menu = elements.documents.querySelector(".attachment-menu");
    const summary = elements.documents.querySelector(".attachment-summary__toggle");
    if (menu) menu.hidden = true;
    if (summary) summary.setAttribute("aria-expanded", "false");
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
    elements.workflowContent.value = readableWorkflowContent(workflow?.contentJson || '{"schema":"assistant.workflow.v1","blocks":[]}');
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
    closeWorkspacePicker();
    closeProjectMemory();
    elements.overlay.hidden = false;
    showWorkflowPreview(selectedWorkflowForDialog());
    requestAnimationFrame(() => elements.workflowSearch.focus());
  }

  function closeWorkflows() {
    elements.overlay.hidden = true;
    state.isWorkflowEditing = false;
  }

  const memoryKindLabels = Object.freeze({
    requirement: "Anforderung",
    decision: "Entscheidung",
    milestone: "Meilenstein",
    errorResolution: "Fehlerlösung",
    verification: "Verifikation"
  });

  function normalizeMemoryKind(value) {
    const compact = String(value ?? "").replace(/[_\s-]/g, "").toLocaleLowerCase();
    if (compact === "decision" || compact === "1") return "decision";
    if (compact === "milestone" || compact === "2") return "milestone";
    if (compact === "errorresolution" || compact === "3") return "errorResolution";
    if (compact === "verification" || compact === "4") return "verification";
    return "requirement";
  }

  function selectedMemoryEntry() {
    return state.projectMemory.entries.find(item => item.id === state.projectMemory.selectedEntryId) || null;
  }

  function memoryEntryHeading(entry) {
    const firstLine = String(entry?.content || "").split(/\r?\n/, 1)[0].trim();
    return firstLine || "Leerer Gedächtniseintrag";
  }

  function renderProjectMemoryList() {
    elements.memoryList.replaceChildren();
    for (const entry of state.projectMemory.entries) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `memory-item${entry.id === state.projectMemory.selectedEntryId ? " active" : ""}`;
      button.setAttribute("role", "listitem");
      const copy = document.createElement("span");
      copy.className = "memory-item__copy";
      const title = document.createElement("strong");
      title.textContent = memoryEntryHeading(entry);
      const detail = document.createElement("span");
      const kind = normalizeMemoryKind(entry.kind);
      detail.textContent = `${memoryKindLabels[kind]} · ${entry.source || "Unbekannte Quelle"}`;
      copy.append(title, detail);
      button.append(copy);
      if (entry.isPinned) {
        const pin = document.createElement("span");
        pin.className = "memory-item__pin";
        pin.textContent = "★";
        pin.setAttribute("aria-label", "Angepinnt");
        button.append(pin);
      }
      button.addEventListener("click", () => showMemoryPreview(entry));
      elements.memoryList.append(button);
    }
    if (!state.projectMemory.entries.length) {
      const empty = document.createElement("p");
      empty.className = "workflow-list-empty";
      empty.textContent = state.projectMemory.available
        ? "Noch keine Erkenntnisse für dieses Projekt."
        : "Kein Projektgedächtnis verfügbar.";
      elements.memoryList.append(empty);
    }
  }

  function setMemoryFooterMode(editing, entry) {
    const persisted = Boolean(entry?.id);
    elements.deleteMemoryEntry.disabled = !persisted;
    elements.confirmMemoryEntry.disabled = !persisted;
    elements.editMemoryEntry.disabled = !persisted;
    elements.deleteMemoryEntry.hidden = editing || !persisted;
    elements.confirmMemoryEntry.hidden = editing || !persisted;
    elements.editMemoryEntry.hidden = editing || !persisted;
    elements.cancelMemoryEdit.hidden = !editing;
    elements.saveMemoryEntry.hidden = !editing;
  }

  function showMemoryPreview(entry) {
    state.projectMemory.isEditing = false;
    state.projectMemory.selectedEntryId = entry?.id || null;
    elements.memoryEditor.hidden = true;
    elements.memoryEmpty.hidden = Boolean(entry);
    elements.memoryPreview.hidden = !entry;
    if (entry) {
      const kind = normalizeMemoryKind(entry.kind);
      elements.memoryPreviewKind.textContent = memoryKindLabels[kind];
      elements.memoryPreviewTitle.textContent = memoryEntryHeading(entry);
      elements.memoryPreviewContent.textContent = entry.content || "";
      elements.memoryPreviewSource.textContent = entry.source || "Unbekannt";
      elements.memoryPreviewUpdated.textContent = timeLabel(entry.updatedAt);
      elements.memoryPreviewConfirmed.textContent = entry.lastConfirmedAt
        ? timeLabel(entry.lastConfirmedAt)
        : "Noch nicht bestätigt";
      elements.pinMemoryEntry.textContent = entry.isPinned ? "★" : "☆";
      elements.pinMemoryEntry.setAttribute("aria-pressed", String(Boolean(entry.isPinned)));
      elements.pinMemoryEntry.setAttribute("aria-label", entry.isPinned
        ? "Gedächtniseintrag lösen"
        : "Gedächtniseintrag anpinnen");
    }
    setMemoryFooterMode(false, entry);
    renderProjectMemoryList();
  }

  function showMemoryEditor(entry) {
    if (!state.projectMemory.available) return;
    state.projectMemory.isEditing = true;
    state.projectMemory.selectedEntryId = entry?.id || null;
    elements.memoryEntryId.value = entry?.id || "";
    elements.memoryEntryVersion.value = String(entry?.version || 0);
    elements.memoryEntryKind.value = normalizeMemoryKind(entry?.kind);
    elements.memoryEntryContent.value = entry?.content || "";
    elements.memoryEmpty.hidden = true;
    elements.memoryPreview.hidden = true;
    elements.memoryEditor.hidden = false;
    setMemoryFooterMode(true, entry);
    renderProjectMemoryList();
    requestAnimationFrame(() => elements.memoryEntryContent.focus());
  }

  function applyProjectMemorySnapshot(payload) {
    const previousSelection = state.projectMemory.selectedEntryId;
    state.projectMemory = {
      ...state.projectMemory,
      available: payload?.available !== false && Boolean(payload?.projectKey),
      projectKey: payload?.projectKey || null,
      projectId: payload?.projectId || null,
      projectLabel: payload?.projectLabel || "Projektgedächtnis",
      autoCaptureEnabled: payload?.autoCaptureEnabled !== false,
      entries: Array.isArray(payload?.entries) ? payload.entries : [],
      disabledReason: payload?.disabledReason || null
    };
    const selected = state.projectMemory.entries.find(item => item.id === previousSelection)
      || state.projectMemory.entries[0]
      || null;
    state.projectMemory.selectedEntryId = selected?.id || null;
    elements.memoryProjectLabel.textContent = state.projectMemory.projectLabel;
    elements.memoryAutoCapture.checked = state.projectMemory.autoCaptureEnabled;
    elements.memoryAutoCapture.disabled = !state.projectMemory.available;
    elements.newMemoryEntry.disabled = !state.projectMemory.available;
    elements.memoryDisabledReason.hidden = !state.projectMemory.disabledReason;
    elements.memoryDisabledReason.textContent = state.projectMemory.disabledReason || "";
    if (!state.projectMemory.isEditing) showMemoryPreview(selected);
    else renderProjectMemoryList();
  }

  function openProjectMemory() {
    closeWorkspacePicker();
    closeWorkflows();
    elements.memoryOverlay.hidden = false;
    state.projectMemory.isEditing = false;
    post("memory.list", { sessionId: state.activeSessionId });
  }

  function closeProjectMemory() {
    elements.memoryOverlay.hidden = true;
    state.projectMemory.isEditing = false;
  }

  async function postChatRequest(payload) {
    if (state.pendingChatSend) return null;
    const requestId = globalThis.crypto.randomUUID();
    const pending = { sessionId: payload.sessionId, prompt: payload.prompt, requestId };
    state.pendingChatSend = pending;
    state.pendingCaptureRequest = payload;
    renderStatus();
    if (state.audioCapture?.isRecording) {
      post("audioCapture.stop", {});
    }
    const posted = post("chat.send", payload, requestId);
    if (posted === null && state.pendingChatSend === pending) { state.pendingChatSend = null; renderStatus(); }
    return posted;
  }

  function resumePendingCaptureRequest() {
    const request = state.pendingCaptureRequest;
    const action = toolActionForExtensionActionId(request?.extensionActionId) || state.selectedToolAction;
    if (!state.waitingForCapture || !request || !hasMediaAnalysisContext(action)) return;
    state.waitingForCapture = false;
    void postChatRequest(request);
  }

  async function handleComposerAction() {
    if (state.pendingChatSend) return;
    if (elements.prompt.value.trim()) await submitPrompt();
    else if (globalThis.goRunSteering?.canSteer(state) || scheduledRunForSession(state.activeSessionId)) {
      post("chat.cancel", { sessionId: state.activeSessionId });
    }
  }

  async function submitPrompt() {
    const prompt = elements.prompt.value.trim();
    if (!prompt) return;
    if (state.pendingChatSend) {
      showToast("Der Auftrag wird vorbereitet. Deine neue Eingabe bleibt erhalten und kann danach umlenken.");
      return;
    }
    const retry = globalThis.goRunSteering?.retryPending(state, prompt, post);
    if (retry) {
      if (retry === "unbound") showToast("Diese Umlenkung ist noch nicht bestätigt. Die Eingabe bleibt erhalten; es wird kein neuer Auftrag gestartet.", true);
      return;
    }
    const scheduledRun = scheduledRunForSession(state.activeSessionId);
    if (globalThis.goRunSteering?.canSteer(state)) {
      globalThis.goRunSteering.request(state, prompt, post);
      return;
    }
    if (scheduledRun) {
      showToast(scheduledRun.queueState === "queued"
        ? `Diese Sitzung wartet bereits auf Platz ${scheduledRun.position}.`
        : "Der Auftrag dieser Sitzung wird gerade gestartet.", true);
      return;
    }
    state.voiceTurn = null;
    renderContext();
    chatScroll.jump(false);
    clearTimeout(draftTimer);
    draftTimer = 0;
    pendingDraft = null;
    const sessionId = state.activeSessionId;
    const automaticResearch = state.chatMode === "claudescience" && shouldAutoResearch(prompt);
    const useDeepResearch = Boolean(state.deepResearch || automaticResearch);
    const sent = await postChatRequest({
      sessionId: state.activeSessionId,
      chatMode: state.chatMode,
      prompt,
      documentIds: state.documents.map(item => item.id),
      extensionActionId: state.selectedExtensionActionId,
      deepResearch: useDeepResearch,
      deepResearchProfile: useDeepResearch ? (state.deepResearchProfile || "auto") : null
    });
    if (sent !== null && state.activeSessionId === sessionId && elements.prompt.value.trim() === prompt) setPromptValue("");
  }

  function shouldAutoResearch(prompt) {
    const text = String(prompt || "").trim();
    if (text.length > 700) return true;
    return /\b(deep research|systematic review|scoping review|literature review|meta-analysis|paper|publikation|primärquelle|quellen|zitiere|forschungslage|stand der forschung|hypothese|replikation|beweise|herleitung|gegenbeispiel|datensatz|statistisch|experiment|wissenschaftlich|aktuellste studien|vergleich der evidenz)\b/i.test(text);
  }

  function appendVoiceDictation(baseText, transcript) {
    const base = String(baseText || "");
    const text = String(transcript || "").trim();
    if (!text) return base;
    return base && !/\s$/u.test(base) ? `${base} ${text}` : `${base}${text}`;
  }

  function updateVoiceDictation(
    turnId,
    transcript,
    isFinal,
    revision = 0,
    stableText = "",
    provisionalText = "") {
    const id = String(turnId || "");
    const text = String(transcript || "").trim();
    const nextRevision = Number.isFinite(Number(revision)) ? Number(revision) : 0;
    if (!id || !text) return;

    let turn = state.voiceTurn;
    if (!turn || String(turn.turnId) !== id) {
      turn = {
        turnId: id,
        baseText: elements.prompt.value,
        text: "",
        stableText: "",
        provisionalText: "",
        renderedValue: elements.prompt.value,
        lastRevision: -1,
        manuallyConfirmed: false,
        draftSavedAt: Date.now()
      };
      state.voiceTurn = turn;
    } else if (nextRevision <= turn.lastRevision) {
      return;
    } else if (elements.prompt.value !== turn.renderedValue) {
      // Preserve manual edits made while Whisper was refining the current
      // partial transcript. Editing inside that provisional suffix explicitly
      // confirms the visible wording; later Whisper revisions cannot replace it.
      const oldSuffix = appendVoiceDictation(turn.baseText, turn.text)
        .slice(turn.baseText.length);
      if (oldSuffix && elements.prompt.value.endsWith(oldSuffix)) {
        turn.baseText = elements.prompt.value.slice(0, -oldSuffix.length);
      } else {
        turn.baseText = elements.prompt.value;
        turn.renderedValue = elements.prompt.value;
        turn.manuallyConfirmed = true;
      }
    }

    turn.lastRevision = nextRevision;
    if (turn.manuallyConfirmed) {
      if (isFinal) state.voiceTurn = null;
      scheduleDraftSave();
      if (isFinal || Date.now() - turn.draftSavedAt >= 1500) {
        flushDraft();
        turn.draftSavedAt = Date.now();
      }
      renderContext();
      return;
    }

    const promptWasFocused = document.activeElement === elements.prompt;
    const oldSelectionStart = elements.prompt.selectionStart ?? elements.prompt.value.length;
    const oldSelectionEnd = elements.prompt.selectionEnd ?? oldSelectionStart;
    const oldRenderedLength = elements.prompt.value.length;
    turn.text = text;
    turn.stableText = String(stableText || "").trim();
    turn.provisionalText = String(provisionalText || "").trim();
    turn.renderedValue = appendVoiceDictation(turn.baseText, text);
    setPromptValue(turn.renderedValue);
    if (isFinal) state.voiceTurn = null;
    scheduleDraftSave();
    if (isFinal || Date.now() - turn.draftSavedAt >= 1500) {
      flushDraft();
      turn.draftSavedAt = Date.now();
    }
    if (promptWasFocused) {
      const lengthDelta = elements.prompt.value.length - oldRenderedLength;
      const generatedStart = turn.baseText.length;
      const adjustPosition = position => position <= generatedStart
        ? position
        : Math.max(generatedStart, Math.min(elements.prompt.value.length, position + lengthDelta));
      elements.prompt.setSelectionRange(
        adjustPosition(oldSelectionStart),
        adjustPosition(oldSelectionEnd));
    }
    renderContext();
  }

  function discardVoiceDictation(turnId) {
    const turn = state.voiceTurn;
    if (!turn || (turnId && String(turn.turnId) !== String(turnId))) return;
    if (elements.prompt.value === turn.renderedValue) {
      setPromptValue(turn.baseText);
    } else {
      const suffix = appendVoiceDictation(turn.baseText, turn.text)
        .slice(turn.baseText.length);
      if (suffix && elements.prompt.value.endsWith(suffix)) {
        setPromptValue(elements.prompt.value.slice(0, -suffix.length));
      }
    }
    state.voiceTurn = null;
    scheduleDraftSave();
    renderContext();
  }

  function ensureEditableContext() {
    if (!state.isRunning && !scheduledRunForSession(state.activeSessionId) && !state.pendingChatSend) return true;
    showToast("Während des laufenden Auftrags kannst du mit Text umlenken. Werkzeuge, Workspace und Anhänge lassen sich danach ändern.", true);
    return false;
  }

  function selectToolAction(action, persist = true) {
    if (persist && !ensureEditableContext()) return;
    const previous = state.selectedToolAction;
    const previousExtensionActionId = state.selectedExtensionActionId
      || extensionActionIdForToolAction(previous);
    const requested = normalizeToolAction(action) === "coding" ? null : normalizeToolAction(action);
    const requestedExtensionActionId = extensionActionIdForToolAction(requested);
    if (persist) {
      const targetExtensionActionId = requestedExtensionActionId || previousExtensionActionId;
      if (!targetExtensionActionId) return;
      const descriptor = availableActionDescriptors().find(item => item.actionId === targetExtensionActionId);
      if (!descriptor) {
        showToast("Diese Erweiterungsaktion ist derzeit nicht verfügbar.", true);
        return;
      }
      post("action.invoke", {
        actionId: descriptor.actionId,
        sessionId: state.activeSessionId,
        selectionBehavior: "toggle",
        enabled: Boolean(requested)
      });
      return;
    }
    const persistentFallback = normalizeToolAction(state.persistentToolAction);
    const persistentFallbackExtensionActionId = state.persistentExtensionActionId
      || extensionActionIdForToolAction(persistentFallback);
    state.selectedToolAction = !requested
      && previous
      && !persistentToolActions.has(previous)
      && persistentFallback
        ? persistentFallback
        : requested;
    state.selectedExtensionActionId = !requestedExtensionActionId
      && previousExtensionActionId
      && !persistentExtensionActionIds.has(previousExtensionActionId)
      && persistentFallbackExtensionActionId
        ? persistentFallbackExtensionActionId
        : requestedExtensionActionId;
    if (state.selectedExtensionActionId && !state.selectedToolAction) {
      state.selectedToolAction = toolActionForExtensionActionId(state.selectedExtensionActionId);
    }
    if (state.deepResearch && state.selectedToolAction && !["coding"].includes(state.selectedToolAction)) {
      state.deepResearch = false;
      persistDeepResearch();
    }
    for (const option of document.querySelectorAll(".service-option[data-tool-action]")) {
      option.classList.toggle("active", option.dataset.toolAction === state.selectedToolAction);
    }
    elements.toolsButton.title = "Hinzufügen";
    if (typeof updateActionMenuState === "function") updateActionMenuState();
    renderContext();
    if (!state.messages.length) renderMessages(false);
  }

  function clearCompletedOneShotToolAction() {
    if (state.selectedExtensionActionId
      && state.selectedExtensionActionId !== state.persistentExtensionActionId) {
      state.selectedExtensionActionId = state.persistentExtensionActionId;
      state.selectedToolAction = toolActionForExtensionActionId(state.persistentExtensionActionId);
      if (typeof updateActionMenuState === "function") updateActionMenuState();
      renderContext();
    } else if (state.selectedToolAction && !persistentToolActions.has(state.selectedToolAction)) {
      selectToolAction(state.persistentToolAction, false);
    }
  }

  function persistDeepResearch() {
    if (!state.activeSessionId) return;
    try {
      globalThis.localStorage.setItem(`assistant.session.deep-research.v1:${state.activeSessionId}`, state.deepResearch ? "1" : "0");
      globalThis.localStorage.setItem(`assistant.session.deep-research-profile.v1:${state.activeSessionId}`, state.deepResearchProfile || "auto");
    }
    catch { /* Optional WebView storage must not prevent sending a prompt. */ }
  }

  function restoreDeepResearch() {
    state.deepResearch = false;
    state.deepResearchProfile = "auto";
    if (!state.activeSessionId) return;
    try {
      state.deepResearch = readMigratedStorage(
        `assistant.session.deep-research.v1:${state.activeSessionId}`,
        `go.assistant.deep-research.v1:${state.activeSessionId}`) === "1";
      const profile = globalThis.localStorage.getItem(`assistant.session.deep-research-profile.v1:${state.activeSessionId}`);
      if (["auto", "web", "scientificEvidence", "systematicReview", "scopingReview", "literatureUpdate",
        "replicationAudit", "openProblem", "mathematicalInvestigation"].includes(profile)) state.deepResearchProfile = profile;
    }
    catch { /* Default to ordinary chat when storage is unavailable. */ }
  }

  function selectDeepResearch(enabled, persist = true) {
    if (persist) {
      if (!ensureEditableContext()) return;
      const actionId = extensionActionIdForToolAction("deepResearch");
      const descriptor = availableActionDescriptors().find(item => item.actionId === actionId);
      if (!descriptor) {
        showToast("Deep Research ist derzeit nicht verfügbar.", true);
        return;
      }
      post("action.invoke", {
        actionId: descriptor.actionId,
        sessionId: state.activeSessionId,
        selectionBehavior: "toggle",
        enabled: Boolean(enabled)
      });
      return;
    }
    if (enabled && (state.selectedExtensionActionId || state.selectedToolAction)) selectToolAction(null, false);
    state.deepResearch = Boolean(enabled);
    persistDeepResearch();
    if (typeof updateActionMenuState === "function") updateActionMenuState();
    renderContext();
  }

  function renderDeepResearch() {
    for (const option of document.querySelectorAll('[data-tool-toggle="deepResearch"]')) {
      option.classList.toggle("active", Boolean(state.deepResearch));
      option.setAttribute("aria-checked", state.deepResearch ? "true" : "false");
    }
    if (!state.deepResearch || state.chatMode === "claudescience") return;
    const chip = document.createElement("div");
    chip.className = "active-tool-chip";
    chip.dataset.toolToggle = "deepResearch";
    const profile = document.createElement("select");
    profile.className = "active-tool-chip__profile";
    profile.setAttribute("aria-label", "Deep-Research-Profil");
    for (const [value, label] of [["auto", "Deep Research · Automatisch"], ["web", "Webrecherche"],
      ["scientificEvidence", "Wissenschaftliche Evidenz"], ["systematicReview", "Systematischer Review"],
      ["scopingReview", "Scoping Review"], ["literatureUpdate", "Literatur aktualisieren"],
      ["replicationAudit", "Replikationsprüfung"], ["openProblem", "Offenes Forschungsproblem"],
      ["mathematicalInvestigation", "Mathematische Untersuchung"]]) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      option.selected = value === state.deepResearchProfile;
      profile.append(option);
    }
    profile.addEventListener("change", () => {
      state.deepResearchProfile = profile.value;
      persistDeepResearch();
    });
    const status = document.createElement("button");
    status.type = "button";
    status.className = "active-tool-chip__details";
    status.textContent = "Stand";
    status.title = "Gespeicherten Forschungsstand öffnen";
    status.addEventListener("click", () => openScientificResearch());
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "active-tool-chip__remove";
    remove.textContent = "×";
    remove.title = "Deep Research abwählen";
    remove.setAttribute("aria-label", remove.title);
    remove.addEventListener("click", () => selectDeepResearch(false));
    chip.append(createToolIcon(toolVisuals.webSearch[1]), profile, status, remove);
    elements.activeTools.append(chip);
  }

  function openScientificResearch() {
    state.scienceView = "overview";
    post("research.list", { sessionId: state.activeSessionId });
    renderScienceWorkbench();
  }

  function closeScientificResearch() {
    state.scienceChatVisible = true;
    renderScienceWorkbench();
  }

  function renderScientificResearch() {
    renderScienceWorkbench();
  }
  function resetTransientVoiceStateForSessionChange() {
    const browserCaptureActive = Boolean(
      globalThis.goVoiceCapture?.isActive
      || globalThis.goVoiceCapture?.isStarting);
    state.voiceTurn = null;
    state.voiceStarting = false;
    state.voiceCaptureFeedbackAction = null;
    state.voiceCaptureFeedbackStarted = false;
    state.voiceLevel = 0;
    state.voiceFrequency = 0;
    state.voiceDominantHz = 0;
    state.microphone = {
      ...state.microphone,
      isBusy: browserCaptureActive && Boolean(state.microphone?.isBusy),
      partialTranscript: browserCaptureActive ? state.microphone?.partialTranscript || "" : "",
      status: browserCaptureActive ? state.microphone?.status || "Aktiv" : "Inaktiv",
      error: null
    };
  }

  function conversationMessagesDiffer(previousMessages, nextMessages) {
    const previous = Array.isArray(previousMessages) ? previousMessages : [];
    const next = Array.isArray(nextMessages) ? nextMessages : [];
    if (previous.length !== next.length) return true;

    for (let index = 0; index < previous.length; index += 1) {
      const left = previous[index] || {};
      const right = next[index] || {};
      if (String(left.id || "") !== String(right.id || "")
        || Number(left.revision || 0) !== Number(right.revision || 0)
        || String(left.status || "") !== String(right.status || "")
        || String(left.updatedAt || "") !== String(right.updatedAt || "")
        || String(left.content || "") !== String(right.content || "")
        || String(left.error || "") !== String(right.error || "")
        || (Array.isArray(left.artifacts) ? left.artifacts.length : 0)
          !== (Array.isArray(right.artifacts) ? right.artifacts.length : 0)) {
        return true;
      }
    }

    return false;
  }

  function contextProfileForSnapshot(payload) {
    return JSON.stringify([payload.activeSessionId || "", payload.reasoningRole || "general",
      payload.reasoningModelId || "", String(payload.workspacePath || payload.codingWorkspacePath || "").toLocaleLowerCase()]);
  }

  function persistMeasuredContext() {
    if (state.contextSource !== "measured" || !state.contextProfile) return;
    try {
      globalThis.localStorage.setItem(`assistant.context.measurement.v1:${state.contextProfile}`, JSON.stringify({
        messageId: state.contextMeasurementMessageId,
        used: state.contextUsed, limit: state.contextLimit, truncated: state.contextWasTruncated
      }));
    } catch { /* Optional display storage must never block navigation. */ }
  }

  function restoreSnapshotContext(payload) {
    state.contextProfile = contextProfileForSnapshot(payload);
    state.contextSource = payload.contextSource === "measured" ? "measured" : "estimated";
    state.contextMeasurementMessageId = payload.contextMessageId || state.messages.at(-1)?.id || null;
    state.contextUsed = Number.isFinite(payload.contextUsed) ? payload.contextUsed : 0;
    state.contextLimit = Number.isFinite(payload.contextLimit) && payload.contextLimit > 0 ? payload.contextLimit : 8192;
    state.contextWasTruncated = Boolean(payload.contextWasTruncated);
    state.contextNotice = payload.contextNotice || null;
    if (state.contextSource !== "measured") {
      try {
        const saved = JSON.parse(readMigratedStorage(
          `assistant.context.measurement.v1:${state.contextProfile}`,
          `go.assistant.context.v1:${state.contextProfile}`) || "null");
        if (saved && saved.messageId === (state.messages.at(-1)?.id || null)
          && Number.isFinite(saved.used) && saved.used >= 0 && Number.isFinite(saved.limit) && saved.limit > 0) {
          state.contextUsed = saved.used;
          // Snapshot estimates use the catalog maximum. The last measured run
          // may have fitted a smaller native context, which remains authoritative.
          state.contextLimit = saved.limit;
          state.contextWasTruncated = Boolean(saved.truncated);
          state.contextSource = "measured";
          state.contextNotice = null;
        }
      } catch { /* Invalid optional state falls back to the host estimate. */ }
    }
    persistMeasuredContext();
  }

  function belongsToActiveSession(payload) {
    const sessionId = payload?.sessionId || payload?.message?.sessionId;
    return !sessionId || String(sessionId) === String(state.activeSessionId || "");
  }

  function applySnapshot(payload) {
    globalThis.goBridge?.configureContract?.(payload?.bridgeContract);
    const dialogWasOpen = !elements.overlay.hidden;
    const memoryWasOpen = Boolean(elements.memoryOverlay && !elements.memoryOverlay.hidden);
    const researchWasOpen = state.chatMode === "claudescience";
    const editorSelection = state.selectedWorkflowEditorId;
    const wasEditing = state.isWorkflowEditing;
    const previousSessionId = state.activeSessionId;
    const previousPersistentExtensionActionId = state.persistentExtensionActionId;
    const nextSessionId = payload.activeSessionId || null;
    const sessionChanged = previousSessionId !== nextSessionId;
    const serverToolAction = normalizeToolAction(payload.selectedToolAction);
    const activeSession = (Array.isArray(payload.sessions) ? payload.sessions : [])
      .find(session => String(session.id || "") === String(nextSessionId || ""));
    const serverExtensionActionId = normalizeExtensionActionId(payload.selectedExtensionActionId)
      || extensionActionIdForToolAction(serverToolAction);
    const persistentExtensionActionId = normalizeExtensionActionId(payload.persistentExtensionActionId)
      || normalizeExtensionActionId(activeSession?.persistentExtensionActionId)
      || serverExtensionActionId;
    const snapshotModeValue = String(payload.chatMode ?? payload.selectedChatMode ?? "").trim().toLocaleLowerCase();
    const snapshotMode = ["claudescience", "claude-science", "science"].includes(snapshotModeValue)
      ? "claudescience"
      : ["coding", "code", "codex"].includes(snapshotModeValue)
        ? "coding"
      : ["general", "chat", "chatgpt"].includes(snapshotModeValue)
        ? "general"
        : serverToolAction === "coding" ? "coding" : "general";
    state.chatMode = snapshotMode;
    state.scienceCapabilities = payload.scienceCapabilities || state.scienceCapabilities || {};
    state.assistantTitle = String(payload.productIdentity?.assistantTitle || payload.assistantTitle || "AI Assistent").trim() || "AI Assistent";
    document.title = state.assistantTitle;
    if (Array.isArray(payload.actionDescriptors) || Array.isArray(payload.extensionActions)) {
      state.actionDescriptors = Array.isArray(payload.actionDescriptors)
        ? payload.actionDescriptors
        : payload.extensionActions;
    }
    if (Array.isArray(payload.activeExtensionActionIds) || Array.isArray(payload.selectedExtensionActionIds)) {
      state.activeActionIds = new Set((payload.activeExtensionActionIds || payload.selectedExtensionActionIds)
        .map(value => String(value || "").trim())
        .filter(Boolean));
    }
    if (sessionChanged) { closeCodingPreview(); state.changesSummary = null; }
    const nextMessages = Array.isArray(payload.messages) ? payload.messages : [];
    const currentSessionMessagesChanged = !sessionChanged
      && conversationMessagesDiffer(state.messages, nextMessages);
    if (sessionChanged && previousSessionId) persistSessionScrollPosition(previousSessionId);
    state.sessions = Array.isArray(payload.sessions) ? payload.sessions : [];
    state.sessionGroups = Array.isArray(payload.sessionGroups) ? payload.sessionGroups : [];
    state.messages = nextMessages;
    state.conversationRevision = Number(payload.conversationRevision) || 0;
    state.conversationRefreshPending = false;
    state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
    state.documents = Array.isArray(payload.documents) ? payload.documents : [];
    state.attachments = Array.isArray(payload.attachments) ? payload.attachments : [];
    state.documentGroupStatus = payload.documentGroupStatus || { total: 0, ready: 0, processing: 0, failed: 0, status: "ready" };
    state.activeSessionId = nextSessionId;
    if (sessionChanged) {
      restoreDeepResearch();
      try {
        state.scienceView = globalThis.localStorage.getItem(`assistant.science.view.v1:${state.activeSessionId}`) || "overview";
        state.scienceChatVisible = globalThis.localStorage.getItem("assistant.science.chat-visible.v1") !== "0";
      } catch { state.scienceView = "overview"; }
      if (!["overview", "research", "data", "analysis", "figures", "manuscript", "review", "provenance"].includes(state.scienceView)) state.scienceView = "overview";
      state.scienceRequestedProjectId = null;
    }
    globalThis.goVoiceCapture?.setSessionId(state.activeSessionId);
    if (previousSessionId !== state.activeSessionId) {
      state.messageRunStatus.clear();
      resetTransientVoiceStateForSessionChange();
    }
    state.isRunning = Boolean(payload.isRunning);
    state.isAiBusy = Boolean(payload.isAiBusy ?? payload.isRunning);
    applyRunQueue(payload.runQueue || {
      active: payload.activeRunSessionId ? { sessionId: payload.activeRunSessionId, state: "running", position: 0 } : null,
      pending: [],
      isIdle: !payload.isAiBusy
    });
    state.activeRunSessionId = payload.activeRunSessionId || state.runQueue.active?.sessionId
      || (state.isRunning ? nextSessionId : null);
    state.activeRunId = payload.activeRunId || null;
    state.loadedFiles = payload.loadedFiles ?? null;
    state.model = payload.model || null;
    state.workspacePath = payload.workspacePath || payload.codingWorkspacePath || null;
    // Compatibility alias for the Coding timeline while the shared workspace
    // terminology is adopted throughout the client.
    state.codingWorkspacePath = state.workspacePath;
    state.codingToolStepsExpanded = Boolean(payload.codingToolStepsExpanded);
    if (payload.changesSummary) applyCodingChanges(payload.changesSummary);
    restoreSnapshotContext(payload);
    state.persistentExtensionActionId = persistentExtensionActionId;
    state.persistentToolAction = toolActionForExtensionActionId(persistentExtensionActionId)
      || (persistentToolActions.has(serverToolAction) ? serverToolAction : null);
    const activeOneShotTool = state.selectedExtensionActionId
      && state.selectedExtensionActionId !== previousPersistentExtensionActionId;
    // session state; a one-shot chip survives idle refreshes while composing.
    if (previousSessionId !== state.activeSessionId || !activeOneShotTool) {
      state.selectedExtensionActionId = serverExtensionActionId;
      selectToolAction(toolActionForExtensionActionId(serverExtensionActionId) || serverToolAction, false);
      if (serverExtensionActionId && !state.selectedExtensionActionId) {
        state.selectedExtensionActionId = serverExtensionActionId;
      }
    } else {
      selectToolAction(state.selectedToolAction, false);
    }
    state.runStatus = payload.runStatus || null;
    state.runDetail = payload.runDetail || null;
    const runningMessage = state.isRunning && state.messages.findLast(message => message.role === "assistant"
      && !isTerminalMessageStatus(message.status)
      && (!payload.runMessageId || String(message.id) === String(payload.runMessageId)));
    state.activeRunMessageId = runningMessage?.id || null;
    globalThis.goRunSteering?.observeRun(state);
    if (runningMessage) state.messageRunStatus.set(String(runningMessage.id), {
      status: state.runStatus || "Denkt nach", detail: state.runDetail, model: state.model
    });
    pruneTerminalMessageRunStatuses();
    state.liveCaption = payload.liveCaption || state.liveCaption;
    if (typeof payload.isSessionPaneOpen === "boolean") {
      const collapsed = !payload.isSessionPaneOpen;
      setSessionsCollapsed(collapsed, false);
      try { globalThis.localStorage.setItem(sidebarStorageKey, collapsed ? "1" : "0"); }
      catch { /* WebView storage is optional. */ }
    }
    // Same-session snapshots can race the draft debounce or its asynchronous host save.
    // Keep the live composer authoritative until another session is opened.
    if (sessionChanged) {
      setPromptValue(payload.draft || "");
    }
    if (typeof renderModeSwitcher === "function") renderModeSwitcher();
    if (typeof renderActionMenu === "function") renderActionMenu();
    renderSessions();
    renderMessages(currentSessionMessagesChanged);
    if (sessionChanged) restoreSessionScrollPosition(state.activeSessionId);
    renderContext();
    renderStatus();
    renderLiveCaption();
    renderMicrophone();
    syncVoiceCaptureSuspension();
    renderScreenClip();

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
    if (memoryWasOpen && sessionChanged) {
      post("memory.list", { sessionId: state.activeSessionId });
    }
    if (sessionChanged && state.chatMode === "claudescience") {
      state.scientificResearch = { available: true, projects: [], selectedProjectId: null, detail: null, disabledReason: null };
      post("research.list", { sessionId: state.activeSessionId });
    } else if (researchWasOpen && sessionChanged) {
      state.scientificResearch = { available: true, projects: [], selectedProjectId: null, detail: null, disabledReason: null };
      post("research.list", { sessionId: state.activeSessionId });
    }
  }

  function sortCommittedMessages() {
    state.messages.sort((left, right) => {
      const leftCreated = String(left.createdAt || "");
      const rightCreated = String(right.createdAt || "");
      const leftMilliseconds = Date.parse(leftCreated);
      const rightMilliseconds = Date.parse(rightCreated);
      const timeDifference = leftMilliseconds - rightMilliseconds;
      if (Number.isFinite(timeDifference) && timeDifference !== 0) return timeDifference;

      // Date.parse rounds the database's 100-nanosecond precision down to
      // milliseconds. A user message and its streaming AI placeholder are
      // intentionally only one tick apart, so comparing just Date values can
      // randomly reverse them by GUID. The canonical UTC ISO strings retain
      // those fractional digits and therefore reproduce SQLite's ordering.
      const exactDifference = leftCreated < rightCreated ? -1 : leftCreated > rightCreated ? 1 : 0;
      return exactDifference || String(left.id || "").localeCompare(String(right.id || ""));
    });
  }

  function requestConversationRefresh() {
    if (!state.activeSessionId || state.conversationRefreshPending) return;
    state.conversationRefreshPending = true;
    post("conversation.refresh", { sessionId: state.activeSessionId });
  }

  function applyConversationSnapshot(payload) {
    if (!payload || String(payload.activeSessionId || "") !== String(state.activeSessionId || "")) return;
    const incomingRevision = Number(payload.conversationRevision) || 0;
    const currentRevision = Number(state.conversationRevision) || 0;
    if (incomingRevision < currentRevision) {
      // A refresh can finish after newer commit/stream packets have already
      // reached this client. Never roll the visible conversation back; ask
      // once more for the now-current durable snapshot instead.
      state.conversationRefreshPending = false;
      requestConversationRefresh();
      return;
    }
    state.codingToolStepsExpanded = Boolean(payload.codingToolStepsExpanded);
    const nextMessages = Array.isArray(payload.messages) ? payload.messages : [];
    const messagesChanged = conversationMessagesDiffer(state.messages, nextMessages);
    state.messages = nextMessages;
    sortCommittedMessages();
    if (payload.changesSummary) applyCodingChanges(payload.changesSummary);
    state.conversationRevision = incomingRevision;
    state.conversationRefreshPending = false;
    pruneTerminalMessageRunStatuses();
    renderMessages(messagesChanged);
  }

  function acceptCommittedRevision(payload) {
    const incoming = Number(payload?.conversationRevision) || 0;
    const current = Number(state.conversationRevision) || 0;
    if (incoming < current) return false;
    if (incoming > current + 1) {
      requestConversationRefresh();
      return false;
    }
    state.conversationRevision = Math.max(current, incoming);
    return true;
  }

  function applyCommittedMessage(payload) {
    const message = payload?.message;
    if (!message || String(payload.sessionId || message.sessionId || "") !== String(state.activeSessionId || "")) return;
    if (!acceptCommittedRevision(payload)) return;
    const index = state.messages.findIndex(item => String(item.id || "") === String(message.id || ""));
    if (index >= 0) {
      const existingRevision = Number(state.messages[index].revision) || 0;
      const incomingRevision = Number(message.revision) || 0;
      if (incomingRevision < existingRevision) return;
      state.messages[index] = message;
    } else {
      state.messages.push(message);
    }
    if (isTerminalMessageStatus(message.status)) {
      state.messageRunStatus.delete(String(message.id));
    }
    sortCommittedMessages();
    renderMessages(true);
  }

  function upsertLiveMessage(message, fallback = {}) {
    if (!message?.id) return false;
    const sessionId = message.sessionId || fallback.sessionId || state.activeSessionId;
    if (String(sessionId || "") !== String(state.activeSessionId || "")) return false;
    const index = state.messages.findIndex(item => String(item.id || "") === String(message.id));
    const existing = index >= 0 ? state.messages[index] : null;
    const existingRevision = Number(existing?.revision) || 0;
    const incomingRevision = Number(message.revision) || 0;
    if (existing && incomingRevision > 0 && incomingRevision < existingRevision) return false;
    if (existing && isTerminalMessageStatus(existing.status) && !isTerminalMessageStatus(message.status)) return false;
    const merged = {
      ...(existing || {
        sessionId,
        role: fallback.role || "assistant",
        createdAt: fallback.createdAt || new Date().toISOString()
      }),
      ...message,
      sessionId
    };
    if (index >= 0) state.messages[index] = merged;
    else state.messages.push(merged);
    if (isTerminalMessageStatus(merged.status)) {
      state.messageRunStatus.delete(String(merged.id));
    }
    sortCommittedMessages();
    return true;
  }

  function applyLiveDelta(payload) {
    if (!belongsToActiveSession(payload) || !payload?.messageId) return false;
    const existing = state.messages.find(item => String(item.id || "") === String(payload.messageId));
    if (existing && isTerminalMessageStatus(existing.status)) return false;
    const incomingToolSteps = Array.isArray(payload.toolSteps) ? payload.toolSteps : existing?.toolSteps || [];
    if (existing
      && String(existing.content || "") === String(payload.content ?? existing.content ?? "")
      && JSON.stringify(existing.toolSteps || []) === JSON.stringify(incomingToolSteps)) {
      return false;
    }
    return upsertLiveMessage({
      id: payload.messageId,
      sessionId: payload.sessionId || existing?.sessionId || state.activeSessionId,
      role: existing?.role || "assistant",
      status: existing?.status || "streaming",
      content: payload.content ?? existing?.content ?? "",
      toolSteps: incomingToolSteps,
      updatedAt: payload.updatedAt || existing?.updatedAt
    });
  }

  function handleHostMessage(event) {
    const { type, payload, requestId } = event.detail;
    switch (type) {
      case "state.snapshot":
        applySnapshot(payload);
        break;
      case "conversation.snapshot":
        applyConversationSnapshot(payload);
        break;
      case "conversation.messageCommitted":
        applyCommittedMessage(payload);
        break;
      case "coding.changes":
        applyCodingChanges(payload);
        break;
      case "chat.queued": {
        if (state.pendingChatSend?.requestId === requestId) {
          state.pendingChatSend = null;
          state.pendingCaptureRequest = null;
          state.waitingForCapture = false;
        }
        applyRunQueue(payload?.runQueue || {
          active: state.runQueue?.active || null,
          pending: [...(state.runQueue?.pending || []), payload],
          isIdle: false
        });
        renderSessions();
        renderStatus();
        const queued = scheduledRunForSession(payload?.sessionId);
        if (queued?.queueState === "queued" && belongsToActiveSession(payload)) {
          showToast(`Auftrag eingereiht · Platz ${queued.position}`);
        }
        break;
      }
      case "queue.changed":
        applyRunQueue(payload?.runQueue || payload);
        renderSessions();
        renderStatus();
        break;
      case "chat.started":
        if (state.pendingChatSend?.requestId === requestId) state.pendingChatSend = null;
        state.isAiBusy = true;
        state.activeRunSessionId = payload.sessionId || payload.message?.sessionId || state.activeSessionId;
        state.activeRunId = payload.runId || null;
        state.activeRunMessageId = payload.message?.id || null;
        if (!belongsToActiveSession(payload)) { renderStatus(); break; }
        state.changesSummary = null;
        state.isRunning = true;
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        state.voiceTurn = null;
        upsertLiveMessage(payload.userMessage, { role: "user" });
        upsertLiveMessage(payload.message, { role: "assistant" });
        if (Number.isFinite(payload.conversationRevision)) {
          const currentRevision = Number(state.conversationRevision) || 0;
          const incomingRevision = Number(payload.conversationRevision) || 0;
          const coveredRevisionDelta = Number(Boolean(payload.userMessage?.id))
            + Number(Boolean(payload.message?.id));
          const hasUnexplainedRevisionGap = incomingRevision > currentRevision + coveredRevisionDelta;
          state.conversationRevision = Math.max(currentRevision, incomingRevision);
          state.conversationRefreshPending = false;
          if (hasUnexplainedRevisionGap) requestConversationRefresh();
        }
        if (payload.session) {
          const sessionIndex = state.sessions.findIndex(item => item.id === payload.session.id);
          if (sessionIndex >= 0) state.sessions[sessionIndex] = payload.session;
          else if (sessionMatchesMode(payload.session)) state.sessions.push(payload.session);
        }
        if (Array.isArray(payload.attachments)) {
          state.attachments = payload.attachments;
          renderContext();
        }
        if (Number.isFinite(payload.contextUsed)) {
          state.contextUsed = payload.contextUsed;
          state.contextSource = "measured";
          state.contextMeasurementMessageId = payload.message?.id || state.messages.at(-1)?.id || null;
        }
        if (Number.isFinite(payload.contextLimit) && payload.contextLimit > 0) state.contextLimit = payload.contextLimit;
        if (payload.model) state.model = payload.model;
        state.contextWasTruncated = Boolean(payload.contextWasTruncated);
        state.contextNotice = payload.contextNotice || null;
        state.runStatus = payload.runStatus || "Denkt nach";
        state.runDetail = payload.runDetail || null;
        if (payload.message?.id) state.messageRunStatus.set(String(payload.message.id), {
          status: payload.runStatus || "Denkt nach",
          detail: payload.runDetail || null,
          model: payload.model || state.model || null
        });
        persistMeasuredContext();
        recordCodingActivity({ ...payload, messageId: payload.message?.id, runStatus: state.runStatus });
        renderMessages(true);
        renderSessions();
        renderStatus();
        syncVoiceCaptureSuspension();
        if (state.contextWasTruncated) showToast(state.contextNotice || "Der Modellkontext wurde gekürzt.");
        break;
      case "chat.delta": {
        if (applyLiveDelta(payload)) renderMessages(true);
        break;
      }
      case "chat.steer.accepted": {
        if (state.pendingChatSend?.requestId === requestId) {
          state.pendingChatSend = null;
          state.pendingCaptureRequest = null;
          renderStatus();
        }
        const receipt = globalThis.goRunSteering?.accept(payload, state, elements.prompt.value);
        if (receipt?.clearDraft) { setPromptValue(""); scheduleDraftSave(); flushDraft(); }
        if (receipt?.sameSession) showToast("Umlenkung übernommen.");
        renderStatus();
        break;
      }
      case "chat.completed":
      case "chat.cancelled":
      case "chat.failed":
        if (state.pendingChatSend?.requestId === requestId) state.pendingChatSend = null;
        if (type === "chat.cancelled" && payload?.queued) {
          removeScheduledRun(payload.sessionId, requestId);
          if (belongsToActiveSession(payload)) {
            state.runStatus = payload.runStatus || "Aus Warteschlange entfernt";
            state.runDetail = null;
            showToast(state.runStatus);
          }
          renderSessions();
          renderStatus();
          break;
        }
        globalThis.goRunSteering?.observeRun(state);
        removeScheduledRun(payload?.sessionId || payload?.message?.sessionId, requestId, true);
        state.isAiBusy = !state.runQueue?.isIdle;
        state.activeRunSessionId = state.runQueue?.active?.sessionId || null;
        state.activeRunId = null;
        state.activeRunMessageId = null;
        if (!belongsToActiveSession(payload)) {
          if (payload.session) {
            const index = state.sessions.findIndex(session => session.id === payload.session.id);
            if (index < 0) state.sessions.push(payload.session);
            else state.sessions[index] = payload.session;
          }
          renderSessions();
          renderStatus();
          break;
        }
        state.isRunning = false;
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        state.runStatus = payload.runStatus || null;
        state.runDetail = payload.runDetail || null;
        persistMeasuredContext();
        upsertLiveMessage(payload.message, { role: "assistant" });
        // A terminal chat event ends the only active model run. Clear every
        // transient per-message status so a previously rendered card cannot
        // retain (or regain) a stale "Denkt nach" indicator.
        state.messageRunStatus.clear();
        if (payload.session) {
          const sessionIndex = state.sessions.findIndex(item => item.id === payload.session.id);
          if (sessionIndex >= 0) state.sessions[sessionIndex] = payload.session;
          else state.sessions.push(payload.session);
          if (payload.session.id === state.activeSessionId) {
            state.persistentExtensionActionId = normalizeExtensionActionId(payload.session.persistentExtensionActionId)
              || extensionActionIdForToolAction(payload.session.persistentToolAction);
            state.persistentToolAction = toolActionForExtensionActionId(state.persistentExtensionActionId)
              || payload.session.persistentToolAction || null;
            if (!state.selectedToolAction || persistentToolActions.has(state.selectedToolAction)) {
              state.selectedExtensionActionId = state.persistentExtensionActionId;
              selectToolAction(state.persistentToolAction, false);
            }
          }
        }
        // that session state before choosing the completed one-shot fallback.
        clearCompletedOneShotToolAction();
        renderSessions();
        renderMessages(false);
        renderStatus();
        // Native incremental playback already owns the spoken prefixes and final
        // remainder. Never submit the whole completed answer for a second read.
        state.voicePlaybackPending = false;
        syncVoiceCaptureSuspension();
        if (type === "chat.failed") showToast(payload.error || "Die Antwort ist fehlgeschlagen.", true);
        else setTimeout(() => {
          if (!state.isRunning) {
            state.runStatus = null;
            state.runDetail = null;
            renderStatus();
          }
        }, 1800);
        break;
      case "session.grouped": {
        // A late grouping result only refreshes the sidebar. It must not replace
        // the active session, its measured context, draft, messages or scroll position.
        // Mode switches can overtake an earlier grouping request. Ignore that
        // stale response so General and Coding never replace each other's sidebar.
        const groupedMode = normalizeChatMode(payload?.chatMode ?? payload?.selectedChatMode);
        if (groupedMode && groupedMode !== state.chatMode) break;
        state.sessions = Array.isArray(payload.sessions) ? payload.sessions : state.sessions;
        state.sessionGroups = Array.isArray(payload.sessionGroups) ? payload.sessionGroups : state.sessionGroups;
        renderSessions();
        renderStatus();
        break;
      }
      case "session.changed":
      case "workflow.changed":
        applySnapshot(payload);
        break;
      case "document.changed":
        applySnapshot(payload);
        resumePendingCaptureRequest();
        break;
      case "document.import.started":
        state.pendingDocumentImports = Array.isArray(payload?.files) ? payload.files.map(String) : [];
        renderContext();
        break;
      case "document.import.progress":
        state.pendingDocumentImports = Array.isArray(payload?.remaining) ? payload.remaining.map(String) : [];
        renderContext();
        break;
      case "document.import.completed":
        state.pendingDocumentImports = [];
        renderContext();
        break;
      case "capture.required": {
        if (state.pendingChatSend?.requestId === requestId) state.pendingChatSend = null;
        const action = String(payload?.action || "");
        if (!["audioAnalysis", "videoAnalysis", "imageAnalysis"].includes(action)) break;
        state.waitingForCapture = true;
        selectToolAction(action, false);
        renderStatus();
        void beginMediaCapture(action);
        break;
      }
      case "capture.cancelled": {
        if (state.pendingChatSend?.requestId === requestId) state.pendingChatSend = null;
        const action = String(payload?.action || "");
        if (state.pendingCaptureRequest?.prompt && !elements.prompt.value.trim()
          && String(state.pendingCaptureRequest.sessionId) === String(state.activeSessionId)) {
          setPromptValue(state.pendingCaptureRequest.prompt);
          scheduleDraftSave();
        }
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        if (state.selectedToolAction === action) selectToolAction(null, false);
        renderStatus();
        break;
      }
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
      case "memory.snapshot":
      case "memory.changed":
        applyProjectMemorySnapshot(payload);
        break;
      case "research.snapshot":
        if (payload?.sessionId && String(payload.sessionId) !== String(state.activeSessionId || "")) break;
        {
        const incomingRevision = Number(payload?.detail?.project?.revision || 0);
        const currentRevision = Number(state.scientificResearch?.revision || 0);
        if (payload?.detail && incomingRevision < currentRevision) break;
        state.scientificResearch = {
          available: payload?.available !== false,
          projects: Array.isArray(payload?.projects) ? payload.projects : [],
          selectedProjectId: payload?.selectedProjectId || null,
          detail: payload?.detail || null,
          disabledReason: payload?.disabledReason || null,
          revision: Math.max(currentRevision, incomingRevision)
        };
        state.scienceRequestedProjectId = null;
        renderScientificResearch();
        const candidate = !payload?.detail && (payload?.selectedProjectId
          || (Array.isArray(payload?.projects) ? payload.projects[0]?.id : null));
        if (candidate) openScienceProject(candidate);
        break;
        }
      case "research.exported":
        state.scienceExporting = false;
        renderScienceWorkbench();
        showToast(`Forschungspaket erstellt: ${payload?.directory || "Workspace"}`);
        break;
      case "action.completed":
        {
          const descriptor = availableActionDescriptors().find(item => item.actionId === payload?.actionId);
          const extensionActionId = normalizeExtensionActionId(payload?.extensionActionId || payload?.actionId)
            || extensionActionIdForToolAction(payload?.toolAction);
          const enabled = payload?.enabled !== false;
          if (descriptor && isSelectableAction(descriptor)) {
            if (enabled) state.activeActionIds.add(descriptor.actionId);
            else state.activeActionIds.delete(descriptor.actionId);
          }
          const toolToggle = String(payload?.toolToggle || descriptor?.toolToggle || "");
          const toolAction = toolActionForExtensionActionId(extensionActionId)
            || normalizeToolAction(payload?.toolAction || descriptor?.toolAction);
          if (toolToggle === "deepResearch") selectDeepResearch(enabled, false);
          if (descriptor && isSelectableAction(descriptor) && toolToggle !== "deepResearch") {
            if (persistentExtensionActionIds.has(extensionActionId)) {
              state.persistentExtensionActionId = enabled ? extensionActionId : null;
              state.persistentToolAction = enabled ? toolAction : null;
            }
            if (enabled) {
              state.selectedExtensionActionId = extensionActionId;
              state.selectedToolAction = toolAction;
            } else if (state.selectedExtensionActionId === extensionActionId
              || toolAction && state.selectedToolAction === toolAction) {
              state.selectedExtensionActionId = state.persistentExtensionActionId;
              state.selectedToolAction = state.persistentToolAction;
            }
            renderContext();
          }
          if (!enabled && extensionActionId === "builtin.coding/plan-mode" && state.pendingPlanImplementation) {
            const implementationPrompt = state.pendingPlanImplementation;
            state.pendingPlanImplementation = null;
            setPromptValue(implementationPrompt);
            void submitPrompt();
          }
          const captureAction = String(payload?.captureAction || descriptor?.captureAction || "");
          if (enabled && ["audioAnalysis", "videoAnalysis", "imageAnalysis"].includes(captureAction)) {
            void beginMediaCapture(captureAction);
          }
        }
        break;
      case "workflow.draft":
        elements.overlay.hidden = false;
        showWorkflowEditor(payload.workflow || null);
        break;
      case "status.changed": {
        const statusMessageId = payload?.messageId ? String(payload.messageId) : null;
        const statusMessage = statusMessageId
          ? state.messages.find(message => String(message.id) === statusMessageId)
          : null;
        const statusSessionMatches = !payload?.sessionId
          || String(payload.sessionId) === String(state.activeSessionId || "");
        const acceptsRunStatus = statusSessionMatches && (!statusMessageId
          || (state.isRunning && !isTerminalMessageStatus(statusMessage?.status)));

        if (acceptsRunStatus) {
          if (payload.runId) {
            state.activeRunId = payload.runId;
            state.activeRunSessionId = state.activeSessionId;
            if (payload.messageId) state.activeRunMessageId = payload.messageId;
          }
          globalThis.goRunSteering?.observeRun(state);
          recordCodingActivity(payload);
          // Reasoning packets update only their card. They must not alternate
          // with model progress or replace token details with an internal name.
          if (["assistant.reasoning", "assistant.steering"].includes(payload.toolStep?.tool)) {
            renderMessages(false);
            break;
          }
          if (payload.model) state.model = payload.model;
          if (Number.isFinite(payload.contextUsed)) {
            state.contextUsed = payload.contextUsed;
            state.contextSource = "measured";
            state.contextMeasurementMessageId = statusMessageId || state.messages.at(-1)?.id || null;
          }
          if (Number.isFinite(payload.contextLimit) && payload.contextLimit > 0) state.contextLimit = payload.contextLimit;
          if (typeof payload.contextWasTruncated === "boolean") state.contextWasTruncated = payload.contextWasTruncated;
          if (Object.hasOwn(payload, "loadedFiles")) state.loadedFiles = payload.loadedFiles;
          persistMeasuredContext();
          state.runStatus = payload.runStatus || state.runStatus;
          state.runDetail = payload.runDetail ?? state.runDetail;
          if (statusMessageId) state.messageRunStatus.set(statusMessageId, {
            status: payload.runStatus || "Denkt nach",
            detail: payload.runDetail || null,
            model: payload.model || state.model || null
          });
        } else if (statusMessageId) {
          state.messageRunStatus.delete(statusMessageId);
        }
        renderContext();
        renderMessages(false);
        renderStatus();
        break;
      }
      case "speech.status":
        state.speechStatus = {
          active: Boolean(payload?.active),
          status: payload?.status || null,
          detail: payload?.detail || null,
          model: payload?.model || null,
          directionModel: payload?.directionModel || null,
          error: payload?.error || null,
          cacheHit: Boolean(payload?.cacheHit)
        };
        if (!state.speechStatus.active) {
          state.speechProgress = {
            sessionId: null,
            sourceMessageId: null,
            sourceKind: null,
            playbackId: null,
            eventSequence: 0,
            sourceUnits: [],
            activeSourceUnitIds: [],
            state: null
          };
          clearSpeechHighlight();
          clearCompletedOneShotToolAction();
          state.voicePlaybackPending = false;
          syncVoiceCaptureSuspension();
        }
        renderSpeechStatus();
        renderStatus();
        if (state.speechStatus.error) showToast(state.speechStatus.error, true);
        break;
      case "speech.progress":
        updateSpeechProgress(payload);
        break;
      case "caption.changed":
        state.liveCaption = payload || state.liveCaption;
        renderLiveCaption();
        break;
      case "microphone.changed":
        const wasSpeaking = Boolean(state.microphone?.isSpeaking);
        state.microphone = payload || state.microphone;
        if (payload?.error && !payload?.isRecording) state.voiceTurn = null;
        if (state.voiceCaptureFeedbackAction && payload?.isSpeaking) {
          state.voiceCaptureFeedbackStarted = true;
        }
        if (state.voiceCaptureFeedbackAction
          && !payload?.isSpeaking
          && ((state.voiceCaptureFeedbackStarted && wasSpeaking) || payload?.error)) {
          const action = state.voiceCaptureFeedbackAction;
          state.voiceCaptureFeedbackAction = null;
          state.voiceCaptureFeedbackStarted = false;
          void beginMediaCapture(action, true);
        }
        if (payload?.isSpeaking || payload?.error || !payload?.isRecording) {
          state.voicePlaybackPending = false;
        }
        renderMicrophone();
        renderSpeechStatus();
        renderStatus();
        syncVoiceCaptureSuspension();
        // The audio service emits progress independently. Rebuilding the whole
        // message DOM for every unchanged speaking snapshot briefly reapplied
        // the previous sentence and caused visible highlight jumps.
        if (!payload?.isSpeaking || !wasSpeaking || payload?.error) {
          renderMessages(false);
        }
        if (!payload?.isRecording
          && !payload?.isBusy
          && !payload?.error
          && !state.voiceStarting
          && globalThis.goVoiceCapture?.isActive) {
          globalThis.goVoiceCapture.stop(false);
          state.voiceTurn = null;
          renderMessages(false);
        }
        if (payload?.error) showToast(payload.error, true);
        break;
      case "microphone.transcript": {
        if (payload?.clientSessionId
          && String(payload.clientSessionId) !== String(state.activeSessionId || "")) {
          break;
        }
        const text = String(payload?.text || "").trim();
        if (payload?.isFinal && payload?.stopVoice) {
          discardVoiceDictation(payload.turnId);
          void stopVoiceControl(false);
          break;
        }
        if (payload?.isFinal && text && finishActiveMediaCaptureFromVoice(text)) {
          discardVoiceDictation(payload.turnId);
          break;
        }
        if (payload?.isFinal && payload?.sendPrompt) {
          discardVoiceDictation(payload.turnId);
          void submitPrompt();
          break;
        }
        if (payload?.isFinal && (payload?.noise || payload?.control)) {
          discardVoiceDictation(payload.turnId);
          break;
        }
        if (text && payload?.dictation !== false) {
          updateVoiceDictation(
            payload.turnId,
            text,
            Boolean(payload.isFinal),
            payload.revision,
            payload.stableText,
            payload.provisionalText);
        }
        break;
      }
      case "artifact.previewReady": {
        const artifactId = String(payload?.artifactId || "");
        state.artifactPreviewPending.delete(artifactId);
        const previewUrl = resourceUrl(payload?.url);
        const posterUrl = resourceUrl(payload?.posterUrl);
        if (artifactId && previewUrl) state.artifactPreviewUrls.set(artifactId, {
          url: previewUrl,
          posterUrl: posterUrl || null
        });
        for (const card of document.querySelectorAll(".artifact-card")) {
          if (card.dataset.artifactId !== artifactId) continue;
          const image = card.querySelector("img");
          const audio = card.querySelector("audio");
          const video = card.querySelector("video");
          if (image && previewUrl) {
            image.addEventListener("load", () => card.classList.remove("artifact-card--failed"), { once: true });
            image.src = previewUrl;
          }
          if (audio && previewUrl) {
            audio.src = previewUrl;
            audio.load();
          }
          if (video && previewUrl) {
            video.src = previewUrl;
            if (posterUrl) video.poster = posterUrl;
            video.load();
          }
        }
        break;
      }
      case "screenClip.changed":
        state.screenClip = payload || state.screenClip;
        renderScreenClip();
        if (payload?.error) showToast(payload.error, true);
        break;
      case "audioCapture.changed":
        state.audioCapture = payload || state.audioCapture;
        renderContext();
        if (payload?.error) showToast(payload.error, true);
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
        if (state.scienceExporting) {
          state.scienceExporting = false;
          if (state.chatMode === "claudescience") {
            state.scientificResearch = { ...state.scientificResearch, disabledReason: payload.message || "Die Forschungsaktion ist fehlgeschlagen." };
            renderScienceWorkbench();
          }
        }
        if (state.pendingChatSend?.requestId === requestId) {
          const pending = state.pendingChatSend;
          state.pendingChatSend = null;
          state.pendingCaptureRequest = null;
          if (String(state.activeSessionId) === String(pending.sessionId) && !elements.prompt.value.trim()) {
            setPromptValue(pending.prompt);
            scheduleDraftSave();
          }
          renderStatus();
          showToast(payload.message || "Der Auftrag konnte nicht gestartet werden. Deine Eingabe bleibt erhalten.", true);
          break;
        }
        if (globalThis.goRunSteering?.ownsRequest(requestId, state)) {
          showToast(payload.message || "Umlenken fehlgeschlagen. Der Text bleibt im Eingabefeld.", true);
          break;
        }
        state.artifactPreviewPending.clear();
        if (state.voiceStarting) {
          state.voiceStarting = false;
          globalThis.goVoiceCapture?.stop(false);
          renderMicrophone();
        }
        state.voicePlaybackPending = false;
        state.voiceLevel = 0;
        state.voiceFrequency = 0;
        state.voiceDominantHz = 0;
        if (state.waitingForCapture && state.pendingCaptureRequest?.prompt) {
          setPromptValue(state.pendingCaptureRequest.prompt);
          scheduleDraftSave();
        }
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        syncVoiceCaptureSuspension();
        renderVoiceMeter();
        showToast(payload.message || "Unbekannter Fehler", true);
        break;
      default:
        break;
    }
  }

  restoreSessionsCollapsed();
  renderModeSwitcher();
  renderActionMenu();

  elements.modeSwitcherButton.addEventListener("click", event => {
    event.stopPropagation();
    setToolsMenuOpen(false);
    setModeMenuOpen(elements.modeSwitcherMenu.hidden);
  });
  elements.modeSwitcherMenu.addEventListener("click", event => event.stopPropagation());
  for (const option of elements.modeSwitcherMenu.querySelectorAll("[data-chat-mode]")) {
    option.addEventListener("click", () => switchChatMode(option.dataset.chatMode));
  }
  elements.scienceChatToggle.addEventListener("click", () => {
    state.scienceChatVisible = !state.scienceChatVisible;
    try { globalThis.localStorage.setItem("assistant.science.chat-visible.v1", state.scienceChatVisible ? "1" : "0"); }
    catch { /* Layout preference is optional. */ }
    renderScienceWorkbench();
  });
  elements.scienceExport.addEventListener("click", () => {
    const projectId = state.scientificResearch?.selectedProjectId || state.scientificResearch?.detail?.project?.id;
    if (!projectId || state.scienceExporting) return;
    state.scienceExporting = true;
    renderScienceWorkbench();
    post("research.export", { sessionId: state.activeSessionId, projectId,
      expectedRevision: state.scientificResearch?.detail?.project?.revision || 0 });
  });
  for (const tab of elements.scienceWorkbench.querySelectorAll("[data-science-view]")) {
    tab.addEventListener("click", () => {
      state.scienceView = tab.dataset.scienceView || "overview";
      try { globalThis.localStorage.setItem(`assistant.science.view.v1:${state.activeSessionId}`, state.scienceView); }
      catch { /* Session view is a convenience preference. */ }
      if (state.scienceView === "research" || state.scienceView === "review") post("research.list", { sessionId: state.activeSessionId });
      renderScienceWorkbench();
    });
  }
  elements.toggleSessions.addEventListener("click", () => {
    const collapsed = !elements.appShell.classList.contains("sessions-collapsed");
    setSessionsCollapsed(collapsed, true);
    post("ui.sessionPane", { isOpen: !collapsed });
  });
  elements.newSession.addEventListener("click", createWorkspaceProject);
  elements.clearSessions.addEventListener("click", () => {
    const hasVisibleSessions = state.sessions.some(session => !session.chatMode
      || String(session.chatMode).toLocaleLowerCase() === String(state.chatMode || "general").toLocaleLowerCase());
    const label = state.chatMode === "claudescience" ? "Claude Science" : state.chatMode === "coding" ? "Codex" : "ChatGPT";
    if (hasVisibleSessions && globalThis.confirm(
      `Alle Sitzungen der ${label}-Ansicht und ihre Nachrichten endgültig löschen?`)) {
      flushDraft();
      post("session.clear", { chatMode: state.chatMode });
    }
  });
  elements.sessionSearch.addEventListener("input", renderSessions);
  byId("open-sessions").addEventListener("click", () => document.body.classList.add("sessions-open"));
  byId("collapse-sessions").addEventListener("click", () => document.body.classList.remove("sessions-open"));
  byId("close-workspace-picker").addEventListener("click", closeWorkspacePicker);
  byId("cancel-workspace-picker").addEventListener("click", closeWorkspacePicker);
  elements.workspaceOverlay.addEventListener("click", event => {
    if (event.target === elements.workspaceOverlay) closeWorkspacePicker();
  });
  elements.workspacePickerForm.addEventListener("submit", event => {
    event.preventDefault();
    const workspacePath = elements.workspacePath.value.trim();
    if (!workspacePath) {
      elements.workspacePath.focus();
      return;
    }
    post("session.projectCreate", { workspacePath, chatMode: state.chatMode });
    closeWorkspacePicker();
  });

  elements.prompt.addEventListener("input", () => {
    resizePrompt();
    renderStatus();
    scheduleDraftSave();
  });
  elements.prompt.addEventListener("paste", event => {
    const clipboard = event.clipboardData;
    if (!clipboard) return;
    const containsFiles = clipboard.files.length > 0
      || Array.from(clipboard.items || []).some(item => item.kind === "file");
    if (!containsFiles) return;
    event.preventDefault();
    if (ensureEditableContext()) {
      if (globalThis.goBridge.isLanBrowser) {
        const files = clipboard.files.length > 0
          ? clipboard.files
          : Array.from(clipboard.items || []).map(item => item.getAsFile()).filter(Boolean);
        void globalThis.goBridge.uploadFiles(files, state.activeSessionId);
      } else {
        post("document.paste", { sessionId: state.activeSessionId });
      }
    }
  });
  elements.prompt.addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      submitPrompt();
    }
  });
  elements.send.addEventListener("click", handleComposerAction);
  elements.composerSpeechPause.addEventListener("click", () => {
    if (!elements.composerSpeechPause.disabled) post("microphone.toggleSpeechPause", {});
  });
  elements.composerSpeechStop.addEventListener("click", () => {
    if (!elements.composerSpeechStop.disabled) post("microphone.stopSpeech", {});
  });
  elements.microphone.addEventListener("click", async () => {
    if (state.voiceStarting) return;
    const active = Boolean(globalThis.goVoiceCapture?.isActive || globalThis.goVoiceCapture?.isStarting);
    if (active) {
      await stopVoiceControl(true);
      return;
    }

    state.voiceStarting = true;
    renderMicrophone();
    renderMessages(true);
    try {
      const source = await globalThis.goVoiceCapture.start(state.activeSessionId);
      post("microphone.start", {
        deviceLabel: source.deviceLabel,
        sampleRate: source.sampleRate
      });
    } catch (error) {
      await globalThis.goVoiceCapture?.stop(false);
      showToast(microphoneErrorMessage(error), true);
    } finally {
      state.voiceStarting = false;
      renderMicrophone();
      renderMessages(false);
    }
  });
  globalThis.addEventListener("go:voice-level", event => {
    const detail = event.detail || {};
    state.voiceLevel = Number(detail.level) || 0;
    state.voiceFrequency = Number(detail.frequency) || 0;
    state.voiceDominantHz = Number(detail.dominantHz) || 0;
    renderVoiceMeter();
  });
  globalThis.addEventListener("go:voice-capture-ended", async () => {
    await stopVoiceControl(true);
    showToast("Die Mikrofonquelle wurde von Windows beendet.", true);
  });
  globalThis.addEventListener("beforeunload", () => {
    globalThis.goVoiceCapture?.stop(false);
  });
  elements.toolsButton.addEventListener("click", event => {
    event.stopPropagation();
    setModeMenuOpen(false);
    setToolsMenuOpen(elements.toolsMenu.hidden);
  });
  elements.toolsMenu.addEventListener("click", event => event.stopPropagation());
  elements.toolsMenu.addEventListener("keydown", event => {
    const items = [...elements.toolsMenu.querySelectorAll(".service-option")].filter(item => !item.disabled);
    if (!items.length) return;
    const current = items.indexOf(document.activeElement);
    let next = current;
    if (event.key === "ArrowDown") next = (current + 1 + items.length) % items.length;
    else if (event.key === "ArrowUp") next = (current - 1 + items.length) % items.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = items.length - 1;
    else return;
    event.preventDefault();
    items[next].focus();
  });
  document.addEventListener("pointerdown", event => {
    if (event.button === 2) captureReadFromContextTarget(event);
  }, { capture: true });
  document.addEventListener("contextmenu", captureReadFromContextTarget, { capture: true });
  document.addEventListener("click", () => {
    setToolsMenuOpen(false);
    setModeMenuOpen(false);
    closeAttachmentMenu();
  });

  byId("close-workflows").addEventListener("click", closeWorkflows);
  elements.overlay.addEventListener("click", event => {
    if (event.target === elements.overlay) closeWorkflows();
  });
  elements.workflowSearch.addEventListener("input", renderWorkflows);
  elements.newWorkflow.addEventListener("click", () => {
    showWorkflowEditor(null);
  });
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
  elements.cancelWorkflowEdit.addEventListener("click", () => {
    showWorkflowPreview(selectedWorkflowForDialog());
  });
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

  byId("close-memory").addEventListener("click", closeProjectMemory);
  elements.memoryOverlay.addEventListener("click", event => {
    if (event.target === elements.memoryOverlay) closeProjectMemory();
  });
  elements.memoryAutoCapture.addEventListener("change", () => {
    if (!state.projectMemory.available) return;
    post("memory.autoCapture", {
      sessionId: state.activeSessionId,
      projectKey: state.projectMemory.projectKey,
      projectId: state.projectMemory.projectId,
      enabled: elements.memoryAutoCapture.checked
    });
  });
  elements.newMemoryEntry.addEventListener("click", () => showMemoryEditor(null));
  elements.editMemoryEntry.addEventListener("click", () => showMemoryEditor(selectedMemoryEntry()));
  elements.cancelMemoryEdit.addEventListener("click", () => showMemoryPreview(selectedMemoryEntry()));
  elements.pinMemoryEntry.addEventListener("click", () => {
    const entry = selectedMemoryEntry();
    if (!entry) return;
    post("memory.pin", { entryId: entry.id, version: Number(entry.version), isPinned: !entry.isPinned });
  });
  elements.confirmMemoryEntry.addEventListener("click", () => {
    const entry = selectedMemoryEntry();
    if (!entry) return;
    post("memory.confirm", { entryId: entry.id, version: Number(entry.version) });
  });
  elements.deleteMemoryEntry.addEventListener("click", () => {
    const entry = selectedMemoryEntry();
    if (entry && globalThis.confirm("Diesen Gedächtniseintrag endgültig löschen?")) {
      post("memory.delete", { entryId: entry.id, version: Number(entry.version) });
    }
  });
  elements.memoryEditor.addEventListener("submit", event => {
    event.preventDefault();
    const content = elements.memoryEntryContent.value.trim();
    if (!content) {
      elements.memoryEntryContent.focus();
      return;
    }
    const entryId = elements.memoryEntryId.value || null;
    post(entryId ? "memory.update" : "memory.create", {
      sessionId: state.activeSessionId,
      entryId,
      version: Number(elements.memoryEntryVersion.value || 0),
      kind: elements.memoryEntryKind.value,
      content,
      source: "manual"
    });
  });

  globalThis.addEventListener("go:host-message", handleHostMessage);
  globalThis.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    if (elements.documents.querySelector(".attachment-menu:not([hidden])")) closeAttachmentMenu();
    else if (!elements.toolsMenu.hidden) setToolsMenuOpen(false);
    else if (!elements.modeSwitcherMenu.hidden) setModeMenuOpen(false);
    else if (!elements.workspaceOverlay.hidden) closeWorkspacePicker();
    else if (!elements.memoryOverlay.hidden) closeProjectMemory();
    else if (!elements.overlay.hidden) closeWorkflows();
  });
  globalThis.goCaptureDraft = () => ({ sessionId: state.activeSessionId, draft: elements.prompt.value });
  globalThis.goGetReadFromContextTarget = () => state.readFromContextTarget
    ? { ...state.readFromContextTarget }
    : null;
  globalThis.goFlushDraft = flushDraft;
  let preparedPdfBook = null;

  function pdfExportDate(value) {
    const date = value instanceof Date ? value : new Date(value || Date.now());
    if (Number.isNaN(date.getTime())) return "";
    return new Intl.DateTimeFormat("de-DE", {
      dateStyle: "long",
      timeStyle: "short"
    }).format(date);
  }

  function finishBookPdf() {
    preparedPdfBook?.remove();
    preparedPdfBook = null;
    document.documentElement.classList.remove("pdf-exporting");
    document.body.classList.remove("pdf-exporting");
  }

  function preparePdfMedia(source, clone) {
    const sourceImages = [...source.querySelectorAll("img")];
    [...clone.querySelectorAll("img")].forEach((image, index) => {
      const original = sourceImages[index];
      const resolvedSource = original?.currentSrc || original?.getAttribute("src") || image.getAttribute("src");
      if (resolvedSource) image.setAttribute("src", resolvedSource);
      image.setAttribute("loading", "eager");
      image.removeAttribute("decoding");
    });

    const sourceVideos = [...source.querySelectorAll("video")];
    [...clone.querySelectorAll("video")].forEach((video, index) => {
      const original = sourceVideos[index];
      const poster = original?.poster || original?.getAttribute("poster") || video.getAttribute("poster");
      const label = video.closest(".artifact-card")?.querySelector(".artifact-card__footer span")?.textContent
        || "Videoanhang";
      if (poster) {
        const image = document.createElement("img");
        image.className = "pdf-book__video-poster";
        image.src = poster;
        image.alt = label;
        video.replaceWith(image);
      } else {
        const placeholder = document.createElement("div");
        placeholder.className = "pdf-book__media-placeholder";
        placeholder.textContent = label;
        video.replaceWith(placeholder);
      }
    });

    [...clone.querySelectorAll("audio")].forEach(audio => {
      const label = audio.closest(".artifact-card")?.querySelector(".artifact-card__footer span")?.textContent
        || "Audioanhang";
      const placeholder = document.createElement("div");
      placeholder.className = "pdf-book__media-placeholder";
      placeholder.textContent = label;
      audio.replaceWith(placeholder);
    });
  }

  function preparePdfMessage(source) {
    const clone = source.cloneNode(true);
    clone.classList.add("pdf-book__message");
    clone.querySelector(".avatar")?.remove();
    clone.querySelector(".message-meta")?.remove();
    clone.querySelector(".message-footer")?.remove();
    clone.querySelectorAll(".coding-live-phase").forEach(phase => phase.remove());
    const sourceDisclosures = [...source.querySelectorAll(".coding-step__disclosure")];
    clone.querySelectorAll(".coding-step__disclosure").forEach((step, index) => {
      // cloneNode copies neither lazy factories nor toggle listeners. Populate
      // the export clone directly without opening or changing the live chat.
      const original = sourceDisclosures[index];
      if (!step.querySelector(".coding-step__content") && original?._codingCreateContent)
        step.append(original._codingCreateContent());
      step.open = true;
    });
    clone.querySelectorAll("button").forEach(button => button.remove());
    clone.querySelectorAll(".message-status-spinner").forEach(spinner => spinner.remove());
    clone.querySelectorAll(".stream-cursor").forEach(content => content.classList.remove("stream-cursor"));
    clone.querySelectorAll("[data-speech-source-active]").forEach(node => {
      node.removeAttribute("data-speech-source-active");
      node.removeAttribute("aria-current");
      node.classList.remove("speech-source-active--block");
    });
    preparePdfMedia(source, clone);

    const messageId = String(source.dataset.messageId || "");
    const message = state.messages.find(item => String(item.id) === messageId);
    const role = source.classList.contains("user") ? "user" : "assistant";
    const heading = document.createElement("div");
    heading.className = "pdf-book__message-heading";
    const timestamp = message ? timeLabel(message.createdAt || message.updatedAt) : "";
    heading.textContent = `${role === "user" ? "Du" : state.assistantTitle}${timestamp ? ` · ${timestamp}` : ""}`;
    clone.querySelector(".message-body")?.prepend(heading);
    return clone;
  }

  globalThis.goPrepareBookPdf = messageId => {
    finishBookPdf();
    const normalizedId = String(messageId || "");
    const sources = [...elements.messageList.querySelectorAll(":scope > .message")]
      .filter(message => !normalizedId || message.dataset.messageId === normalizedId);
    if (sources.length === 0) return false;

    const session = state.sessions.find(item => item.id === state.activeSessionId);
    const sessionTitle = String(session?.title || "Neue Sitzung").trim() || "Neue Sitzung";
    const book = document.createElement("article");
    book.className = `pdf-book ${normalizedId ? "pdf-book--message" : "pdf-book--chat"}`;
    book.lang = "de";
    book.setAttribute("aria-hidden", "true");

    const header = document.createElement("header");
    header.className = "pdf-book__header";
    const eyebrow = document.createElement("div");
    eyebrow.className = "pdf-book__eyebrow";
    eyebrow.textContent = String(state.assistantTitle || "AI Assistent").toLocaleUpperCase();
    const title = document.createElement("h1");
    title.textContent = normalizedId ? `Nachricht aus „${sessionTitle}“` : sessionTitle;
    const subtitle = document.createElement("p");
    subtitle.textContent = `${normalizedId ? "Einzelne Nachricht" : "Chatprotokoll"} · Exportiert am ${pdfExportDate(new Date())}`;
    header.append(eyebrow, title, subtitle);

    const content = document.createElement("section");
    content.className = "pdf-book__content";
    sources.forEach(source => content.append(preparePdfMessage(source)));

    const endMark = document.createElement("footer");
    endMark.className = "pdf-book__end-mark";
    endMark.textContent = "◆";
    book.append(header, content, endMark);
    document.body.append(book);
    preparedPdfBook = book;
    document.documentElement.classList.add("pdf-exporting");
    document.body.classList.add("pdf-exporting");
    return true;
  };
  globalThis.goPdfBookReady = () => {
    if (!preparedPdfBook) return true;
    const fontsReady = !document.fonts || document.fonts.status === "loaded";
    const imagesReady = [...preparedPdfBook.querySelectorAll("img")].every(image => image.complete);
    return fontsReady && imagesReady;
  };
  globalThis.goFinishBookPdf = finishBookPdf;
  globalThis.goPrepareMessagePdf = globalThis.goPrepareBookPdf;
  globalThis.goFinishMessagePdf = finishBookPdf;
  globalThis.addEventListener("pagehide", flushDraft);
  globalThis.addEventListener("beforeunload", flushDraft);
  globalThis.addEventListener("resize", schedulePromptResize);
  globalThis.visualViewport?.addEventListener("resize", schedulePromptResize);
  if (globalThis.ResizeObserver) {
    const promptLayoutObserver = new ResizeObserver(schedulePromptResize);
    for (const node of [elements.chatPane, elements.composerRegion, elements.prompt]) {
      promptLayoutObserver.observe(node);
    }
  }
  document.fonts?.ready.then(schedulePromptResize);
  schedulePromptResize();
  post("app.ready", {});
})();
