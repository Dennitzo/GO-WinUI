const assert = require("node:assert/strict");

// Minimal DOM with real fragment transfer and parent ownership. Production
// Markdown and timeline code run unchanged; HTML injection fails explicitly.
class TestNode {
  constructor(tagName, text = "") {
    this.tagName = tagName.toUpperCase();
    this.nodeType = tagName === "#text" ? 3 : tagName === "#fragment" ? 11 : 1;
    this.className = "";
    this.dataset = {};
    this.attributes = { [Symbol.iterator]: () => this.attributeEntries()[Symbol.iterator]() };
    this.listeners = new Map();
    this.style = {};
    this.parentNode = null;
    this.childNodes = [];
    this.text = text;
    this.classList = {
      add: (...names) => { this.className = [...new Set([...this.className.split(/\s+/).filter(Boolean), ...names])].join(" "); },
      remove: (...names) => { this.className = this.className.split(/\s+/).filter(name => !names.includes(name)).join(" "); },
      contains: name => this.className.split(/\s+/).includes(name),
      toggle: (name, force) => {
        const add = force ?? !this.classList.contains(name);
        this.classList[add ? "add" : "remove"](name);
        return add;
      }
    };
  }
  get nodeName() { return this.tagName; }
  get nodeValue() { return this.nodeType === 3 ? this.text : null; }
  set nodeValue(value) { if (this.nodeType === 3) this.text = String(value); }
  appendData(value) { if (this.nodeType === 3) this.text += String(value); }
  get textContent() { return this.nodeType === 3 ? this.text : this.childNodes.map(node => node.textContent).join(""); }
  set textContent(value) {
    for (const child of this.childNodes) child.parentNode = null;
    this.childNodes = [];
    this.text = "";
    if (this.nodeType === 3) this.text = String(value ?? "");
    else if (value !== null && value !== undefined && String(value) !== "") this.append(new TestNode("#text", String(value)));
  }
  set innerHTML(value) { throw new Error(`Unsafe innerHTML assignment: ${String(value).slice(0, 80)}`); }
  get children() { return this.childNodes.filter(node => node.nodeType === 1); }
  get firstChild() { return this.childNodes[0] || null; }
  get lastChild() { return this.childNodes.at(-1) || null; }
  get parentElement() { return this.parentNode?.nodeType === 1 ? this.parentNode : null; }
  get isConnected() { return this.tagName === "BODY" || Boolean(this.parentNode?.isConnected); }
  getBoundingClientRect() { return this.bounds || { top: 100, bottom: 160, left: 0, right: 500 }; }
  append(...nodes) {
    for (const input of nodes) {
      const node = typeof input === "string" ? new TestNode("#text", input) : input;
      if (node.nodeType === 11) { this.append(...node.childNodes.slice()); continue; }
      node.remove();
      node.parentNode = this;
      this.childNodes.push(node);
    }
  }
  appendChild(node) { this.append(node); return node; }
  replaceChildren(...nodes) { this.textContent = ""; this.append(...nodes); }
  insertBefore(node, before) {
    if (!before) return this.appendChild(node);
    assert.equal(before.parentNode, this);
    node.remove();
    node.parentNode = this;
    this.childNodes.splice(this.childNodes.indexOf(before), 0, node);
    return node;
  }
  remove() {
    this.removed = true;
    if (!this.parentNode) return;
    const siblings = this.parentNode.childNodes;
    siblings.splice(siblings.indexOf(this), 1);
    this.parentNode = null;
  }
  replaceWith(node) {
    if (!this.parentNode) return;
    this.parentNode.insertBefore(node, this);
    this.remove();
  }
  attributeEntries() {
    const values = { ...this.attributes };
    if (this.className) values.class = this.className;
    for (const [key, value] of Object.entries(this.dataset)) values[`data-${key.replace(/[A-Z]/g, char => `-${char.toLowerCase()}`)}`] = value;
    return Object.entries(values).map(([name, value]) => ({ name, value }));
  }
  setAttribute(name, value) {
    this.attributes[name] = String(value);
    if (name === "class") this.className = String(value);
    if (name.startsWith("data-")) this.dataset[name.slice(5).replace(/-([a-z])/g, (_, char) => char.toUpperCase())] = String(value);
  }
  getAttribute(name) {
    if (name === "class") return this.className || this.attributes.class || null;
    if (name.startsWith("data-")) return this.dataset[name.slice(5).replace(/-([a-z])/g, (_, char) => char.toUpperCase())] ?? null;
    return this.attributes[name] ?? null;
  }
  hasAttribute(name) { return this.getAttribute(name) !== null; }
  removeAttribute(name) {
    delete this.attributes[name];
    if (name === "class") this.className = "";
    if (name.startsWith("data-")) delete this.dataset[name.slice(5).replace(/-([a-z])/g, (_, char) => char.toUpperCase())];
  }
  addEventListener(type, callback) {
    if (!this.listeners.has(type)) this.listeners.set(type, []);
    this.listeners.get(type).push(callback);
    this.listeners[type] = callback;
  }
  showModal() { this.open = true; }
  close() { this.open = false; this.listeners.close?.(); }
  async dispatch(type) {
    for (const listener of this.listeners.get(type) || [])
      await listener({ type, target: this, preventDefault() {}, stopPropagation() {} });
  }
  querySelectorAll(selector) {
    const descendants = this.childNodes.flatMap(node => [node, ...node.querySelectorAll("*")]);
    if (selector === "*") return descendants.filter(node => node.nodeType === 1);
    const alternatives = selector.split(",").map(part => part.trim());
    const matchesSimple = (node, simple) => {
      if (node.nodeType !== 1) return false;
      const tag = simple.match(/^[a-z][a-z0-9-]*/i)?.[0];
      if (tag && node.tagName !== tag.toUpperCase()) return false;
      for (const match of simple.matchAll(/\.([\w-]+)/g)) if (!node.classList.contains(match[1])) return false;
      for (const match of simple.matchAll(/\[([\w-]+)(?:=["']?([^\]"']+)["']?)?\]/g)) {
        const value = node.getAttribute(match[1]);
        if (value === null || match[2] !== undefined && value !== match[2]) return false;
      }
      return true;
    };
    return descendants.filter(node => alternatives.some(alternative => {
      const parts = alternative.split(/\s+/);
      if (!matchesSimple(node, parts.pop())) return false;
      let ancestor = node.parentNode;
      while (parts.length) {
        const part = parts.pop();
        if (part === ">") {
          const direct = parts.pop();
          if (direct === ":scope") return ancestor === this && !parts.length;
          if (!ancestor || !matchesSimple(ancestor, direct)) return false;
          ancestor = ancestor.parentNode;
          continue;
        }
        while (ancestor && ancestor !== this && !matchesSimple(ancestor, part)) ancestor = ancestor.parentNode;
        if (!ancestor || !matchesSimple(ancestor, part)) return false;
        ancestor = ancestor.parentNode;
      }
      return true;
    }));
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
}

module.exports = { TestNode };
