(() => {
  "use strict";
  const cache = new Map();
  const maxSourceLength = 256 * 1024;
  const autoLanguages = ["python", "javascript", "typescript", "csharp", "cpp", "json", "bash", "powershell", "sql", "xml", "css"];

  function pythonCommandTree(engine, source, label) {
    if (label && !/^(?:python|py|bash|sh|shell|powershell|ps1|dos|bat|cmd)$/.test(label)) return null;
    // Diagnostic commands often embed a complete Python program in one quoted
    // -c argument. A shell grammar colors that whole program as one string.
    // Recognize only an actual leading Python invocation, and keep every byte
    // (including quotes, indentation and escapes) in the displayed/copy source.
    const executable = '(?:[^\\s"\'`]*[\\\\/])?python(?:\\d+(?:\\.\\d+)*)?(?:\\.exe)?';
    const quotedExecutable = '["\'][^"\'\\r\\n]*[\\\\/]python(?:\\d+(?:\\.\\d+)*)?(?:\\.exe)?["\']';
    const invocation = new RegExp(`^\\s*(?:&\\s+)?(?:${executable}|${quotedExecutable})\\s+(?:(?:-[uBIEsSOPq]|-OO)\\s+)*-c\\s+(["'])`).exec(source);
    if (!invocation) return null;
    const quote = invocation[1], start = invocation[0].length;
    let end = source.length;
    for (let index = start; index < source.length; index++) {
      if (source[index] === "\\" || (quote === '"' && source[index] === "`")) {
        index++;
      } else if (source[index] === quote) {
        if (quote === "'" && source[index + 1] === "'") { index++; continue; }
        // An additional command, pipe or argument makes the shell structure
        // ambiguous. Its normal grammar remains the safe display fallback.
        if (source.slice(index + 1).trim()) return null;
        end = index;
        break;
      }
    }
    const script = source.slice(start, end);
    if (!script) return null;
    const tree = engine.highlight(script, { language: "python", ignoreIllegals: true })._emitter?.rootNode;
    if (!tree) return null;
    return { children: [source.slice(0, start), tree, source.slice(end)] };
  }

  function appendToken(parent, token) {
    if (typeof token === "string") {
      parent.append(document.createTextNode(token));
      return;
    }
    let target = parent;
    if (token.scope) {
      target = document.createElement("span");
      // Only token classes and text enter the DOM. Code is never parsed as HTML.
      const scope = String(token.scope).replace(/[^a-zA-Z0-9_.-]/g, "");
      target.className = `hljs-${scope.split(".")[0]}`;
      parent.append(target);
    }
    for (const child of token.children || []) appendToken(target, child);
  }

  function appendTo(code, content, language) {
    const source = String(content ?? "");
    code.textContent = source;
    const engine = globalThis.hljs;
    const label = String(language || "").trim().split(/\s+/)[0].toLowerCase().replace(/^language-/, "");
    if (!engine || !source || source.length > maxSourceLength || /^(?:text|txt|plain|plaintext|console|output)$/.test(label)) return;
    if (label && !engine.getLanguage(label)) return;
    try {
      const key = `${label}\0${source}`;
      let tree = cache.get(key);
      if (!tree) {
        tree = pythonCommandTree(engine, source, label);
        const result = tree ? null : label
          ? engine.highlight(source, { language: label, ignoreIllegals: true })
          : engine.highlightAuto(source, autoLanguages);
        // Pinned highlight.js 11.11.1 token tree avoids injecting its HTML output.
        tree ||= result?._emitter?.rootNode;
        if (!tree) return;
        if (source.length <= 64 * 1024) {
          if (cache.size >= 16) cache.delete(cache.keys().next().value);
          cache.set(key, tree);
        }
      }
      const fragment = document.createDocumentFragment();
      appendToken(fragment, tree);
      // Retain the exact source even if a future grammar changes its token output.
      if (fragment.textContent !== source) return;
      code.textContent = "";
      code.append(fragment);
    } catch {
      code.textContent = source;
    }
  }

  globalThis.goCodeHighlight = Object.freeze({ appendTo });
})();
