(function () {
  "use strict";

  const version = 1;
  const allowedOutbound = new Set([
    "app.ready", "chat.send", "chat.cancel", "session.create", "session.open",
    "session.rename", "session.pin", "session.delete", "session.clear", "session.draft", "document.pick",
    "document.remove", "workflow.list", "workflow.insert", "workflow.create",
    "workflow.update", "workflow.delete",
    "workflow.createFromMessage", "chat.exportPdf", "message.exportPdf", "message.copy",
    "attachment.remove",
    "artifact.save", "artifact.preview", "screen.capture", "screenClip.start", "screenClip.stop", "screenClip.cancel",
    "audioCapture.start", "audioCapture.stop", "audioCapture.cancel",
    "microphone.start", "microphone.audio", "microphone.speak", "microphone.stopSpeech", "microphone.toggleSpeechPause", "microphone.stop", "microphone.cancel",
    "liveCaption.start", "liveCaption.stop", "workspace.pick", "session.mode", "session.tool", "ui.sessionPane", "external.open"
  ]);
  const allowedInbound = new Set([
    "state.snapshot", "chat.started", "chat.delta", "chat.completed",
    "chat.cancelled", "chat.failed", "session.changed", "workflow.snapshot",
    "workflow.changed", "workflow.draft", "document.changed", "status.changed", "speech.status", "speech.progress", "theme.changed",
    "draft.saved", "caption.changed", "screenClip.changed", "audioCapture.changed", "capture.required", "capture.cancelled",
    "microphone.changed", "microphone.transcript", "composer.transcript", "artifact.previewReady",
    "host.error"
  ]);

  function newRequestId() {
    if (globalThis.crypto && typeof globalThis.crypto.randomUUID === "function") {
      return globalThis.crypto.randomUUID();
    }
    return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  }

  function post(type, payload, requestId) {
    if (!allowedOutbound.has(type)) {
      throw new Error(`Nicht erlaubter Bridge-Typ: ${type}`);
    }
    if (!globalThis.chrome?.webview) {
      throw new Error("Die native GO-Bridge ist nicht verfügbar.");
    }
    const envelope = { version, type, requestId: requestId || newRequestId(), payload: payload || {} };
    globalThis.chrome.webview.postMessage(envelope);
    return envelope.requestId;
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

  globalThis.goBridge = Object.freeze({ post, version });
})();
