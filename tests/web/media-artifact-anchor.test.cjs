const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/GoWinUI.App/Assets/Web");

function harness() {
  const document = {
    body: new TestNode("body"),
    createElement: tag => new TestNode(tag),
    createTextNode: text => new TestNode("#text", text),
    createDocumentFragment: () => new TestNode("#fragment")
  };
  const context = vm.createContext({ document, URL, setTimeout: () => {},
    state: { selectedToolAction: "coding", activeSessionId: "session-a", codingActivity: new Map() },
    navigator: { clipboard: { writeText: async () => {} } },
    goBridge: { post: () => {} } });
  for (const script of ["markdown.js", "coding-timeline.js"])
    vm.runInContext(fs.readFileSync(path.join(webRoot, script), "utf8"), context, { filename: script });
  return { context };
}

const answer = (extra = {}) => ({ id: "answer-1", role: "assistant", content: "", status: "completed", ...extra });
const step = (extra = {}) => ({ id: "step-1", tool: "coding.read", label: "Datei lesen", status: "completed", ...extra });
const artifact = (extra = {}) => ({ id: "artifact-1", fileName: "render.png", contentType: "image/png", length: 123, provider: "go-ai", ...extra });

test("media artifacts bound to a tool step render inside that step instead of the message end", async () => {
  const { context } = harness();
  const render = (message, steps, options = {}) => context.goCodingTimeline.render(message, steps, {
    renderMarkdown: text => context.goMarkdown.render(text),
    enhanceCodeBlocks: () => {},
    sanitizeText: text => text,
    createArtifacts: items => {
      const list = new TestNode("div");
      list.className = "message-artifacts";
      for (const item of items) {
        const card = new TestNode("section");
        card.className = "artifact-card";
        card.dataset.artifactId = item.id;
        list.append(card);
      }
      return list;
    },
    ...options
  });

  const message = answer({
    artifacts: [artifact({ id: "thumb-1", stepId: "step-1" }), artifact({ id: "thumb-2" })]
  });
  const timeline = render(message, [step({ id: "step-1",
    inputJson: JSON.stringify({ path: "src/math.py" }),
    outputJson: JSON.stringify({ totalLines: 1 }) })]);

  const stepSection = timeline.querySelector('[data-step-id="step-1"]');
  assert.ok(stepSection, "the tool step section exists");
  const disclosure = stepSection.querySelector("details");
  disclosure.setAttribute("open", "");
  await disclosure.dispatch("toggle");
  const anchored = stepSection.querySelector(".coding-step__artifacts .artifact-card");
  assert.ok(anchored, "the bound artifact renders inside the tool step");
  assert.equal(anchored.dataset.artifactId, "thumb-1");
  assert.equal(stepSection.querySelectorAll(".coding-step__artifacts .artifact-card").length, 1,
    "exactly one artifact is anchored to this step");
});
