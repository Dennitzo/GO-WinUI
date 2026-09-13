(() => {
  "use strict";

  const count = value => Number.isSafeInteger(value) && value >= 0 ? value : null;
  const pathKey = value => String(value || "").replace(/\\/g, "/").replace(/\/$/, "").toLowerCase();
  const node = (tag, className, text) => {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = String(text);
    return result;
  };

  function normalize(summary) {
    if (!summary || typeof summary !== "object") return null;
    const files = new Map();
    for (const file of Array.isArray(summary.files) ? summary.files : []) {
      if (!file || typeof file.path !== "string" || !file.path.trim()) continue;
      files.set(pathKey(file.path), { ...file, path: file.path, addedLines: count(file.addedLines),
        removedLines: count(file.removedLines), diff: typeof file.diff === "string" ? file.diff : "",
        binary: Boolean(file.isBinary ?? file.binary), created: file.kind === "added", deleted: file.kind === "deleted" });
    }
    const values = [...files.values()];
    return { ...summary, files: values, isPartial: Boolean(summary.isPartial), notice: String(summary.notice || ""),
      addedLines: values.length && values.every(file => file.addedLines !== null) ? values.reduce((sum, file) => sum + file.addedLines, 0) : null,
      removedLines: values.length && values.every(file => file.removedLines !== null) ? values.reduce((sum, file) => sum + file.removedLines, 0) : null };
  }

  function counts(value) {
    const result = node("span", "coding-changes-counts");
    if (value.addedLines !== null) result.append(node("span", "coding-changes-added", `+${value.addedLines}`));
    if (value.removedLines !== null) result.append(node("span", "coding-changes-removed", `−${value.removedLines}`));
    return result;
  }

  function fileKind(file) {
    if (file.observedOnly) return "Werkzeugbearbeitungen";
    if (file.deleted) return "Gelöscht";
    if (file.untracked || file.created) return "Neu";
    return file.binary ? "Binär" : "Geändert";
  }

  function renderDiff(file) {
    const content = node("div", "coding-changes-file__content");
    if (file.diffTruncated || file.truncated) content.append(node("p", "coding-changes-note", "Der Diff zeigt einen Ausschnitt der Änderung."));
    if (file.notice) content.append(node("p", "coding-changes-note", file.notice));
    if (!file.diff) {
      content.append(node("p", "coding-changes-note", file.binary
        ? "Binäre Datei geändert; ein Text-Diff ist nicht verfügbar." : "Für diese Datei ist kein Text-Diff verfügbar."));
      return content;
    }
    const copy = node("button", "coding-changes-copy", "Diff kopieren");
    copy.type = "button";
    copy.setAttribute("aria-label", `Diff für ${file.path} kopieren`);
    copy.addEventListener("click", async () => {
      try {
        if (globalThis.goBridge?.post) globalThis.goBridge.post("message.copy", { text: file.diff });
        else await navigator.clipboard.writeText(file.diff);
        copy.textContent = "Kopiert";
      } catch { copy.textContent = "Kopieren fehlgeschlagen"; }
    });
    content.append(copy);
    const patches = globalThis.goCodingTimeline?.parseUnifiedDiff(file.diff) || [];
    if (!patches.length) content.append(node("pre", "coding-changes-raw", file.diff));
    for (const patch of patches) {
      const lines = node("div", "coding-changes-diff");
      for (const line of patch.lines) {
        const row = node("div", `coding-changes-line coding-changes-line--${line.kind}`);
        const before = node("span", "coding-changes-line__number", line.oldLine ?? "");
        const after = node("span", "coding-changes-line__number", line.newLine ?? "");
        before.setAttribute("aria-hidden", "true");
        after.setAttribute("aria-hidden", "true");
        row.append(before, after, node("code", "coding-changes-line__text", line.text));
        lines.append(row);
      }
      content.append(lines);
    }
    return content;
  }

  function create({ host, onLayout = () => {} }) {
    const button = node("button", "coding-changes-chip");
    button.type = "button";
    button.setAttribute("aria-haspopup", "dialog");
    button.setAttribute("aria-controls", "coding-changes-dialog");
    button.setAttribute("aria-expanded", "false");
    const icon = node("span", "coding-changes-chip__icon", "±");
    icon.setAttribute("aria-hidden", "true");
    const label = node("span", "coding-changes-chip__label");
    const numbers = node("span", "coding-changes-chip__counts");
    const partial = node("span", "coding-changes-chip__partial", "(teilweise)");
    partial.setAttribute("aria-hidden", "true");
    button.append(icon, label, numbers, partial);
    host.replaceChildren(button);
    host.hidden = true;
    let current = null, scope = null, dialog = null, list = null, total = null, notice = null;
    let lastSummary = undefined, lastMode = undefined;
    const fileNodes = new Map();
    const expanded = new Map();

    const close = () => {
      if (!dialog) return;
      dialog.close();
    };
    const showFileContent = section => {
      const content = section.querySelector(".coding-changes-file__content");
      if (!section.hasAttribute("open")) content?.remove();
      else if (!content) section.append(renderDiff(section._file));
      expanded.set(pathKey(section._file.path), section.hasAttribute("open"));
    };
    const updateDialog = () => {
      if (!dialog || !current) return;
      total.textContent = current.files.length
        ? `${current.files.length} ${current.files.length === 1 ? "Datei" : "Dateien"} ${current.hasObservedChanges ? "erfasst" : "geändert"}` : "Dateiänderungen";
      total.append(counts(current));
      notice.textContent = current.notice || (current.isPartial ? "Die Änderungsübersicht ist noch unvollständig." : "");
      notice.hidden = !notice.textContent;
      const wanted = new Set();
      for (const [index, file] of current.files.entries()) {
        const key = pathKey(file.path);
        wanted.add(key);
        let section = fileNodes.get(key);
        if (!section) {
          section = node("details", "coding-changes-file");
          section.dataset.path = file.path;
          const title = node("summary", "coding-changes-file__title");
          title.append(node("span", "coding-changes-file__chevron", "›"), node("span", "coding-changes-file__path"),
            node("span", "coding-changes-file__kind"), node("span", "coding-changes-file__counts"));
          section.append(title);
          section.addEventListener("toggle", () => showFileContent(section));
          if (expanded.get(key) ?? true) section.setAttribute("open", "");
          fileNodes.set(key, section);
        }
        const changed = !section._file || section._file.diff !== file.diff || section._file.binary !== file.binary
          || section._file.diffTruncated !== file.diffTruncated || section._file.notice !== file.notice;
        section._file = file;
        section.dataset.path = file.path;
        section.querySelector(".coding-changes-file__path").textContent = file.path;
        section.querySelector(".coding-changes-file__kind").textContent = fileKind(file);
        section.querySelector(".coding-changes-file__counts").replaceChildren(counts(file));
        if (changed) section.querySelector(".coding-changes-file__content")?.remove();
        showFileContent(section);
        const before = list.children[index];
        if (before !== section) list.insertBefore(section, before || null);
      }
      for (const [key, section] of fileNodes) {
        if (wanted.has(key)) continue;
        section.remove(); fileNodes.delete(key);
      }
    };
    const open = () => {
      if (dialog || !current || host.hidden) return;
      dialog = node("dialog", "coding-changes-dialog");
      dialog.id = "coding-changes-dialog";
      dialog.setAttribute("aria-labelledby", "coding-changes-heading");
      const header = node("header", "coding-changes-dialog__header");
      const heading = node("h2", "", "Änderungen dieses Laufs");
      heading.id = "coding-changes-heading";
      const dismiss = node("button", "coding-changes-close", "×");
      dismiss.type = "button";
      dismiss.setAttribute("aria-label", "Änderungsübersicht schließen");
      dismiss.addEventListener("click", close);
      header.append(heading, dismiss);
      const body = node("div", "coding-changes-dialog__body");
      total = node("div", "coding-changes-total");
      notice = node("p", "coding-changes-note");
      list = node("div", "coding-changes-files");
      body.append(total, notice, list);
      dialog.append(header, body);
      dialog.addEventListener("close", () => {
        dialog?.remove(); dialog = list = total = notice = null;
        fileNodes.clear(); button.setAttribute("aria-expanded", "false");
        if (!host.hidden) button.focus?.();
      });
      document.body.append(dialog);
      updateDialog();
      dialog.showModal();
      button.setAttribute("aria-expanded", "true");
    };
    button.addEventListener("click", open);

    return { close, update(summary, context) {
      const nextScope = `${context.sessionId || ""}|${context.messageId || ""}|${pathKey(context.workspacePath)}`;
      if (scope === nextScope && lastSummary === summary && lastMode === context.isCoding) return;
      lastSummary = summary; lastMode = context.isCoding;
      if (scope !== nextScope) { close(); expanded.clear(); scope = nextScope; }
      current = normalize(summary);
      const visible = context.isCoding && current && String(current.sessionId || "") === String(context.sessionId || "")
        && String(current.messageId || "") === String(context.messageId || "")
        && pathKey(current.workspacePath) === pathKey(context.workspacePath)
        && (current.files.length > 0 || current.isPartial);
      const hidden = !visible;
      const layoutChanged = host.hidden !== hidden;
      host.hidden = hidden;
      if (hidden) { close(); if (layoutChanged) onLayout(); return; }
      label.textContent = current.files.length
        ? `${current.files.length} ${current.files.length === 1 ? "Datei" : "Dateien"} ${current.hasObservedChanges ? "erfasst" : "geändert"}` : "Dateiänderungen";
      numbers.replaceChildren(current.files.length ? counts(current) : node("span", ""));
      partial.hidden = !current.isPartial;
      button.title = "Änderungen dieses Laufs" + (current.isPartial ? " · Übersicht unvollständig" : "");
      button.setAttribute("aria-label", `${label.textContent}; ${current.addedLines ?? "unbekannt"} hinzugefügte und ${current.removedLines ?? "unbekannt"} entfernte Zeilen${current.isPartial ? "; Übersicht unvollständig" : ""}. Änderungsübersicht öffnen.`);
      updateDialog();
      if (layoutChanged) onLayout();
    } };
  }

  globalThis.goCodingChanges = { create, normalize };
})();
