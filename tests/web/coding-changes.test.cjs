const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
const scope = { sessionId: "session-a", messageId: "answer-a", workspacePath: "C:\\Projects\\Example", isCoding: true };
const answer = { id: scope.messageId, role: "assistant", content: "", status: "streaming" };
const patch = "diff --git a/math.py b/math.py\n--- a/math.py\n+++ b/math.py\n@@ -1 +1,2 @@\n-return 1\n+return 2\n+# Added";
const file = extra => ({ path: "math.py", addedLines: 2, removedLines: 1, diff: patch, kind: "modified", isBinary: false, ...extra });
const summary = extra => ({ ...scope, runId: "run-a", revision: 1000, files: [file()], isPartial: false, ...extra });

function loadAppFunction(context, name) {
  const start = app.indexOf(`  function ${name}(`);
  const ending = app.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
  const end = ending ? start + ending.index + ending[0].length : -1;
  assert.ok(start >= 0 && end > start, `production ${name} exists`);
  vm.runInContext(app.slice(start, end), context, { filename: `app.js:${name}` });
}

function harness() {
  const document = {
    body: new TestNode("body"),
    createElement: tag => new TestNode(tag),
    createTextNode: text => new TestNode("#text", text),
    createDocumentFragment: () => new TestNode("#fragment")
  };
  const host = new TestNode("div");
  document.body.append(host);
  const posts = [];
  const state = { activeSessionId: scope.sessionId, codingWorkspacePath: scope.workspacePath,
    selectedToolAction: "coding", messages: [{ ...answer }], documents: [], attachments: [],
    messageRunStatus: new Map(), changesSummary: null };
  const elements = { codingChanges: host, contextStrip: new TestNode("div") };
  let layoutCalls = 0;
  const context = vm.createContext({ document, state, elements, URL,
    goBridge: { post: (type, payload) => posts.push({ type, payload }) },
    schedulePromptResize: () => layoutCalls++, isAudioCaptureActive: () => false, isScreenClipActive: () => false,
    pruneTerminalMessageRunStatuses() {}, recordCodingActivity() {}, renderSessions() {}, renderStatus() {},
    syncVoiceCaptureSuspension() {}, clearCompletedOneShotToolAction() {},
    renderMicrophone() {}, clearSpeechHighlight() {},
    setTimeout() {}
  });
  for (const script of ["coding-timeline.js", "coding-changes.js"])
    vm.runInContext(fs.readFileSync(path.join(webRoot, script), "utf8"), context, { filename: script });
  for (const name of ["renderCodingChanges", "applyCodingChanges", "updateContextStripVisibility",
    "sortCommittedMessages", "conversationMessagesDiffer", "applyConversationSnapshot", "handleHostMessage", "belongsToActiveSession", "persistMeasuredContext",
    "cleanStatusMetadata", "uniqueStatusParts", "renderSpeechStatus"])
    loadAppFunction(context, name);
  context.renderMessages = () => context.renderCodingChanges();
  context.renderContext = () => context.renderCodingChanges();
  context.renderCodingChanges();
  return { context, state, elements, host, document, posts, layoutCalls: () => layoutCalls,
    update: (value, current = scope) => host._view.update(value, current),
    async open() { await host.firstChild.dispatch("click"); return document.body.querySelector("dialog"); }
  };
}

test("the chip reflects the latest net snapshot, unique Windows paths and complete rollback", () => {
  const { context, host, update } = harness();
  const initial = summary({ files: [file({ path: "src\\Math.py", addedLines: 90 }),
    file({ path: "SRC/math.py", addedLines: 3 }), file({ path: "tests/test_math.py", addedLines: 104, removedLines: 0, kind: "added" })] });
  update(initial);
  assert.equal(host.hidden, false);
  assert.equal(host.querySelector(".coding-changes-chip__label").textContent, "2 Dateien geändert");
  assert.equal(host.querySelector(".coding-changes-added").textContent, "+107");
  assert.equal(host.querySelector(".coding-changes-removed").textContent, "−1");
  assert.equal(context.goCodingChanges.normalize(initial).files.length, 2);
  update(summary({ revision: 1001, files: [file({ addedLines: 1, removedLines: 0 })] }));
  assert.equal(host.querySelector(".coding-changes-chip__label").textContent, "1 Datei geändert");
  assert.equal(host.querySelector(".coding-changes-added").textContent, "+1", "updates replace earlier net changes, never sum tool patches");
  update(summary({ revision: 1002, files: [] }));
  assert.equal(host.hidden, true, "reverting to the run baseline removes the chip");
});

test("unknown or partial scans stay honest and binary/added/deleted kinds come from the host contract", async () => {
  const { host, update, open } = harness();
  update(summary({ files: [], isPartial: true, notice: "Der ursprüngliche Dateistand ist nicht verfügbar." }));
  assert.equal(host.hidden, false);
  assert.equal(host.querySelector(".coding-changes-chip__label").textContent, "Dateiänderungen");
  assert.equal(host.querySelectorAll(".coding-changes-added, .coding-changes-removed").length, 0);
  const dialog = await open();
  assert.ok(dialog.textContent.includes("Der ursprüngliche Dateistand ist nicht verfügbar."));
  assert.equal(dialog.textContent.includes("0 Dateien"), false);
  update(summary({ files: [file({ path: "image.png", diff: "", isBinary: true, addedLines: null, removedLines: null }),
    file({ path: "new.py", kind: "added" }), file({ path: "old.py", kind: "deleted" })] }));
  assert.deepEqual(dialog.querySelectorAll(".coding-changes-file__kind").map(node => node.textContent), ["Binär", "Neu", "Gelöscht"]);
  assert.ok(dialog.textContent.includes("Binäre Datei geändert"));
  assert.equal(host.querySelectorAll(".coding-changes-added").length, 0, "unknown binary counts do not become an invented total");
});

test("real unified diffs preserve full literal output, line numbers and exact copy without rendering HTML", async () => {
  const { document, posts, update, open } = harness();
  const injected = '<script>parent.goBridge.post("session.clear", {})</script>';
  const lines = Array.from({ length: 1500 }, (_, index) => `+line ${index} ${injected}`);
  const fullPatch = `diff --git a/a.txt b/a.txt\r\n--- a/a.txt\r\n+++ b/a.txt\r\n@@ -1 +1,1500 @@\r\n-old\r\n${lines.join("\r\n")}\r\n\\ No newline at end of file`;
  update(summary({ files: [file({ path: '<img src=x onerror="alert(1)">.txt', diff: fullPatch, addedLines: 1500 })] }));
  const dialog = await open();
  assert.equal(dialog.querySelectorAll(".coding-changes-line--added").length, 1500);
  assert.equal(dialog.querySelector(".coding-changes-line--removed .coding-changes-line__number").textContent, "1");
  assert.equal(dialog.querySelectorAll(".coding-changes-line--added").at(-1).querySelectorAll(".coding-changes-line__number")[1].textContent, "1500");
  assert.ok(dialog.textContent.includes(lines.at(-1)));
  assert.ok(dialog.textContent.includes("\\ No newline at end of file"));
  assert.equal(document.body.querySelectorAll("script, img").length, 0, "all paths and patch lines must stay literal DOM text");
  await dialog.querySelector(".coding-changes-copy").dispatch("click");
  assert.equal(posts.at(-1).type, "message.copy");
  assert.equal(posts.at(-1).payload.text, fullPatch);
});

test("an open overview preserves disclosure choices, unchanged diff nodes and reader scroll across live updates", async () => {
  const { host, update, open, layoutCalls } = harness();
  const first = summary({ files: [file(), file({ path: "other.py" })] });
  update(first);
  const dialog = await open();
  const body = dialog.querySelector(".coding-changes-dialog__body");
  body.scrollTop = 550;
  const [one, two] = dialog.querySelectorAll("details");
  assert.equal(one.hasAttribute("open"), true);
  assert.equal(two.hasAttribute("open"), true, "every file's changes are visible by default");
  assert.ok(two.querySelector(".coding-changes-file__content"));
  one.removeAttribute("open"); await one.dispatch("toggle");
  assert.equal(one.querySelector(".coding-changes-file__content"), null, "manually collapsed files release their hidden diff");
  const originalContent = two.querySelector(".coding-changes-file__content");
  const originalLine = two.querySelector(".coding-changes-line--added");
  update(summary({ revision: 1001, files: [file({ addedLines: 3, diff: patch + "\n+latest" }), file({ path: "other.py" }), file({ path: "new.py" })] }));
  assert.equal(dialog.querySelectorAll("details")[0], one);
  assert.equal(one.hasAttribute("open"), false);
  assert.equal(dialog.querySelectorAll("details")[2].hasAttribute("open"), true,
    "a newly received file opens while existing manual choices remain unchanged");
  assert.equal(two.querySelector(".coding-changes-file__content"), originalContent);
  assert.equal(two.querySelector(".coding-changes-line--added"), originalLine);
  assert.equal(body.scrollTop, 550);
  assert.equal(layoutCalls(), 1, "the hidden-to-visible transition resizes the composer once");
  update(summary({ revision: 1002, files: [file({ path: "other.py", diff: patch + "\n+changed" })] }));
  assert.equal(dialog.querySelectorAll("details").length, 1);
  assert.equal(two.hasAttribute("open"), true);
  assert.notEqual(two.querySelector(".coding-changes-file__content"), originalContent, "new file output invalidates only that diff");
  assert.ok(two.textContent.includes("+changed"));
  await dialog.querySelector(".coding-changes-close").dispatch("click");
  assert.equal(host.firstChild.getAttribute("aria-expanded"), "false");
  const reopened = await open();
  assert.equal(reopened.querySelector("details").hasAttribute("open"), true);
});

test("new messages, sessions, workspaces and General mode close or hide an unrelated run overview", async () => {
  for (const different of [{ messageId: "answer-b" }, { sessionId: "session-b" },
    { workspacePath: "C:\\Projects\\Other" }, { isCoding: false }]) {
    const { document, host, update, open } = harness();
    update(summary()); await open();
    assert.ok(document.body.querySelector("dialog"));
    update(summary(), { ...scope, ...different });
    assert.equal(host.hidden, true);
    assert.equal(document.body.querySelector("dialog"), null);
  }
});

test("the real event and snapshot path rejects stale revisions and foreign sessions while restoring a persisted latest run", () => {
  const { context, state, host } = harness();
  const emit = payload => context.handleHostMessage({ detail: { type: "coding.changes", payload } });
  const current = summary({ revision: 1003 });
  emit(current);
  assert.equal(state.changesSummary, current);
  for (const stale of [summary({ revision: 1002, files: [] }), summary({ revision: 1003, files: [] }),
    summary({ sessionId: "session-b", revision: 1004 }), summary({ messageId: "answer-b", revision: 1004 })]) emit(stale);
  assert.equal(state.changesSummary, current);
  context.applyConversationSnapshot({ activeSessionId: scope.sessionId, messages: [answer], changesSummary: summary({ revision: 1001, files: [] }) });
  assert.equal(state.changesSummary, current, "an older asynchronous snapshot must not roll back the live net changes");
  context.applyConversationSnapshot({ activeSessionId: scope.sessionId, messages: [answer], changesSummary: null });
  assert.equal(state.changesSummary, current, "a missing cache must not erase a newer live same-message summary");
  state.changesSummary = null;
  context.applyConversationSnapshot({ activeSessionId: scope.sessionId, messages: [answer], changesSummary: JSON.parse(JSON.stringify(current)) });
  assert.equal(host.hidden, false);
  assert.equal(host.querySelector(".coding-changes-added").textContent, "+2");
  context.applyConversationSnapshot({ activeSessionId: scope.sessionId, messages: [answer, { ...answer, id: "answer-b" }], changesSummary: null });
  assert.equal(host.hidden, true, "the preceding run's summary must not leak into the next answer");
});

test("chat start clears the previous run and resume can immediately restore its authoritative summary", () => {
  const { context, state, host } = harness();
  context.applyCodingChanges(summary());
  context.handleHostMessage({ detail: { type: "chat.started", payload: { message: answer } } });
  assert.equal(state.changesSummary, null);
  assert.equal(host.hidden, true);
  context.handleHostMessage({ detail: { type: "coding.changes", payload: summary({ revision: 1001 }) } });
  assert.equal(host.hidden, false);
  assert.equal(state.changesSummary.revision, 1001);
});

test("speech uses only the visible Vorlesen label while retaining accessible live details and Pause/Stop state", () => {
  const { context, state, elements } = harness();
  for (const name of ["composerSpeechStatus", "composerSpeechDetail", "composerSpeechPause", "composerSpeechPauseIcon", "composerSpeechStop"])
    elements[name] = new TestNode("span");
  state.selectedToolAction = null;
  state.microphone = { canPauseSpeech: false, isSpeechPaused: false };
  state.speechStatus = { active: true, status: "Antwort wird fortlaufend vorgelesen", detail: "Warte auf weiteren Antworttext.", model: "local-voice" };
  context.renderSpeechStatus();
  assert.equal(elements.composerSpeechStatus.hidden, false);
  assert.equal(elements.composerSpeechPause.disabled, true);
  assert.equal(elements.composerSpeechStop.disabled, false);
  assert.ok(elements.composerSpeechDetail.textContent.includes("Warte auf weiteren Antworttext."));
  assert.ok(elements.composerSpeechDetail.textContent.includes("local-voice"));
  assert.equal(elements.contextStrip.hidden, false);
  state.microphone = { canPauseSpeech: true, isSpeechPaused: true };
  context.renderSpeechStatus();
  assert.equal(elements.composerSpeechPause.getAttribute("aria-label"), "Fortsetzen");
  assert.equal(elements.composerSpeechPause.getAttribute("aria-pressed"), "true");
  assert.equal(elements.composerSpeechStatus.classList.contains("paused"), true);
  state.speechStatus = { active: false };
  context.renderSpeechStatus();
  assert.equal(elements.composerSpeechStatus.hidden, true);
  assert.equal(elements.composerSpeechDetail.textContent, "");
  assert.equal(elements.composerSpeechStop.disabled, true);
  assert.equal(elements.contextStrip.hidden, true);

  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  const speechMarkup = html.slice(html.indexOf('<div id="composer-speech-status"'), html.indexOf('<div class="composer">'));
  assert.match(speechMarkup, /<span class="composer-speech-status__label">Vorlesen<\/span>/);
  assert.match(speechMarkup, /<span aria-hidden="true">×<\/span>/);
  assert.doesNotMatch(speechMarkup, /<strong>|<rect/);
  assert.ok(speechMarkup.indexOf('id="composer-speech-stop"') < speechMarkup.indexOf('id="composer-speech-detail"'));
  assert.match(speechMarkup, /role="status" aria-live="polite"/);
  assert.match(speechMarkup, /composer-speech-status__indicator/);
  const css = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8");
  const hiddenDetail = css.match(/\.composer-speech-status #composer-speech-detail\s*\{([^}]+)\}/)?.[1];
  assert.match(hiddenDetail, /position:\s*absolute/);
  assert.match(hiddenDetail, /clip-path:\s*inset\(50%\)/);
  assert.doesNotMatch(hiddenDetail, /display:\s*none|visibility:\s*hidden/, "status details remain in the accessibility tree");
});

test("an active streaming speech session can pause in preparation and text gaps without microphone capture or pause resets", () => {
  for (const microphoneFirst of [false, true]) {
    const { context, state, elements } = harness();
    for (const name of ["composerSpeechStatus", "composerSpeechDetail", "composerSpeechPause", "composerSpeechPauseIcon", "composerSpeechStop"])
      elements[name] = new TestNode("span");
    state.isRunning = true;
    state.speechStatus = { active: false };
    state.microphone = { isRecording: false, isSpeaking: false, canPauseSpeech: false, isSpeechPaused: false };
    const emit = (type, payload) => context.handleHostMessage({ detail: { type, payload } });
    // The host owns the whole queue's pause capability, including intervals
    // with no audio player. IsSpeaking does not mean microphone capture.
    const queuedSpeech = { isRecording: false, isBusy: false, isSpeaking: true,
      canPauseSpeech: true, isSpeechPaused: false, status: "Sprachausgabe wird vorbereitet" };
    const waiting = { active: true, status: "Antwort wird fortlaufend vorgelesen", detail: "Warte auf weiteren Antworttext." };
    const initial = [["speech.status", waiting], ["microphone.changed", queuedSpeech]];
    if (microphoneFirst) initial.reverse();
    for (const [type, payload] of initial) emit(type, payload);
    assert.equal(elements.composerSpeechPause.disabled, false, "the queue can pause before its first audio segment");
    assert.equal(elements.composerSpeechStop.disabled, false);
    assert.equal(state.microphone.isRecording, false);

    emit("microphone.changed", { ...queuedSpeech, isSpeechPaused: true, status: "Pausiert" });
    assert.equal(elements.composerSpeechPause.getAttribute("aria-label"), "Fortsetzen");
    assert.equal(elements.composerSpeechPause.getAttribute("aria-pressed"), "true");
    for (const detail of ["Nächster Sprachabschnitt wird vorbereitet.", "Warte auf weiteren Antworttext."]) {
      emit("speech.status", { ...waiting, detail });
      assert.equal(elements.composerSpeechPause.disabled, false);
      assert.equal(elements.composerSpeechPause.getAttribute("aria-label"), "Fortsetzen");
      assert.equal(elements.composerSpeechStatus.classList.contains("paused"), true,
        "generation and queue status updates cannot reset the host's paused state");
    }
    emit("microphone.changed", { ...queuedSpeech, status: "Wiedergabe" });
    assert.equal(elements.composerSpeechPause.getAttribute("aria-label"), "Pausieren");
    assert.equal(elements.composerSpeechPause.getAttribute("aria-pressed"), "false");
    emit("speech.status", waiting);
    assert.equal(elements.composerSpeechPause.disabled, false, "the gap after a segment remains pausible");
    emit("microphone.changed", { ...queuedSpeech, isSpeechPaused: true });
    assert.equal(elements.composerSpeechPause.getAttribute("aria-label"), "Fortsetzen");
    emit("speech.status", { active: false });
    assert.equal(elements.composerSpeechStatus.hidden, true);
    assert.equal(elements.composerSpeechPause.disabled, true);
    assert.equal(elements.composerSpeechStop.disabled, true);
    assert.equal(state.isRunning, true, "stopping speech does not stop the independent Coding run");
  }
});

test("the changes overview has one responsive outer scroller and all required cached assets load before the app", () => {
  const css = fs.readFileSync(path.join(webRoot, "coding-changes.css"), "utf8");
  assert.match(css, /\.coding-changes-dialog__body\s*\{[^}]*overflow:\s*auto/);
  assert.match(css, /\.coding-changes-file__content\s*\{[^}]*max-height:\s*none;\s*overflow:\s*visible/);
  assert.match(css, /\.coding-changes-diff, \.coding-changes-raw\s*\{\s*max-height:\s*none;\s*overflow:\s*visible/);
  assert.equal((css.match(/overflow:\s*(?:auto|scroll)/g) || []).length, 1);
  assert.match(css, /@media \(max-width: 620px\)/);
  assert.match(css, /focus-visible/);
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  for (const asset of ["styles.css", "coding-changes.css", "coding-timeline.css", "bridge.js", "coding-timeline.js", "coding-changes.js", "app.js"]) {
    const revision = asset === "app.js" ? "20260919-workspace-1"
      : asset === "styles.css" ? "20260919-agents-3"
      : asset === "coding-timeline.js" ? "20260919-workspace-1"
      : asset === "bridge.js" ? "20260919-agents-1" : "20260913-3";
    assert.ok(html.includes(`${asset}?v=${revision}`), `${asset} must use the deployed cache revision`);
  }
  assert.ok(html.indexOf('src="coding-timeline.js') < html.indexOf('src="coding-changes.js'));
  assert.ok(html.indexOf('src="coding-changes.js') < html.indexOf('src="app.js'));
  assert.ok(html.indexOf('id="active-tool-chips"') < html.indexOf('id="coding-changes"'));
  assert.ok(html.indexOf('id="coding-changes"') < html.indexOf('id="prompt"'));
});
