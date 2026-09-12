const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

// Only the DOM primitives are substituted. Both the production sanitizer and
// the complete production Markdown renderer execute unchanged in the VM.
class DomNode {
  constructor(tagName, text = "") {
    this.tagName = tagName;
    this.childNodes = [];
    this.className = "";
    this.dataset = {};
    this.attributes = {};
    this.listeners = {};
    this.textContent = text;
    this.classList = {
      add: (...names) => { this.className = [...new Set([...this.className.split(" ").filter(Boolean), ...names])].join(" "); },
      remove: (...names) => { this.className = this.className.split(" ").filter(name => !names.includes(name)).join(" "); }
    };
  }
  set textContent(value) { this.text = String(value); this.childNodes = []; }
  get textContent() { return this.text + this.childNodes.map(node => node.textContent).join(""); }
  append(...nodes) {
    for (const node of nodes) this.childNodes.push(typeof node === "string" ? new DomNode("#text", node) : node);
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener(name, callback) { this.listeners[name] = callback; }
}

function pipeline(source) {
  const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
  const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
  const start = app.indexOf("  function sanitizeVisibleMessageContent(value) {");
  const end = app.indexOf("\n  function ", start + 1);
  assert.ok(start >= 0 && end > start, "the production sanitizer must be found");
  const copied = [];
  const context = vm.createContext({
    document: {
      createElement: tag => new DomNode(tag),
      createTextNode: text => new DomNode("#text", text),
      createDocumentFragment: () => new DomNode("#fragment")
    },
    URL,
    goBridge: { post: (type, payload) => copied.push({ type, payload }) }
  });
  vm.runInContext(app.slice(start, end), context);
  vm.runInContext(fs.readFileSync(path.join(webRoot, "markdown.js"), "utf8"), context);
  const sanitized = context.sanitizeVisibleMessageContent(source);
  return { sanitized, root: context.goMarkdown.render(sanitized), copied };
}

function descendants(node, predicate) {
  return [node, ...node.childNodes.flatMap(child => descendants(child, () => true))].filter(predicate);
}
const tags = (root, tag) => descendants(root, node => node.tagName === tag);
const classes = (root, name) => descendants(root, node => node.className.split(" ").includes(name));

test("the actual final Coding response renders a Python code block", async () => {
  const source = "Der aktuelle Rückgabeausdruck der Funktion `multiply` ist:\n\n```python\nreturn a * b\n```";
  const { sanitized, root, copied } = pipeline(source);
  assert.equal(sanitized, source);
  assert.equal(classes(root, "code-block").length, 1);
  assert.equal(tags(root, "pre").length, 1);
  assert.equal(tags(tags(root, "pre")[0], "code")[0].textContent, "return a * b");
  assert.equal(tags(root, "code")[0].textContent, "multiply");
  assert.equal(classes(root, "code-header")[0].childNodes[0].textContent, "python");
  await tags(classes(root, "code-block")[0], "button")[0].listeners.click();
  assert.equal(copied[0].type, "message.copy");
  assert.equal(copied[0].payload.text, "return a * b");
});

test("complete strong emphasis keeps its closing delimiter", () => {
  const source = "**Geändert (nur die Funktion `multiply`):**\n\n**Tests bestanden.**";
  const { sanitized, root } = pipeline(source);
  assert.equal(sanitized, source);
  assert.deepEqual(tags(root, "strong").map(node => node.textContent), ["Geändert (nur die Funktion multiply):", "Tests bestanden."]);
  assert.equal(tags(root, "code")[0].textContent, "multiply");
  assert.ok(!root.textContent.includes("**"));
});

test("longer tool-output fences preserve literal Markdown fences and exact copied stdout", async () => {
  const stdout = "before\n```python\nprint('<br>')\n```\n````\n**literal bold**\n| left | right |\nafter";
  const fence = "`".repeat(5);
  const { root, copied } = pipeline(`${fence}text\n${stdout}\n${fence}\n\n**Completed**`);
  assert.equal(classes(root, "code-block").length, 1);
  assert.equal(tags(root, "pre")[0].textContent, stdout);
  assert.deepEqual(tags(root, "strong").map(node => node.textContent), ["Completed"]);
  assert.equal(tags(root, "table").length, 0);
  await tags(classes(root, "code-block")[0], "button")[0].listeners.click();
  assert.equal(copied[0].payload.text, stdout);
});

test("br normalization applies to prose while fenced and inline source remain literal", () => {
  const { root } = pipeline("first<br>second\n\n`<br>`\n\n```html\n<div>first<br>second</div>\n```suffix is output\nlast\n```\n");
  assert.ok(tags(root, "p")[0].textContent.includes("first\nsecond"));
  assert.equal(tags(root, "code")[0].textContent, "<br>");
  assert.equal(tags(root, "pre")[0].textContent, "<div>first<br>second</div>\n```suffix is output\nlast");
});

test("inline code survives at the end of a line", () => {
  const source = "Verwende `multiply`\n\nDer Ausdruck ist `a ** 2`.";
  const { sanitized, root } = pipeline(source);
  assert.equal(sanitized, source);
  assert.deepEqual(tags(root, "code").map(node => node.textContent), ["multiply", "a ** 2"]);
  assert.equal(tags(root, "strong").length, 0);
});

test("fenced contents keep indentation, pipes, emphasis markers and literal HTML", () => {
  const code = 'if ready:\n  result = "a | b"\n  print("**done**", "<script>alert(1)</script>")';
  const { root } = pipeline("```python\n" + code + "\n```");
  assert.equal(tags(tags(root, "pre")[0], "code")[0].textContent, code);
  assert.equal(tags(root, "table").length, 0);
  assert.equal(tags(root, "script").length, 0);
  assert.equal(tags(root, "strong").length, 0);
});

test("an unfinished streamed fence remains a code block", () => {
  const { root } = pipeline("```python\nreturn a *");
  assert.equal(tags(tags(root, "pre")[0], "code")[0].textContent, "return a *");
});

test("inline and display LaTeX retain their existing selectable source nodes", () => {
  const source = String.raw`Die Formel ist $a \cdot b$ und \(x^2\).

\[
\frac{a}{b}
\]`;
  const { sanitized, root } = pipeline(source);
  assert.equal(sanitized, source);
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), [
    String.raw`$a \cdot b$`, String.raw`\(x^2\)`, "\\[\n\\frac{a}{b}\n\\]"
  ]);
  assert.equal(classes(root, "display").length, 1);
});

test("legacy title metadata is removed while surrounding Markdown remains intact", () => {
  const source = "**GO_SESSION_TITLE:** Verborgener Titel\n\n**Ergebnis:** `multiply`\n\n## **GO\\_SESSION\\_TITLE:** Auch verborgen";
  const { root, sanitized } = pipeline(source);
  assert.equal(sanitized, "**Ergebnis:** `multiply`");
  assert.equal(tags(root, "strong")[0].textContent, "Ergebnis:");
  assert.equal(tags(root, "code")[0].textContent, "multiply");
  assert.ok(!root.textContent.includes("verborgen"));
});

test("math inside inline code stays literal", () => {
  const { root } = pipeline(String.raw`Nutze \(a+b\), aber kopiere \`$x$\``.replace(/\\`/g, "`"));
  assert.equal(classes(root, "math-source-text").length, 1);
  assert.equal(tags(root, "code")[0].textContent, "$x$");
});
