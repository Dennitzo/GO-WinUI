const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");

// Execute the shipped grammar, safe token renderer and Markdown pipeline. The
// DOM substitute explicitly rejects innerHTML, so source cannot become markup.
function renderer({ loadHighlight = true } = {}) {
  const copied = [];
  const context = vm.createContext({
    document: {
      createElement: tag => new TestNode(tag),
      createTextNode: text => new TestNode("#text", text),
      createDocumentFragment: () => new TestNode("#fragment")
    },
    URL,
    goBridge: { post: (type, payload) => copied.push({ type, payload }) }
  });
  if (loadHighlight) {
    for (const file of [
      "vendor/highlightjs/11.11.1/highlight.min.js",
      "vendor/highlightjs/11.11.1/powershell.min.js",
      "code-highlighting.js"
    ]) vm.runInContext(fs.readFileSync(path.join(webRoot, file), "utf8"), context, { filename: file });
  }
  vm.runInContext(fs.readFileSync(path.join(webRoot, "markdown.js"), "utf8"), context, { filename: "markdown.js" });
  return { render: source => context.goMarkdown.render(source), copied };
}

function fenced(render, language, source, close = true) {
  const root = render(`\`\`\`${language}\n${source}${close ? "\n```" : ""}`);
  const blocks = root.querySelectorAll(".code-block");
  assert.equal(blocks.length, 1);
  const code = blocks[0].querySelector("pre code");
  assert.ok(code);
  assert.equal(code.textContent, source, "highlighting and wrapping must preserve the exact code text");
  return { root, block: blocks[0], code };
}

function assertToken(code, tokenClass, text) {
  assert.ok(code.querySelectorAll(`.${tokenClass}`).some(node => node.textContent === text),
    `expected ${tokenClass} token ${JSON.stringify(text)}`);
}

test("Python sample uses distinct keyword, literal, number, string and function tokens", () => {
  const { render } = renderer();
  const source = [
    "import psutil, os",
    'print("physical", psutil.cpu_count(logical=False), "logical", psutil.cpu_count())',
    "vm = psutil.virtual_memory()",
    'print("total GB", round(vm.total/2**30,1), "available GB", round(vm.available/2**30,1))',
    'print("affinity", len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else "n/a")'
  ].join("\n");
  const { code } = fenced(render, "python", source);
  assertToken(code, "hljs-keyword", "import");
  assertToken(code, "hljs-keyword", "if");
  assertToken(code, "hljs-string", '"physical"');
  assertToken(code, "hljs-number", "30");
  assertToken(code, "hljs-literal", "False");
  assertToken(code, "hljs-built_in", "print");
});

test("Python -c diagnostics highlight the embedded program instead of one shell string", async () => {
  const { render, copied } = renderer();
  const program = [
    "import sys,json;info={'version':sys.version,'executable':sys.executable};",
    "mods={};",
    "for m in ('numpy','scipy','psutil','torch','cupy','numba','threadpoolctl'):",
    "    try:",
    "        mod=__import__(m); mods[m]=getattr(mod,'__version__','?')",
    "    except Exception as e: mods[m]=type(e).__name__",
    "info['mods']=mods",
    "print(json.dumps(info))"
  ].join("\n");
  for (const [language, prefix] of [["", ".venv/Scripts/python.exe -c "],
    ["powershell", '& "C:\\Python Tools\\python.exe" -u -c '], ["bash", "python3 -c "]]) {
    const source = prefix + '"' + program + '"';
    const { block, code } = fenced(render, language, source);
    assertToken(code, "hljs-keyword", "import");
    assertToken(code, "hljs-keyword", "for");
    assertToken(code, "hljs-keyword", "except");
    assertToken(code, "hljs-built_in", "print");
    assertToken(code, "hljs-string", "'numpy'");
    await block.querySelector("button").dispatch("click");
    assert.equal(copied.at(-1).payload.text, source);
  }
});

test("streamed Python commands preserve unfinished quotes, escapes and inert HTML", () => {
  const { render } = renderer();
  for (const tail of ["import sys", "import sys\nprint('value", "import sys\nprint('<img src=x onerror=alert(1)>')"] ) {
    const source = 'python -c "' + tail;
    const { code, root } = fenced(render, "", source, false);
    assertToken(code, "hljs-keyword", "import");
    assert.equal(code.textContent, source);
    assert.equal(root.querySelectorAll("img").length, 0);
  }
  const source = 'python -c "import sys; print(\\"literal \\n\\")"';
  const { code } = fenced(render, "bash", source);
  assertToken(code, "hljs-keyword", "import");
  assert.equal(code.textContent, source);
});

test("indented and tilde fences retain program indentation and ignore mismatched closing markers", async () => {
  const { render, copied } = renderer();
  for (const fence of ["```", "~~~~"]) {
    const source = "if True:\n    print('formatted')\n";
    const root = render(`Vorher\n\n  ${fence}python\n  if True:\n      print('formatted')\n  \n  ${fence}\n\nNachher`);
    const block = root.querySelector(".code-block");
    const code = block.querySelector("pre code");
    assert.equal(code.textContent, source);
    assertToken(code, "hljs-keyword", "if");
    await block.querySelector("button").dispatch("click");
    assert.equal(copied.at(-1).payload.text, source);
  }
  const code = render("~~~text\n```\nvalue\n~~~").querySelector("pre code");
  assert.equal(code.textContent, "```\nvalue");
});

test("JavaScript alias highlights executable syntax without parsing string HTML", () => {
  const { render } = renderer();
  const source = 'const label = "<img src=x onerror=alert(1)>";\n// explanation\nif (label.length > 2) console.log(label);';
  const { root, code } = fenced(render, "js", source);
  assertToken(code, "hljs-keyword", "const");
  assertToken(code, "hljs-keyword", "if");
  assertToken(code, "hljs-comment", "// explanation");
  assertToken(code, "hljs-string", '"<img src=x onerror=alert(1)>"');
  assert.equal(root.querySelectorAll("img").length, 0);
});

test("PowerShell grammar colors cmdlets, variables and conditional syntax", () => {
  const { render } = renderer();
  const source = '$items = Get-ChildItem -LiteralPath "C:\\Work"\nif ($items.Count -gt 0) {\n\tWrite-Output "gefunden"\n}';
  const { code } = fenced(render, "powershell", source);
  assertToken(code, "hljs-keyword", "if");
  assertToken(code, "hljs-built_in", "Get-ChildItem");
  assertToken(code, "hljs-variable", "$items");
  assertToken(code, "hljs-string", '"gefunden"');
});

test("copy retains indentation, blank lines, long lines and literal escaped newlines", async () => {
  const { render, copied } = renderer();
  const source = `def show():\n\tvalue = "literal \\n and \\t"\n\n\tprint("${"word ".repeat(80)}")\n\treturn value\n`;
  const { block, code } = fenced(render, "py", source);
  assert.equal(code.textContent.split("\n").length, source.split("\n").length);
  await block.querySelector("button").dispatch("click");
  assert.equal(copied.length, 1);
  assert.equal(copied[0].type, "message.copy");
  assert.equal(copied[0].payload.text, source);
});

test("HTML source is highlighted as inert text, including script and event handlers", () => {
  const { render } = renderer();
  const source = '<div onclick="alert(1)"><script>alert("x")</script>\n<img src=x onerror=alert(1)></div>';
  const { root, code } = fenced(render, "html", source);
  assert.ok(code.querySelectorAll("span").length > 0);
  assert.equal(root.querySelectorAll("script, img").length, 0);
  for (const node of code.querySelectorAll("*")) {
    assert.equal(node.tagName, "SPAN");
    assert.equal(node.getAttribute("onclick"), null);
    assert.equal(node.getAttribute("onerror"), null);
  }
});

test("unfinished streamed fences and strings remain colored and lossless as text grows", () => {
  const { render } = renderer();
  for (const source of [
    "import os\nprint(",
    'import os\nprint("A very',
    'import os\nprint("A very long value")\n'
  ]) {
    const { code } = fenced(render, "python", source, false);
    assertToken(code, "hljs-keyword", "import");
    assert.equal(code.textContent, source);
  }
});

test("explicit plaintext and unknown language hints use readable inert fallback", () => {
  const { render } = renderer();
  const source = 'import os\n  <script>alert(1)</script>\n\t{{ unknown syntax }}';
  for (const language of ["text", "plaintext", "unknown-language"]) {
    const { code, root } = fenced(render, language, source);
    assert.equal(code.querySelectorAll("span").length, 0);
    assert.equal(root.querySelectorAll("script").length, 0);
  }
});

test("code remains readable and copyable when the optional highlighter is unavailable", async () => {
  const { render, copied } = renderer({ loadHighlight: false });
  const source = 'import os\n\nprint("still readable")';
  const { block, code } = fenced(render, "python", source);
  assert.equal(code.querySelectorAll("span").length, 0);
  await block.querySelector("button").dispatch("click");
  assert.equal(copied[0].payload.text, source);
});

test("rendering identical code in separate messages does not move or mutate the first token nodes", async () => {
  const { render, copied } = renderer();
  const source = 'import os\nprint("repeated code")';
  const first = fenced(render, "python", source);
  const firstTokens = first.code.querySelectorAll("span");
  assert.ok(firstTokens.length > 0);
  const second = fenced(render, "python", source);
  assert.equal(first.code.textContent, source);
  assert.equal(first.code.querySelectorAll("span").length, firstTokens.length);
  assert.notEqual(firstTokens[0], second.code.querySelectorAll("span")[0]);
  await first.block.querySelector("button").dispatch("click");
  await second.block.querySelector("button").dispatch("click");
  assert.deepEqual(copied.map(item => item.payload.text), [source, source]);
});

test("very large code blocks retain their entire source and copy text when highlighting falls back", async () => {
  const { render, copied } = renderer();
  const source = 'print("' + "x".repeat(300 * 1024) + '")\n# last line';
  const { block, code } = fenced(render, "python", source);
  assert.equal(code.textContent, source);
  await block.querySelector("button").dispatch("click");
  assert.equal(copied[0].payload.text, source);
});
