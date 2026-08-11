(function () {
  "use strict";

  const version = 1;
  const allowedOutbound = new Set([
    "app.ready", "chat.send", "chat.cancel", "session.create", "session.open",
    "session.rename", "session.delete", "session.draft", "document.pick",
    "document.remove", "workflow.list", "workflow.select", "workflow.create",
    "workflow.update", "workflow.delete", "workflow.clone",
    "workflow.createFromMessage", "chat.exportPdf", "message.copy", "external.open"
  ]);
  const allowedInbound = new Set([
    "state.snapshot", "chat.started", "chat.delta", "chat.completed",
    "chat.cancelled", "chat.failed", "session.changed", "workflow.snapshot",
    "workflow.changed", "document.changed", "status.changed", "theme.changed",
    "draft.saved", "host.error"
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
