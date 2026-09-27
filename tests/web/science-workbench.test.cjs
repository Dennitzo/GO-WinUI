const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");
const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
const css = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8");
const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
const bridge = fs.readFileSync(path.join(webRoot, "bridge.js"), "utf8");

test("Claude Science owns a full workbench and a separately collapsible conversation panel", () => {
  assert.match(html, /id="science-workbench"/);
  assert.match(html, /id="conversation-pane"/);
  assert.match(html, /id="science-chat-toggle"/);
  assert.doesNotMatch(html, /id="research-overlay"|Forschungsstand öffnen/);
  for (const view of ["overview", "research", "data", "analysis", "figures", "manuscript", "review", "provenance"])
    assert.match(html, new RegExp(`data-science-view="${view}"`));
  assert.match(css, /\.app-shell\.science-mode \.chat-pane\s*\{[^}]*grid-template-columns:/);
  assert.match(css, /\.science-workbench__content\s*\{[^}]*overflow:\s*auto/);
  assert.match(css, /@media \(max-width: 820px\)/);
});

test("science views render persisted evidence rather than static placeholder copy", () => {
  for (const collection of ["detail.nodes", "detail.works", "detail.evidence", "detail.experiments",
    "detail.verifications", "detail.claims", "detail.edges"])
    assert.ok(app.includes(collection), `${collection} is rendered by the workbench`);
  assert.match(app, /goMarkdown\.render\(detail\.report\?\.contentMarkdown/);
  assert.match(app, /openScienceProject\(item\.id\)/);
  assert.match(app, /incomingRevision < currentRevision/);
});

test("research bridge types are available during bootstrap and configurable from the host contract", () => {
  for (const type of ["research.list", "research.open", "research.export", "research.snapshot", "research.exported"])
    assert.ok(bridge.includes(`"${type}"`), `${type} is part of the browser bridge`);
  assert.match(bridge, /function configureContract\(contract\)/);
  assert.match(app, /configureContract\?\.\(payload\?\.bridgeContract\)/);
});

test("science keeps shared tools while deep research remains workbench-owned", () => {
  const coordinator = fs.readFileSync(path.resolve(__dirname,
    "../../src/GoWinUI.App/Services/AssistantCoordinator.cs"), "utf8");
  assert.match(coordinator, /chatMode != ChatMode\.ClaudeScience/);
  assert.match(coordinator, /BuiltInActionIds\.DeepResearch/);
});
