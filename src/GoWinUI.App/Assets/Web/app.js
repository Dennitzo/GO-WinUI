(function () {
  "use strict";

  const state = {
    sessions: [],
    messages: [],
    workflows: [],
    campaignDefinitions: [],
    codingCampaign: null,
    codingRun: null,
    conversationRevision: 0,
    conversationRefreshPending: false,
    codingWorkspaceOpen: false,
    codingWorkspaceSessionId: null,
    assistantMode: "general",
    workflowOverlayMode: "workflow",
    selectedCampaignDefinitionId: null,
    documents: [],
    attachments: [],
    documentGroupStatus: { total: 0, ready: 0, processing: 0, failed: 0, status: "ready" },
    pendingDocumentImports: [],
    activeSessionId: null,
    selectedWorkflowEditorId: null,
    isWorkflowEditing: false,
    pendingWorkflowTitle: null,
    isRunning: false,
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
    maximizedCodingPanelKind: null,
    maximizedCodingPanelScrollTop: 0,
    maximizedCodingPanelScrollLeft: 0,
    artifactPreviewUrls: new Map(),
    artifactPreviewPending: new Set(),
    selectedToolAction: null,
    persistentToolAction: null,
    pendingCaptureRequest: null,
    waitingForCapture: false,
    captureStopRequested: false,
    audioCaptureStopRequested: false,
    workspacePath: null,
    workspaceName: null,
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
  const persistentToolActions = new Set(["code", "bricsCad", "audiobook"]);
  const toolVisuals = Object.freeze({
    audioAnalysis: ["Audio analysieren", "M4 12h2m2-5 4 10 3-7 2 4h3"],
    imageAnalysis: ["Bild analysieren", "M4 5h16v14H4zM7 15l3-3 3 3 2-2 2 2"],
    imageGeneration: ["Bild erstellen", "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"],
    bricsCad: ["BricsCAD", "M4 18V6l8-3 8 3v12l-8 3zM12 3v18M4 6l8 4 8-4"],
    code: ["Coding", "M9 18l-6-6 6-6M15 6l6 6-6 6"],
    audiobook: ["Hörbuch erstellen", "M4 5c3-1 5-1 8 1v14c-3-2-5-2-8-1zM20 5c-3-1-5-1-8 1v14c3-2 5-2 8-1z"],
    translation: ["Übersetzen", "M4 5h10M9 3v2c0 5-2 8-5 10M6 9c2 3 4 5 8 7M15 9l5 12M18 9l-5 12M14 18h7"],
    videoAnalysis: ["Video analysieren", "M3 6h13v12H3zM16 10l5-3v10l-5-3z"],
    textToSpeech: ["Vorlesen", "M5 9v6h4l5 4V5L9 9zM17 9c1 1 1 5 0 6M19 6c3 3 3 9 0 12"],
    webSearch: ["Websuche", "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM16 16l5 5M4 11h14M11 4c3 3 3 11 0 14M11 4c-3 3-3 11 0 14"],
    youTubeSearch: ["YouTube", "M3 7c0-2 1-3 3-3h12c2 0 3 1 3 3v10c0 2-1 3-3 3H6c-2 0-3-1-3-3zM10 9l5 3-5 3z"],
    "screen.capture": ["Bild aufnehmen", "M4 7h4l2-2h4l2 2h4v12H4zM12 10a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"],
    "screenClip.toggle": ["Video aufnehmen", "M3 6h13v12H3zM16 10l5-3v10l-5-3z"],
    "liveCaption.start": ["Live-Untertitel", "M4 8h2M4 12h4M4 16h2M10 7v10M14 9v6M18 6v12M22 9v6"]
  });

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
    pinSession: byId("pin-session"),
    newSession: byId("new-session"),
    messageList: byId("message-list"),
    messageScroll: byId("message-scroll"),
    prompt: byId("prompt"),
    composerSpeechStatus: byId("composer-speech-status"),
    composerSpeechDetail: byId("composer-speech-detail"),
    composerSpeechPause: byId("composer-speech-pause"),
    composerSpeechPauseIcon: byId("composer-speech-pause-icon"),
    composerSpeechStop: byId("composer-speech-stop"),
    send: byId("send"),
    stop: byId("stop"),
    reasoning: byId("reasoning"),
    toolsButton: byId("tools-button"),
    toolsMenu: byId("tools-menu"),
    workspaceButton: byId("workspace-button"),
    workflowsButton: byId("open-workflows"),
    workflowsButtonLabel: byId("workflows-button-label"),
    context: byId("context-meter"),
    contextLabel: byId("context-label"),
    contextStrip: byId("context-strip"),
    activeTools: byId("active-tool-chips"),
    documents: byId("document-chips"),
    codingWorkspace: byId("coding-workspace"),
    codingWorkspaceToggle: byId("coding-workspace-toggle"),
    codingWorkspaceStatus: byId("coding-workspace-status"),
    codingWorkspaceClose: byId("coding-workspace-close"),
    codingWorkspaceContent: byId("coding-workspace-content"),
    liveCaption: byId("live-caption"),
    liveCaptionTitle: byId("live-caption-title"),
    liveCaptionStatus: byId("live-caption-status"),
    liveCaptionTranscript: byId("live-caption-transcript"),
    liveCaptionError: byId("live-caption-error"),
    stopLiveCaption: byId("stop-live-caption"),
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
    newWorkflow: byId("new-workflow")
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
      item.className = `session-item${session.id === state.activeSessionId ? " active" : ""}${session.isPinned ? " pinned" : ""}`;
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

  function renderMessages(scrollToEnd) {
    const previousScrollTop = elements.messageScroll.scrollTop;
    const previousScrollLeft = elements.messageScroll.scrollLeft;
    captureMaximizedCodingPanelScroll();
    const preserveForSpeech = Boolean(state.speechStatus?.active)
      || ["buffering", "playing", "paused"].includes(String(state.speechProgress?.state || ""));
    elements.messageList.replaceChildren();
    for (const message of state.messages) {
      elements.messageList.append(createMessage(message));
    }
    if (!state.messages.length && state.activeSessionId) {
      const empty = document.createElement("div");
      empty.className = "chat-empty-state";
      empty.textContent = "Wobei kann ich dich in der TGA-Planung unterstützen?";
      elements.messageList.append(empty);
    }
    applySpeechHighlight();
    if (scrollToEnd && !preserveForSpeech) {
      requestAnimationFrame(() => { elements.messageScroll.scrollTop = elements.messageScroll.scrollHeight; });
    } else {
      elements.messageScroll.scrollTop = previousScrollTop;
      elements.messageScroll.scrollLeft = previousScrollLeft;
    }
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
  const codingPanelMaximizeIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3H3v5M16 3h5v5M21 16v5h-5M3 16v5h5"/></svg>';
  const codingPanelRestoreIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 4v5H4M15 4v5h5M20 15h-5v5M4 15h5v5"/></svg>';

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

  function captureMaximizedCodingPanelScroll() {
    const panel = document.querySelector(".coding-panel--maximized");
    if (!panel) return;
    state.maximizedCodingPanelScrollTop = panel.scrollTop;
    state.maximizedCodingPanelScrollLeft = panel.scrollLeft;
  }

  function maximizedCodingPanelUsesAutoScroll(panelKind) {
    return panelKind === "trace" || panelKind === "powershell";
  }

  function restoreMaximizedCodingPanelScroll(panel, panelKind) {
    requestAnimationFrame(() => {
      if (!panel.classList.contains("coding-panel--maximized")
        || state.maximizedCodingPanelKind !== panelKind) return;
      panel.scrollTop = maximizedCodingPanelUsesAutoScroll(panelKind)
        ? panel.scrollHeight
        : state.maximizedCodingPanelScrollTop;
      panel.scrollLeft = state.maximizedCodingPanelScrollLeft;
      state.maximizedCodingPanelScrollTop = panel.scrollTop;
      state.maximizedCodingPanelScrollLeft = panel.scrollLeft;
    });
  }

  function clearCodingPanelMaximizedState() {
    state.maximizedCodingPanelKind = null;
    state.maximizedCodingPanelScrollTop = 0;
    state.maximizedCodingPanelScrollLeft = 0;
    document.body.classList.remove("coding-panel-maximized");
  }

  function setCodingPanelMaximized(panel, button, label, maximized) {
    const panelKind = panel.dataset.codingPanelKind || label;
    for (const otherPanel of document.querySelectorAll(".coding-panel--maximized")) {
      if (otherPanel === panel) continue;
      otherPanel.classList.remove("coding-panel--maximized");
      const otherButton = otherPanel.querySelector(".coding-panel-maximize");
      if (otherButton) {
        otherButton.innerHTML = codingPanelMaximizeIcon;
        otherButton.title = `${otherButton.dataset.panelLabel || "Modul"} maximieren`;
        otherButton.setAttribute("aria-label", otherButton.title);
        otherButton.setAttribute("aria-pressed", "false");
      }
    }
    if (maximized && state.maximizedCodingPanelKind !== panelKind) {
      state.maximizedCodingPanelScrollTop = 0;
      state.maximizedCodingPanelScrollLeft = 0;
    }
    if (maximized) state.maximizedCodingPanelKind = panelKind;
    else if (state.maximizedCodingPanelKind === panelKind) clearCodingPanelMaximizedState();
    panel.classList.toggle("coding-panel--maximized", maximized);
    document.body.classList.toggle("coding-panel-maximized", maximized);
    button.innerHTML = maximized ? codingPanelRestoreIcon : codingPanelMaximizeIcon;
    button.title = maximized ? `${label} wiederherstellen` : `${label} maximieren`;
    button.setAttribute("aria-label", button.title);
    button.setAttribute("aria-pressed", String(maximized));
    if (maximized) {
      restoreMaximizedCodingPanelScroll(panel, panelKind);
    }
  }

  function attachCodingPanelMaximize(panel, header, label, panelKind) {
    panel.dataset.codingPanelKind = panelKind;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "coding-panel-maximize";
    button.dataset.panelLabel = label;
    button.innerHTML = codingPanelMaximizeIcon;
    button.title = `${label} maximieren`;
    button.setAttribute("aria-label", button.title);
    button.setAttribute("aria-pressed", "false");
    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      setCodingPanelMaximized(panel, button, label, !panel.classList.contains("coding-panel--maximized"));
    });
    header.append(button);
    if (state.maximizedCodingPanelKind === panelKind) {
      setCodingPanelMaximized(panel, button, label, true);
    }
  }

  function createCodeDiff(message, force = false) {
    const diff = String(message.codeDiff || "").replace(/\r\n?/g, "\n");
    const hasDiff = Boolean(diff.trim());
    if (!force && !hasDiff) return null;

    const lines = hasDiff ? diff.split("\n") : [];
    const fileCount = lines.filter(line => line.startsWith("diff --git ")).length;
    const addedLines = lines.filter(line => line.startsWith("+") && !line.startsWith("+++")).length;
    const deletedLines = lines.filter(line => line.startsWith("-") && !line.startsWith("---")).length;
    const details = document.createElement("section");
    details.className = "message-code-diff coding-workspace-module";

    const summary = document.createElement("header");
    summary.className = "message-code-diff__summary";
    const icon = createToolIcon("M16 18l6-6-6-6M8 6l-6 6 6 6M14 4l-4 16");
    icon.classList.add("message-code-diff__icon");
    const title = document.createElement("span");
    title.className = "message-code-diff__title";
    title.textContent = "Codeänderungen";
    const stats = document.createElement("span");
    stats.className = "message-code-diff__stats";
    stats.textContent = hasDiff
      ? `${fileCount} ${fileCount === 1 ? "Datei" : "Dateien"} · +${addedLines} · −${deletedLines}`
      : "Bereit";
    summary.append(icon, title, stats);
    attachCodingPanelMaximize(details, summary, "Codeänderungen", "diff");
    details.append(summary);

    if (hasDiff) {
      const toolbar = document.createElement("div");
      toolbar.className = "message-code-diff__toolbar";
      const copy = createMessageIconAction("Git-Diff kopieren", messageCopyIcon, button => {
        post("message.copy", { text: diff });
        flashMessageAction(button, messageCopyIcon, "Git-Diff kopieren");
      });
      toolbar.append(copy);
      details.append(toolbar);
    }

    const code = document.createElement("code");
    const maximumRenderedLines = 5000;
    for (const line of lines.slice(0, maximumRenderedLines)) {
      const row = document.createElement("span");
      row.className = line.startsWith("diff --git ") || line.startsWith("index ")
        || line.startsWith("--- ") || line.startsWith("+++ ")
        ? "diff-line diff-line--header"
        : line.startsWith("@@")
          ? "diff-line diff-line--hunk"
          : line.startsWith("+")
            ? "diff-line diff-line--added"
            : line.startsWith("-")
              ? "diff-line diff-line--deleted"
              : "diff-line";
      row.textContent = `${line}\n`;
      code.append(row);
    }
    if (!hasDiff) {
      const empty = document.createElement("span");
      empty.className = "diff-line diff-line--notice";
      empty.textContent = "Noch keine Codeänderungen.\n";
      code.append(empty);
    }
    if (lines.length > maximumRenderedLines) {
      const omitted = document.createElement("span");
      omitted.className = "diff-line diff-line--notice";
      omitted.textContent = `[${lines.length - maximumRenderedLines} weitere Zeilen – vollständigen Diff über die Kopierfunktion übernehmen]\n`;
      code.append(omitted);
    }
    const pre = document.createElement("pre");
    pre.className = "message-code-diff__content";
    pre.append(code);
    details.append(pre);
    return details;
  }

  function createCodingTrace(message, force = false) {
    const entries = Array.isArray(message.codingTrace) ? message.codingTrace : [];
    const visibleEntries = entries.filter(entry => ![
      "Coding-Modell wird geladen",
      "Coding-Modell geladen"
    ].includes(String(entry?.title || "")));
    if (!force && !visibleEntries.length) return null;

    const details = document.createElement("section");
    details.className = "message-coding-trace coding-workspace-module";

    const last = visibleEntries[visibleEntries.length - 1] || {};
    const summary = document.createElement("header");
    summary.className = "message-coding-trace__summary";
    const icon = createToolIcon(toolVisuals.code[1]);
    icon.classList.add("message-coding-trace__icon");
    const title = document.createElement("span");
    title.className = "message-coding-trace__title";
    title.textContent = "Coding-Ablauf";
    const current = document.createElement("span");
    current.className = `message-coding-trace__current trace-status--${String(last.status || "running").toLowerCase()}`;
    current.textContent = visibleEntries.length
      ? `${visibleEntries.length} Schritte · ${last.title || "Wird vorbereitet"}`
      : "Bereit · Warte auf Agentenaktion";
    summary.append(icon, title, current);
    attachCodingPanelMaximize(details, summary, "Coding-Ablauf", "trace");
    details.append(summary);

    const list = document.createElement("ol");
    list.className = "message-coding-trace__list";
    for (const entry of visibleEntries) {
      const row = document.createElement("li");
      row.className = `message-coding-trace__entry trace-status--${String(entry.status || "running").toLowerCase()}`;
      const marker = document.createElement("span");
      marker.className = "message-coding-trace__marker";
      marker.setAttribute("aria-hidden", "true");
      const text = document.createElement("div");
      text.className = "message-coding-trace__text";
      const heading = document.createElement("div");
      heading.className = "message-coding-trace__heading";
      const time = entry.timestamp ? timeLabel(entry.timestamp) : "";
      heading.textContent = [time, entry.title].filter(Boolean).join(" · ");
      const metadata = document.createElement("div");
      metadata.className = "message-coding-trace__metadata";
      const duration = Number.isFinite(entry.durationMilliseconds)
        ? `${Math.max(0, entry.durationMilliseconds)} ms`
        : null;
      metadata.textContent = [entry.target, entry.tool, duration, entry.detail]
        .filter(Boolean)
        .join(" · ");
      text.append(heading);
      if (metadata.textContent) text.append(metadata);
      row.append(marker, text);
      list.append(row);
    }
    if (!visibleEntries.length) {
      const row = document.createElement("li");
      row.className = "message-coding-trace__entry trace-status--running";
      const marker = document.createElement("span");
      marker.className = "message-coding-trace__marker";
      marker.setAttribute("aria-hidden", "true");
      const text = document.createElement("div");
      text.className = "message-coding-trace__text";
      text.textContent = "Warte auf die erste Agentenaktion …";
      row.append(marker, text);
      list.append(row);
    }
    details.append(list);
    requestAnimationFrame(() => {
      list.scrollTop = list.scrollHeight;
    });
    return details;
  }

  function createPowerShellPanel(message, force = false) {
    const entries = Array.isArray(message.codingTrace) ? message.codingTrace : [];
    if (!force && !entries.length) return null;
    const latestByOperation = new Map();
    for (const entry of entries) {
      const processConsole = entry?.processConsole;
      const operationId = String(processConsole?.operationId || "");
      if (operationId) latestByOperation.set(operationId, { ...processConsole, sequence: Number(entry.sequence) || 0 });
    }
    const consoles = [...latestByOperation.values()].sort((left, right) => left.sequence - right.sequence);

    const details = document.createElement("section");
    details.className = "message-coding-powershell coding-workspace-module";

    const latest = consoles.length ? consoles[consoles.length - 1] : null;
    const summary = document.createElement("header");
    summary.className = "message-coding-powershell__summary";
    const icon = createToolIcon("M4 5h16v14H4zM7 9l3 3-3 3M12 15h5");
    icon.classList.add("message-coding-powershell__icon");
    const title = document.createElement("span");
    title.className = "message-coding-powershell__title";
    title.textContent = "PowerShell";
    const current = document.createElement("span");
    current.className = `message-coding-powershell__current console-status--${String(latest?.status || "idle").toLowerCase()}`;
    current.textContent = !latest
      ? "Bereit"
      : latest.status === "running"
        ? "Befehl läuft"
        : latest.exitCode == null
          ? "Beendet"
          : `Exit-Code ${latest.exitCode}`;
    summary.append(icon, title, current);
    attachCodingPanelMaximize(details, summary, "PowerShell", "powershell");
    details.append(summary);

    const body = document.createElement("div");
    body.className = "message-coding-powershell__body";
    if (!consoles.length) {
      const idle = document.createElement("div");
      idle.className = "message-coding-powershell__idle";
      const idlePrompt = document.createElement("span");
      idlePrompt.className = "message-coding-powershell__prompt";
      idlePrompt.textContent = "PS>";
      const idleText = document.createElement("span");
      idleText.textContent = "Warte auf einen Terminalbefehl …";
      idle.append(idlePrompt, idleText);
      body.append(idle);
    }
    for (const item of consoles) {
      const section = document.createElement("section");
      section.className = `message-coding-powershell__run console-status--${String(item.status || "running").toLowerCase()}`;
      const command = document.createElement("div");
      command.className = "message-coding-powershell__command";
      const prompt = document.createElement("span");
      prompt.className = "message-coding-powershell__prompt";
      prompt.textContent = "PS>";
      const commandText = document.createElement("code");
      commandText.textContent = item.command || "PowerShell-Befehl";
      command.append(prompt, commandText);

      const metadata = document.createElement("div");
      metadata.className = "message-coding-powershell__metadata";
      const purposeLabels = {
        inspect: "Prüfung",
        test: "Test",
        build: "Build",
        start: "Programmstart",
        verify: "Verifikation"
      };
      metadata.textContent = [
        item.workingDirectory && item.workingDirectory !== "." ? item.workingDirectory : null,
        purposeLabels[String(item.purpose || "").toLowerCase()] || "Terminalbefehl",
        item.exitCode == null ? null : `Exit-Code ${item.exitCode}`
      ].filter(Boolean).join(" · ");
      section.append(command);
      if (metadata.textContent) section.append(metadata);

      const output = document.createElement("pre");
      output.className = "message-coding-powershell__output";
      const stdout = filterPowerShellOutput(item.standardOutput);
      const stderr = filterPowerShellOutput(item.standardError);
      if (stdout) {
        const stdoutNode = document.createElement("span");
        stdoutNode.textContent = stdout;
        output.append(stdoutNode);
      }
      if (stderr) {
        if (stdout) output.append(document.createTextNode("\n"));
        const stderrNode = document.createElement("span");
        stderrNode.className = "message-coding-powershell__stderr";
        stderrNode.textContent = stderr;
        output.append(stderrNode);
      }
      if (!stdout && !stderr) {
        output.textContent = item.status === "running" ? "Befehl wird ausgeführt …" : "[Keine Konsolenausgabe]";
      }
      section.append(output);
      body.append(section);
    }
    details.append(body);
    requestAnimationFrame(() => {
      body.scrollTop = body.scrollHeight;
    });
    return details;
  }

  function filterPowerShellOutput(value) {
    const warning = /warning:\s+in the working copy of ['"][^'"\r\n]+['"],\s+(?:LF|CRLF) will be replaced by (?:LF|CRLF) the next time Git touches it\.?/gi;
    return String(value || "")
      .replace(warning, "")
      .replace(/^[ \t]+$/gm, "")
      .replace(/\n{3,}/g, "\n\n")
      .trimEnd();
  }

  function isCodingWorkspaceActive() {
    return state.assistantMode === "code" || state.persistentToolAction === "code";
  }

  function setCodingWorkspaceExpanded(expanded) {
    state.codingWorkspaceOpen = Boolean(expanded) && isCodingWorkspaceActive();
    renderCodingWorkspace();
  }

  function renderCodingWorkspace() {
    const active = isCodingWorkspaceActive();
    elements.codingWorkspace.hidden = !active;
    if (!active) {
      elements.codingWorkspaceContent.replaceChildren();
      state.codingWorkspaceOpen = false;
      clearCodingPanelMaximizedState();
      return;
    }

    const run = state.codingRun && String(state.codingRun.sessionId || "") === String(state.activeSessionId || "")
      ? state.codingRun
      : null;
    const entries = Array.isArray(run?.entries) ? run.entries : [];
    const visibleEntries = entries.filter(entry => ![
      "Coding-Modell wird geladen",
      "Coding-Modell geladen"
    ].includes(String(entry?.title || "")));
    const latest = visibleEntries[visibleEntries.length - 1];
    const runStatus = String(run?.status || "idle").toLowerCase();
    elements.codingWorkspaceStatus.textContent = latest?.title
      || (runStatus === "running" ? "Agent arbeitet" : runStatus === "failed" ? "Fehlgeschlagen" : "Bereit");
    elements.codingWorkspaceStatus.dataset.status = runStatus;
    elements.codingWorkspaceToggle.setAttribute("aria-expanded", String(state.codingWorkspaceOpen));
    elements.codingWorkspace.classList.toggle("is-open", state.codingWorkspaceOpen);
    elements.codingWorkspaceContent.hidden = !state.codingWorkspaceOpen;
    if (!state.codingWorkspaceOpen) {
      elements.codingWorkspaceContent.replaceChildren();
      return;
    }

    captureMaximizedCodingPanelScroll();
    const oldDiff = elements.codingWorkspaceContent.querySelector(".message-code-diff__content");
    const oldDiffScrollTop = oldDiff?.scrollTop || 0;
    const oldDiffScrollLeft = oldDiff?.scrollLeft || 0;
    const panelMessage = {
      id: run?.messageId || run?.id || `coding-panels-${state.activeSessionId || "current"}`,
      status: run?.status || (state.isRunning ? "streaming" : "completed"),
      codingTrace: entries,
      codeDiff: run?.codeDiff || ""
    };
    const grid = document.createElement("div");
    grid.className = "message-coding-panels";
    grid.append(
      createCodingTrace(panelMessage, true),
      createPowerShellPanel(panelMessage, true),
      createCodeDiff(panelMessage, true)
    );
    elements.codingWorkspaceContent.replaceChildren(grid);
    requestAnimationFrame(() => {
      const nextDiff = elements.codingWorkspaceContent.querySelector(".message-code-diff__content");
      if (nextDiff) {
        nextDiff.scrollTop = oldDiffScrollTop;
        nextDiff.scrollLeft = oldDiffScrollLeft;
      }
    });
  }

  function sanitizeVisibleMessageContent(value) {
    const marker = /GO(?:\\?_)?SESSION(?:\\?_)?TITLE\s*:\s*/ig;
    return String(value || "")
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .filter(line => !/^\s*(?:#{1,6}\s*)?(?:(?:\*\*|__|`)+\s*)?GO_SESSION_TITLE\s*:/i.test(
        line.replace(/\u00a0/g, " ").replace(/\\_/g, "_")))
      .map(line => line.replace(marker, "").replace(/(?:\*\*|__|`)+\s*(?=(?:\*\*|__|`)+|$)/g, ""))
      .join("\n")
      .trim();
  }

  function createMessage(message) {
    const role = String(message.role).toLowerCase();
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
      meta.textContent = messageTime ? `AI - ${messageTime}` : "AI";
      const liveStatus = state.messageRunStatus.get(String(message.id));
      if (message.status && !message.tool && !liveStatus && !["completed", "Completed"].includes(message.status)) {
        const status = document.createElement("span");
        status.className = `message-status ${String(message.status).toLowerCase()}`;
        status.textContent = statusLabel(message.status);
        meta.append(" · ", status);
      }
      if (liveStatus?.status) {
        const spinner = document.createElement("span");
        spinner.className = "message-status-spinner";
        spinner.setAttribute("aria-hidden", "true");
        const status = document.createElement("span");
        status.className = "message-status streaming";
        status.textContent = runStatusText(liveStatus);
        meta.append(" · ", spinner, status);
      }
      body.append(meta);
    }

    const content = document.createElement("div");
    content.className = "message-content";
    if (["streaming", "Streaming"].includes(message.status)) content.classList.add("stream-cursor");
    content.append(globalThis.goMarkdown.render(sanitizeVisibleMessageContent(message.content)));
    annotateReadableSpeechBlocks(message, article, content);
    body.append(content);
    if (message.tool) {
      const toolBox = document.createElement("div");
      toolBox.className = `message-tool-box message-tool-box--${String(message.tool.status || "").toLowerCase()}`;
      toolBox.textContent = [message.tool.tool, message.tool.context, message.tool.detail, message.tool.status]
        .filter(Boolean).join(" · ");
      body.append(toolBox);
    }
    const artifactItems = Array.isArray(message.artifacts) ? message.artifacts : [];
    if (artifactItems.length) body.append(createArtifactList(artifactItems));
    body.append(createMessageFooter(message, article));

    article.append(body);
    return article;
  }

  function createArtifactList(items) {
    const list = document.createElement("div");
    list.className = "message-artifacts";
    for (const artifact of items) {
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
      footer.append(info, save);
      card.append(footer);
      list.append(card);
      if (mediaType.startsWith("image/") || mediaType.startsWith("audio/") || mediaType.startsWith("video/")) {
        const cached = state.artifactPreviewUrls.get(String(artifact.id));
        if (cached) {
          const image = card.querySelector("img");
          const audio = card.querySelector("audio");
          const video = card.querySelector("video");
          if (image && cached.url) image.src = cached.url;
          if (audio && cached.url) {
            audio.src = cached.url;
            audio.load();
          }
          if (video && cached.url) {
            video.src = cached.url;
            if (cached.posterUrl) video.poster = cached.posterUrl;
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

  function runStatusText(liveStatus) {
    const status = String(liveStatus?.status || "").trim();
    const detail = cleanStatusMetadata(liveStatus?.detail);
    const model = String(liveStatus?.model || state.model || "").trim();
    return uniqueStatusParts(status, model ? `Modell: ${model}` : null, detail).join(" · ");
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
      state.voiceTurn = null;
      renderMessages(false);
      post("audioCapture.stop", {});
      return true;
    }
    if (state.screenClip?.isRecording) {
      state.voiceTurn = null;
      renderMessages(false);
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
      if (state.pendingCaptureRequest?.prompt) elements.prompt.value = state.pendingCaptureRequest.prompt;
      state.pendingCaptureRequest = null;
      if (state.selectedToolAction === action) selectToolAction(null, false);
      showToast(microphoneErrorMessage(error), true);
    }
  }

  function renderContext() {
    elements.activeTools.replaceChildren();
    elements.documents.replaceChildren();

    if (state.selectedToolAction && state.selectedToolAction !== "code" && toolVisuals[state.selectedToolAction]) {
      const [label, iconPath] = toolVisuals[state.selectedToolAction];
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
      chip.addEventListener("click", () => selectToolAction(null));
      elements.activeTools.append(chip);
    }

    const browserVoiceActive = Boolean(
      globalThis.goVoiceCapture?.isActive
      || globalThis.goVoiceCapture?.isStarting
      || state.voiceStarting);
    if (browserVoiceActive || state.voiceTurn?.text || state.microphone?.isBusy) {
      const chip = document.createElement("div");
      chip.className = "active-tool-chip voice-context-chip";
      const label = document.createElement("span");
      label.textContent = state.voiceTurn?.text
        ? `Sprache erkannt · ${state.voiceTurn.text}`
        : state.microphone?.isBusy
          ? "Sprache wird erkannt …"
          : state.voiceStarting
            ? "Mikrofon wird geöffnet …"
            : "Ich höre zu … Sprich jetzt.";
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
      remove.addEventListener("click", () => post(
        file.kind === "document" ? "document.remove" : "attachment.remove",
        file.kind === "document" ? { documentId: file.item.id } : { attachmentId: file.item.id }
      ));
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
      && !(state.selectedToolAction && state.selectedToolAction !== "code")
      && !isAudioCaptureActive()
      && !isScreenClipActive()
      && !state.voiceTurn?.text
      && !state.microphone?.isBusy
      && !globalThis.goVoiceCapture?.isActive
      && !globalThis.goVoiceCapture?.isStarting
      && !state.voiceStarting
      && !state.speechStatus?.active;
  }

  function renderStatus() {
    const campaignRunning = state.codingCampaign?.status === "running";
    const canStop = state.isRunning || campaignRunning || Boolean(state.microphone?.isSpeaking) || Boolean(state.speechStatus?.active);
    elements.send.hidden = canStop;
    elements.stop.hidden = !canStop;
    elements.prompt.disabled = false;
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

  function renderWorkspace() {
    const active = Boolean(state.workspacePath);
    elements.workspaceButton.classList.remove("active");
    elements.workspaceButton.setAttribute("aria-pressed", String(active));
    elements.workspaceButton.title = active
      ? `Workspace: ${state.workspacePath} · Klicken zum Ändern`
      : "Workspace-Ordner freigeben";
    const campaignMode = isCodingCampaignMode();
    elements.workflowsButtonLabel.textContent = "Workflows";
    elements.workflowsButton.title = campaignMode ? "Coding-Workflows öffnen" : "Workflow wählen";
    elements.workflowsButton.setAttribute("aria-label", elements.workflowsButton.title);
  }

  function renderLiveCaption() {
    const caption = state.liveCaption || {};
    const hasContent = Boolean(caption.isActive);
    elements.liveCaption.hidden = !hasContent;
    elements.liveCaptionTitle.textContent = caption.mode === "translateToEnglish"
      ? "Live-Übersetzung · Englisch"
      : "Live-Untertitel";
    elements.liveCaptionStatus.textContent = [caption.status, caption.provider].filter(Boolean).join(" · ");
    elements.liveCaptionTranscript.textContent = caption.transcript || (caption.isActive
      ? "Warte auf Windows-Systemaudio …"
      : "Noch kein Sprachinhalt erkannt.");
    elements.liveCaptionError.textContent = caption.error || "";
    elements.liveCaptionError.hidden = !caption.error;
    elements.stopLiveCaption.hidden = !caption.isActive;
    elements.liveCaptionTranscript.scrollTop = elements.liveCaptionTranscript.scrollHeight;
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
    // Keep Chromium microphone capture active while speech is played. The native
    // side accepts only pause/resume/cancel commands during playback and discards
    // every other transcript, preventing the AI voice from feeding itself back.
    globalThis.goVoiceCapture.setSuspended(!state.microphone?.isRecording);
  }

  async function stopVoiceControl(notifyHost = true) {
    state.voiceStarting = false;
    state.voicePlaybackPending = false;
    state.voiceTurn = null;
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
      return "Der Mikrofonzugriff wurde nicht erlaubt. Erlaube GO den Zugriff im WebView2-Dialog und versuche es erneut.";
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

  function closeAttachmentMenu() {
    const menu = elements.documents.querySelector(".attachment-menu");
    const summary = elements.documents.querySelector(".attachment-summary__toggle");
    if (menu) menu.hidden = true;
    if (summary) summary.setAttribute("aria-expanded", "false");
  }

  function isCodingCampaignMode() {
    return state.assistantMode === "code" || state.persistentToolAction === "code";
  }

  function campaignStatusLabel(status) {
    return ({ running: "Dauerlauf aktiv", faulted: "Laufzeitfehler", stopped: "Gestoppt" })[status] || status || "Inaktiv";
  }

  function campaignPhaseLabel(phase) {
    return ({ bootstrap: "Projektgrundlage", iteration: "Iteration", correction: "Korrektur", validation: "Abnahme" })[phase] || phase || "";
  }

  function selectedCampaignDefinition() {
    return state.campaignDefinitions.find(item => item.id === state.selectedCampaignDefinitionId)
      || state.campaignDefinitions.find(item => item.id === state.codingCampaign?.definitionId)
      || state.campaignDefinitions[0]
      || null;
  }

  function setCampaignFooterMode() {
    const selected = selectedCampaignDefinition();
    for (const control of [elements.deleteWorkflow, elements.editWorkflow, elements.cancelWorkflowEdit, elements.saveWorkflow]) {
      control.hidden = true;
    }
    elements.selectWorkflow.hidden = !selected;
    elements.selectWorkflow.textContent = "Workflow laden";
  }

  function showCampaignPreview(definition) {
    state.selectedCampaignDefinitionId = definition?.id || null;
    state.isWorkflowEditing = false;
    elements.workflowEditor.hidden = true;
    elements.workflowEmpty.hidden = Boolean(definition);
    elements.workflowPreview.hidden = !definition;
    if (definition) {
      const active = state.codingCampaign?.definitionId === definition.id ? state.codingCampaign : null;
      elements.workflowPreviewTitle.textContent = definition.title;
      elements.workflowPreviewId.textContent = definition.category || "Coding-Workflow";
      elements.workflowPreviewBadge.hidden = !active;
      elements.workflowPreviewBadge.textContent = active ? campaignStatusLabel(active.status) : "";
      elements.workflowPreviewTags.replaceChildren();
      elements.workflowPreviewTags.hidden = true;
      elements.workflowPreviewDescription.textContent = definition.description || "Fortlaufender autonomer Coding-Workflow.";
      elements.workflowPreviewSummary.textContent = "Lädt den Workflow und vorhandene Lösungsstände. Der Dauerlauf beginnt erst über Senden im Promptfenster und endet über denselben Stop-Button.";
      elements.workflowPreviewContent.textContent = active
        ? [`Status: ${campaignStatusLabel(active.status)}`, `Iteration: ${active.iteration}`, `Phase: ${campaignPhaseLabel(active.phase)}`, active.challenge ? `Schwerpunkt: ${active.challenge}` : null, active.error ? `Fehler: ${active.error}` : null].filter(Boolean).join("\n")
        : "Wird in den aktuell freigegebenen Workspace geladen und verwendet beim Start das gewählte Coding-Modell.";
    }
    setCampaignFooterMode();
    renderCampaignList();
  }

  function renderCampaignList() {
    const query = elements.workflowSearch.value.trim().toLocaleLowerCase();
    const definitions = state.campaignDefinitions.filter(item => !query
      || `${item.title} ${item.description} ${item.category}`.toLocaleLowerCase().includes(query));
    elements.workflowList.replaceChildren();
    if (definitions.length === 0) {
      const empty = document.createElement("p");
      empty.className = "workflow-list-empty";
      empty.textContent = query ? "Kein passender Coding-Workflow." : "Keine Coding-Workflows verfügbar.";
      elements.workflowList.append(empty);
      return;
    }
    for (const definition of definitions) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `workflow-item${definition.id === state.selectedCampaignDefinitionId ? " active" : ""}`;
      const title = document.createElement("strong");
      title.textContent = definition.title;
      const description = document.createElement("span");
      description.textContent = definition.description;
      button.append(title, description);
      if (definition.id === state.codingCampaign?.definitionId) {
        const badge = document.createElement("span");
        badge.className = "built-in-badge";
        badge.textContent = campaignStatusLabel(state.codingCampaign.status);
        button.append(badge);
      }
      button.addEventListener("click", () => showCampaignPreview(definition));
      elements.workflowList.append(button);
    }
  }

  function configureWorkflowOverlayMode() {
    const campaignMode = state.workflowOverlayMode === "campaign";
    elements.workflowDialogTitle.textContent = campaignMode ? "Coding-Workflows" : "Workflows";
    elements.workflowDialogSubtitle.textContent = campaignMode
      ? "Fortlaufende Coding-Workflows im freigegebenen Workspace"
      : "Gespeicherte Abläufe auswählen und verwalten";
    elements.workflowSearch.placeholder = campaignMode ? "Coding-Workflows durchsuchen …" : "Workflows durchsuchen …";
    elements.newWorkflow.hidden = campaignMode;
    if (campaignMode) showCampaignPreview(selectedCampaignDefinition());
  }

  function renderWorkflows() {
    if (state.workflowOverlayMode === "campaign") {
      renderCampaignList();
      return;
    }
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
    state.workflowOverlayMode = isCodingCampaignMode() ? "campaign" : "workflow";
    configureWorkflowOverlayMode();
    if (state.workflowOverlayMode === "campaign") {
      post("campaign.list", { sessionId: state.activeSessionId });
      showCampaignPreview(selectedCampaignDefinition());
    } else {
      post("workflow.list", { search: elements.workflowSearch.value });
      showWorkflowPreview(selectedWorkflowForDialog());
    }
    requestAnimationFrame(() => elements.workflowSearch.focus());
  }

  function closeWorkflows() {
    elements.overlay.hidden = true;
    state.isWorkflowEditing = false;
  }

  async function postChatRequest(payload) {
    state.pendingCaptureRequest = payload;
    if (state.audioCapture?.isRecording) {
      post("audioCapture.stop", {});
    }
    post("chat.send", payload);
  }

  function resumePendingCaptureRequest() {
    const request = state.pendingCaptureRequest;
    const action = request?.toolAction || state.selectedToolAction;
    if (!state.waitingForCapture || !request || !hasMediaAnalysisContext(action)) return;
    state.waitingForCapture = false;
    post("chat.send", request);
  }

  async function submitPrompt() {
    const prompt = elements.prompt.value.trim();
    const loadedCodingWorkflow = isCodingCampaignMode() && Boolean(state.codingCampaign);
    if (loadedCodingWorkflow) {
      clearTimeout(draftTimer);
      draftTimer = 0;
      pendingDraft = null;
      post("campaign.run", {
        sessionId: state.activeSessionId,
        instruction: prompt || null
      });
      elements.prompt.value = "";
      return;
    }
    if (!prompt) return;
    clearTimeout(draftTimer);
    draftTimer = 0;
    pendingDraft = null;
    await postChatRequest({
      sessionId: state.activeSessionId,
      prompt,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: elements.reasoning.value,
      toolAction: state.selectedToolAction
    });
    elements.prompt.value = "";
  }

  async function submitVoicePrompt(prompt) {
    const text = String(prompt || "").trim();
    if (!text || !state.activeSessionId) return;
    if (finishActiveMediaCaptureFromVoice(text)) return;
    if (isCodingCampaignMode() && state.codingCampaign) {
      post("campaign.run", {
        sessionId: state.activeSessionId,
        instruction: text
      });
      return;
    }
    await postChatRequest({
      sessionId: state.activeSessionId,
      prompt: text,
      documentIds: state.documents.map(item => item.id),
      reasoningEffort: elements.reasoning.value,
      toolAction: state.selectedToolAction
    });
  }

  function selectToolAction(action, persist = true) {
    const previous = state.selectedToolAction;
    const requested = action || null;
    state.selectedToolAction = !requested
      && previous
      && !persistentToolActions.has(previous)
      && state.persistentToolAction
        ? state.persistentToolAction
        : requested;
    for (const option of document.querySelectorAll(".service-option[data-tool-action]")) {
      option.classList.toggle("active", option.dataset.toolAction === state.selectedToolAction);
    }
    const selected = document.querySelector(`.service-option[data-tool-action="${state.selectedToolAction || ""}"] span`);
    elements.toolsButton.title = selected ? `Aktiv: ${selected.textContent}` : "Tools";
    renderContext();
    if (persist && state.activeSessionId) {
      const selectedIsPersistent = persistentToolActions.has(state.selectedToolAction);
      const explicitlyClearedPersistent = !state.selectedToolAction && persistentToolActions.has(previous);
      if (selectedIsPersistent || explicitlyClearedPersistent) {
        state.persistentToolAction = selectedIsPersistent ? state.selectedToolAction : null;
        post("session.tool", {
          sessionId: state.activeSessionId,
          action: state.persistentToolAction
        });
      }
    }
    renderCodingWorkspace();
    renderWorkspace();
  }

  function clearCompletedOneShotToolAction() {
    if (state.selectedToolAction && !persistentToolActions.has(state.selectedToolAction)) {
      selectToolAction(state.persistentToolAction, false);
    }
  }

  function applySnapshot(payload) {
    const dialogWasOpen = !elements.overlay.hidden;
    const editorSelection = state.selectedWorkflowEditorId;
    const wasEditing = state.isWorkflowEditing;
    const previousSessionId = state.activeSessionId;
    state.sessions = Array.isArray(payload.sessions) ? payload.sessions : [];
    state.messages = Array.isArray(payload.messages) ? payload.messages : [];
    state.conversationRevision = Number(payload.conversationRevision) || 0;
    state.conversationRefreshPending = false;
    state.codingRun = payload.codingRun || null;
    state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
    state.campaignDefinitions = Array.isArray(payload.codingCampaignDefinitions) ? payload.codingCampaignDefinitions : state.campaignDefinitions;
    state.codingCampaign = payload.codingCampaign || null;
    state.assistantMode = payload.assistantMode || "general";
    state.documents = Array.isArray(payload.documents) ? payload.documents : [];
    state.attachments = Array.isArray(payload.attachments) ? payload.attachments : [];
    state.documentGroupStatus = payload.documentGroupStatus || { total: 0, ready: 0, processing: 0, failed: 0, status: "ready" };
    state.activeSessionId = payload.activeSessionId || null;
    const activeCodingMode = state.assistantMode === "code"
      || payload.selectedToolAction === "code";
    if (previousSessionId !== state.activeSessionId) {
      state.codingWorkspaceOpen = false;
      state.codingWorkspaceSessionId = state.activeSessionId;
    }
    if ((previousSessionId && previousSessionId !== state.activeSessionId) || !activeCodingMode) {
      clearCodingPanelMaximizedState();
    }
    state.isRunning = Boolean(payload.isRunning);
    state.model = payload.model || null;
    if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
    if (Number.isFinite(payload.contextLimit) && payload.contextLimit > 0) state.contextLimit = payload.contextLimit;
    state.contextWasTruncated = Boolean(payload.contextWasTruncated);
    state.contextNotice = payload.contextNotice || null;
    state.workspacePath = payload.workspacePath || null;
    state.workspaceName = payload.workspaceName || null;
    const serverToolAction = payload.selectedToolAction || null;
    state.persistentToolAction = serverToolAction;
    const activeOneShotTool = state.selectedToolAction && !persistentToolActions.has(state.selectedToolAction);
    if (previousSessionId !== state.activeSessionId || !activeOneShotTool) {
      selectToolAction(serverToolAction, false);
    } else {
      selectToolAction(state.selectedToolAction, false);
    }
    state.runStatus = payload.runStatus || null;
    state.runDetail = payload.runDetail || null;
    state.liveCaption = payload.liveCaption || state.liveCaption;
    if (typeof payload.isSessionPaneOpen === "boolean") {
      const collapsed = !payload.isSessionPaneOpen;
      setSessionsCollapsed(collapsed, false);
      try { globalThis.localStorage.setItem(sidebarStorageKey, collapsed ? "1" : "0"); }
      catch { /* WebView storage is optional. */ }
    }
    elements.prompt.value = payload.draft || "";
    setReasoning(payload.reasoningEffort || "medium");
    renderSessions();
    renderSessionPin();
    renderMessages(true);
    renderContext();
    renderCodingWorkspace();
    renderWorkspace();
    renderStatus();
    renderLiveCaption();
    renderMicrophone();
    syncVoiceCaptureSuspension();
    renderScreenClip();

    if (dialogWasOpen && isCodingCampaignMode()) {
      state.workflowOverlayMode = "campaign";
      configureWorkflowOverlayMode();
      showCampaignPreview(selectedCampaignDefinition());
    } else if (dialogWasOpen && !wasEditing) {
      state.workflowOverlayMode = "workflow";
      configureWorkflowOverlayMode();
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

  function renderSessionPin() {
    const session = state.sessions.find(item => item.id === state.activeSessionId);
    if (!elements.pinSession) return;
    const label = session?.isPinned ? "Sitzung loslösen" : "Sitzung anpinnen";
    elements.pinSession.textContent = label;
    elements.pinSession.title = label;
    elements.pinSession.setAttribute("aria-label", label);
    elements.pinSession.disabled = !session;
  }

  function sortCommittedMessages() {
    state.messages.sort((left, right) => {
      const timeDifference = new Date(left.createdAt || 0).getTime() - new Date(right.createdAt || 0).getTime();
      return timeDifference || String(left.id || "").localeCompare(String(right.id || ""));
    });
  }

  function requestConversationRefresh() {
    if (!state.activeSessionId || state.conversationRefreshPending) return;
    state.conversationRefreshPending = true;
    post("conversation.refresh", { sessionId: state.activeSessionId });
  }

  function applyConversationSnapshot(payload) {
    if (!payload || String(payload.activeSessionId || "") !== String(state.activeSessionId || "")) return;
    state.messages = Array.isArray(payload.messages) ? payload.messages : [];
    sortCommittedMessages();
    state.conversationRevision = Number(payload.conversationRevision) || 0;
    state.codingRun = payload.codingRun || null;
    state.conversationRefreshPending = false;
    renderMessages(false);
    renderCodingWorkspace();
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
    sortCommittedMessages();
    renderMessages(true);
  }

  function applyCommittedRemoval(payload) {
    if (!payload?.messageId || String(payload.sessionId || "") !== String(state.activeSessionId || "")) return;
    if (!acceptCommittedRevision(payload)) return;
    const messageId = String(payload.messageId);
    state.messages = state.messages.filter(item => String(item.id || "") !== messageId);
    state.messageRunStatus.delete(messageId);
    renderMessages(false);
  }

  function applyCommittedCodingSnapshot(payload) {
    if (!payload || String(payload.sessionId || "") !== String(state.activeSessionId || "")) return;
    if (!acceptCommittedRevision(payload)) return;
    state.codingRun = payload.codingRun || null;
    renderCodingWorkspace();
  }

  function handleHostMessage(event) {
    const { type, payload } = event.detail;
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
      case "conversation.messageRemoved":
        applyCommittedRemoval(payload);
        break;
      case "coding.snapshotCommitted":
        applyCommittedCodingSnapshot(payload);
        break;
      case "chat.started":
        state.isRunning = true;
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        state.voiceTurn = null;
        if (Array.isArray(payload.attachments)) {
          state.attachments = payload.attachments;
          renderContext();
        }
        if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
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
        renderSessions();
        renderStatus();
        syncVoiceCaptureSuspension();
        if (state.contextWasTruncated) showToast(state.contextNotice || "Der Modellkontext wurde gekürzt.");
        break;
      case "chat.delta": {
        break;
      }
      case "chat.codeDiff": {
        break;
      }
      case "chat.codingTrace": {
        break;
      }
      case "chat.message": {
        requestConversationRefresh();
        break;
      }
      case "chat.removed": {
        requestConversationRefresh();
        break;
      }
      case "chat.completed":
      case "chat.cancelled":
      case "chat.failed":
        state.isRunning = false;
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        clearCompletedOneShotToolAction();
        state.runStatus = payload.runStatus || null;
        state.runDetail = payload.runDetail || null;
        if (payload.message?.id) state.messageRunStatus.delete(String(payload.message.id));
        if (payload.session) {
          const sessionIndex = state.sessions.findIndex(item => item.id === payload.session.id);
          if (sessionIndex >= 0) state.sessions[sessionIndex] = payload.session;
          else state.sessions.push(payload.session);
          if (payload.session.id === state.activeSessionId) {
            state.persistentToolAction = payload.session.persistentToolAction || null;
            if (!state.selectedToolAction || persistentToolActions.has(state.selectedToolAction)) {
              selectToolAction(state.persistentToolAction, false);
            }
          }
        }
        renderSessions();
        renderStatus();
        if (type === "chat.completed"
          && isVoiceControlActive()
          && payload.message?.content
          && payload.message?.contentProfile !== "audiobook"
          && payload.message.content.trim() !== "Der Text wurde vorgelesen.") {
          state.voicePlaybackPending = true;
          syncVoiceCaptureSuspension();
          post("microphone.speak", {
            text: payload.message.content,
            messageId: payload.message.id,
            sessionId: payload.message.sessionId
          });
        } else {
          state.voicePlaybackPending = false;
          syncVoiceCaptureSuspension();
        }
        if (type === "chat.failed") showToast(payload.error || "Die Antwort ist fehlgeschlagen.", true);
        else setTimeout(() => {
          if (!state.isRunning) {
            state.runStatus = null;
            state.runDetail = null;
            renderStatus();
          }
        }, 1800);
        break;
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
        const action = String(payload?.action || "");
        if (!["audioAnalysis", "videoAnalysis", "imageAnalysis"].includes(action)) break;
        state.waitingForCapture = true;
        selectToolAction(action, false);
        void beginMediaCapture(action);
        break;
      }
      case "capture.cancelled": {
        const action = String(payload?.action || "");
        if (state.pendingCaptureRequest?.prompt) {
          elements.prompt.value = state.pendingCaptureRequest.prompt;
          scheduleDraftSave();
        }
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        if (state.selectedToolAction === action) selectToolAction(null, false);
        break;
      }
      case "campaign.snapshot":
      case "campaign.changed":
        state.campaignDefinitions = Array.isArray(payload.definitions) ? payload.definitions : state.campaignDefinitions;
        state.codingCampaign = payload.activeCampaign || null;
        if (state.codingCampaign && state.codingCampaign.status !== "running") {
          state.isRunning = false;
        }
        renderCodingWorkspace();
        renderStatus();
        if (!elements.overlay.hidden && state.workflowOverlayMode === "campaign") {
          configureWorkflowOverlayMode();
          showCampaignPreview(selectedCampaignDefinition());
        }
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
        if (payload.model) state.model = payload.model;
        if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
        if (Number.isFinite(payload.contextLimit) && payload.contextLimit > 0) state.contextLimit = payload.contextLimit;
        if (typeof payload.contextWasTruncated === "boolean") state.contextWasTruncated = payload.contextWasTruncated;
        if (Object.hasOwn(payload, "loadedFiles")) state.loadedFiles = payload.loadedFiles;
        state.runStatus = payload.runStatus || state.runStatus;
        state.runDetail = payload.runDetail ?? state.runDetail;
        if (payload?.messageId) state.messageRunStatus.set(String(payload.messageId), {
          status: payload.runStatus || "Denkt nach",
          detail: payload.runDetail || null,
          model: payload.model || state.model || null
        });
        renderContext();
        renderMessages(false);
        renderStatus();
        break;
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
        const text = String(payload?.text || "").trim();
        if (payload?.isFinal && payload?.stopVoice) {
          void stopVoiceControl(false);
          break;
        }
        state.voiceTurn = text ? {
          turnId: payload.turnId,
          text,
          isFinal: Boolean(payload.isFinal)
        } : null;
        renderMessages(true);
        if (payload?.isFinal && text && finishActiveMediaCaptureFromVoice(text)) {
          break;
        }
        if (payload?.isFinal && payload?.execute && text) submitVoicePrompt(text);
        if (payload?.isFinal && payload?.noise) {
          state.voiceTurn = null;
          renderMessages(false);
        }
        break;
      }
      case "artifact.previewReady": {
        const artifactId = String(payload?.artifactId || "");
        state.artifactPreviewPending.delete(artifactId);
        if (artifactId && payload?.url) state.artifactPreviewUrls.set(artifactId, {
          url: payload.url,
          posterUrl: payload.posterUrl || null
        });
        for (const card of document.querySelectorAll(".artifact-card")) {
          if (card.dataset.artifactId !== artifactId) continue;
          const image = card.querySelector("img");
          const audio = card.querySelector("audio");
          const video = card.querySelector("video");
          if (image && payload?.url) {
            image.addEventListener("load", () => card.classList.remove("artifact-card--failed"), { once: true });
            image.src = payload.url;
          }
          if (audio && payload?.url) {
            audio.src = payload.url;
            audio.load();
          }
          if (video && payload?.url) {
            video.src = payload.url;
            if (payload?.posterUrl) video.poster = payload.posterUrl;
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
      case "composer.transcript": {
        const text = String(payload?.text || "").trim();
        if (text) {
          const current = elements.prompt.value.trim();
          elements.prompt.value = current ? `${current}\n\n${text}` : text;
          scheduleDraftSave();
          elements.prompt.focus();
          elements.prompt.setSelectionRange(elements.prompt.value.length, elements.prompt.value.length);
        }
        break;
      }
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
          elements.prompt.value = state.pendingCaptureRequest.prompt;
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
  elements.stop.addEventListener("click", () => {
    const stopsCodingCampaign = state.codingCampaign?.status === "running";
    if (stopsCodingCampaign) {
      state.codingCampaign = { ...state.codingCampaign, status: "stopping" };
      state.isRunning = false;
      renderCodingWorkspace();
      renderStatus();
    }
    if (stopsCodingCampaign) {
      post("campaign.stop", { sessionId: state.activeSessionId });
    } else {
      post("chat.cancel", {});
    }
    post("microphone.stopSpeech", {});
  });
  elements.codingWorkspaceToggle.addEventListener("click", () => {
    setCodingWorkspaceExpanded(!state.codingWorkspaceOpen);
  });
  elements.codingWorkspaceClose.addEventListener("click", event => {
    event.preventDefault();
    event.stopPropagation();
    selectToolAction(null);
  });
  elements.composerSpeechPause.addEventListener("click", () => {
    if (!elements.composerSpeechPause.disabled) post("microphone.toggleSpeechPause", {});
  });
  elements.composerSpeechStop.addEventListener("click", () => {
    if (!elements.composerSpeechStop.disabled) post("microphone.stopSpeech", {});
  });
  byId("pick-document").addEventListener("click", () => post("document.pick", { sessionId: state.activeSessionId }));
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
      const source = await globalThis.goVoiceCapture.start();
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
  byId("export-pdf").addEventListener("click", () => post("chat.exportPdf", { sessionId: state.activeSessionId }));
  elements.pinSession?.addEventListener("click", () => {
    const session = state.sessions.find(item => item.id === state.activeSessionId);
    if (session) post("session.pin", { sessionId: session.id, pinned: !session.isPinned });
  });

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
  for (const option of document.querySelectorAll(".service-option[data-tool-action]")) {
    const visual = toolVisuals[option.dataset.toolAction];
    if (visual) option.prepend(createToolIcon(visual[1]));
    option.addEventListener("click", () => {
      const action = option.dataset.toolAction;
      const selecting = state.selectedToolAction !== action;
      selectToolAction(selecting ? action : null);
      setToolsMenuOpen(false);
      elements.prompt.focus();
      if (selecting && ["audioAnalysis", "videoAnalysis", "imageAnalysis"].includes(action)) {
        void beginMediaCapture(action);
      }
    });
  }
  for (const option of document.querySelectorAll(".service-option[data-tool-immediate]")) {
    const visual = toolVisuals[option.dataset.toolImmediate];
    if (visual) option.prepend(createToolIcon(visual[1]));
    option.addEventListener("click", () => {
      const action = option.dataset.toolImmediate;
      if (action === "screen.capture") post("screen.capture", { sessionId: state.activeSessionId });
      else if (action === "screenClip.toggle") post(state.screenClip?.isRecording ? "screenClip.stop" : "screenClip.start", { sessionId: state.activeSessionId });
      else if (action === "liveCaption.start") post("liveCaption.start", { mode: "transcribe" });
      setToolsMenuOpen(false);
    });
  }
  elements.stopLiveCaption.addEventListener("click", () => post("liveCaption.stop", {}));
  document.addEventListener("pointerdown", event => {
    if (event.button === 2) captureReadFromContextTarget(event);
  }, { capture: true });
  document.addEventListener("contextmenu", captureReadFromContextTarget, { capture: true });
  document.addEventListener("click", () => {
    setToolsMenuOpen(false);
    closeAttachmentMenu();
  });

  elements.workflowsButton.addEventListener("click", openWorkflows);
  elements.workspaceButton.addEventListener("click", () => post("workspace.pick", {}));
  byId("close-workflows").addEventListener("click", closeWorkflows);
  elements.overlay.addEventListener("click", event => {
    if (event.target === elements.overlay) closeWorkflows();
  });
  elements.workflowSearch.addEventListener("input", renderWorkflows);
  elements.newWorkflow.addEventListener("click", () => {
    if (state.workflowOverlayMode === "workflow") showWorkflowEditor(null);
  });
  elements.selectWorkflow.addEventListener("click", () => {
    if (state.workflowOverlayMode === "campaign") {
      const definition = selectedCampaignDefinition();
      if (definition) {
        post("campaign.select", { sessionId: state.activeSessionId, definitionId: definition.id });
        closeWorkflows();
      }
      return;
    }
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
    if (elements.documents.querySelector(".attachment-menu:not([hidden])")) closeAttachmentMenu();
    else if (!elements.toolsMenu.hidden) setToolsMenuOpen(false);
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
    heading.textContent = `${role === "user" ? "Du" : "GO AI"}${timestamp ? ` · ${timestamp}` : ""}`;
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
    eyebrow.textContent = "GO · AI ASSISTENT";
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
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const panel = document.querySelector(".coding-panel--maximized");
    const button = panel?.querySelector(".coding-panel-maximize");
    if (!panel || !button) return;
    event.preventDefault();
    setCodingPanelMaximized(panel, button, button.dataset.panelLabel || "Modul", false);
  });
  globalThis.addEventListener("pagehide", flushDraft);
  globalThis.addEventListener("beforeunload", flushDraft);
  post("app.ready", {});
})();
