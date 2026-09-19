(() => {
  "use strict";
  const pending = new Map();
  const storageKey = sessionId => `go.assistant.steer.v1:${sessionId}`;

  function canSteer(state) {
    return Boolean(state.isRunning && state.activeSessionId
      && (!state.activeRunSessionId || String(state.activeRunSessionId) === String(state.activeSessionId)));
  }

  function stored(sessionId) {
    if (pending.has(sessionId)) return pending.get(sessionId);
    try {
      const value = JSON.parse(globalThis.localStorage?.getItem(storageKey(sessionId)) || "null");
      if (value?.sessionId === sessionId && typeof value.inputId === "string" && typeof value.prompt === "string") return value;
    } catch { /* Optional storage must not prevent a new steering request. */ }
    return null;
  }

  function belongsToRun(request, state) {
    if (request.expectedRunId && state.activeRunId) return request.expectedRunId === state.activeRunId;
    return Boolean(request.originMessageId && request.originMessageId === state.activeRunMessageId);
  }

  function send(request, post) {
    // The message identity is local retry metadata, never part of the native contract.
    const { sessionId, prompt, inputId, expectedRunId } = request;
    post("chat.steer", { sessionId, prompt, inputId, ...(expectedRunId ? { expectedRunId } : {}) }, inputId);
  }

  function request(state, text, post) {
    if (!canSteer(state)) return null;
    const prompt = String(text || "").trim();
    if (!prompt) return null;
    const sessionId = String(state.activeSessionId);
    const previous = stored(sessionId);
    const runId = state.activeRunId || null;
    const request = previous?.prompt === prompt && belongsToRun(previous, state)
      ? { ...previous, ...(runId ? { expectedRunId: runId } : {}) }
      : { sessionId, prompt, inputId: globalThis.crypto.randomUUID(), originMessageId: state.activeRunMessageId || null,
          ...(runId ? { expectedRunId: runId } : {}) };
    pending.set(sessionId, request);
    try { globalThis.localStorage?.setItem(storageKey(sessionId), JSON.stringify(request)); } catch { /* Retry still works in this page. */ }
    send(request, post);
    return request;
  }

  function observeRun(state) {
    if (!canSteer(state) || !state.activeRunId) return;
    const sessionId = String(state.activeSessionId);
    const request = stored(sessionId);
    if (!request || request.expectedRunId || !belongsToRun(request, state)) return;
    const bound = { ...request, expectedRunId: state.activeRunId };
    pending.set(sessionId, bound);
    try { globalThis.localStorage?.setItem(storageKey(sessionId), JSON.stringify(bound)); } catch { /* In-memory retry remains available. */ }
  }

  function pendingRetry(state, text) {
    const previous = stored(String(state.activeSessionId || ""));
    return previous?.prompt === String(text || "").trim() ? previous : null;
  }

  function retryPending(state, text, post) {
    const previous = pendingRetry(state, text);
    if (!previous || canSteer(state) && belongsToRun(previous, state)) return null;
    if (!previous.expectedRunId) return "unbound";
    send(previous, post);
    return "sent";
  }

  function accept(payload, state, composerText) {
    const sessionId = String(payload?.sessionId || "");
    const request = stored(sessionId);
    if (!request || request.inputId !== payload?.inputId) return { accepted: false, clearDraft: false };
    pending.delete(sessionId);
    try { globalThis.localStorage?.removeItem(storageKey(sessionId)); } catch { /* Optional storage. */ }
    const sameSession = sessionId === String(state.activeSessionId || "");
    return { accepted: true, clearDraft: sameSession && String(composerText || "").trim() === request.prompt, sameSession };
  }

  function ownsRequest(requestId, state) {
    if (!requestId) return false;
    if ([...pending.values()].some(request => request.inputId === requestId)) return true;
    return stored(String(state.activeSessionId || ""))?.inputId === requestId;
  }

  globalThis.goRunSteering = { canSteer, request, accept, ownsRequest, observeRun, pendingRetry, retryPending };
})();
