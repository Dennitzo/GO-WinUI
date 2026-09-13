(function () {
  "use strict";

  const state = {
    sessions: [],
    messages: [],
    workflows: [],
    conversationRevision: 0,
    conversationRefreshPending: false,
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
    codingActivity: new Map(),
    codingToolStepsExpanded: false,
    changesSummary: null,
    codingPreviewDialog: null,
    artifactPreviewUrls: new Map(),
    artifactPreviewPending: new Set(),
    selectedToolAction: null,
    persistentToolAction: null,
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
  const persistentToolActions = new Set(["bricsCad", "audiobook", "coding"]);
  const toolVisuals = Object.freeze({
    coding: ["Coding", "M8 6l-6 6 6 6M16 6l6 6-6 6M14 3l-4 18"],
    audioAnalysis: ["Audio analysieren", "M4 12h2m2-5 4 10 3-7 2 4h3"],
    imageAnalysis: ["Bild analysieren", "M4 5h16v14H4zM7 15l3-3 3 3 2-2 2 2"],
    imageGeneration: ["Bild erstellen", "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"],
    bricsCad: ["BricsCAD", "M4 18V6l8-3 8 3v12l-8 3zM12 3v18M4 6l8 4 8-4"],
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

  function normalizeToolAction(action) {
    const value = action || null;
    return value && Object.prototype.hasOwnProperty.call(toolVisuals, value) ? value : null;
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
    pinSession: byId("pin-session"),
    newSession: byId("new-session"),
    messageList: byId("message-list"),
    messageScroll: byId("message-scroll"),
    codingWorkspace: byId("coding-workspace"),
    codingChanges: byId("coding-changes"),
    prompt: byId("prompt"),
    chatPane: document.querySelector(".chat-pane"),
    chatHeader: document.querySelector(".chat-header"),
    composerRegion: document.querySelector(".composer-region"),
    composerSpeechStatus: byId("composer-speech-status"),
    composerSpeechDetail: byId("composer-speech-detail"),
    composerSpeechPause: byId("composer-speech-pause"),
    composerSpeechPauseIcon: byId("composer-speech-pause-icon"),
    composerSpeechStop: byId("composer-speech-stop"),
    send: byId("send"),
    stop: byId("stop"),
    toolsButton: byId("tools-button"),
    toolsMenu: byId("tools-menu"),
    workflowsButton: byId("open-workflows"),
    context: byId("context-meter"),
    contextLabel: byId("context-label"),
    contextStrip: byId("context-strip"),
    activeTools: byId("active-tool-chips"),
    documents: byId("document-chips"),
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
  const chatScroll = globalThis.createGoChatScroll({
    scroller: elements.messageScroll, content: elements.messageList, button: byId("scroll-to-latest")
  });
  const sessionScrollStoragePrefix = "go.assistant.session-scroll.v1:";
  let draftTimer = 0;
  let pendingDraft = null;
  let promptResizeFrame = 0;

  function resizePrompt() {
    const prompt = elements.prompt;
    const pane = elements.chatPane.getBoundingClientRect();
    if (pane.height <= 0 || prompt.clientWidth <= 0) return;
    const viewport = globalThis.visualViewport;
    const bottom = Math.min(pane.bottom, viewport ? viewport.offsetTop + viewport.height : globalThis.innerHeight);
    const overhead = elements.composerRegion.getBoundingClientRect().height - prompt.getBoundingClientRect().height;
    const minimum = parseFloat(globalThis.getComputedStyle(prompt).minHeight) || 58;
    // Reserve the actual toolbar, attachments and header; the conversation can
    // yield its space to the draft until the available window height is filled.
    const maximum = Math.max(minimum, Math.floor(bottom - pane.top
      - elements.chatHeader.getBoundingClientRect().height - overhead - 12));
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
  }

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

  function sessionScrollStorageKey(sessionId) {
    return `${sessionScrollStoragePrefix}${String(sessionId || "")}`;
  }

  function readSessionScrollPosition(sessionId) {
    if (!sessionId) return null;
    try {
      const value = JSON.parse(globalThis.localStorage.getItem(sessionScrollStorageKey(sessionId)) || "null");
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
    renderCodingChanges();
    if (state.selectedToolAction === "coding" || state.messages.some(message => message.toolSteps?.length)) {
      renderCodingMessages(scrollToEnd);
      return;
    }
    const previousScrollTop = elements.messageScroll.scrollTop;
    const previousScrollLeft = elements.messageScroll.scrollLeft;
    elements.messageList.replaceChildren();
    for (const message of state.messages) {
      elements.messageList.append(createMessage(message));
    }
    if (!state.messages.length && state.activeSessionId) {
      const empty = document.createElement("div");
      empty.className = "chat-empty-state";
      if (state.selectedToolAction === "coding") {
        const heading = document.createElement("h2");
        heading.textContent = "Woran arbeiten wir?";
        const description = document.createElement("p");
        description.textContent = state.codingWorkspacePath
          ? "Beschreibe eine Änderung, einen Fehler oder eine Frage zum Projekt."
          : "Wähle über Workspace deinen Projektordner und beschreibe die gewünschte Codeänderung.";
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
        : "Wähle über Workspace deinen Projektordner und beschreibe die gewünschte Codeänderung.";
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
      .replace(/\r\n?/g, "\n")
      .split("\n")
      .filter(line => !/^\s*(?:#{1,6}\s*)?(?:(?:\*\*|__|`)+\s*)?GO_SESSION_TITLE\s*:/i.test(
        line.replace(/\u00a0/g, " ").replace(/\\_/g, "_")))
      .map(line => line.replace(marker, ""))
      .join("\n")
      .trim();
  }

  function createMessage(message, previousArticle = null) {
    const role = String(message.role).toLowerCase();
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
      const assistantLabel = state.selectedToolAction === "coding" ? "Coding Agent" : "AI";
      meta.textContent = messageTime ? `${assistantLabel} - ${messageTime}` : assistantLabel;
      const liveStatus = state.messageRunStatus.get(String(message.id));
      if (message.status && !message.tool && !liveStatus && !["completed", "Completed"].includes(message.status)) {
        const status = document.createElement("span");
        status.className = `message-status ${String(message.status).toLowerCase()}`;
        status.textContent = statusLabel(message.status);
        meta.append(" · ", status);
      }
      if (liveStatus?.status && state.selectedToolAction !== "coding") {
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

    const timeline = role === "assistant" && (state.selectedToolAction === "coding" || message.toolSteps?.length)
      ? createCodingActivity(contentMessage, previousArticle?.querySelector(".coding-timeline")) : null;
    if (timeline) {
      body.append(timeline);
      annotateReadableSpeechBlocks(contentMessage, article, timeline);
    } else {
    const content = document.createElement("div");
    content.className = "message-content";
    if (["streaming", "Streaming"].includes(message.status)) content.classList.add("stream-cursor");
    content.append(globalThis.goMarkdown.render(sanitizeVisibleMessageContent(contentMessage.content)));
    if (state.selectedToolAction === "coding") enhanceCodingCodeBlocks(content);
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
    const artifactItems = Array.isArray(message.artifacts) ? message.artifacts : [];
    if (artifactItems.length) body.append(createArtifactList(artifactItems));
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

  function codingToolLabel(value) {
    const name = String(value || "");
    return ({
      "assistant.reasoning": "Denkprozess",
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
      label: codingToolLabel(tool.tool), status: codingStepState(tool.status),
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
    if (state.selectedToolAction !== "coding" || !payload?.messageId
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
    const steps = mergeCodingToolSteps(message).filter(step => step.kind === "tool");
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
      onPreview: (messageId, stepId) => { if (state.activeSessionId === sessionId) openCodingPreview(messageId, stepId); }
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
    // The native resource handler loads the persisted result. Never inject model HTML into GO's document.
    frame.src = `https://go-coding-preview.local/coding/${encodeURIComponent(messageId)}/${encodeURIComponent(stepId)}`;
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
    const coding = state.selectedToolAction === "coding";
    elements.appShell.classList.toggle("coding-mode", coding);
    elements.prompt.placeholder = coding ? "Änderung beschreiben oder Frage zum Projekt stellen …" : "Nachricht eingeben …";
    elements.codingWorkspace.classList.toggle("active", Boolean(state.codingWorkspacePath));
    elements.codingWorkspace.title = state.codingWorkspacePath
      ? `Workspace: ${state.codingWorkspacePath}\nOrdner wechseln` : "Workspace auswählen und Coding starten";
    elements.codingWorkspace.setAttribute("aria-label", state.codingWorkspacePath
      ? `Workspace ${state.codingWorkspacePath} wechseln` : "Workspace auswählen und Coding starten");
    elements.codingWorkspace.disabled = state.isRunning || !state.activeSessionId;
    renderCodingChanges();
  }

  function renderCodingChanges() {
    if (!elements.codingChanges || !globalThis.goCodingChanges) return;
    const latest = state.messages.filter(message => message.role === "assistant").at(-1);
    const view = elements.codingChanges._view ||= globalThis.goCodingChanges.create({
      host: elements.codingChanges, onLayout: schedulePromptResize
    });
    view.update(state.changesSummary, { sessionId: state.activeSessionId, messageId: latest?.id,
      workspacePath: state.codingWorkspacePath, isCoding: state.selectedToolAction === "coding", toolSteps: latest?.toolSteps });
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

  function pickCodingWorkspace() {
    if (state.activeSessionId && !state.isRunning) {
      post("coding.pickWorkspace", { sessionId: state.activeSessionId });
    }
  }

  function runStatusText(liveStatus) {
    const status = String(liveStatus?.status || "").trim();
    const detail = cleanStatusMetadata(liveStatus?.detail);
    const model = visibleModelLabel(liveStatus?.model || state.model);
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

    if (state.selectedToolAction && toolVisuals[state.selectedToolAction]) {
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
      && !state.selectedToolAction
      && !isAudioCaptureActive()
      && !isScreenClipActive()
      && !globalThis.goVoiceCapture?.isActive
      && !globalThis.goVoiceCapture?.isStarting
      && !state.voiceStarting
      && (elements.codingChanges?.hidden ?? true)
      && !state.speechStatus?.active;
  }

  function renderStatus() {
    renderCodingWorkspace();
    // Speech playback is an independent activity. The composer stop button only
    // cancels the current AI run; playback has its own chip
    // controls so sending/aborting a prompt cannot interrupt it.
    const canStop = state.isRunning;
    elements.send.hidden = canStop;
    elements.stop.hidden = !canStop;
    elements.prompt.disabled = false;
    elements.newSession.disabled = state.isRunning;
    const hasUnpinnedSessions = state.sessions.some(session => !session.isPinned);
    elements.clearSessions.disabled = state.isRunning || !hasUnpinnedSessions;
    elements.clearSessions.title = hasUnpinnedSessions
      ? "Alle nicht angepinnten Sitzungen löschen"
      : "Keine nicht angepinnten Sitzungen vorhanden";
    elements.clearSessions.setAttribute("aria-label", elements.clearSessions.title);

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
    state.voiceTurn = null;
    renderContext();
    if (!prompt) return;
    chatScroll.jump(false);
    clearTimeout(draftTimer);
    draftTimer = 0;
    pendingDraft = null;
    await postChatRequest({
      sessionId: state.activeSessionId,
      prompt,
      documentIds: state.documents.map(item => item.id),
      toolAction: state.selectedToolAction
    });
    setPromptValue("");
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

  function selectToolAction(action, persist = true) {
    const previous = state.selectedToolAction;
    const requested = normalizeToolAction(action);
    const persistentFallback = normalizeToolAction(state.persistentToolAction);
    state.selectedToolAction = !requested
      && previous
      && !persistentToolActions.has(previous)
      && persistentFallback
        ? persistentFallback
        : requested;
    for (const option of document.querySelectorAll(".service-option[data-tool-action]")) {
      option.classList.toggle("active", option.dataset.toolAction === state.selectedToolAction);
    }
    const selected = document.querySelector(`.service-option[data-tool-action="${state.selectedToolAction || ""}"] span`);
    elements.toolsButton.title = selected ? `Aktiv: ${selected.textContent}` : "Tools";
    renderContext();
    if (!state.messages.length) renderMessages(false);
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
  }

  function clearCompletedOneShotToolAction() {
    if (state.selectedToolAction && !persistentToolActions.has(state.selectedToolAction)) {
      selectToolAction(state.persistentToolAction, false);
    }
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

  function applySnapshot(payload) {
    const dialogWasOpen = !elements.overlay.hidden;
    const editorSelection = state.selectedWorkflowEditorId;
    const wasEditing = state.isWorkflowEditing;
    const previousSessionId = state.activeSessionId;
    const nextSessionId = payload.activeSessionId || null;
    const sessionChanged = previousSessionId !== nextSessionId;
    if (sessionChanged) { closeCodingPreview(); state.changesSummary = null; }
    const nextMessages = Array.isArray(payload.messages) ? payload.messages : [];
    const currentSessionMessagesChanged = !sessionChanged
      && conversationMessagesDiffer(state.messages, nextMessages);
    if (sessionChanged && previousSessionId) persistSessionScrollPosition(previousSessionId);
    state.sessions = Array.isArray(payload.sessions) ? payload.sessions : [];
    state.messages = nextMessages;
    state.conversationRevision = Number(payload.conversationRevision) || 0;
    state.conversationRefreshPending = false;
    state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
    state.documents = Array.isArray(payload.documents) ? payload.documents : [];
    state.attachments = Array.isArray(payload.attachments) ? payload.attachments : [];
    state.documentGroupStatus = payload.documentGroupStatus || { total: 0, ready: 0, processing: 0, failed: 0, status: "ready" };
    state.activeSessionId = nextSessionId;
    globalThis.goVoiceCapture?.setSessionId(state.activeSessionId);
    if (previousSessionId !== state.activeSessionId) {
      state.messageRunStatus.clear();
      resetTransientVoiceStateForSessionChange();
    }
    state.isRunning = Boolean(payload.isRunning);
    state.model = payload.model || null;
    state.codingWorkspacePath = payload.codingWorkspacePath || null;
    state.codingToolStepsExpanded = Boolean(payload.codingToolStepsExpanded);
    if (payload.changesSummary) applyCodingChanges(payload.changesSummary);
    if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
    if (Number.isFinite(payload.contextLimit) && payload.contextLimit > 0) state.contextLimit = payload.contextLimit;
    state.contextWasTruncated = Boolean(payload.contextWasTruncated);
    state.contextNotice = payload.contextNotice || null;
    const serverToolAction = normalizeToolAction(payload.selectedToolAction);
    state.persistentToolAction = persistentToolActions.has(serverToolAction) ? serverToolAction : null;
    const activeOneShotTool = state.selectedToolAction && !persistentToolActions.has(state.selectedToolAction);
    if (previousSessionId !== state.activeSessionId || !activeOneShotTool) {
      selectToolAction(serverToolAction, false);
    } else {
      selectToolAction(state.selectedToolAction, false);
    }
    state.runStatus = payload.runStatus || null;
    state.runDetail = payload.runDetail || null;
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
    renderSessions();
    renderSessionPin();
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
    state.codingToolStepsExpanded = Boolean(payload.codingToolStepsExpanded);
    const nextMessages = Array.isArray(payload.messages) ? payload.messages : [];
    const messagesChanged = conversationMessagesDiffer(state.messages, nextMessages);
    state.messages = nextMessages;
    sortCommittedMessages();
    if (payload.changesSummary) applyCodingChanges(payload.changesSummary);
    state.conversationRevision = Number(payload.conversationRevision) || 0;
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
      case "coding.changes":
        applyCodingChanges(payload);
        break;
      case "chat.started":
        state.changesSummary = null;
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
        recordCodingActivity({ ...payload, messageId: payload.message?.id, runStatus: state.runStatus });
        renderMessages(true);
        renderSessions();
        renderStatus();
        syncVoiceCaptureSuspension();
        if (state.contextWasTruncated) showToast(state.contextNotice || "Der Modellkontext wurde gekürzt.");
        break;
      case "chat.delta": {
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
        // A terminal chat event ends the only active model run. Clear every
        // transient per-message status so a previously rendered card cannot
        // retain (or regain) a stale "Denkt nach" indicator.
        state.messageRunStatus.clear();
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
          setPromptValue(state.pendingCaptureRequest.prompt);
          scheduleDraftSave();
        }
        state.pendingCaptureRequest = null;
        state.waitingForCapture = false;
        if (state.selectedToolAction === action) selectToolAction(null, false);
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
        const acceptsRunStatus = !statusMessageId
          || (state.isRunning
            && statusSessionMatches
            && !isTerminalMessageStatus(statusMessage?.status));

        if (acceptsRunStatus) {
          recordCodingActivity(payload);
          // Reasoning packets update only their card. They must not alternate
          // with model progress or replace token details with an internal name.
          if (payload.toolStep?.tool === "assistant.reasoning") {
            renderMessages(false);
            break;
          }
          if (payload.model) state.model = payload.model;
          if (Number.isFinite(payload.contextUsed)) state.contextUsed = payload.contextUsed;
          if (Number.isFinite(payload.contextLimit) && payload.contextLimit > 0) state.contextLimit = payload.contextLimit;
          if (typeof payload.contextWasTruncated === "boolean") state.contextWasTruncated = payload.contextWasTruncated;
          if (Object.hasOwn(payload, "loadedFiles")) state.loadedFiles = payload.loadedFiles;
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
    const hasUnpinnedSessions = state.sessions.some(session => !session.isPinned);
    if (hasUnpinnedSessions && globalThis.confirm(
      "Alle nicht angepinnten Sitzungen und ihre Nachrichten endgültig löschen? Angepinnte Sitzungen bleiben erhalten.")) {
      flushDraft();
      post("session.clear", {});
    }
  });
  elements.sessionSearch.addEventListener("input", renderSessions);
  byId("open-sessions").addEventListener("click", () => document.body.classList.add("sessions-open"));
  byId("collapse-sessions").addEventListener("click", () => document.body.classList.remove("sessions-open"));

  elements.prompt.addEventListener("input", () => {
    resizePrompt();
    scheduleDraftSave();
  });
  elements.prompt.addEventListener("keydown", event => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      submitPrompt();
    }
  });
  elements.send.addEventListener("click", submitPrompt);
  elements.codingWorkspace.addEventListener("click", pickCodingWorkspace);
  elements.stop.addEventListener("click", () => {
    post("chat.cancel", {});
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
  globalThis.addEventListener("pagehide", flushDraft);
  globalThis.addEventListener("beforeunload", flushDraft);
  globalThis.addEventListener("resize", schedulePromptResize);
  globalThis.visualViewport?.addEventListener("resize", schedulePromptResize);
  if (globalThis.ResizeObserver) {
    const promptLayoutObserver = new ResizeObserver(schedulePromptResize);
    for (const node of [elements.chatPane, elements.chatHeader, elements.composerRegion, elements.prompt]) {
      promptLayoutObserver.observe(node);
    }
  }
  document.fonts?.ready.then(schedulePromptResize);
  schedulePromptResize();
  post("app.ready", {});
})();
