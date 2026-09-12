const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");

function harness({ codingToolStepsExpanded = false } = {}) {
  const posts = [];
  const document = {
    body: new TestNode("body"),
    createElement: tag => new TestNode(tag),
    createTextNode: text => new TestNode("#text", text),
    createDocumentFragment: () => new TestNode("#fragment")
  };
  const context = vm.createContext({ document, URL, setTimeout: () => {},
    state: { selectedToolAction: "coding", activeSessionId: "session-a", codingActivity: new Map() },
    navigator: { clipboard: { writeText: async text => posts.push({ type: "clipboard", text }) } },
    goBridge: { post: (type, payload) => posts.push({ type, payload }) } });
  for (const script of ["markdown.js", "coding-timeline.js"])
    vm.runInContext(fs.readFileSync(path.join(webRoot, script), "utf8"), context, { filename: script });
  const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
  for (const name of ["normalizeCodingStep", "codingToolLabel", "codingStepState", "codingPreviewHtml",
    "isTerminalMessageStatus", "cleanStatusMetadata", "recordCodingActivity", "mergeCodingToolSteps"]) {
    const start = app.indexOf(`  function ${name}(`);
    const end = app.indexOf("\n  function ", start + 1);
    assert.ok(start >= 0 && end > start, `production ${name} exists`);
    vm.runInContext(app.slice(start, end), context, { filename: `app.js:${name}` });
  }
  const previews = [];
  const render = (message, steps, options = {}) => context.goCodingTimeline.render(message, steps, {
    renderMarkdown: text => context.goMarkdown.render(text),
    enhanceCodeBlocks: () => {},
    sanitizeText: text => text,
    onPreview: (...args) => previews.push(args),
    codingToolStepsExpanded,
    ...options
  });
  return { context, document, render, previews, posts };
}

const answer = (extra = {}) => ({ id: "answer-1", role: "assistant", content: "", status: "completed", ...extra });
const step = (extra = {}) => ({ id: "step-1", tool: "coding.read", label: "Datei lesen", status: "completed", ...extra });

test("each tool defaults to its own native collapsed disclosure with a short factual header", async () => {
  const { render } = harness();
  const timeline = render(answer(), [step({ inputJson: JSON.stringify({ path: "src/math.py" }),
    outputJson: JSON.stringify({ totalLines: 45, content: "FULL FILE CONTENT" }) })]);
  const disclosure = timeline.querySelector("details.coding-step__disclosure");
  assert.ok(disclosure);
  assert.equal(disclosure.hasAttribute("open"), false);
  assert.equal(disclosure.firstChild.nodeName, "SUMMARY", "native summary provides Enter/Space and expanded-state accessibility");
  assert.equal(disclosure.firstChild.querySelector("button"), null, "disclosure activation must not contain competing copy/preview buttons");
  assert.equal(disclosure.querySelector(".coding-step__summary").textContent, "src/math.py · 45 Zeilen");
  assert.equal(disclosure.querySelector(".coding-step__status").textContent, "Abgeschlossen");
  assert.equal(disclosure.querySelector(".coding-step__content"), null, "closed details must not build hidden receipt DOM");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.ok(disclosure.querySelector(".coding-step__content").textContent.includes("FULL FILE CONTENT"));
  assert.equal(disclosure.firstChild.textContent.includes("FULL FILE CONTENT"), false);
});

test("new disclosures follow the global default while reader choices survive updates and default changes", () => {
  const { context, render, document } = harness();
  const message = answer({ status: "streaming" });
  const first = step({ id: "first", tool: "coding.command", status: "running", outputJson: JSON.stringify({ stdout: "Started" }) });
  const current = render(message, [first]);
  document.body.append(current);
  const disclosure = current.querySelector(".coding-step__disclosure");
  disclosure.setAttribute("open", "");
  const changed = { ...first, outputJson: JSON.stringify({ stdout: "Started\nProgress" }) };
  context.goCodingTimeline.reconcile(current, render(message, [changed, step({ id: "second" })], {
    codingToolStepsExpanded: false, previousTimeline: current
  }));
  assert.equal(current.querySelector(".coding-step__disclosure"), disclosure);
  assert.equal(disclosure.hasAttribute("open"), true);
  assert.equal(current.querySelector('[data-step-id="second"] details').hasAttribute("open"), false);

  disclosure.removeAttribute("open");
  context.goCodingTimeline.reconcile(current, render(message, [changed, step({ id: "second" }), step({ id: "third" })], {
    codingToolStepsExpanded: true, previousTimeline: current
  }));
  assert.equal(disclosure.hasAttribute("open"), false, "changing the default must not override a reader's closed tool");
  assert.equal(current.querySelector('[data-step-id="third"] details').hasAttribute("open"), true);

  // Reconciliation also keeps user state when a caller cannot provide the render cache.
  context.goCodingTimeline.reconcile(current, render(message, [changed, step({ id: "second" }), step({ id: "third" })], {
    codingToolStepsExpanded: true
  }));
  assert.equal(disclosure.hasAttribute("open"), false);
  assert.equal(disclosure.querySelector(".coding-step__content"), null);
  assert.equal(current.querySelector('[data-step-id="third"] details').hasAttribute("open"), true);
});

test("one thousand collapsed receipts create only headers and build their exact latest patch on demand", async () => {
  const { context, render, document, posts } = harness();
  const patch = "diff --git a/café.py b/café.py\r\n--- a/café.py\r\n+++ b/café.py\r\n@@ -1 +1 @@\r\n-old\r\n+new 日本語\r\n\\ No newline at end of file";
  let codeNodes = 0, preNodes = 0, buttons = 0;
  const createElement = document.createElement;
  document.createElement = tag => {
    if (tag === "code") codeNodes++;
    if (tag === "pre") preNodes++;
    if (tag === "button") buttons++;
    return createElement(tag);
  };
  const tools = Array.from({ length: 1000 }, (_, index) => step({ id: `receipt-${index}`, tool: "coding.edit",
    inputJson: JSON.stringify({ path: "café.py", oldText: "old", newText: "new 日本語" }),
    outputJson: JSON.stringify({ path: "café.py", applied: true, diff: patch, addedLines: 1, removedLines: 1 }) }));
  const current = render(answer(), tools);
  document.body.append(current);
  assert.equal(current.querySelectorAll("summary").length, 1000);
  assert.equal(current.querySelectorAll(".coding-step__content").length, 0);
  assert.equal(codeNodes, 0, "no hidden patch is parsed into line nodes");
  assert.equal(preNodes, 0);
  assert.equal(buttons, 0, "closed receipts do not allocate copy-payload handlers");

  const last = current.querySelector('[data-step-id="receipt-999"] details');
  const focusedSummary = last.firstChild;
  const latestPatch = patch.replace("+new 日本語", "+latest Grüße € 日本語");
  const updated = tools.map((tool, index) => index === 999 ? { ...tool,
    outputJson: JSON.stringify({ path: "café.py", applied: true, diff: latestPatch, addedLines: 1, removedLines: 1 }) } : tool);
  context.goCodingTimeline.reconcile(current, render(answer(), updated, { previousTimeline: current }));
  assert.equal(last.firstChild, focusedSummary);
  assert.equal(current.querySelectorAll(".coding-step__content").length, 0);
  assert.equal(codeNodes, 0);
  last.setAttribute("open", "");
  await last.dispatch("toggle");
  assert.equal(current.querySelectorAll(".coding-step__content").length, 1);
  assert.ok(last.textContent.includes("+latest Grüße € 日本語"));
  assert.ok(last.textContent.includes("Angewendete Änderung"));
  await last.querySelector(".coding-diff__header button").dispatch("click");
  assert.equal(posts.at(-1).payload.text, latestPatch, "CRLF and no-final-newline receipt remain exact");
  await last.querySelector(".coding-step__copy").dispatch("click");
  assert.equal(JSON.parse(posts.at(-1).payload.text).output.diff, latestPatch);

  last.removeAttribute("open");
  await last.dispatch("toggle");
  assert.equal(current.querySelectorAll(".coding-step__content").length, 0, "closing frees the rendered body again");
  last.setAttribute("open", "");
  await last.dispatch("toggle");
  await last.dispatch("toggle");
  assert.equal(current.querySelectorAll(".coding-step__content").length, 1, "queued toggle events cannot duplicate the receipt");
  assert.equal(last.listeners.get("toggle").length, 1, "reconciliation retains exactly one toggle listener");
});

test("reopening a live command shows final cancellation diagnostics and refreshed copy handlers", async () => {
  const { context, render, document, posts } = harness();
  const command = step({ tool: "coding.command", status: "running", inputJson: '{"executable":"python","arguments":["checks.py"]}',
    outputJson: '{"stdout":"Started","stderr":"","partial":true}' });
  const current = render(answer({ status: "streaming" }), [command]);
  document.body.append(current);
  const disclosure = current.querySelector("details");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.ok(disclosure.textContent.includes("Started"));
  disclosure.removeAttribute("open");
  await disclosure.dispatch("toggle");
  const finalOutput = { stdout: "Started\nLast actual output", stderr: "Cancelled after diagnostic 日本語", partial: true };
  context.goCodingTimeline.reconcile(current, render(answer({ status: "cancelled" }), [{ ...command,
    status: "cancelled", outputJson: JSON.stringify(finalOutput) }], { previousTimeline: current }));
  assert.equal(disclosure.querySelector(".coding-step__content"), null);
  assert.equal(disclosure.querySelector(".coding-step__status").textContent, "Abgebrochen");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.ok(disclosure.textContent.includes(finalOutput.stderr));
  await disclosure.querySelector(".coding-step__copy").dispatch("click");
  assert.deepEqual(JSON.parse(posts.at(-1).payload.text).output, finalOutput);
  assert.ok(!disclosure.textContent.includes("Exitcode"), "cancellation must not invent a completed exit code");
});

test("a lazily opened HTML receipt gets the latest preview callback without embedding model HTML", async () => {
  const { context, render, previews } = harness();
  const initial = step({ tool: "coding.renderHtml", status: "running" });
  const current = render(answer({ status: "streaming" }), [initial]);
  const previewHtml = "<script>throw new Error('must stay inert')</script><p>Latest preview</p>";
  context.goCodingTimeline.reconcile(current, render(answer(), [{ ...initial, status: "completed", previewHtml }], { previousTimeline: current }));
  assert.equal(current.querySelector("button"), null);
  const disclosure = current.querySelector("details");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.equal(current.querySelector("script"), null);
  assert.equal(current.querySelector("iframe"), null);
  await current.querySelector(".coding-preview-button").dispatch("click");
  assert.deepEqual(previews, [["answer-1", "step-1"]]);
});

test("collapsed live headers update without replacing the focused summary or hiding final diagnostics", async () => {
  const { context, render, document } = harness();
  const message = answer({ status: "streaming" });
  const command = step({ tool: "coding.command", label: "Befehl ausführen", status: "running",
    inputJson: JSON.stringify({ executable: "python", arguments: ["checks.py"] }),
    outputJson: JSON.stringify({ stdout: "Test 1 läuft\n", stderr: "" }) });
  const current = render(message, [command]);
  document.body.append(current);
  const disclosure = current.querySelector("details");
  const focusedSummary = current.querySelector("summary");
  assert.equal(current.querySelector(".coding-step__summary").textContent, "python checks.py · stdout: Test 1 läuft");
  const fullOutput = `Test 1 läuft\n${"x".repeat(600)}\n<script>literal stdout</script>`;
  context.goCodingTimeline.reconcile(current, render(message, [{ ...command,
    outputJson: JSON.stringify({ stdout: fullOutput, stderr: "", partial: true }) }], { previousTimeline: current }));
  assert.equal(current.querySelector("summary"), focusedSummary);
  assert.equal(disclosure.hasAttribute("open"), false);
  assert.equal(current.querySelector(".coding-step__summary").textContent, "python checks.py · stdout: <script>literal stdout</script>");
  assert.equal(current.querySelector("script"), null);
  assert.equal(current.querySelector(".coding-step__content"), null);

  const error = "Check failed: " + "e".repeat(400);
  context.goCodingTimeline.reconcile(current, render(answer({ status: "failed" }), [{ ...command, status: "failed",
    outputJson: JSON.stringify({ stdout: fullOutput, stderr: error, exitCode: 1 }) }], { previousTimeline: current }));
  const header = current.querySelector(".coding-step__summary").textContent;
  assert.ok(header.includes("Exitcode 1 · stdout: <script>literal stdout</script>"));
  assert.ok(header.includes("stderr: Check failed:"));
  assert.ok(header.endsWith("…"));
  assert.ok(header.length <= 263, "only the compact summary is bounded");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.ok(current.querySelector(".coding-step__content").textContent.includes(fullOutput));
  assert.ok(current.querySelector(".coding-step__content").textContent.includes(error));
  assert.equal(current.querySelector(".coding-step__status").textContent, "Fehlgeschlagen");
});

test("a previous stderr warning cannot hide new stdout progress in the collapsed summary", async () => {
  const { context, render, document } = harness();
  const message = answer({ status: "streaming" });
  const command = step({ tool: "coding.command", label: "Befehl ausführen", status: "running",
    inputJson: '{"executable":"python","arguments":["checks.py"]}',
    outputJson: JSON.stringify({ stdout: "Test 1 läuft", stderr: "Warnung: optionale Erweiterung fehlt" }) });
  const current = render(message, [command]);
  document.body.append(current);
  const summary = current.querySelector(".coding-step__summary");
  assert.equal(summary.textContent, "python checks.py · stdout: Test 1 läuft · stderr: Warnung: optionale Erweiterung fehlt");
  const updated = { ...command, outputJson: JSON.stringify({ stdout: "Test 1 läuft\nTest 2 bestanden", stderr: "Warnung: optionale Erweiterung fehlt" }) };
  context.goCodingTimeline.reconcile(current, render(message, [updated], { previousTimeline: current }));
  assert.equal(current.querySelector(".coding-step__summary"), summary);
  assert.equal(summary.textContent, "python checks.py · stdout: Test 2 bestanden · stderr: Warnung: optionale Erweiterung fehlt");
  assert.equal(current.querySelector("details").hasAttribute("open"), false);

  const stdout = "Ergebnis: " + "s".repeat(500), stderr = "Warnung: " + "w".repeat(500);
  context.goCodingTimeline.reconcile(current, render(answer(), [{ ...updated, status: "completed",
    outputJson: JSON.stringify({ stdout, stderr, exitCode: 0 }) }], { previousTimeline: current }));
  assert.ok(summary.textContent.includes("Exitcode 0"));
  assert.ok(summary.textContent.includes("stdout: Ergebnis:"));
  assert.ok(summary.textContent.includes("stderr: Warnung:"));
  assert.ok(summary.textContent.length <= 263);
  assert.equal((summary.textContent.match(/…/g) || []).length, 2, "both streams receive their own bounded space");
  const disclosure = current.querySelector("details");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  assert.ok(current.querySelector(".coding-step__content").textContent.includes(stdout));
  assert.ok(current.querySelector(".coding-step__content").textContent.includes(stderr));
});

test("tool summaries report received counts and applied changes without claiming unconfirmed writes", () => {
  const { render } = harness();
  const summary = value => render(answer({ status: "streaming" }), [step(value)]).querySelector(".coding-step__summary")?.textContent || "";
  assert.equal(summary({ tool: "coding.search", inputJson: '{"query":"multiply"}', outputJson: '{"matches":[{},{}]}' }), "multiply · 2 Treffer");
  assert.equal(summary({ tool: "coding.list", inputJson: '{"path":"src"}', outputJson: '{"entries":[{}],"truncated":true}' }), "src · 1 Eintrag · Ausgabe gekürzt");
  assert.equal(summary({ tool: "coding.edit", inputJson: '{"path":"math.py"}', status: "running", outputJson: '{"addedLines":2,"removedLines":1,"applied":false}' }), "math.py · +2 / −1");
  assert.equal(summary({ tool: "coding.edit", inputJson: '{"path":"math.py"}', outputJson: '{"addedLines":2,"removedLines":1,"applied":true}' }), "math.py · +2 / −1 · Gespeichert");
  assert.equal(summary({ tool: "coding.write", inputJson: '{"path":"math.py"}' }), "math.py");
});

test("expanded disclosure content keeps full-height output and patch rules", () => {
  const css = fs.readFileSync(path.join(webRoot, "coding-timeline.css"), "utf8");
  for (const selector of [".coding-timeline .coding-step__content", ".coding-timeline .coding-step__detail", ".coding-output__text"]) {
    const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const rule = new RegExp(`${escaped} \\{([^}]+)\\}`).exec(css)?.[1];
    assert.ok(rule, `rule exists: ${selector}`);
    assert.match(rule, /max-height:\s*none/);
    assert.match(rule, /overflow:\s*visible/);
  }
  assert.match(css, /\.coding-step__title:focus-visible\s*\{[^}]*outline:/);
});

test("narration and individual tool executions follow persisted text offsets without an aggregate disclosure", () => {
  const { render } = harness({ codingToolStepsExpanded: true });
  const before = "Ich lese die betroffene Funktion.\n\n";
  const middle = "Die Ursache ist die Addition. Ich korrigiere sie.\n\n";
  const after = "Die Änderung ist geprüft.\n";
  const timeline = render(answer({ content: before + middle + after }), [
    step({ id: "read-1", contentOffset: before.length, explanation: "Prüfe die vorhandene Implementierung." }),
    step({ id: "edit-2", tool: "coding.edit", contentOffset: before.length + middle.length }),
    step({ id: "test-3", tool: "coding.command", contentOffset: before.length + middle.length })
  ]);

  assert.ok(timeline.classList.contains("coding-timeline"));
  assert.equal(timeline.querySelector("details.coding-activity"), null);
  assert.ok(!timeline.textContent.includes("Werkzeugschritte"));
  assert.deepEqual(timeline.children.map(node => node.dataset.stepId || node.textContent.trim()), [
    before.trim(), "read-1", middle.trim(), "edit-2", "test-3", after.trim()
  ]);
  assert.ok(timeline.querySelector('[data-step-id="read-1"]').textContent.includes("Prüfe die vorhandene Implementierung."));
});

test("legacy persisted steps remain visible exactly once without inventing narration or timestamps", () => {
  const { render } = harness({ codingToolStepsExpanded: true });
  const old = answer({ content: "Die frühere Antwort bleibt erhalten." });
  const steps = [step({ id: "old-read", detail: "Frühere Datei gelesen." }),
    step({ id: "old-edit", tool: "coding.edit", detail: "Frühere Änderung gespeichert." })];
  const timeline = render(old, steps);

  assert.deepEqual(timeline.querySelectorAll(".coding-step").map(node => node.dataset.stepId), ["old-read", "old-edit"]);
  assert.ok(timeline.textContent.includes(old.content));
  assert.ok(timeline.textContent.includes(steps[0].detail));
  assert.ok(timeline.textContent.includes(steps[1].detail));
  assert.ok(!timeline.textContent.includes("undefined"));
  assert.ok(!timeline.textContent.includes("Invalid Date"));
  const narrationOnly = render(old, []);
  assert.equal(narrationOnly.querySelectorAll(".coding-step").length, 0);
  assert.equal(narrationOnly.textContent.trim(), old.content);
});

test("stored offsets address raw UTF-16 message text before display sanitization removes metadata", () => {
  const { render } = harness();
  const before = "[INTERNAL]Ich prüfe die Datei 🔎.\n\n";
  const after = "Danach prüfe ich das Ergebnis.";
  const timeline = render(answer({ content: before + after }), [step({ contentOffset: before.length })], {
    sanitizeText: value => value.replace("[INTERNAL]", "")
  });

  assert.deepEqual(timeline.children.map(node => node.dataset.stepId || node.textContent.trim()),
    ["Ich prüfe die Datei 🔎.", "step-1", after]);
  assert.ok(!timeline.textContent.includes("[INTERNAL]"));
});

test("terminal messages never turn unacknowledged pending or running tools into successful changes", () => {
  const { render } = harness();
  for (const terminal of ["completed", "failed", "cancelled", "interrupted"]) {
    for (const unacknowledged of ["pending", "running"]) {
      const timeline = render(answer({ status: terminal }), [step({ status: unacknowledged })]);
      const entry = timeline.querySelector(".coding-step");
      assert.ok(entry.classList.contains("coding-step--interrupted"), `${terminal}/${unacknowledged}`);
      assert.ok(!entry.classList.contains("coding-step--completed"));
    }
  }
});

test("structured command output preserves complete stdout and stderr as literal text", () => {
  const { render } = harness({ codingToolStepsExpanded: true });
  const stdout = Array.from({ length: 500 }, (_, index) => `stdout ${index} ${"x".repeat(50)}`).join("\n")
    + '\n```\n<script>parent.goBridge.post("session.clear", {})</script>\n<br>\nSTDOUT-END';
  const stderr = Array.from({ length: 200 }, (_, index) => `stderr ${index}`).join("\n") + "\nSTDERR-END";
  const timeline = render(answer({ status: "streaming" }), [step({
    tool: "coding.command", status: "running", contentOffset: 0,
    inputJson: JSON.stringify({ executable: "python", arguments: ["checks.py"], workingDirectory: "." }),
    outputJson: JSON.stringify({ stdout, stderr, truncated: false, elapsedMilliseconds: 2500 })
  })]);

  assert.ok(timeline.textContent.includes(stdout), "stdout must not be truncated, parsed as Markdown, or lose newlines");
  assert.ok(timeline.textContent.includes(stderr), "stderr must stay separately visible and complete");
  assert.equal(timeline.querySelector("script"), null);
  assert.equal(timeline.querySelector("iframe"), null);
  assert.ok(timeline.textContent.includes("python"));
  assert.ok(timeline.textContent.includes("checks.py"));
});

test("model HTML remains inert in a tool and opens only through the explicit preview callback", async () => {
  const { render, previews, document } = harness({ codingToolStepsExpanded: true });
  const previewHtml = "<script>parent.goBridge.post('session.clear', {})</script><p>Preview</p>";
  const timeline = render(answer(), [step({ tool: "coding.renderHtml", previewHtml })]);
  assert.equal(timeline.querySelector("script"), null);
  assert.equal(timeline.querySelector("iframe"), null);
  assert.equal(document.body.childNodes.length, 0);
  assert.equal(previews.length, 0);
  const button = timeline.querySelector(".coding-preview-button");
  assert.ok(button);
  await button.dispatch("click");
  assert.equal(previews.length, 1);
  for (const status of ["running", "pending", "failed", "denied", "cancelled"]) {
    assert.equal(render(answer({ status: "streaming" }), [step({ tool: "coding.renderHtml", status, previewHtml })])
      .querySelector(".coding-preview-button"), null);
  }
});

const multipleFileDiff = [
  "diff --git a/one.py b/one.py", "--- a/one.py", "+++ b/one.py", "@@ -2,3 +2,4 @@",
  " keep", "-old", "+new", "+extra", " tail", "@@ -10 +11 @@", "-last", "+last-new",
  "\\ No newline at end of file", "diff --git a/new.py b/new.py", "new file mode 100644",
  "--- /dev/null", "+++ b/new.py", "@@ -0,0 +1,2 @@", "+first", "+second", ""
].join("\n");

test("unified patches retain file and hunk boundaries, each side's line numbers, and no-newline markers", () => {
  const { context } = harness();
  const files = Array.from(context.goCodingTimeline.parseUnifiedDiff(multipleFileDiff));
  assert.deepEqual(files.map(file => [file.path, file.added, file.removed]), [["one.py", 3, 2], ["new.py", 2, 0]]);
  assert.deepEqual(Array.from(files[0].lines, line => [line.kind, line.oldLine, line.newLine, line.text]).filter(line => !["meta", "hunk"].includes(line[0])), [
    ["context", 2, 2, " keep"], ["removed", 3, null, "-old"], ["added", null, 3, "+new"],
    ["added", null, 4, "+extra"], ["context", 4, 5, " tail"], ["removed", 10, null, "-last"], ["added", null, 11, "+last-new"]
  ]);
  const marker = files[0].lines.find(line => line.text === "\\ No newline at end of file");
  assert.equal(marker.kind, "meta");
  assert.equal(marker.oldLine, null);
  assert.equal(marker.newLine, null);
  assert.deepEqual(Array.from(files[1].lines.filter(line => line.kind === "added"), line => line.newLine), [1, 2]);
});

test("a removed source line beginning with two hyphens is not mistaken for another file header", () => {
  const { context } = harness();
  const source = "--- a/note.txt\n+++ b/note.txt\n@@ -1,2 +1,2 @@\n--- heading\n+-- updated\n tail\n";
  const files = context.goCodingTimeline.parseUnifiedDiff(source);
  assert.equal(files.length, 1);
  assert.equal(files[0].path, "note.txt");
  const removed = files[0].lines.find(line => line.text === "--- heading");
  assert.equal(removed.kind, "removed");
  assert.equal(removed.oldLine, 1);
  assert.equal(files[0].added, 1);
  assert.equal(files[0].removed, 1);
});

test("quoted Git paths decode UTF-8 octal escapes while preserving literal percent text and exact CRLF patch copy", async () => {
  const { context, render, posts } = harness({ codingToolStepsExpanded: true });
  const quotedPath = '"b/Gr\\303\\274n%20 name.py"';
  const source = `diff --git "a/Gr\\303\\274n%20 name.py" ${quotedPath}\r\n--- "a/Gr\\303\\274n%20 name.py"\r\n+++ ${quotedPath}\r\n@@ -1 +1 @@\r\n-old\r\n+new\r\n`;
  const file = context.goCodingTimeline.parseUnifiedDiff(source)[0];
  assert.equal(file.path, "Grün%20 name.py");
  assert.equal(file.rawText, source);
  const timeline = render(answer(), [step({ tool: "coding.edit", outputJson: JSON.stringify({ diff: source, success: true }) })]);
  await timeline.querySelector(".coding-diff__header button").dispatch("click");
  assert.equal(posts.at(-1).payload?.text ?? posts.at(-1).text, source);
});

test("truncated Git excerpts stop claiming line numbers until the next explicit hunk", () => {
  const { context, render } = harness({ codingToolStepsExpanded: true });
  const diff = "--- a/test.py\n+++ b/test.py\n@@ -1,20 +1,20 @@\n first\n...[Ausgabe gekürzt]...\n+unknown-location\n@@ -90 +100 @@\n-old-tail\n+new-tail\n";
  const file = context.goCodingTimeline.parseUnifiedDiff(diff)[0];
  const unknown = file.lines.find(line => line.text === "+unknown-location");
  assert.equal(unknown.oldLine, null);
  assert.equal(unknown.newLine, null);
  assert.equal(unknown.kind, "meta");
  assert.equal(file.lines.find(line => line.text === "+new-tail").newLine, 100);
  const timeline = render(answer(), [step({ tool: "coding.gitDiff", outputJson: JSON.stringify({ diff: { stdout: diff, truncated: true } }) })]);
  assert.ok(timeline.textContent.includes("gekürzter Auszug"));
});

test("a committed file remains labelled applied when the operation ends in a later failure", () => {
  const { render } = harness({ codingToolStepsExpanded: true });
  const diff = "--- a/test.py\n+++ b/test.py\n@@ -1 +1 @@\n-old\n+new\n";
  const timeline = render(answer({ status: "failed" }), [step({ tool: "coding.edit", status: "failed",
    outputJson: JSON.stringify({ success: false, applied: true, diff, error: "Nachprüfung fehlgeschlagen" }) })]);
  assert.ok(timeline.textContent.includes("Angewendete Änderung"));
  assert.ok(!timeline.textContent.includes("noch nicht angewendet"));
  assert.ok(timeline.textContent.includes("Nachprüfung fehlgeschlagen"));
});

test("per-step patches distinguish proposed changes from applied ones and copy their exact patch text", async () => {
  const { render, posts } = harness({ codingToolStepsExpanded: true });
  const diff = "--- a/example.py\n+++ b/example.py\n@@ -1 +1 @@\n-old\n+<script>alert(1)</script>\n";
  const proposed = step({ tool: "coding.edit", status: "running", inputJson: JSON.stringify({ path: "example.py" }),
    outputJson: JSON.stringify({ diff, success: false }) });
  const pending = render(answer({ status: "streaming" }), [proposed]);
  assert.ok(pending.textContent.includes("noch nicht angewendet"));
  assert.ok(!pending.textContent.includes("Angewendete Änderung"));
  const completed = render(answer(), [{ ...proposed, status: "completed", outputJson: JSON.stringify({ diff, success: true }) }]);
  assert.ok(completed.textContent.includes("Angewendete Änderung"));
  assert.equal(completed.querySelectorAll(".coding-diff").length, 1);
  assert.equal(completed.querySelector("script"), null);
  assert.equal(completed.querySelector(".coding-diff__line--added code").textContent, "+<script>alert(1)</script>");
  await completed.querySelector(".coding-diff__header button").dispatch("click");
  assert.equal(posts.at(-1).payload?.text ?? posts.at(-1).text, diff);
});

test("failed edits without a computed patch still expose the full proposed input literally", () => {
  const { render } = harness({ codingToolStepsExpanded: true });
  const oldText = "return a + b";
  const newText = "return a * b\n# <script>never execute this</script>";
  const timeline = render(answer({ status: "failed" }), [step({ tool: "coding.edit", status: "failed",
    inputJson: JSON.stringify({ path: "math.py", oldText, newText, expectedSha256: "original-hash" }),
    outputJson: JSON.stringify({ errorCode: "coding.invalid_edit", error: "Fundstelle nicht eindeutig." })
  })]);

  assert.ok(timeline.textContent.includes(oldText));
  assert.ok(timeline.textContent.includes(newText));
  assert.ok(timeline.textContent.includes("Fundstelle nicht eindeutig."));
  assert.equal(timeline.querySelector("script"), null);
  assert.ok(!timeline.textContent.includes("Angewendete Änderung"));
});

test("live reconciliation preserves completed diff line and text identities while terminal output grows", () => {
  const { context, render, document } = harness({ codingToolStepsExpanded: true });
  const applied = step({ id: "edit", tool: "coding.edit", contentOffset: 0,
    outputJson: JSON.stringify({ success: true, diff: multipleFileDiff }) });
  const command = step({ id: "command", tool: "coding.command", status: "running", contentOffset: 0,
    outputJson: JSON.stringify({ stdout: "Test 1 läuft\n", stderr: "" }) });
  const message = answer({ status: "streaming" });
  const current = render(message, [applied, command]);
  document.body.append(current);
  const retainedLine = current.querySelector(".coding-diff__line--added");
  const selectedText = retainedLine.querySelector("code").firstChild;
  const completedStep = current.querySelector('[data-step-id="edit"]');
  const next = render(message, [applied, { ...command,
    outputJson: JSON.stringify({ stdout: "Test 1 läuft\nTest 1 bestanden\nTest 2 läuft\n", stderr: "" }) }]);

  assert.equal(context.goCodingTimeline.reconcile(current, next), current);
  assert.equal(current.querySelector('[data-step-id="edit"]'), completedStep);
  assert.equal(current.querySelector(".coding-diff__line--added"), retainedLine);
  assert.equal(current.querySelector(".coding-diff__line--added code").firstChild, selectedText,
    "a user's text selection must still point into the attached original text node");
  assert.equal(selectedText.parentNode.parentNode, retainedLine);
  assert.ok(current.textContent.includes("Test 2 läuft"));
  assert.equal(current.querySelectorAll('[data-step-id="command"]').length, 1);
});

test("inserting narration before keyed steps retains their nodes and finishing removes only the live phase", () => {
  const { context, render, document } = harness();
  const tools = [step({ id: "first", contentOffset: 0 }), step({ id: "second", contentOffset: 0 })];
  const current = render(answer({ status: "streaming" }), tools, { liveStatus: { status: "Antwort wird formuliert" } });
  document.body.append(current);
  const retained = current.querySelectorAll(".coding-step");
  const content = "Zuerst prüfe ich beide Dateien.\n\n";
  const next = render(answer({ content }), tools.map(tool => ({ ...tool, contentOffset: content.length })));
  context.goCodingTimeline.reconcile(current, next);

  assert.deepEqual(current.children.map(node => node.dataset.stepId || node.textContent.trim()), [content.trim(), "first", "second"]);
  assert.deepEqual(current.querySelectorAll(".coding-step"), retained);
  assert.equal(current.querySelector(".coding-live-phase"), null);
  assert.equal(current.querySelectorAll(".coding-narration").length, 1);
});

test("reconnect overlays new live output on stored execution order and preserves every structured field", () => {
  const { context, render } = harness();
  const stored = [step({ id: "first", contentOffset: 0, detail: "Datei gelesen" }), step({
    id: "second", tool: "coding.command", status: "running", contentOffset: 14,
    inputJson: JSON.stringify({ executable: "python", arguments: ["test.py"] }),
    outputJson: JSON.stringify({ stdout: "Beginn" }), explanation: "Prüfe den neuen Rückgabewert.",
    startedAt: "2026-09-11T12:00:00Z", updatedAt: "2026-09-11T12:00:01Z"
  })];
  const live = { ...stored[1], outputJson: JSON.stringify({ stdout: "Beginn\nTest bestanden" }), updatedAt: "2026-09-11T12:00:03Z" };
  context.recordCodingActivity({ sessionId: "session-a", messageId: "answer-1", toolStep: live });
  const merged = context.mergeCodingToolSteps(answer({ status: "streaming", toolSteps: stored }));
  assert.deepEqual(Array.from(merged, item => item.id), ["first", "second"]);
  for (const property of ["inputJson", "outputJson", "explanation", "contentOffset", "startedAt", "updatedAt"])
    assert.equal(merged[1][property], live[property]);
  assert.ok(render(answer({ status: "streaming" }), merged).textContent.includes("Test bestanden"));

  const final = { ...live, status: "completed", completedAt: "2026-09-11T12:00:04Z", updatedAt: "2026-09-11T12:00:04Z" };
  const completedMessage = answer({ toolSteps: [stored[0], final] });
  context.state.codingActivity.clear();
  const restored = context.mergeCodingToolSteps(completedMessage);
  assert.deepEqual(Array.from(restored, item => item.id), ["first", "second"]);
  assert.equal(restored[1].outputJson, final.outputJson);
  assert.equal(restored[1].completedAt, final.completedAt);
});

test("replayed older events cannot regress terminal status or replace a newer final result", () => {
  const { context } = harness();
  const final = step({ outputJson: JSON.stringify({ content: "Neuester Inhalt" }), updatedAt: "2026-09-11T12:00:04Z" });
  for (const toolStep of [final, { ...final, status: "running", updatedAt: "2026-09-11T12:00:05Z" },
    { ...final, outputJson: JSON.stringify({ content: "Veralteter Inhalt" }), updatedAt: "2026-09-11T12:00:02Z" }])
    context.recordCodingActivity({ sessionId: "session-a", messageId: "answer-1", toolStep });

  const merged = context.mergeCodingToolSteps(answer({ toolSteps: [{ ...final, status: "running", updatedAt: "2026-09-11T12:00:01Z" }] }));
  assert.equal(merged.length, 1);
  assert.equal(merged[0].status, "completed");
  assert.equal(merged[0].outputJson, final.outputJson);
  const persisted = { ...final, outputJson: JSON.stringify({ content: "Noch neuerer finaler Inhalt" }), updatedAt: "2026-09-11T12:00:06Z" };
  assert.equal(context.mergeCodingToolSteps(answer({ toolSteps: [persisted] }))[0].outputJson, persisted.outputJson);
});

test("growing only the tail reuses completed tools and earlier narration without reparsing Markdown or diff rows", () => {
  const { context, render, document } = harness({ codingToolStepsExpanded: true });
  const before = "Die Datei wird überprüft.\n\n";
  const legacyDetail = "**Frühere Datei geprüft.**";
  const tools = [step({ id: "read", contentOffset: before.length, detail: legacyDetail }),
    step({ id: "edit", tool: "coding.edit", contentOffset: before.length, outputJson: JSON.stringify({ success: true, diff: multipleFileDiff }) })];
  const renderedMarkdown = [];
  let createdCodeNodes = 0;
  const createElement = document.createElement;
  document.createElement = tag => { if (tag === "code") createdCodeNodes++; return createElement(tag); };
  const options = { renderMarkdown: text => { renderedMarkdown.push(text); return context.goMarkdown.render(text); } };
  const current = render(answer({ content: before + "Ergebnis", status: "streaming" }), tools, options);
  document.body.append(current);
  const previousNarration = current.querySelector(".coding-narration");
  const previousDiff = current.querySelector(".coding-diff");
  const previousRead = current.querySelector('[data-step-id="read"]');
  renderedMarkdown.length = 0;
  createdCodeNodes = 0;

  const next = render(answer({ content: before + "Ergebnis: Alle Prüfungen bestanden.", status: "streaming" }), tools,
    { ...options, previousTimeline: current });
  assert.deepEqual(renderedMarkdown, ["Ergebnis: Alle Prüfungen bestanden."]);
  assert.equal(createdCodeNodes, 0, "the completed patch must not rebuild its code rows on each token");
  context.goCodingTimeline.reconcile(current, next);
  assert.equal(current.querySelector(".coding-narration"), previousNarration);
  assert.equal(current.querySelector(".coding-diff"), previousDiff);
  assert.equal(current.querySelector('[data-step-id="read"]'), previousRead);
  assert.ok(current.textContent.includes("Alle Prüfungen bestanden."));
});

test("changed output invalidates the step cache and appends to the original selected live text node", async () => {
  const { context, render, document, posts } = harness({ codingToolStepsExpanded: true });
  const initial = step({ tool: "coding.command", status: "running", outputJson: JSON.stringify({ stdout: "Test 1", stderr: "" }) });
  const message = answer({ status: "streaming" });
  const current = render(message, [initial]);
  document.body.append(current);
  const pre = current.querySelector(".coding-output--terminal pre");
  const selectedText = pre.firstChild;
  const selectedRange = { startContainer: selectedText, startOffset: 0, endContainer: selectedText, endOffset: 6 };
  let appended = "";
  selectedText.appendData = value => { appended += value; TestNode.prototype.appendData.call(selectedText, value); };
  const growing = "Test 1 bestanden\nTest 2 läuft\n";
  const next = render(message, [{ ...initial, outputJson: JSON.stringify({ stdout: growing, stderr: "Neue Diagnose" }) }],
    { previousTimeline: current });
  context.goCodingTimeline.reconcile(current, next);

  assert.equal(current.querySelector(".coding-output--terminal pre"), pre);
  assert.equal(pre.firstChild, selectedText);
  assert.equal(appended, growing.slice(6));
  assert.equal(selectedRange.startContainer, selectedText);
  assert.equal(selectedText.textContent.slice(selectedRange.startOffset, selectedRange.endOffset), "Test 1");
  assert.ok(current.textContent.includes("Neue Diagnose"));
  await current.querySelector(".coding-output--terminal button").dispatch("click");
  assert.equal(posts.at(-1).payload?.text ?? posts.at(-1).text, growing, "the copy callback must use the changed output");
});

test("a denied live tool cannot return to pending or running after replay even with a later timestamp", () => {
  const { context, render } = harness();
  const denied = step({ status: "denied", detail: "Ausführung abgelehnt.", updatedAt: "2026-09-11T12:00:03Z" });
  for (const toolStep of [denied, { ...denied, status: "running", updatedAt: "2026-09-11T12:00:04Z" },
    { ...denied, status: "pending", updatedAt: "2026-09-11T12:00:05Z" }])
    context.recordCodingActivity({ sessionId: "session-a", messageId: "answer-1", toolStep });
  const merged = context.mergeCodingToolSteps(answer({ status: "streaming", toolSteps: [{ ...denied, status: "running", updatedAt: "2026-09-11T12:00:01Z" }] }));
  assert.equal(merged.length, 1);
  assert.equal(merged[0].status, "denied");
  assert.ok(render(answer({ status: "streaming" }), merged).querySelector(".coding-step").classList.contains("coding-step--denied"));
});
