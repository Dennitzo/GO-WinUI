(() => {
  "use strict";

  const terminalStates = new Set(["completed", "failed", "denied", "cancelled", "interrupted", "steered"]);
  const statusLabels = { completed: "Abgeschlossen", running: "Läuft", pending: "Wartet",
    failed: "Fehlgeschlagen", denied: "Abgelehnt", cancelled: "Abgebrochen", interrupted: "Nicht abgeschlossen", steered: "Umgeleitet" };

  function node(tag, className, text) {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = String(text);
    return result;
  }

  function json(value) {
    if (value && typeof value === "object") return value;
    try { return value ? JSON.parse(value) : null; } catch { return null; }
  }

  // Unified patches are data, never Markdown or HTML. Keep metadata and no-newline
  // markers, and advance each side independently so line numbers remain truthful.
  function parseUnifiedDiff(value) {
    const files = [];
    let file = null, oldLine = null, newLine = null, inHunk = false, oldRemaining = 0, newRemaining = 0;
    const begin = path => {
      file = { path, lines: [], added: 0, removed: 0, rawText: "" };
      files.push(file);
      oldLine = newLine = null;
      inHunk = false;
    };
    const source = String(value || "");
    const lines = source.split("\n");
    if (lines.at(-1) === "") lines.pop();
    for (const [lineIndex, rawLine] of lines.entries()) {
      const text = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
      const rawText = rawLine + (lineIndex < lines.length - 1 || source.endsWith("\n") ? "\n" : "");
      if (/^\.{3}\[Ausgabe gekürzt\]\.\.\.$/.test(text)) {
        inHunk = false; oldLine = newLine = null;
      }
      if (text.startsWith("diff --git ")) {
        const paths = /^diff --git ("(?:[^"\\]|\\.)*"|\S+) ("(?:[^"\\]|\\.)*"|\S+)$/.exec(text);
        begin(paths ? gitPathLabel(paths[2]) : text.slice(11));
        file.rawText += rawText;
        file.lines.push({ kind: "meta", text, oldLine: null, newLine: null });
        continue;
      }
      if (!file || text.startsWith("--- ") && inHunk && oldRemaining === 0 && newRemaining === 0) begin("Änderung");
      file.rawText += rawText;
      if (text.startsWith("--- ") && !inHunk) {
        file.path = gitPathLabel(text.slice(4));
      }
      if (text.startsWith("+++ ") && !inHunk && text.slice(4) !== "/dev/null") {
        file.path = gitPathLabel(text.slice(4));
      }
      const hunk = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(text);
      if (hunk) {
        oldLine = Number(hunk[1]); newLine = Number(hunk[3]); inHunk = true;
        oldRemaining = Number(hunk[2] ?? 1); newRemaining = Number(hunk[4] ?? 1);
        file.lines.push({ kind: "hunk", text, oldLine: null, newLine: null });
      } else if (inHunk && text.startsWith("+")) {
        file.added++;
        newRemaining--;
        file.lines.push({ kind: "added", text, oldLine: null, newLine: newLine++ });
      } else if (inHunk && text.startsWith("-")) {
        file.removed++;
        oldRemaining--;
        file.lines.push({ kind: "removed", text, oldLine: oldLine++, newLine: null });
      } else if (inHunk && text.startsWith(" ")) {
        oldRemaining--; newRemaining--;
        file.lines.push({ kind: "context", text, oldLine: oldLine++, newLine: newLine++ });
      } else {
        file.lines.push({ kind: "meta", text, oldLine: null, newLine: null });
      }
    }
    return files;
  }

  function gitPathLabel(path) {
    if (path.startsWith('"') && path.endsWith('"')) {
      const escaped = path.slice(1, -1).replace(/%/g, "%25").replace(/\\([0-7]{3}|[tnr"\\])/g, (_, escape) => {
        const value = /^[0-7]{3}$/.test(escape) ? parseInt(escape, 8) : ({ t: 9, n: 10, r: 13, '"': 34, "\\": 92 })[escape];
        return `%${value.toString(16).padStart(2, "0")}`;
      });
      try { path = decodeURIComponent(escaped); } catch { path = escaped; }
    }
    return path.replace(/^[ab]\//, "");
  }

  function copyButton(text, label = "Kopieren") {
    const button = node("button", "coding-copy", label);
    button.type = "button";
    button.setAttribute("aria-label", label);
    button.addEventListener("click", async () => {
      try {
        if (globalThis.goBridge?.post) globalThis.goBridge.post("message.copy", { text });
        else await navigator.clipboard.writeText(text);
        button.textContent = "Kopiert";
      } catch { button.textContent = "Kopieren fehlgeschlagen"; }
    });
    return button;
  }

  function outputBlock(label, text, kind = "output") {
    const block = node("div", `coding-output coding-output--${kind}`);
    block.dataset.timelineKey = label;
    const header = node("div", "coding-output__header");
    header.append(node("span", "", label), copyButton(String(text)));
    block.append(header, node("pre", "coding-output__text", text));
    return block;
  }

  function diffView(text, label) {
    const view = node("div", "coding-diffs");
    view.dataset.timelineKey = label;
    for (const [index, file] of parseUnifiedDiff(text).entries()) {
      const section = node("section", "coding-diff");
      section.dataset.timelineKey = `diff-${index}-${file.path}`;
      const header = node("div", "coding-diff__header");
      const identity = node("div", "coding-diff__identity");
      identity.append(node("span", "coding-diff__label", label), node("strong", "coding-diff__path", file.path));
      const counts = node("span", "coding-diff__counts");
      counts.setAttribute("aria-label", `${file.added} hinzugefügte, ${file.removed} entfernte Zeilen`);
      counts.append(node("span", "coding-diff__added", `+${file.added}`), node("span", "coding-diff__removed", `−${file.removed}`));
      header.append(identity, counts, copyButton(file.rawText, "Diff kopieren"));
      const lines = node("div", "coding-diff__lines");
      for (const [lineIndex, line] of file.lines.entries()) {
        const row = node("div", `coding-diff__line coding-diff__line--${line.kind}`);
        row.dataset.timelineKey = `line-${lineIndex}`;
        const before = node("span", "coding-diff__number", line.oldLine ?? "");
        const after = node("span", "coding-diff__number", line.newLine ?? "");
        before.setAttribute("aria-label", line.oldLine == null ? "" : `Vorher Zeile ${line.oldLine}`);
        after.setAttribute("aria-label", line.newLine == null ? "" : `Nachher Zeile ${line.newLine}`);
        row.append(before, after, node("code", "coding-diff__code", line.text));
        lines.append(row);
      }
      section.append(header, lines);
      view.append(section);
    }
    return view;
  }

  function describe(tool, input) {
    const target = input?.path || input?.workingDirectory || ".";
    const query = input?.query;
    return ({
      "coding.list": `Dateien in ${target} erfassen.`,
      "coding.read": `Den aktuellen Inhalt von ${target} lesen.`,
      "coding.search": `Im Projekt nach ${query ? `„${query}“` : "passenden Codeabschnitten"} suchen.`,
      "coding.write": `Den vorgesehenen Dateiinhalt für ${target} prüfen und speichern.`,
      "coding.edit": `Die passenden Stellen in ${target} prüfen und ändern.`,
      "coding.command": `Den angezeigten Befehl in ${target} ausführen und die Ausgabe prüfen.`,
      "coding.gitDiff": `Die Git-Änderungen in ${target} prüfen.`,
      "coding.searchHistory": `Frühere Nachrichten nach ${query ? `„${query}“` : "relevantem Kontext"} durchsuchen.`,
      "coding.searchKnowledge": `Dokumentwissen nach ${query ? `„${query}“` : "relevanten Informationen"} durchsuchen.`,
      "coding.renderHtml": "Eine isolierte HTML-Vorschau vorbereiten.",
      "web.search": "Passende Quellen im Web suchen.",
      "web.fetch": "Die ausgewählte Quelle lesen.",
      "web.deepResearch": "Quellen suchen, lesen und für die Recherche auswerten."
    })[tool] || "Das Werkzeug ausführen und sein Ergebnis prüfen.";
  }

  function fields(values, className = "coding-facts") {
    const list = node("dl", className);
    const labels = { path: "Pfad", sha256: "Prüfsumme", originalSha256: "Prüfsumme vorher", expectedSha256: "Geprüfte Ausgangsversion",
      bytes: "Bytes", created: "Neue Datei", totalLines: "Zeilen", startLine: "Ab Zeile", maximumLines: "Zeilenlimit",
      nextLine: "Fortsetzung ab Zeile", query: "Suche", searchedFiles: "Durchsuchte Dateien", maximumResults: "Trefferlimit",
      maximumEntries: "Dateilimit", ignoredDirectories: "Ausgenommene Ordner", partial: "Unvollständige Ausgabe",
      truncated: "Ausgabe gekürzt", diffTruncated: "Diff gekürzt", errorCode: "Fehler", message: "Hinweis", note: "Hinweis",
      progressError: "Übertragungsfehler", stdout: "Standardausgabe", stderr: "Fehlerausgabe", exitCode: "Exitcode",
      elapsedMilliseconds: "Laufzeit (ms)", toolStatus: "Status", addedLines: "Hinzugefügte Zeilen", removedLines: "Entfernte Zeilen" };
    for (const [key, value] of Object.entries(values || {})) {
      if (value === undefined || value === null) continue;
      // These transport flags duplicate the visible execution status. The exact
      // input and receipt remain available through the step's data-copy action.
      if (["success", "toolStatus", "phase", "applied"].includes(key)) continue;
      if (["partial", "truncated", "diffTruncated", "created"].includes(key) && value === false) continue;
      const fact = node("div", "coding-fact");
      fact.append(node("dt", "", labels[key] || key), node("dd", "", typeof value === "object" ? JSON.stringify(value, null, 2)
        : typeof value === "boolean" ? value ? "Ja" : "Nein" : String(value)));
      list.append(fact);
    }
    return list;
  }

  function renderData(value) {
    if (typeof value === "string") return outputBlock("Ergebnis", value);
    if (!value || typeof value !== "object") return node("p", "coding-note", value == null ? "Kein Ergebnis verfügbar." : String(value));
    const result = node("div", "coding-data");
    const rest = { ...value };
    for (const key of ["entries", "matches", "results", "sources", "citations"]) {
      if (!Array.isArray(value[key])) continue;
      const list = node("div", "coding-result-list");
      list.dataset.timelineKey = key;
      for (const [index, item] of value[key].entries()) {
        const row = node("div", "coding-result");
        row.dataset.timelineKey = `${key}-${index}`;
        if (!item || typeof item !== "object") row.append(node("span", "", item));
        else {
          const title = item.path || item.title || item.url || item.name;
          const details = { ...item };
          if (title) {
            const label = item.line ? `${title}:${item.line}` : title;
            let link = null;
            try { if (/^https?:$/.test(new URL(item.url).protocol)) link = node("a", "coding-result__title", label); } catch { /* Plain text for other targets. */ }
            if (link) { link.href = item.url; link.target = "_blank"; link.rel = "noopener noreferrer"; }
            row.append(link || node("strong", "coding-result__title", label));
            for (const key of ["path", "title", "name"]) if (details[key] === title) delete details[key];
            if (item.line) delete details.line;
          }
          for (const key of ["text", "snippet", "content"]) {
            if (typeof details[key] !== "string") continue;
            row.append(node("pre", "coding-result__text", details[key]));
            delete details[key];
          }
          if (Object.keys(details).length) row.append(fields(details));
        }
        list.append(row);
      }
      if (!value[key].length) list.append(node("p", "coding-note", "Keine Treffer."));
      result.append(list);
      delete rest[key];
    }
    for (const key of ["content", "text", "answer", "summary"]) {
      if (typeof rest[key] !== "string") continue;
      result.append(outputBlock(key === "summary" ? "Zusammenfassung" : "Inhalt", rest[key]));
      delete rest[key];
    }
    if (Object.keys(rest).length) result.append(fields(rest));
    return result;
  }

  function processOutput(output, running) {
    const body = node("div", "coding-process-output");
    if (output.stdout) body.append(outputBlock("Standardausgabe", output.stdout, "terminal"));
    if (output.stderr) body.append(outputBlock("Fehlerausgabe", output.stderr, "stderr"));
    if (!output.stdout && !output.stderr) body.append(node("p", "coding-note", running ? "Warte auf die erste Ausgabe …" : "Der Prozess hat keine Textausgabe geliefert."));
    const facts = {};
    if (output.exitCode != null) facts.Exitcode = output.exitCode;
    if (output.elapsedMilliseconds != null) facts.Laufzeit = `${(output.elapsedMilliseconds / 1000).toLocaleString("de-DE", { maximumFractionDigits: 1 })} s`;
    if (output.timedOut) facts.Status = "Zeitlimit erreicht";
    if (output.truncated) facts.Hinweis = "Die Prozessausgabe wurde an der Erfassungsgrenze gekürzt.";
    if (Object.keys(facts).length) body.append(fields(facts));
    return body;
  }

  function stepBody(step, status, options) {
    const body = node("div", "coding-step__detail");
    const input = json(step.inputJson), output = json(step.outputJson);
    const running = !terminalStates.has(status);
    if (!input && !output) {
      // Historic messages did not store structured input/output or text positions.
      // Their original details remain readable without inventing an execution log.
      if (step.detail) {
        body.append(options.renderMarkdown(step.detail));
        options.enhanceCodeBlocks?.(body);
      }
      return body;
    }
    if (step.tool === "coding.command") {
      if (input) {
        const quote = value => /[\s"']/.test(String(value)) ? `'${String(value).replace(/'/g, "''")}'` : String(value);
        const command = [input.executable, ...(input.arguments || [])].filter(value => value != null).map(quote).join(" ");
        body.append(outputBlock("Befehl", command, "command"));
        body.append(fields({ Arbeitsordner: input.workingDirectory || ".", ...(input.timeoutSeconds ? { Zeitlimit: `${input.timeoutSeconds} s` } : {}) }));
      }
      if (output) {
        body.append(processOutput(output, running));
        const other = Object.fromEntries(Object.entries(output).filter(([key]) => !["stdout", "stderr", "exitCode", "elapsedMilliseconds", "timedOut", "truncated", "success"].includes(key)));
        if (Object.keys(other).length) body.append(fields(other));
      } else body.append(node("p", "coding-note", "Prozess wird gestartet …"));
      return body;
    }
    if (input) {
      const parameters = { ...input };
      // The full content is displayed once in the actual patch/preview below.
      if (["coding.write", "coding.edit"].includes(step.tool)) {
        for (const key of ["content", "oldText", "newText", "edits"]) delete parameters[key];
      }
      if (step.tool === "coding.renderHtml") delete parameters.code;
      if (Object.keys(parameters).length) body.append(fields(parameters, "coding-facts coding-facts--input"));
      if (["coding.write", "coding.edit"].includes(step.tool) && !output?.diff) {
        if (typeof input.content === "string") body.append(outputBlock("Angeforderter Dateiinhalt", input.content));
        const edits = input.edits || (input.oldText != null ? [input] : []);
        for (const [index, edit] of edits.entries()) {
          body.append(outputBlock(`Ersetzung ${index + 1} · bisher`, edit.oldText || ""));
          body.append(outputBlock(`Ersetzung ${index + 1} · vorgesehen`, edit.newText || ""));
        }
      }
    }
    if (output && (typeof output.diff === "string" || output.diff?.stdout != null)) {
      const diff = typeof output.diff === "string" ? output.diff : output.diff.stdout;
      const label = step.tool === "coding.gitDiff" ? "Git-Diff" : output.applied === true || status === "completed" && output.success !== false
        ? "Angewendete Änderung" : "Vorbereitete Änderung · noch nicht angewendet";
      if (diff) body.append(diffView(diff, label));
      else body.append(node("p", "coding-note", output.diffTruncated ? "Der Diff überschreitet die Erfassungsgrenze." : "Keine Textänderungen."));
      if (diff && (output.diffTruncated || output.diff?.truncated)) body.append(node("p", "coding-note", step.tool === "coding.gitDiff"
        ? "Der Git-Diff ist ein gekürzter Auszug. An Auslassungen werden keine Zeilennummern behauptet."
        : "Der Diff ist gekürzt; angezeigt werden vollständig erfasste Änderungsblöcke."));
      if (output.stagedDiff?.stdout) body.append(diffView(output.stagedDiff.stdout, "Vorgemerkte Änderungen"));
      const remaining = { ...output };
      delete remaining.diff; delete remaining.stagedDiff;
      if (remaining.stdout === diff) delete remaining.stdout;
      if (remaining.stderr) { body.append(outputBlock("Git-Fehlerausgabe", remaining.stderr, "stderr")); delete remaining.stderr; }
      if (!output.diffTruncated) { delete remaining.addedLines; delete remaining.removedLines; }
      if (typeof output.diff === "object") {
        const { stdout, ...diagnostics } = output.diff;
        if (diagnostics.stderr || diagnostics.exitCode || diagnostics.truncated) body.append(fields(diagnostics));
      }
      if (output.stagedDiff && (output.stagedDiff.stderr || output.stagedDiff.exitCode || output.stagedDiff.truncated)) {
        const { stdout, ...diagnostics } = output.stagedDiff;
        body.append(fields(diagnostics));
      }
      if (remaining.status && typeof remaining.status === "object") {
        body.append(outputBlock("Git-Status", remaining.status.stdout || "Keine Einträge."));
        const { stdout, ...diagnostics } = remaining.status;
        if (diagnostics.stderr || diagnostics.exitCode || diagnostics.truncated) body.append(fields(diagnostics));
        delete remaining.status;
      }
      if (Object.keys(remaining).length) body.append(fields(remaining));
    } else if (output) body.append(renderData(output));
    else if (running) body.append(node("p", "coding-note", ["coding.edit", "coding.write"].includes(step.tool)
      ? "Datei wird geprüft; der Diff erscheint vor dem Speichern." : "Ausführung läuft …"));
    if (output?.error || output?.errorCode) body.classList.add("coding-step__detail--error");
    return body;
  }

  function stepSummary(step, status) {
    const input = json(step.inputJson), output = json(step.outputJson);
    const compact = value => String(value ?? "").replace(/\s+/g, " ").trim();
    const shorten = (value, maximum) => {
      const text = compact(value);
      return text.length > maximum ? `${text.slice(0, maximum - 1)}…` : text;
    };
    const lastLine = value => String(value || "").trim().split(/\r?\n/).filter(line => line.trim()).at(-1) || "";
    let target = input?.path || input?.url || input?.query || input?.title || "";
    let progress = "";
    if (step.tool === "coding.command") {
      target = [input?.executable, ...(input?.arguments || [])].filter(value => value != null).join(" ");
      const stdout = lastLine(output?.stdout), stderr = lastLine(output?.stderr);
      const lineLimit = stdout && stderr ? 50 : 120;
      progress = [output?.exitCode != null ? `Exitcode ${output.exitCode}` : "",
        stdout ? `stdout: ${shorten(stdout, lineLimit)}` : "",
        stderr ? `stderr: ${shorten(stderr, lineLimit)}` : "",
        !stdout && !stderr ? output?.error || output?.errorCode || "" : ""].filter(Boolean).join(" · ");
    } else if (output?.error || output?.errorCode) {
      progress = output.error || output.errorCode;
    } else if (Array.isArray(output?.entries)) {
      progress = `${output.entries.length} ${output.entries.length === 1 ? "Eintrag" : "Einträge"}`;
    } else if (Array.isArray(output?.matches) || Array.isArray(output?.results)) {
      progress = `${(output.matches || output.results).length} Treffer`;
    } else if (Array.isArray(output?.sources)) {
      progress = `${output.sources.length} ${output.sources.length === 1 ? "Quelle" : "Quellen"}`;
    } else if (["coding.write", "coding.edit", "coding.gitDiff"].includes(step.tool)) {
      const counts = [Number.isFinite(output?.addedLines) ? `+${output.addedLines}` : "",
        Number.isFinite(output?.removedLines) ? `−${output.removedLines}` : ""].filter(Boolean).join(" / ");
      const applied = step.tool !== "coding.gitDiff" && output && (output.applied === true || status === "completed" && output.success !== false);
      progress = [counts, applied ? "Gespeichert" : ""].filter(Boolean).join(" · ");
    } else if (Number.isFinite(output?.totalLines)) {
      progress = `${output.totalLines} ${output.totalLines === 1 ? "Zeile" : "Zeilen"}`;
    } else if (step.tool === "coding.renderHtml" && status === "completed" && step.previewHtml) {
      progress = "Vorschau verfügbar";
    }
    if (output?.truncated || output?.diffTruncated) progress = [progress, "Ausgabe gekürzt"].filter(Boolean).join(" · ");
    // Only this compact header is shortened; the disclosure keeps all captured data.
    return [shorten(target, 100), shorten(progress, 160)].filter(Boolean).join(" · ");
  }

  function stepContent(step, status, messageId, options) {
    const content = node("div", "coding-step__content");
    if (step.inputJson || step.outputJson) {
      const copy = copyButton(JSON.stringify({ tool: step.tool, status, explanation: step.explanation,
        input: json(step.inputJson), output: json(step.outputJson) }, null, 2), "Daten kopieren");
      copy.classList.add("coding-step__copy");
      content.append(copy);
    }
    const input = json(step.inputJson);
    const explanation = step.explanation && !/^GO soll (?:coding|web)\./.test(step.explanation)
      ? step.explanation : input ? describe(step.tool, input) : "";
    if (explanation && explanation !== step.label) content.append(node("p", "coding-step__explanation", explanation));
    content.append(stepBody(step, status, options));
    if (step.tool === "coding.renderHtml" && status === "completed" && step.previewHtml) {
      const preview = node("button", "coding-preview-button", "HTML-Vorschau öffnen");
      preview.type = "button";
      preview.addEventListener("click", () => options.onPreview?.(messageId, String(step.id)));
      content.append(preview);
    }
    return content;
  }

  function syncDisclosure(disclosure) {
    const content = disclosure.querySelector(".coding-step__content");
    if (!disclosure.hasAttribute("open")) content?.remove();
    else if (!content && disclosure._codingCreateContent) disclosure.append(disclosure._codingCreateContent());
  }

  function disclosureToggled(event) {
    syncDisclosure(event.currentTarget || event.target);
  }

  function expandsChangesByDefault(step) {
    // Open mutations before the first receipt arrives so their live preview is
    // immediately visible. Replacements, added files and deletions are all
    // represented by these tools' unified patches, including /dev/null sides.
    if (["coding.edit", "coding.write", "coding.gitDiff"].includes(step.tool)) return true;
    const output = json(step.outputJson);
    return Boolean(typeof output?.diff === "string" ? output.diff : output?.diff?.stdout)
      || Boolean(output?.stagedDiff?.stdout);
  }

  function displayStepStatus(step, message) {
    const status = String(step.status || "running").toLowerCase();
    const output = json(step.outputJson);
    if (status === "interrupted" && (output?.state === "steered" || output?.generation?.state === "steered")) return "steered";
    if (!terminalStates.has(status) && terminalStates.has(String(message.status).toLowerCase())) {
      return message.status === "steered" ? "steered" : "interrupted";
    }
    return status;
  }

  function renderStep(step, index, message, options, previous) {
    const status = displayStepStatus(step, message);
    const section = node("section", `coding-step coding-step--${status}`);
    section.dataset.stepId = String(step.id);
    section.dataset.timelineKey = `tool-${step.id}`;
    const signature = [step.tool, step.label, status, index, step.inputJson, step.outputJson, step.detail, step.explanation, step.previewHtml];
    section._codingSignature = signature;
    if (previous?._codingSignature?.every((value, i) => value === signature[i])) {
      section._codingKeep = previous;
      return section;
    }
    const label = step.label || step.tool;
    section.setAttribute("aria-label", `Schritt ${index + 1}: ${label}`);
    const disclosure = node("details", "coding-step__disclosure");
    const previousDisclosure = previous?.querySelector(".coding-step__disclosure");
    if (previousDisclosure ? previousDisclosure.hasAttribute("open")
      : expandsChangesByDefault(step) || options.codingToolStepsExpanded === true) {
      disclosure.setAttribute("open", "");
    }
    const title = node("summary", "coding-step__title");
    const number = node("span", "coding-step__number", String(index + 1).padStart(2, "0"));
    number.setAttribute("aria-hidden", "true");
    const name = node("strong", "coding-step__name", label);
    name.title = step.tool;
    const state = node("span", "coding-step__status", statusLabels[status] || statusLabels.running);
    state.setAttribute("role", "status");
    state.setAttribute("aria-live", "polite");
    const chevron = node("span", "coding-step__chevron", "›");
    chevron.setAttribute("aria-hidden", "true");
    title.append(number, name, state, chevron);
    const summary = stepSummary(step, status);
    if (summary) title.append(node("span", "coding-step__summary", summary));
    disclosure.append(title);
    // Retain the receipt, not thousands of hidden patch/text DOM nodes. Avoid
    // capturing previousTimeline here, which would retain older render trees.
    const contentOptions = { renderMarkdown: options.renderMarkdown, enhanceCodeBlocks: options.enhanceCodeBlocks,
      onPreview: options.onPreview };
    const messageId = String(message.id);
    disclosure._codingCreateContent = () => stepContent(step, status, messageId, contentOptions);
    disclosure.addEventListener("toggle", disclosureToggled);
    syncDisclosure(disclosure);
    section.append(disclosure);
    return section;
  }

  function renderReasoning(step, message, options, previous) {
    const status = displayStepStatus(step, message);
    const active = !terminalStates.has(status);
    const input = json(step.inputJson);
    const round = Number.isSafeInteger(input?.round) && input.round > 0 ? input.round : null;
    const compaction = input?.phase === "compaction";
    const text = String(step.detail || "");
    const section = node("section", `coding-reasoning coding-reasoning--${status}`);
    section.dataset.stepId = String(step.id);
    section.dataset.timelineKey = `tool-${step.id}`;
    // Model reasoning is a separate display channel, never answer narration or
    // a real execution. In particular, it must not enter read-aloud selection.
    section.dataset.speechExclude = "true";
    section.setAttribute("aria-label", ["Denkprozess", round ? `Runde ${round}` : "", compaction ? "Kontextverdichtung" : ""].filter(Boolean).join(" · "));
    const signature = [status, round, text, compaction];
    section._codingSignature = signature;
    if (previous?._codingSignature?.every((value, i) => value === signature[i])) {
      section._codingKeep = previous;
      return section;
    }

    const disclosure = node("details", "coding-step__disclosure coding-reasoning__disclosure");
    const previousDisclosure = previous?.querySelector(".coding-reasoning__disclosure");
    if (!previousDisclosure || previousDisclosure.hasAttribute("open")) disclosure.setAttribute("open", "");
    const title = node("summary", "coding-reasoning__title");
    const indicator = node("span", active ? "coding-reasoning__indicator message-status-spinner" : "coding-reasoning__indicator",
      active ? "" : status === "completed" ? "✓" : "·");
    indicator.setAttribute("aria-hidden", "true");
    const name = node("strong", "coding-reasoning__name", "Denkprozess");
    title.append(indicator, name);
    if (round) title.append(node("span", "coding-reasoning__round", `Runde ${round}`));
    if (compaction) title.append(node("span", "coding-reasoning__round coding-reasoning__phase", "Kontextverdichtung"));
    const state = node("span", "coding-reasoning__status", active ? "Denkt nach" : statusLabels[status]);
    state.setAttribute("role", "status");
    state.setAttribute("aria-live", "polite");
    const chevron = node("span", "coding-step__chevron", "›");
    chevron.setAttribute("aria-hidden", "true");
    title.append(state, chevron);
    // Only the closed preview is shortened. The expanded text retains every
    // received paragraph and code block, without an inner scroll container.
    const preview = text.trim().split(/\r?\n/).filter(line => line.trim()).at(-1) || "Gedanken werden empfangen …";
    title.append(node("span", "coding-reasoning__preview", preview.replace(/\s+/g, " ").slice(0, 180)));
    disclosure.append(title);
    const renderMarkdown = options.renderMarkdown, enhanceCodeBlocks = options.enhanceCodeBlocks;
    disclosure._codingCreateContent = () => {
      const content = node("div", "coding-step__content coding-reasoning__content");
      const body = node("div", "message-content coding-reasoning__body");
      body.setAttribute("aria-live", "off");
      if (text.trim()) {
        body.append(renderMarkdown(text));
        enhanceCodeBlocks?.(body);
      } else body.append(node("p", "coding-note", active ? "Gedanken werden empfangen …" : "Kein Denktext empfangen."));
      content.append(body);
      return content;
    };
    disclosure.addEventListener("toggle", disclosureToggled);
    syncDisclosure(disclosure);
    section.append(disclosure);
    return section;
  }

  function render(message, steps, options) {
    const timeline = node("div", "coding-timeline");
    timeline.dataset.timelineKey = "timeline";
    const content = String(message.content || "");
    const previousByKey = new Map(options.previousTimeline
      ? [...options.previousTimeline.children].map(item => [item.dataset.timelineKey, item]) : []);
    let offset = 0;
    function narration(text, key, streaming = false) {
      const visible = options.sanitizeText ? options.sanitizeText(text) : text;
      if (!visible.trim()) return;
      const block = node("div", `message-content coding-narration${streaming ? " stream-cursor" : ""}`);
      block.dataset.timelineKey = key;
      block._codingSignature = [visible, streaming, terminalStates.has(String(message.status).toLowerCase())];
      const previous = previousByKey.get(key);
      if (previous?._codingSignature?.every((value, i) => value === block._codingSignature[i])) {
        block._codingKeep = previous;
        timeline.append(block);
        return;
      }
      block.append(options.renderMarkdown(visible));
      options.enhanceCodeBlocks?.(block);
      timeline.append(block);
    }
    let executionIndex = 0;
    steps.filter(step => step.kind !== "phase").forEach(step => {
      if (Number.isInteger(step.contentOffset)) {
        const end = Math.max(offset, Math.min(content.length, step.contentOffset));
        narration(content.slice(offset, end), `text-before-${step.id}`);
        offset = end;
      }
      const previous = previousByKey.get(`tool-${step.id}`);
      timeline.append(step.agentNotice && globalThis.goAgentTabs
        ? globalThis.goAgentTabs.renderNotice(step, options)
        : step.tool === "assistant.steering"
        ? renderSteering(step)
        : ["assistant.narration", "assistant.progress"].includes(step.tool)
        ? renderAgentActivity(step, message, options)
        : step.tool === "assistant.reasoning"
        ? renderReasoning(step, message, options, previous)
        : renderStep(step, executionIndex++, message, options, previous));
    });
    const active = !terminalStates.has(String(message.status).toLowerCase());
    narration(content.slice(offset), "text-tail", active && !steps.some(step => !terminalStates.has(String(step.status).toLowerCase())));
    if (active && options.liveStatus?.status) {
      const phase = node("div", "coding-live-phase");
      phase.dataset.timelineKey = "live-phase";
      phase.setAttribute("role", "status");
      phase.setAttribute("aria-live", "polite");
      phase.append(node("span", "message-status-spinner"), node("span", "", options.liveStatus.status));
      if (options.liveStatus.detail) phase.append(node("span", "coding-live-phase__detail", options.liveStatus.detail));
      timeline.append(phase);
    }
    return timeline;
  }

  function renderAgentActivity(step, message, options) {
    const section = node("section", `subagent-activity subagent-activity--${step.tool.split(".")[1]}`);
    section.dataset.timelineKey = `tool-${step.id}`;
    section.dataset.stepId = String(step.id);
    const round = json(step.inputJson)?.round;
    if (step.tool === "assistant.narration") {
      if (round) section.append(node("span", "subagent-activity__round", `Runde ${round}`));
      const body = node("div", "message-content");
      body.append(options.renderMarkdown(String(step.detail || "")));
      options.enhanceCodeBlocks?.(body);
      section.append(body);
    } else {
      const displayStatus = displayStepStatus(step, message);
      const running = !terminalStates.has(displayStatus);
      const status = node("div", "subagent-activity__status");
      status.setAttribute("role", "status");
      if (running) status.append(node("span", "message-status-spinner"));
      status.append(node("span", "", displayStatus === "steered" ? "Umgeleitet" : String(step.detail || (running ? "Arbeitet" : "Abgeschlossen"))));
      section.append(status);
      const data = json(step.outputJson);
      const generation = data?.generation || {}, context = data?.context || {}, measured = data?.metrics?.metrics || data?.metrics || {};
      const inputTokens = data?.metrics?.inputTokens ?? measured.inputTokens ?? generation.inputTokens ?? generation.promptTokens;
      const outputTokens = data?.metrics?.outputTokens ?? measured.outputTokens ?? generation.outputTokens ?? generation.generatedTokens;
      const hasTokens = Number.isFinite(inputTokens) || Number.isFinite(outputTokens);
      const totalTokens = (Number.isFinite(inputTokens) ? inputTokens : 0) + (Number.isFinite(outputTokens) ? outputTokens : 0);
      const metrics = [
        hasTokens ? `${totalTokens.toLocaleString("de-DE")} Token` : "",
        Number.isFinite(context.contextLimit) ? `Kontext: ${(context.estimatedInputTokens ?? inputTokens ?? 0).toLocaleString("de-DE")} / ${context.contextLimit.toLocaleString("de-DE")}` : ""
      ].filter(Boolean);
      if (metrics.length) section.append(node("p", "subagent-activity__metrics", metrics.join(" · ")));
    }
    return section;
  }

  function renderSteering(step) {
    const section = node("section", "steering-message");
    section.dataset.timelineKey = `tool-${step.id}`;
    section.dataset.stepId = String(step.id);
    section.dataset.speechExclude = "true";
    const input = json(step.inputJson);
    const label = node("div", "steering-message__label", "Du · Umlenkung");
    const body = node("div", "steering-message__text", String(step.detail || input?.text || ""));
    const status = node("span", "steering-message__status", step.status === "completed" ? "Angewendet"
      : terminalStates.has(String(step.status).toLowerCase()) ? "Nicht angewendet" : "Übernommen · wird angewendet");
    section.append(label, body, status);
    return section;
  }

  // Keep completed rows and unchanged text nodes in place while output grows.
  // This preserves selection and avoids resetting embedded controls on each token.
  function reconcile(current, next) {
    if (next._codingKeep === current) return current;
    if (!current || current.nodeType !== next.nodeType || current.nodeName !== next.nodeName) {
      current?.replaceWith(next);
      return next;
    }
    if (current.nodeType === 3) {
      if (current.nodeValue !== next.nodeValue) {
        if (next.nodeValue.startsWith(current.nodeValue) && typeof current.appendData === "function") current.appendData(next.nodeValue.slice(current.nodeValue.length));
        else current.nodeValue = next.nodeValue;
      }
      return current;
    }
    if (current.nodeType !== 1) return current;
    if (next._codingSignature) current._codingSignature = next._codingSignature;
    // Native disclosure state belongs to the reader. Streaming output and setting
    // changes update the contents without opening or closing an existing step.
    if (current.nodeName === "DETAILS" && current.classList.contains("coding-step__disclosure")) {
      if (current.hasAttribute("open")) next.setAttribute("open", "");
      else next.removeAttribute("open");
      current._codingCreateContent = next._codingCreateContent;
      // Reconciliation can also be called without the previous render cache.
      // Build only the reader's actual open state, then reconcile its contents.
      syncDisclosure(next);
    }
    for (const attr of [...current.attributes]) if (!next.hasAttribute(attr.name)) current.removeAttribute(attr.name);
    for (const attr of [...next.attributes]) if (current.getAttribute(attr.name) !== attr.value) current.setAttribute(attr.name, attr.value);
    // Event handlers capture the most recent copy payload and message footer data.
    if (["BUTTON", "A", "AUDIO", "VIDEO", "IFRAME"].includes(current.nodeName)) {
      current.replaceWith(next);
      return next;
    }
    const oldChildren = [...current.childNodes];
    const keyed = new Map(oldChildren.filter(child => child.dataset?.timelineKey).map(child => [child.dataset.timelineKey, child]));
    let index = 0;
    for (const child of [...next.childNodes]) {
      const key = child.dataset?.timelineKey;
      const old = key ? keyed.get(key) : oldChildren[index]?.dataset?.timelineKey ? null : oldChildren[index];
      const retained = old ? reconcile(old, child) : child;
      const at = current.childNodes[index];
      if (retained !== at) current.insertBefore(retained, at || null);
      index++;
    }
    while (current.childNodes.length > index) current.lastChild.remove();
    return current;
  }

  globalThis.goCodingTimeline = { render, parseUnifiedDiff, reconcile };
})();
