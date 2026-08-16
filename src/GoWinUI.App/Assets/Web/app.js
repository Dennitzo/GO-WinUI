(function () {
  "use strict";

  const state = {
    sessions: [],
    messages: [],
    workflows: [],
    documents: [],
    attachments: [],
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
    messageRunStatus: new Map(),
    artifactPreviewUrls: new Map(),
    artifactPreviewPending: new Set(),
    selectedToolAction: null,
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
  const toolVisuals = Object.freeze({
    audioAnalysis: ["Audio analysieren", "M4 12h2m2-5 4 10 3-7 2 4h3"],
    imageAnalysis: ["Bild analysieren", "M4 5h16v14H4zM7 15l3-3 3 3 2-2 2 2"],
    imageGeneration: ["Bild erstellen", "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"],
    bricsCad: ["BricsCAD", "M4 18V6l8-3 8 3v12l-8 3zM12 3v18M4 6l8 4 8-4"],
    code: ["Coding", "M9 18l-6-6 6-6M15 6l6 6-6 6"],
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
    send: byId("send"),
    stop: byId("stop"),
    reasoning: byId("reasoning"),
    toolsButton: byId("tools-button"),
    toolsMenu: byId("tools-menu"),
    workspaceButton: byId("workspace-button"),
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
    if (isScreenClipActive()) {
      elements.messageList.append(createScreenClipProgressMessage());
    }
    if (state.voiceTurn?.text) {
      elements.messageList.append(createMessage({
        id: `voice-${state.voiceTurn.turnId || "current"}`,
        sessionId: state.activeSessionId,
        role: "user",
        content: state.voiceTurn.text,
        status: state.voiceTurn.isFinal ? "pending" : "streaming",
        createdAt: new Date().toISOString(),
        isVoicePreview: true
      }));
    }
    const browserVoiceActive = Boolean(
      globalThis.goVoiceCapture?.isActive
      || globalThis.goVoiceCapture?.isStarting
      || state.voiceStarting);
    if (browserVoiceActive && !state.isRunning && !state.voiceTurn?.text) {
      elements.messageList.append(createMessage({
        id: "voice-listening-preview",
        sessionId: state.activeSessionId,
        role: "user",
        content: state.microphone?.isBusy
          ? "Sprache wird erkannt …"
          : state.voiceStarting
            ? "Mikrofon wird geöffnet …"
            : "Ich höre zu … Sprich jetzt.",
        status: "streaming",
        createdAt: new Date().toISOString(),
        isVoicePreview: true
      }));
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
      footer.append(createMessageFooterLink("Vorlesen", () => {
        post("chat.send", {
          sessionId: state.activeSessionId,
          prompt: "Lies die ausgewählte Nachricht vor",
          speechMessageId: String(message.id),
          documentIds: [],
          reasoningEffort: elements.reasoning.value,
          toolAction: "textToSpeech"
        });
      }));
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
        status.textContent = [liveStatus.status, liveStatus.detail].filter(Boolean).join(" · ");
        meta.append(" · ", spinner, status);
      }
      body.append(meta);
    }

    const content = document.createElement("div");
    content.className = "message-content";
    if (["streaming", "Streaming"].includes(message.status)) content.classList.add("stream-cursor");
    content.append(globalThis.goMarkdown.render(message.content || ""));
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
    if (!message.isVoicePreview) body.append(createMessageFooter(message, article));

    article.append(body);
    if (message.isVoicePreview) article.classList.add("voice-preview");
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
        const image = document.createElement("img");
        image.alt = artifact.fileName || "Erzeugtes Bild";
        image.loading = "lazy";
        image.decoding = "async";
        image.addEventListener("error", () => {
          card.classList.add("artifact-card--failed");
          image.alt = `${artifact.fileName || "Bild"} konnte nicht als Vorschau geladen werden`;
        }, { once: true });
        card.append(image);
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
    return Boolean(state.microphone?.isRecording || globalThis.goVoiceCapture?.isActive || state.voiceStarting);
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
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "active-tool-chip screen-clip-chip";
      chip.disabled = Boolean(clip.isBusy);
      chip.title = clip.isBusy ? "Video wird vorbereitet" : "Aufnahme übernehmen";
      chip.setAttribute("aria-label", chip.title);
      const label = document.createElement("span");
      label.textContent = clip.isBusy
        ? "Video wird vorbereitet"
        : `Video aufnehmen · ${formatClipTime(elapsed)} / ${formatClipTime(maximum)}`;
      const indicator = document.createElement("span");
      indicator.className = "screen-clip-chip__indicator";
      indicator.setAttribute("aria-hidden", "true");
      chip.append(createToolIcon(toolVisuals["screenClip.toggle"][1]), label, indicator);
      chip.addEventListener("click", () => {
        if (state.screenClip?.isRecording) post("screenClip.stop", { sessionId: state.activeSessionId });
      });
      elements.activeTools.append(chip);
    }

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
    for (const attachment of state.attachments) {
      const chip = document.createElement("div");
      chip.className = "context-chip attachment";
      const icon = document.createElement("span");
      icon.textContent = String(attachment.contentType || "").startsWith("image/") ? "▧" : "◇";
      const name = document.createElement("span");
      name.textContent = attachment.fileName;
      name.title = attachment.fileName;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "×";
      remove.setAttribute("aria-label", `${attachment.fileName} entfernen`);
      remove.addEventListener("click", () => post("attachment.remove", { attachmentId: attachment.id }));
      chip.append(icon, name, remove);
      elements.documents.append(chip);
    }
    elements.contextStrip.hidden = state.documents.length === 0
      && state.attachments.length === 0
      && !state.selectedToolAction
      && !isAudioCaptureActive()
      && !isScreenClipActive();
  }

  function renderStatus() {
    const canStop = state.isRunning || Boolean(state.microphone?.isSpeaking);
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

  function renderWorkspace() {
    const active = Boolean(state.workspacePath);
    elements.workspaceButton.classList.remove("active");
    elements.workspaceButton.setAttribute("aria-pressed", String(active));
    elements.workspaceButton.title = active
      ? `Workspace: ${state.workspacePath} · Klicken zum Ändern`
      : "Workspace-Ordner freigeben";
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
    const microphone = state.microphone || {};
    const browserActive = Boolean(globalThis.goVoiceCapture?.isActive || globalThis.goVoiceCapture?.isStarting);
    const active = Boolean(microphone.isRecording || browserActive || state.voiceStarting);
    elements.microphone.classList.toggle("recording", active);
    elements.microphone.classList.toggle("speaking", Boolean(microphone.isSpeaking));
    elements.microphone.disabled = Boolean(state.voiceStarting || (microphone.isBusy && !active));
    const label = active ? "Sprachsteuerung beenden" : "Sprachsteuerung starten";
    elements.microphone.title = microphone.status && microphone.status !== "Inaktiv"
      ? `${label} · ${microphone.status}`
      : label;
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
    globalThis.goVoiceCapture.setSuspended(
      !state.microphone?.isRecording
      || Boolean(state.microphone?.isSpeaking)
      || state.voicePlaybackPending);
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
    updateScreenClipProgressMessage();
  }

  function isScreenClipActive() {
    return Boolean(state.screenClip?.isRecording || state.screenClip?.isBusy);
  }

  function formatClipTime(totalSeconds) {
    const value = Math.max(0, Math.floor(Number(totalSeconds) || 0));
    return `${String(Math.floor(value / 60)).padStart(2, "0")}:${String(value % 60).padStart(2, "0")}`;
  }

  function createScreenClipProgressMessage() {
    const article = document.createElement("article");
    article.className = "message assistant screen-clip-progress-message";
    article.dataset.screenClipProgress = "true";

    const avatar = document.createElement("div");
    avatar.className = "avatar screen-clip-progress__avatar";
    avatar.textContent = "REC";

    const body = document.createElement("div");
    body.className = "message-body";
    const meta = document.createElement("div");
    meta.className = "message-meta";
    meta.textContent = "Tool · Video aufnehmen";

    const panel = document.createElement("div");
    panel.className = "screen-clip-progress";
    const heading = document.createElement("div");
    heading.className = "screen-clip-progress__heading";
    const status = document.createElement("strong");
    status.className = "screen-clip-progress__status";
    const time = document.createElement("span");
    time.className = "screen-clip-progress__time";
    heading.append(status, time);

    const source = document.createElement("p");
    source.className = "screen-clip-progress__source";
    const track = document.createElement("div");
    track.className = "screen-clip-progress__track";
    track.setAttribute("role", "progressbar");
    track.setAttribute("aria-valuemin", "0");
    const fill = document.createElement("span");
    track.append(fill);

    const actions = document.createElement("div");
    actions.className = "screen-clip-progress__actions";
    const accept = document.createElement("button");
    accept.type = "button";
    accept.className = "screen-clip-progress__accept";
    accept.textContent = "Übernehmen";
    accept.addEventListener("click", () => post("screenClip.stop", { sessionId: state.activeSessionId }));
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "screen-clip-progress__cancel";
    cancel.textContent = "Verwerfen";
    cancel.addEventListener("click", () => post("screenClip.cancel", {}));
    actions.append(accept, cancel);

    panel.append(heading, source, track, actions);
    body.append(meta, panel);
    article.append(avatar, body);
    updateScreenClipProgressMessage(article);
    return article;
  }

  function updateScreenClipProgressMessage(existingArticle = null) {
    let article = existingArticle || elements.messageList.querySelector("[data-screen-clip-progress]");
    if (!isScreenClipActive()) {
      article?.remove();
      return;
    }
    if (!article) {
      const distanceFromEnd = elements.messageScroll.scrollHeight
        - elements.messageScroll.scrollTop
        - elements.messageScroll.clientHeight;
      article = createScreenClipProgressMessage();
      elements.messageList.append(article);
      if (distanceFromEnd < 80) {
        requestAnimationFrame(() => { elements.messageScroll.scrollTop = elements.messageScroll.scrollHeight; });
      }
      return;
    }

    const clip = state.screenClip || {};
    const elapsed = Math.max(0, Number(clip.elapsedSeconds) || 0);
    const maximum = Math.max(1, Number(clip.maximumSeconds) || 30);
    const progress = Math.max(0, Math.min(100, elapsed / maximum * 100));
    article.querySelector(".screen-clip-progress__status").textContent = clip.status
      || (clip.isBusy ? "Video wird vorbereitet" : "Bildschirmclip wird aufgenommen");
    article.querySelector(".screen-clip-progress__time").textContent = `${formatClipTime(elapsed)} / ${formatClipTime(maximum)}`;
    article.querySelector(".screen-clip-progress__source").textContent = clip.sourceLabel
      ? `Quelle: ${clip.sourceLabel}`
      : "Ausgewählte Bildschirmquelle";
    const track = article.querySelector(".screen-clip-progress__track");
    track.setAttribute("aria-valuenow", String(Math.round(progress)));
    track.setAttribute("aria-valuemax", "100");
    track.querySelector("span").style.width = `${progress}%`;
    for (const button of article.querySelectorAll(".screen-clip-progress__actions button")) {
      button.hidden = Boolean(clip.isBusy);
    }
    article.classList.toggle("busy", Boolean(clip.isBusy));
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
    state.selectedToolAction = action || null;
    for (const option of document.querySelectorAll(".service-option[data-tool-action]")) {
      option.classList.toggle("active", option.dataset.toolAction === state.selectedToolAction);
    }
    const selected = document.querySelector(`.service-option[data-tool-action="${state.selectedToolAction || ""}"] span`);
    elements.toolsButton.title = selected ? `Aktiv: ${selected.textContent}` : "Tools";
    renderContext();
    if (persist && state.activeSessionId && (previous === "code" || state.selectedToolAction === "code")) {
      post("session.mode", {
        sessionId: state.activeSessionId,
        mode: state.selectedToolAction === "code" ? "code" : "general"
      });
    }
  }

  function clearCompletedOneShotToolAction() {
    if (state.selectedToolAction && state.selectedToolAction !== "code") {
      selectToolAction(null, false);
    }
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
    const previousSessionId = state.activeSessionId;
    state.sessions = Array.isArray(payload.sessions) ? payload.sessions : [];
    state.messages = Array.isArray(payload.messages) ? payload.messages : [];
    state.workflows = Array.isArray(payload.workflows) ? payload.workflows : [];
    state.documents = Array.isArray(payload.documents) ? payload.documents : [];
    state.attachments = Array.isArray(payload.attachments) ? payload.attachments : [];
    state.activeSessionId = payload.activeSessionId || null;
    state.isRunning = Boolean(payload.isRunning);
    state.model = payload.model || null;
    state.contextUsed = payload.contextUsed || 0;
    state.contextLimit = payload.contextLimit || 8192;
    state.contextWasTruncated = Boolean(payload.contextWasTruncated);
    state.contextNotice = payload.contextNotice || null;
    state.workspacePath = payload.workspacePath || null;
    state.workspaceName = payload.workspaceName || null;
    const serverToolAction = payload.selectedToolAction || null;
    if (serverToolAction === "code"
      || previousSessionId !== state.activeSessionId
      || state.selectedToolAction === "code") {
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
    renderWorkspace();
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
    const label = session?.isPinned ? "Unpin" : "Pin";
    elements.pinSession.textContent = label;
    elements.pinSession.title = label;
    elements.pinSession.setAttribute("aria-label", label);
    elements.pinSession.disabled = !session;
  }

  function handleHostMessage(event) {
    const { type, payload } = event.detail;
    switch (type) {
      case "state.snapshot":
        applySnapshot(payload);
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
        if (Number.isFinite(payload.contextLimit)) state.contextLimit = payload.contextLimit;
        state.contextWasTruncated = Boolean(payload.contextWasTruncated);
        state.contextNotice = payload.contextNotice || null;
        state.runStatus = payload.runStatus || "Denkt nach";
        state.runDetail = payload.runDetail || null;
        if (payload.message?.id) state.messageRunStatus.set(String(payload.message.id), {
          status: payload.runStatus || "Denkt nach",
          detail: payload.runDetail || null
        });
        for (const nextMessage of [payload.userMessage, payload.message]) {
          if (!nextMessage || nextMessage.sessionId !== state.activeSessionId) continue;
          const index = state.messages.findIndex(item => item.id === nextMessage.id);
          if (index >= 0) state.messages[index] = nextMessage;
          else state.messages.push(nextMessage);
        }
        renderSessions();
        renderMessages(true);
        renderStatus();
        syncVoiceCaptureSuspension();
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
        }
        if (payload.message && payload.message.sessionId === state.activeSessionId) {
          const index = state.messages.findIndex(item => item.id === payload.message.id);
          if (index >= 0) state.messages[index] = payload.message;
          else state.messages.push(payload.message);
        }
        renderSessions();
        renderMessages(true);
        renderStatus();
        if (type === "chat.completed"
          && state.microphone?.isRecording
          && payload.message?.content
          && payload.message.content.trim() !== "Der Text wurde vorgelesen.") {
          state.voicePlaybackPending = true;
          syncVoiceCaptureSuspension();
          post("microphone.speak", { text: payload.message.content, messageId: payload.message.id });
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
        if (payload?.messageId) state.messageRunStatus.set(String(payload.messageId), {
          status: payload.runStatus || "Denkt nach",
          detail: payload.runDetail || null
        });
        renderContext();
        renderMessages(false);
        renderStatus();
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
        renderStatus();
        syncVoiceCaptureSuspension();
        renderMessages(false);
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
    post("chat.cancel", {});
    post("microphone.stopSpeech", {});
  });
  byId("pick-document").addEventListener("click", () => post("document.pick", { sessionId: state.activeSessionId }));
  elements.microphone.addEventListener("click", async () => {
    if (state.voiceStarting) return;
    const active = Boolean(state.microphone?.isRecording || globalThis.goVoiceCapture?.isActive);
    if (active) {
      state.voicePlaybackPending = false;
      state.voiceTurn = null;
      state.voiceLevel = 0;
      state.voiceFrequency = 0;
      state.voiceDominantHz = 0;
      globalThis.goVoiceCapture?.setSuspended(true);
      await globalThis.goVoiceCapture?.stop(true);
      renderMessages(false);
      renderMicrophone();
      post("microphone.stop", {});
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
    await globalThis.goVoiceCapture?.stop(false);
    state.voiceTurn = null;
    state.voiceLevel = 0;
    state.voiceFrequency = 0;
    state.voiceDominantHz = 0;
    renderMessages(false);
    renderMicrophone();
    post("microphone.stop", {});
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
  document.addEventListener("click", () => setToolsMenuOpen(false));

  byId("open-workflows").addEventListener("click", openWorkflows);
  elements.workspaceButton.addEventListener("click", () => post("workspace.pick", {}));
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
