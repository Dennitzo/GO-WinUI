(function () {
  "use strict";

  function renderMath(expression, target, displayMode) {
    target.setAttribute("aria-label", `Formel: ${expression}`);
    if (globalThis.katex && typeof globalThis.katex.render === "function") {
      globalThis.katex.render(expression, target, {
        displayMode,
        throwOnError: false,
        strict: "warn",
        trust: false,
        output: "htmlAndMathml"
      });
    } else {
      target.textContent = expression;
    }
  }

  function appendInline(parent, text) {
    const expression = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*|\$[^$]+\$|https?:\/\/[^\s<]+)/g;
    let cursor = 0;
    for (const match of text.matchAll(expression)) {
      if (match.index > cursor) parent.append(document.createTextNode(text.slice(cursor, match.index)));
      const token = match[0];
      if (token.startsWith("`")) {
        const code = document.createElement("code");
        code.textContent = token.slice(1, -1);
        parent.append(code);
      } else if (token.startsWith("**")) {
        const strong = document.createElement("strong");
        strong.textContent = token.slice(2, -2);
        parent.append(strong);
      } else if (token.startsWith("*")) {
        const emphasis = document.createElement("em");
        emphasis.textContent = token.slice(1, -1);
        parent.append(emphasis);
      } else if (token.startsWith("$")) {
        const math = document.createElement("span");
        math.className = "math-expression";
        const expression = token.slice(1, -1);
        renderMath(expression, math, false);
        parent.append(math);
      } else {
        const link = document.createElement("a");
        link.href = token;
        link.textContent = token;
        link.rel = "noopener noreferrer";
        link.addEventListener("click", event => {
          event.preventDefault();
          globalThis.goBridge.post("external.open", { url: token });
        });
        parent.append(link);
      }
      cursor = match.index + token.length;
    }
    if (cursor < text.length) parent.append(document.createTextNode(text.slice(cursor)));
  }

  function isTableSeparator(line) {
    return /^\s*\|?\s*:?-{3,}/.test(line) && line.includes("|");
  }

  function tableCells(line) {
    return line.trim().replace(/^\||\|$/g, "").split("|").map(cell => cell.trim());
  }

  function render(markdown) {
    const root = document.createDocumentFragment();
    const lines = String(markdown || "").replace(/\r\n?/g, "\n").split("\n");
    let index = 0;
    while (index < lines.length) {
      const line = lines[index];
      if (line.trim().startsWith("$$")) {
        const expressionLines = [];
        const first = line.trim().slice(2);
        if (first.endsWith("$$")) {
          expressionLines.push(first.slice(0, -2));
          index += 1;
        } else {
          if (first) expressionLines.push(first);
          index += 1;
          while (index < lines.length && !lines[index].trim().endsWith("$$")) expressionLines.push(lines[index++]);
          if (index < lines.length) {
            expressionLines.push(lines[index].trim().slice(0, -2));
            index += 1;
          }
        }
        const math = document.createElement("div");
        math.className = "math-expression math-display";
        renderMath(expressionLines.join("\n"), math, true);
        root.append(math);
        continue;
      }
      if (line.startsWith("```")) {
        const language = line.slice(3).trim();
        const content = [];
        index += 1;
        while (index < lines.length && !lines[index].startsWith("```")) content.push(lines[index++]);
        index += index < lines.length ? 1 : 0;
        const block = document.createElement("div");
        block.className = "code-block";
        const header = document.createElement("div");
        header.className = "code-header";
        header.textContent = language || "Code";
        const copy = document.createElement("button");
        copy.type = "button";
        copy.textContent = "Kopieren";
        copy.addEventListener("click", () => globalThis.goBridge.post("message.copy", { text: content.join("\n") }));
        header.append(copy);
        const pre = document.createElement("pre");
        const code = document.createElement("code");
        code.textContent = content.join("\n");
        pre.append(code);
        block.append(header, pre);
        root.append(block);
        continue;
      }
      if (index + 1 < lines.length && line.includes("|") && isTableSeparator(lines[index + 1])) {
        const table = document.createElement("table");
        const head = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const cellText of tableCells(line)) {
          const cell = document.createElement("th"); appendInline(cell, cellText); headRow.append(cell);
        }
        head.append(headRow); table.append(head); index += 2;
        const body = document.createElement("tbody");
        while (index < lines.length && lines[index].includes("|") && lines[index].trim()) {
          const row = document.createElement("tr");
          for (const cellText of tableCells(lines[index++])) {
            const cell = document.createElement("td"); appendInline(cell, cellText); row.append(cell);
          }
          body.append(row);
        }
        table.append(body); const wrap = document.createElement("div"); wrap.className = "table-wrap"; wrap.append(table); root.append(wrap);
        continue;
      }
      if (/^#{1,4}\s/.test(line)) {
        const level = Math.min(4, line.match(/^#+/)?.[0].length || 2);
        const heading = document.createElement(`h${level}`); appendInline(heading, line.replace(/^#{1,4}\s+/, "")); root.append(heading); index += 1; continue;
      }
      if (/^\s*[-*]\s+/.test(line)) {
        const list = document.createElement("ul");
        while (index < lines.length && /^\s*[-*]\s+/.test(lines[index])) {
          const item = document.createElement("li"); appendInline(item, lines[index++].replace(/^\s*[-*]\s+/, "")); list.append(item);
        }
        root.append(list); continue;
      }
      if (/^\s*\d+\.\s+/.test(line)) {
        const list = document.createElement("ol");
        while (index < lines.length && /^\s*\d+\.\s+/.test(lines[index])) {
          const item = document.createElement("li"); appendInline(item, lines[index++].replace(/^\s*\d+\.\s+/, "")); list.append(item);
        }
        root.append(list); continue;
      }
      if (line.startsWith("> ")) {
        const quote = document.createElement("blockquote"); appendInline(quote, line.slice(2)); root.append(quote); index += 1; continue;
      }
      if (!line.trim()) { index += 1; continue; }
      const paragraph = document.createElement("p"); appendInline(paragraph, line); root.append(paragraph); index += 1;
    }
    return root;
  }

  globalThis.goMarkdown = Object.freeze({ render });
})();
