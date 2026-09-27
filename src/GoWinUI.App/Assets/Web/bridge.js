(function () {
  "use strict";

  const version = 1;
  const clientId = (() => {
    try {
      const saved = globalThis.sessionStorage?.getItem("assistant.lan.client-id");
      if (saved) return saved;
      const created = globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
      globalThis.sessionStorage?.setItem("assistant.lan.client-id", created);
      return created;
    } catch {
      return globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }
  })();
  let lanSocket = null;
  let lanEvents = null;
  let lanHttpReady = false;
  let lanTransportWasReady = false;
  let lanHttpSendChain = Promise.resolve();
  let browserSpeech = null;
  let browserSpeechPaused = false;
  let browserSpeechGeneration = 0;
  const pendingLanMessages = [];
  const allowedOutbound = new Set([
    "app.ready", "conversation.refresh", "chat.send", "chat.steer", "chat.cancel", "session.create", "session.open",
    "mode.switch", "action.invoke",
    "reasoning.get", "reasoning.set",
    "session.rename", "session.delete", "session.clear", "session.draft", "session.groupCollapse", "session.projectCreate", "session.workspaceCreate", "document.pick", "document.paste", "document.upload",
    "document.remove", "workflow.list", "workflow.insert", "workflow.create",
    "workflow.update", "workflow.delete",
    "memory.list", "memory.create", "memory.update", "memory.pin", "memory.confirm", "memory.delete", "memory.autoCapture",
    "research.list", "research.open", "research.export",
    "workflow.createFromMessage", "message.exportPdf", "message.copy",
    "attachment.remove", "coding.pickWorkspace",
    "artifact.save", "artifact.preview", "artifact.open", "screen.capture", "screenClip.start", "screenClip.stop", "screenClip.cancel",
    "audioCapture.start", "audioCapture.stop", "audioCapture.cancel",
    "microphone.start", "microphone.audio", "microphone.speak", "microphone.stopSpeech", "microphone.toggleSpeechPause", "microphone.stop", "microphone.cancel",
    "liveCaption.start", "liveCaption.stop", "ui.sessionPane", "external.open"
  ]);
  const allowedInbound = new Set([
    "state.snapshot", "conversation.snapshot", "conversation.messageCommitted", "action.completed",
    "reasoning.snapshot",
    "chat.queued", "queue.changed", "chat.started", "chat.delta", "chat.completed", "chat.steer.accepted",
    "chat.cancelled", "chat.failed", "coding.changes", "session.changed", "session.grouped", "workflow.snapshot",
    "workflow.changed", "workflow.draft", "memory.snapshot", "memory.changed", "research.snapshot", "research.exported", "document.changed", "document.import.started", "document.import.progress", "document.import.completed", "status.changed", "speech.status", "speech.progress", "theme.changed",
    "draft.saved", "caption.changed", "screenClip.changed", "audioCapture.changed", "capture.required", "capture.cancelled",
    "microphone.changed", "microphone.transcript", "artifact.previewReady", "host.error"
  ]);

  function newRequestId() {
    if (globalThis.crypto && typeof globalThis.crypto.randomUUID === "function") {
      return globalThis.crypto.randomUUID();
    }
    return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  }

  function isLanBrowser() {
    return !globalThis.chrome?.webview && location.protocol.startsWith("http");
  }

  function browserResourceUrl(value) {
    const raw = String(value || "").trim();
    if (!raw || !isLanBrowser()) return raw;
    let source;
    try { source = new URL(raw, location.href); } catch { return raw; }
    const path = source.pathname.replace(/^\/+/, "");
    const suffix = `${source.search}${source.hash}`;
    if (source.hostname === "assistant-preview.local") {
      return new URL(`preview/${path}${suffix}`, location.href).href;
    }
    if (source.hostname === "assistant.local" && path.startsWith("artifacts/")) {
      return new URL(`${path}${suffix}`, location.href).href;
    }
    if (source.hostname === "assistant-coding-preview.local" && path.startsWith("coding/")) {
      return new URL(`coding-preview/${path.slice("coding/".length)}${suffix}`, location.href).href;
    }
    return raw;
  }

  function fileToBase64(file) {
    return file.arrayBuffer().then(buffer => {
      const bytes = new Uint8Array(buffer);
      let binary = "";
      for (let offset = 0; offset < bytes.length; offset += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(offset, Math.min(offset + 0x8000, bytes.length)));
      }
      return globalThis.btoa(binary);
    });
  }

  async function uploadFiles(files, sessionId, requestId) {
    const selected = Array.from(files || []).filter(file => file instanceof File);
    if (!sessionId || selected.length === 0) return false;
    if (selected.length > 32) {
      globalThis.alert("Wähle höchstens 32 Dateien gleichzeitig aus.");
      return false;
    }
    const totalBytes = selected.reduce((sum, file) => sum + Number(file.size || 0), 0);
    if (selected.some(file => file.size <= 0 || file.size > 32 * 1024 * 1024)
      || totalBytes > 48 * 1024 * 1024) {
      globalThis.alert("Die Auswahl überschreitet das Upload-Limit von 32 MB je Datei beziehungsweise 48 MB insgesamt.");
      return false;
    }
    try {
      const payloadFiles = await Promise.all(selected.map(async (file, index) => ({
        fileName: browserUploadFileName(file, index),
        contentType: file.type || "application/octet-stream",
        base64: await fileToBase64(file)
      })));
      post("document.upload", { sessionId, files: payloadFiles }, requestId);
      return true;
    } catch {
      globalThis.alert("Die ausgewählten Dateien konnten nicht gelesen werden.");
      return false;
    }
  }

  function browserUploadFileName(file, index) {
    const extensionByType = {
      "image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp", "image/gif": ".gif",
      "application/pdf": ".pdf", "text/plain": ".txt", "audio/wav": ".wav", "audio/mpeg": ".mp3",
      "video/mp4": ".mp4"
    };
    const supplied = String(file?.name || "").split(/[\\/]/).pop().trim()
      .replace(/[\\/:*?"<>|\u0000-\u001f]/g, "-").slice(0, 240);
    if (supplied) return supplied;
    const extension = extensionByType[String(file?.type || "").toLocaleLowerCase()] || ".bin";
    return `Zwischenablage-${Date.now()}-${index + 1}${extension}`;
  }

  function pickBrowserFiles(sessionId, requestId) {
    const input = document.createElement("input");
    input.type = "file";
    input.multiple = true;
    input.hidden = true;
    const pickFolder = typeof input.webkitdirectory !== "undefined"
      && globalThis.confirm("Möchtest du einen Ordner auswählen? OK wählt einen Ordner, Abbrechen einzelne Dateien.");
    if (pickFolder) input.webkitdirectory = true;
    const cleanup = () => input.remove();
    input.addEventListener("change", () => {
      void uploadFiles(input.files, sessionId, requestId).finally(cleanup);
    }, { once: true });
    input.addEventListener("cancel", cleanup, { once: true });
    document.body.append(input);
    input.click();
  }

  function copyWithSelection(text) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.append(area);
    area.select();
    const copied = document.execCommand("copy");
    area.remove();
    if (!copied) globalThis.alert("Der Text konnte nicht in die Zwischenablage kopiert werden.");
    return copied;
  }

  function copyInBrowser(text) {
    const value = String(text || "");
    if (globalThis.navigator?.clipboard?.writeText) {
      void globalThis.navigator.clipboard.writeText(value).catch(() => copyWithSelection(value));
      return;
    }
    copyWithSelection(value);
  }

  function emitLocal(type, payload, requestId) {
    receive({ data: { version, type, requestId: requestId || newRequestId(), payload } });
  }

  function browserSpeechState(active, requestId, error = null) {
    emitLocal("speech.status", {
      active,
      status: active ? (browserSpeechPaused ? "Pausiert" : "Sprachausgabe wird wiedergegeben") : (error ? "Fehlgeschlagen" : "Abgeschlossen"),
      detail: "Wiedergabe auf diesem Browser-PC",
      model: "Browser-Sprachausgabe",
      error
    }, requestId);
    emitLocal("microphone.changed", {
      isRecording: false,
      isBusy: active,
      isSpeaking: active,
      canPauseSpeech: active,
      isSpeechPaused: active && browserSpeechPaused,
      status: active ? (browserSpeechPaused ? "Pausiert" : "Spricht") : "Inaktiv",
      provider: "browser",
      error
    }, requestId);
  }

  function finishBrowserSpeech(requestId, generation, error = null) {
    if (generation !== browserSpeechGeneration) return;
    browserSpeech = null;
    browserSpeechPaused = false;
    browserSpeechState(false, requestId, error);
  }

  function speakInBrowser(text, requestId) {
    if (!globalThis.speechSynthesis || typeof globalThis.SpeechSynthesisUtterance !== "function") {
      emitLocal("host.error", { message: "Dieser Browser unterstützt keine lokale Sprachausgabe." }, requestId);
      return;
    }
    browserSpeechGeneration += 1;
    const generation = browserSpeechGeneration;
    globalThis.speechSynthesis.cancel();
    const spokenText = String(text || "")
      .replace(/```[\s\S]*?```/g, " Codeblock. ")
      .replace(/[`*_>#\[\]()~-]+/g, " ")
      .replace(/\s+/g, " ").trim();
    if (!spokenText) return;
    const utterance = new globalThis.SpeechSynthesisUtterance(spokenText);
    utterance.lang = document.documentElement.lang || "de-DE";
    utterance.onstart = () => browserSpeechState(true, requestId);
    utterance.onend = () => finishBrowserSpeech(requestId, generation);
    utterance.onerror = event => finishBrowserSpeech(
      requestId, generation, event?.error === "canceled" ? null : "Die Browser-Sprachausgabe ist fehlgeschlagen.");
    browserSpeech = utterance;
    browserSpeechPaused = false;
    globalThis.speechSynthesis.speak(utterance);
  }

  function stopBrowserSpeech(requestId) {
    browserSpeechGeneration += 1;
    globalThis.speechSynthesis?.cancel();
    browserSpeech = null;
    browserSpeechPaused = false;
    browserSpeechState(false, requestId);
  }

  function toggleBrowserSpeech(requestId) {
    if (!browserSpeech || !globalThis.speechSynthesis) return;
    if (browserSpeechPaused) globalThis.speechSynthesis.resume();
    else globalThis.speechSynthesis.pause();
    browserSpeechPaused = !browserSpeechPaused;
    browserSpeechState(true, requestId);
  }

  async function printInBrowser(messageId) {
    if (!globalThis.goPrepareBookPdf?.(messageId || null)) return;
    let finished = false;
    const finish = () => {
      if (finished) return;
      finished = true;
      globalThis.goFinishBookPdf?.();
    };
    globalThis.addEventListener("afterprint", finish, { once: true });
    for (let attempt = 0; attempt < 40 && !globalThis.goPdfBookReady?.(); attempt += 1) {
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    globalThis.print();
    setTimeout(finish, 60_000);
  }

  function handleBrowserAction(envelope) {
    if (!isLanBrowser()) return false;
    const payload = envelope.payload || {};
    if (envelope.type === "document.pick"
      || (envelope.type === "action.invoke" && payload.actionId === "builtin.workspace/attach-files-and-folders")) {
      pickBrowserFiles(payload.sessionId, envelope.requestId);
      return true;
    }
    if (envelope.type === "message.copy") {
      copyInBrowser(payload.text);
      return true;
    }
    if (envelope.type === "microphone.speak") {
      speakInBrowser(payload.text, envelope.requestId);
      return true;
    }
    if (envelope.type === "microphone.stopSpeech") {
      stopBrowserSpeech(envelope.requestId);
      return true;
    }
    if (envelope.type === "microphone.toggleSpeechPause") {
      toggleBrowserSpeech(envelope.requestId);
      return true;
    }
    if (envelope.type === "message.exportPdf"
      || (envelope.type === "action.invoke" && payload.actionId === "builtin.documents/export-chat-pdf")) {
      void printInBrowser(envelope.type === "message.exportPdf" ? payload.messageId : null);
      return true;
    }
    if ((envelope.type === "artifact.open" || envelope.type === "artifact.save") && payload.artifactId) {
      const suffix = envelope.type === "artifact.save" ? "?download=1" : "";
      const url = new URL(`artifacts/${encodeURIComponent(payload.artifactId)}${suffix}`, location.href).href;
      if (envelope.type === "artifact.open") globalThis.open(url, "_blank", "noopener,noreferrer");
      else {
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = "";
        document.body.append(anchor);
        anchor.click();
        anchor.remove();
      }
      return true;
    }
    if (envelope.type === "external.open" && payload.url) {
      let url;
      try { url = new URL(String(payload.url)); } catch { return true; }
      if (url.protocol === "http:" || url.protocol === "https:") {
        globalThis.open(url.href, "_blank", "noopener,noreferrer");
      }
      return true;
    }
    return false;
  }

  function post(type, payload, requestId) {
    if (!allowedOutbound.has(type)) {
      throw new Error(`Nicht erlaubter Bridge-Typ: ${type}`);
    }
    const envelope = { version, type, requestId: requestId || newRequestId(), payload: payload || {} };
    if (isLanBrowser()) envelope.clientId = clientId;
    if (handleBrowserAction(envelope)) return envelope.requestId;
    if (globalThis.chrome?.webview) {
      globalThis.chrome.webview.postMessage(envelope);
    } else if (location.protocol.startsWith("http")) {
      const serialized = JSON.stringify(envelope);
      if (lanSocket?.readyState === 1) lanSocket.send(serialized);
      else if (lanHttpReady) sendHttp(serialized);
      else pendingLanMessages.push(serialized);
    } else {
      throw new Error("Die Assistenten-Bridge ist noch nicht verbunden.");
    }
    return envelope.requestId;
  }

  function configureContract(contract) {
    if (!contract || Number(contract.version) !== version) return false;
    const outbound = Array.isArray(contract.clientActions) ? contract.clientActions : [];
    const inbound = Array.isArray(contract.hostEvents) ? contract.hostEvents : [];
    if (!outbound.includes("app.ready") || !inbound.includes("state.snapshot") || !inbound.includes("host.error")) return false;
    allowedOutbound.clear();
    outbound.forEach(type => { if (typeof type === "string" && type) allowedOutbound.add(type); });
    allowedInbound.clear();
    inbound.forEach(type => { if (typeof type === "string" && type) allowedInbound.add(type); });
    return true;
  }

  function receive(event) {
    const envelope = event.data;
    if (!envelope || envelope.version !== version || typeof envelope.type !== "string"
      || !allowedInbound.has(envelope.type) || typeof envelope.payload !== "object") {
      return;
    }
    globalThis.dispatchEvent(new CustomEvent("go:host-message", { detail: envelope }));
  }

  if (globalThis.chrome?.webview) {
    globalThis.chrome.webview.addEventListener("message", receive);
  }

  function flushPending(send) {
    while (pendingLanMessages.length > 0) send(pendingLanMessages.shift());
    globalThis.dispatchEvent(new CustomEvent("go:bridge-ready"));
  }

  function sendHttp(serialized) {
    let requestId = null;
    try { requestId = JSON.parse(serialized)?.requestId || null; } catch { /* Local validation reports below. */ }
    lanHttpSendChain = lanHttpSendChain.then(async () => {
      const response = await fetch(new URL("message", location.href), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: serialized,
        cache: "no-store"
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
    }).catch(error => {
      receive({ data: {
        version,
        type: "host.error",
        requestId,
        payload: { message: `Die LAN-Verbindung konnte eine Aktion nicht übertragen (${error?.message || "Netzwerkfehler"}).` }
      } });
    });
    return lanHttpSendChain;
  }

  function readyEnvelope() {
    return JSON.stringify({ version, type: "app.ready", requestId: newRequestId(), payload: {}, clientId });
  }

  function connectHttpBridge() {
    if (lanEvents || typeof EventSource !== "function" || typeof fetch !== "function") return;
    const eventUrl = new URL("events", location.href);
    eventUrl.searchParams.set("clientId", clientId);
    lanEvents = new EventSource(eventUrl);
    lanEvents.addEventListener("message", event => {
      try { receive({ data: JSON.parse(event.data) }); } catch { /* Reject malformed host data. */ }
    });
    lanEvents.addEventListener("open", () => {
      const pendingHasReady = pendingLanMessages.some(serialized => {
        try { return JSON.parse(serialized)?.type === "app.ready"; } catch { return false; }
      });
      lanHttpReady = true;
      if (lanTransportWasReady || !pendingHasReady) sendHttp(readyEnvelope());
      lanTransportWasReady = true;
      flushPending(sendHttp);
    });
    lanEvents.addEventListener("error", () => {
      lanHttpReady = false;
    });
  }

  if (isLanBrowser()) {
    if (typeof WebSocket === "function") {
      const socketProtocol = location.protocol === "https:" ? "wss:" : "ws:";
      try {
        const bridgeUrl = new URL("bridge", `${socketProtocol}//${location.host}${location.pathname}`);
        bridgeUrl.searchParams.set("clientId", clientId);
        lanSocket = new WebSocket(bridgeUrl.href);
      } catch {
        lanSocket = null;
        connectHttpBridge();
      }
      if (!lanSocket) return;
      lanSocket.addEventListener("message", event => {
        try { receive({ data: JSON.parse(event.data) }); } catch { /* Reject malformed host data. */ }
      });
      lanSocket.addEventListener("open", () => {
        lanTransportWasReady = true;
        flushPending(serialized => lanSocket.send(serialized));
      });
      lanSocket.addEventListener("close", () => {
        lanSocket = null;
        connectHttpBridge();
      });
    } else {
      connectHttpBridge();
    }
  }

  globalThis.goBridge = Object.freeze({
    post,
    version,
    clientId,
    isLanBrowser: isLanBrowser(),
    uploadFiles,
    resourceUrl: browserResourceUrl,
    configureContract
  });
})();
