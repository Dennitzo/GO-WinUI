(function () {
  "use strict";

  const version = 1;
  const allowedOutbound = new Set([
    "app.ready", "conversation.refresh", "chat.send", "chat.steer", "chat.cancel", "session.create", "session.open",
    "reasoning.get", "reasoning.set",
    "session.rename", "session.pin", "session.delete", "session.clear", "session.draft", "session.groupCollapse", "session.projectCreate", "session.workspaceCreate", "document.pick",
    "document.remove", "workflow.list", "workflow.insert", "workflow.create",
    "workflow.update", "workflow.delete",
    "workflow.createFromMessage", "chat.exportPdf", "message.exportPdf", "message.copy",
    "attachment.remove", "coding.pickWorkspace",
    "artifact.save", "artifact.preview", "artifact.open", "screen.capture", "screenClip.start", "screenClip.stop", "screenClip.cancel",
    "audioCapture.start", "audioCapture.stop", "audioCapture.cancel",
    "microphone.start", "microphone.audio", "microphone.speak", "microphone.stopSpeech", "microphone.toggleSpeechPause", "microphone.stop", "microphone.cancel",
    "liveCaption.start", "liveCaption.stop", "session.tool", "ui.sessionPane", "external.open"
  ]);
  const allowedInbound = new Set([
    "state.snapshot", "conversation.snapshot", "conversation.messageCommitted",
    "reasoning.snapshot",
    "chat.started", "chat.delta", "chat.completed", "chat.steer.accepted",
    "chat.cancelled", "chat.failed", "coding.changes", "session.changed", "session.grouped", "workflow.snapshot",
    "workflow.changed", "workflow.draft", "document.changed", "document.import.started", "document.import.progress", "document.import.completed", "status.changed", "speech.status", "speech.progress", "theme.changed",
    "draft.saved", "caption.changed", "screenClip.changed", "audioCapture.changed", "capture.required", "capture.cancelled",
    "microphone.changed", "microphone.transcript", "artifact.previewReady", "host.error"
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
