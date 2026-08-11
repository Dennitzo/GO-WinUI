(function () {
  "use strict";

  const mathRenderCache = new Map();
  const maxMathRenderCacheEntries = 600;

  function copyText(text) {
    if (globalThis.goBridge && typeof globalThis.goBridge.post === "function") {
      globalThis.goBridge.post("message.copy", { text });
      return Promise.resolve();
    }
    if (globalThis.navigator?.clipboard?.writeText) {
      return globalThis.navigator.clipboard.writeText(text);
    }
    return Promise.reject(new Error("Clipboard unavailable"));
  }

  function katexStrictMode(errorCode) {
    return errorCode === "unicodeTextInMathMode" ? "ignore" : "error";
  }

  function normalizedMathParts(rawMath) {
    const source = String(rawMath || "").trim();
    let display = false;
    let tex = source;
    if (source.startsWith("\\[") && source.endsWith("\\]")) {
      display = true;
      tex = source.slice(2, -2).trim();
    } else if (source.startsWith("\\(") && source.endsWith("\\)")) {
      tex = source.slice(2, -2).trim();
    } else if (source.startsWith("$$") && source.endsWith("$$") && source.length > 4) {
      display = true;
      tex = source.slice(2, -2).trim();
    } else if (source.startsWith("$") && source.endsWith("$") && source.length > 2) {
      tex = source.slice(1, -1).trim();
    }

    tex = tex
      .replace(/\u00a0/g, "~")
      .replace(/[\u2009\u202f]/g, "\\,")
      .replace(/\\text\{([^{}]*[\u00b7\u22c5][^{}]*)\}/g, (_match, body) => (
        body.split(/[\u00b7\u22c5]/).map(part => `\\text{${part}}`).join("\\cdot")
      ))
      .replace(/[\u00b7\u22c5]/g, "\\cdot");
    return { source, tex, display };
  }

  function cachedKatexHtml(tex, displayMode) {
    if (!globalThis.katex || typeof globalThis.katex.renderToString !== "function") return "";
    const key = `${displayMode ? "display" : "inline"}\n${tex}`;
    if (mathRenderCache.has(key)) return mathRenderCache.get(key);

    let html = "";
    try {
      html = globalThis.katex.renderToString(tex, {
        displayMode,
        throwOnError: true,
        output: "html",
        strict: katexStrictMode,
        trust: false
      });
    } catch {
      html = "";
    }
    if (!html) return "";

    mathRenderCache.set(key, html);
    if (mathRenderCache.size > maxMathRenderCacheEntries) {
      mathRenderCache.delete(mathRenderCache.keys().next().value);
    }
    return html;
  }

  function createSelectableMathNode(rawMath) {
    const { source, tex, display } = normalizedMathParts(rawMath);
    const wrapper = document.createElement("span");
    wrapper.className = `math-selectable${display ? " display" : ""}`;
    wrapper.setAttribute("aria-label", `LaTeX: ${source}`);
    wrapper.title = "LaTeX kopieren";

    const rendered = document.createElement("span");
    rendered.className = "math-render";
    rendered.setAttribute("aria-hidden", "true");
    const katexHtml = cachedKatexHtml(tex, display);

    const sourceText = document.createElement("span");
    sourceText.className = "math-source-text";
    sourceText.textContent = source;

    if (katexHtml) {
      rendered.innerHTML = katexHtml;
      rendered.dataset.mathTypeset = "true";
      wrapper.append(rendered, sourceText);
    } else {
      sourceText.classList.add("fallback");
      wrapper.classList.add("invalid");
      wrapper.append(sourceText);
    }

    wrapper.addEventListener("click", event => {
      const selection = typeof globalThis.getSelection === "function"
        ? String(globalThis.getSelection() || "")
        : "";
      if (selection) return;
      event.preventDefault();
      event.stopPropagation();
      copyText(source).then(() => {
        wrapper.classList.add("copied");
        globalThis.setTimeout(() => wrapper.classList.remove("copied"), 900);
      }).catch(() => {
        wrapper.title = "Kopieren fehlgeschlagen";
        globalThis.setTimeout(() => { wrapper.title = "LaTeX kopieren"; }, 1200);
      });
    });
    return wrapper;
  }

  function isInlineDollarMath(rawMath) {
    const source = String(rawMath || "");
    if (!source.startsWith("$") || !source.endsWith("$") || source.startsWith("$$") || source.length <= 2) {
      return false;
    }
    const body = source.slice(1, -1);
    return body.trim() === body && body.length > 0;
  }

  function isEscapedDelimiter(text, index) {
    let slashCount = 0;
    for (let cursor = index - 1; cursor >= 0 && text[cursor] === "\\"; cursor -= 1) slashCount += 1;
    return slashCount % 2 === 1;
  }

  function closingDelimiterIndex(text, startIndex, delimiter, sameLine) {
    let cursor = startIndex;
    while (cursor < text.length) {
      const found = text.indexOf(delimiter, cursor);
      if (found < 0 || (sameLine && text.slice(startIndex, found).includes("\n"))) return -1;
      if (!isEscapedDelimiter(text, found)) return found;
      cursor = found + delimiter.length;
    }
    return -1;
  }

  function protectMarkdownSegments(text) {
    const source = String(text || "");
    const segments = [];
    let protectedText = "";
    let index = 0;

    const appendProtected = (value, kind) => {
      const token = `\uE000GO_MATH_${segments.length.toString(36).toUpperCase()}\uE001`;
      segments.push({ token, source: value, kind });
      protectedText += token;
    };

    while (index < source.length) {
      if (source[index] === "`") {
        const close = source.indexOf("`", index + 1);
        if (close > index + 1) {
          appendProtected(source.slice(index, close + 1), "code");
          index = close + 1;
          continue;
        }
      }

      let open = "";
      let close = "";
      let sameLine = false;
      if (source.startsWith("\\[", index)) {
        open = "\\[";
        close = "\\]";
      } else if (source.startsWith("\\(", index)) {
        open = "\\(";
        close = "\\)";
        sameLine = true;
      } else if (source.startsWith("$$", index) && !isEscapedDelimiter(source, index)) {
        open = "$$";
        close = "$$";
      } else if (source[index] === "$" && source[index + 1] !== "$" && !isEscapedDelimiter(source, index)) {
        open = "$";
        close = "$";
        sameLine = true;
      }

      if (open) {
        const closeIndex = closingDelimiterIndex(source, index + open.length, close, sameLine);
        if (closeIndex >= 0) {
          const end = closeIndex + close.length;
          const candidate = source.slice(index, end);
          if (open !== "$" || isInlineDollarMath(candidate)) {
            appendProtected(candidate, "math");
            index = end;
            continue;
          }
        } else {
          const lineEnd = source.indexOf("\n", index);
          const end = lineEnd < 0 ? source.length : lineEnd;
          appendProtected(source.slice(index, end), "literal");
          index = end;
          continue;
        }
      }

      protectedText += source[index];
      index += 1;
    }
    return { text: protectedText, segments };
  }

  function restoreSegments(text, segments) {
    let restored = String(text || "");
    for (const segment of segments || []) restored = restored.split(segment.token).join(segment.source);
    return restored;
  }

  function appendExternalLink(parent, url) {
    const link = document.createElement("a");
    link.href = url;
    link.textContent = url;
    link.rel = "noopener noreferrer";
    link.addEventListener("click", event => {
      event.preventDefault();
      globalThis.goBridge?.post("external.open", { url });
    });
    parent.append(link);
  }

  function appendInline(parent, text) {
    const protectedSource = protectMarkdownSegments(String(text || ""));
    const segmentByToken = new Map(protectedSource.segments.map(segment => [segment.token, segment]));
    const pattern = /(\uE000GO_MATH_[0-9A-Z]+\uE001)|\*\*(.+?)\*\*|\*([^*\n]{1,400})\*|<(sub|sup)>([^<>\r\n]+)<\/\4>|(https?:\/\/[^\s<]+)/gi;
    let cursor = 0;
    let match = pattern.exec(protectedSource.text);
    while (match) {
      if (match.index > cursor) parent.append(document.createTextNode(protectedSource.text.slice(cursor, match.index)));
      if (match[1]) {
        const segment = segmentByToken.get(match[1]);
        if (!segment || segment.kind === "literal") {
          parent.append(document.createTextNode(segment?.source || match[1]));
        } else if (segment.kind === "code") {
          const code = document.createElement("code");
          code.textContent = segment.source.slice(1, -1);
          parent.append(code);
        } else {
          parent.append(createSelectableMathNode(segment.source));
        }
      } else if (match[2] !== undefined) {
        const strong = document.createElement("strong");
        appendInline(strong, restoreSegments(match[2], protectedSource.segments));
        parent.append(strong);
      } else if (match[3] !== undefined) {
        const emphasis = document.createElement("em");
        appendInline(emphasis, restoreSegments(match[3], protectedSource.segments));
        parent.append(emphasis);
      } else if (match[4]) {
        const semantic = document.createElement(match[4].toLowerCase());
        semantic.textContent = restoreSegments(match[5], protectedSource.segments);
        parent.append(semantic);
      } else if (match[6]) {
        appendExternalLink(parent, match[6]);
      }
      cursor = match.index + match[0].length;
      match = pattern.exec(protectedSource.text);
    }
    if (cursor < protectedSource.text.length) {
      parent.append(document.createTextNode(protectedSource.text.slice(cursor)));
    }
  }

  function collectDisplayMathBlock(lines, startIndex) {
    const first = String(lines[startIndex] || "").trim();
    const open = first.startsWith("\\[") ? "\\[" : (first.startsWith("$$") ? "$$" : "");
    if (!open) return null;
    const close = open === "\\[" ? "\\]" : "$$";
    const parts = [];
    let index = startIndex;
    while (index < lines.length) {
      const line = String(lines[index] || "");
      const searchStart = index === startIndex ? line.indexOf(open) + open.length : 0;
      const closeIndex = closingDelimiterIndex(line, searchStart, close, false);
      parts.push(line);
      if (closeIndex >= 0) {
        if (line.slice(closeIndex + close.length).trim()) return null;
        return { math: parts.join("\n"), nextIndex: index + 1 };
      }
      index += 1;
    }
    return null;
  }

  function isTableSeparator(line) {
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(cell => /^:?-{3,}:?$/.test(cell));
  }

  function splitTableRow(line) {
    const protectedSource = protectMarkdownSegments(String(line || ""));
    const trimmed = protectedSource.text.trim().replace(/^\||\|$/g, "");
    if (!trimmed.includes("|")) return [];
    return trimmed.split("|").map(cell => restoreSegments(cell.trim(), protectedSource.segments));
  }

  function escapeMarkdownTableCell(value) {
    return String(value ?? "").replace(/\|/g, "\\|").replace(/\n+/g, " ").trim();
  }

  function canonicalMarkdownTableHeader(value) {
    return String(value || "")
      .toLocaleLowerCase("de-DE")
      .replace(/ä/g, "ae")
      .replace(/ö/g, "oe")
      .replace(/ü/g, "ue")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function markdownTableHeaderMatches(actual, expected) {
    const normalizedActual = canonicalMarkdownTableHeader(actual);
    const normalizedExpected = canonicalMarkdownTableHeader(expected);
    return normalizedActual === normalizedExpected || normalizedActual.startsWith(`${normalizedExpected} (`);
  }

  function splitPossibleCadHandleSuffix(value) {
    const match = String(value || "").trim().match(/^(.+\S)\s+([0-9a-f]*[a-f][0-9a-f]*|[0-9a-f]{3,})$/i);
    return match ? [match[1].trim(), match[2].trim()] : null;
  }

  function splitGluedCadObjectRows(cells, columnCount) {
    const output = [];
    for (const cell of cells) {
      if (columnCount > 0 && (output.length + 1) % columnCount === 0) {
        const split = splitPossibleCadHandleSuffix(cell);
        if (split) {
          output.push(split[0], split[1]);
          continue;
        }
      }
      output.push(cell);
    }
    return output;
  }

  function normalizeFlatCadObjectTableLine(line) {
    const source = String(line || "").trim();
    if (!/^Handle\s*\|\s*Typ\s*\|\s*Layer\s*\|/i.test(source)) return "";

    const expectedHeaders = ["Handle", "Typ", "Layer", "Breite X", "Tiefe Y", "Höhe Z", "Länge", "Fläche", "Volumen"];
    const cells = source.split("|").map(cell => cell.trim());
    if (cells[0] === "") cells.shift();
    if (cells[cells.length - 1] === "") cells.pop();
    if (cells.length <= expectedHeaders.length) return "";

    const headers = [];
    for (let index = 0; index < expectedHeaders.length - 1; index += 1) {
      if (!markdownTableHeaderMatches(cells[index], expectedHeaders[index])) return "";
      headers.push(cells[index]);
    }
    const lastHeader = String(cells[expectedHeaders.length - 1] || "").match(/^(Volumen(?:\s*\([^)]*\))?)(?:\s+(.+))?$/i);
    if (!lastHeader) return "";
    headers.push(lastHeader[1].trim());

    let data = cells.slice(expectedHeaders.length);
    if (lastHeader[2]) data.unshift(lastHeader[2].trim());
    data = splitGluedCadObjectRows(data, headers.length);
    if (data.length < headers.length || data.length % headers.length !== 0) return "";

    const rows = [];
    for (let index = 0; index < data.length; index += headers.length) rows.push(data.slice(index, index + headers.length));
    return [
      `| ${headers.map(escapeMarkdownTableCell).join(" | ")} |`,
      `| ${headers.map(() => "---").join(" | ")} |`,
      ...rows.map(row => `| ${row.map(escapeMarkdownTableCell).join(" | ")} |`)
    ].join("\n");
  }

  function splitLooseTableRow(line) {
    const value = String(line || "").trim();
    if (!value
      || value.startsWith("|")
      || /^#{1,6}\s+/.test(value)
      || /^>\s?/.test(value)
      || /^\s*[-*\u2022]\s+/.test(value)
      || /^\s*\d+[.)]\s+/.test(value)
      || isTableSeparator(value)) return null;
    const cells = value.includes("\t") ? value.split(/\t+/) : value.split(/\s{2,}/);
    const normalized = cells.map(cell => cell.trim()).filter(Boolean);
    return normalized.length >= 2 ? normalized : null;
  }

  function normalizePipeTablesWithoutSeparator(lines) {
    const output = [];
    for (let index = 0; index < lines.length;) {
      const first = splitTableRow(lines[index]);
      if (!first.length || isTableSeparator(lines[index])) {
        output.push(lines[index]);
        index += 1;
        continue;
      }

      const block = [lines[index]];
      let next = index + 1;
      while (next < lines.length) {
        const cells = splitTableRow(lines[next]);
        if (!cells.length || cells.length !== first.length) break;
        block.push(lines[next]);
        next += 1;
      }

      const contentRows = block.filter(line => !isTableSeparator(line));
      if (contentRows.length < 2) {
        output.push(lines[index]);
        index += 1;
        continue;
      }

      const separator = block.find(line => isTableSeparator(line))
        || `| ${first.map(() => "---").join(" | ")} |`;
      output.push(contentRows[0], separator, ...contentRows.slice(1));
      index = next;
    }
    return output;
  }

  function normalizeLooseMarkdownTables(text) {
    const pipeNormalized = normalizePipeTablesWithoutSeparator(String(text || "").replace(/\r\n/g, "\n").split("\n"));
    const output = [];
    for (let index = 0; index < pipeNormalized.length;) {
      const flatCadTable = normalizeFlatCadObjectTableLine(pipeNormalized[index]);
      if (flatCadTable) {
        output.push(...flatCadTable.split("\n"));
        index += 1;
        continue;
      }

      const first = splitLooseTableRow(pipeNormalized[index]);
      if (!first) {
        output.push(pipeNormalized[index]);
        index += 1;
        continue;
      }

      const rows = [first];
      let next = index + 1;
      while (next < pipeNormalized.length) {
        const cells = splitLooseTableRow(pipeNormalized[next]);
        if (!cells || cells.length !== first.length) break;
        rows.push(cells);
        next += 1;
      }
      if (rows.length < 2) {
        output.push(pipeNormalized[index]);
        index += 1;
        continue;
      }
      output.push(`| ${rows[0].map(escapeMarkdownTableCell).join(" | ")} |`);
      output.push(`| ${rows[0].map(() => "---").join(" | ")} |`);
      for (const row of rows.slice(1)) output.push(`| ${row.map(escapeMarkdownTableCell).join(" | ")} |`);
      index = next;
    }
    return output.join("\n");
  }

  function normalizeMarkdownStructure(text) {
    const protectedSource = protectMarkdownSegments(String(text || ""));
    return restoreSegments(normalizeLooseMarkdownTables(protectedSource.text), protectedSource.segments);
  }

  function normalizeMarkdownOutsideCodeFences(text) {
    const lines = String(text || "").split("\n");
    const output = [];
    let markdown = [];
    let inFence = false;
    const flushMarkdown = () => {
      if (!markdown.length) return;
      output.push(...normalizeMarkdownStructure(markdown.join("\n")).split("\n"));
      markdown = [];
    };
    for (const line of lines) {
      if (line.startsWith("```")) {
        if (!inFence) flushMarkdown();
        output.push(line);
        inFence = !inFence;
      } else if (inFence) {
        output.push(line);
      } else {
        markdown.push(line);
      }
    }
    flushMarkdown();
    return output.join("\n");
  }

  function createCodeBlock(language, content) {
    const block = document.createElement("div");
    block.className = "code-block";
    const header = document.createElement("div");
    header.className = "code-header";
    header.append(document.createTextNode(language || "Code"));
    const copy = document.createElement("button");
    copy.type = "button";
    copy.textContent = "Kopieren";
    copy.addEventListener("click", () => copyText(content));
    header.append(copy);
    const pre = document.createElement("pre");
    const code = document.createElement("code");
    code.textContent = content;
    pre.append(code);
    block.append(header, pre);
    return block;
  }

  function appendParagraph(root, lines) {
    if (!lines.length) return;
    const paragraph = document.createElement("p");
    appendInline(paragraph, lines.join("\n").trim());
    root.append(paragraph);
    lines.length = 0;
  }

  function render(markdown) {
    const root = document.createDocumentFragment();
    const normalized = normalizeMarkdownOutsideCodeFences(
      String(markdown || "").replace(/<\s*br\s*\/?\s*>/gi, "\n").replace(/\r\n?/g, "\n")
    );
    const lines = normalized.split("\n");
    const paragraph = [];
    let index = 0;

    while (index < lines.length) {
      const line = lines[index];
      const displayMath = collectDisplayMathBlock(lines, index);
      if (displayMath) {
        appendParagraph(root, paragraph);
        root.append(createSelectableMathNode(displayMath.math));
        index = displayMath.nextIndex;
        continue;
      }

      if (line.startsWith("```")) {
        appendParagraph(root, paragraph);
        const language = line.slice(3).trim();
        const content = [];
        index += 1;
        while (index < lines.length && !lines[index].startsWith("```")) content.push(lines[index++]);
        if (index < lines.length) index += 1;
        root.append(createCodeBlock(language, content.join("\n")));
        continue;
      }

      const headerCells = splitTableRow(line);
      if (headerCells.length && index + 1 < lines.length && isTableSeparator(lines[index + 1])) {
        appendParagraph(root, paragraph);
        const table = document.createElement("table");
        const head = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const cellText of headerCells) {
          const cell = document.createElement("th");
          appendInline(cell, cellText);
          headRow.append(cell);
        }
        head.append(headRow);
        table.append(head);
        const body = document.createElement("tbody");
        index += 2;
        while (index < lines.length) {
          const cells = splitTableRow(lines[index]);
          if (!cells.length) break;
          const row = document.createElement("tr");
          for (let cellIndex = 0; cellIndex < headerCells.length; cellIndex += 1) {
            const cell = document.createElement("td");
            appendInline(cell, cells[cellIndex] || "");
            row.append(cell);
          }
          body.append(row);
          index += 1;
        }
        table.append(body);
        const wrap = document.createElement("div");
        wrap.className = "table-wrap";
        wrap.append(table);
        root.append(wrap);
        continue;
      }

      const headingMatch = line.match(/^(#{1,4})\s+(.+)$/);
      if (headingMatch) {
        appendParagraph(root, paragraph);
        const heading = document.createElement(`h${headingMatch[1].length}`);
        appendInline(heading, headingMatch[2]);
        root.append(heading);
        index += 1;
        continue;
      }

      const unorderedMatch = line.match(/^\s*[-*\u2022]\s+(.+)$/);
      if (unorderedMatch) {
        appendParagraph(root, paragraph);
        const list = document.createElement("ul");
        while (index < lines.length) {
          const itemMatch = lines[index].match(/^\s*[-*\u2022]\s+(.+)$/);
          if (!itemMatch) break;
          const item = document.createElement("li");
          appendInline(item, itemMatch[1]);
          list.append(item);
          index += 1;
        }
        root.append(list);
        continue;
      }

      const orderedMatch = line.match(/^\s*(\d+)[.)]\s+(.+)$/);
      if (orderedMatch) {
        appendParagraph(root, paragraph);
        const list = document.createElement("ol");
        list.start = Number(orderedMatch[1]);
        while (index < lines.length) {
          const itemMatch = lines[index].match(/^\s*\d+[.)]\s+(.+)$/);
          if (!itemMatch) break;
          const item = document.createElement("li");
          appendInline(item, itemMatch[1]);
          list.append(item);
          index += 1;
        }
        root.append(list);
        continue;
      }

      if (/^>\s?/.test(line)) {
        appendParagraph(root, paragraph);
        const quoteLines = [];
        while (index < lines.length && /^>\s?/.test(lines[index])) quoteLines.push(lines[index++].replace(/^>\s?/, ""));
        const quote = document.createElement("blockquote");
        appendInline(quote, quoteLines.join("\n"));
        root.append(quote);
        continue;
      }

      if (!line.trim()) {
        appendParagraph(root, paragraph);
        index += 1;
        continue;
      }
      paragraph.push(line);
      index += 1;
    }

    appendParagraph(root, paragraph);
    return root;
  }

  globalThis.goMarkdown = Object.freeze({ render });
})();
