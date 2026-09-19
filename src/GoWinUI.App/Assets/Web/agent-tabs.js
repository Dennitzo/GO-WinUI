(() => {
  "use strict";

  const terminal = new Set(["completed", "failed", "cancelled", "denied", "interrupted", "steered"]);
  const labels = { running: "Arbeitet", pending: "Wartet", completed: "Abgeschlossen", failed: "Fehlgeschlagen",
    cancelled: "Abgebrochen", denied: "Abgelehnt", interrupted: "Nicht abgeschlossen", steered: "Umgeleitet" };
  let controller = null;

  function json(value) {
    if (value && typeof value === "object") return value;
    try { return JSON.parse(value || "null"); } catch { return null; }
  }

  function node(tag, className, text) {
    const element = document.createElement(tag);
    element.className = className;
    if (text != null) element.textContent = String(text);
    return element;
  }

  function agentId(step) {
    if (step?.agentId) return String(step.agentId);
    // Older persisted receipts predate the explicit agentId property.
    if (/^agent-[a-f0-9]+(?:\/|$)/i.test(String(step?.id || ""))) return String(step.id).split("/")[0];
    if (step?.tool === "coding.agentStart") return json(step.outputJson)?.agentId || null;
    return null;
  }

  function isChild(step) {
    return Boolean(agentId(step)) || /^Subagent GPU1(?:\b|\s|·)/i.test(String(step?.explanation || ""))
      || /^Subagent GPU1$/i.test(String(step?.detail || ""));
  }

  function groupsForMessage(message, steps) {
    const groups = new Map();
    const ensure = id => {
      if (!groups.has(id)) groups.set(id, { id, messageId: String(message.id), steps: [], task: "",
        paths: [], status: "running", result: "", first: null, source: message });
      return groups.get(id);
    };
    for (const step of steps) {
      if (!isChild(step)) continue;
      const knownIds = [...groups.keys()];
      const id = agentId(step) || (knownIds.length === 1 ? knownIds[0] : `legacy-${message.id}`);
      const group = ensure(id);
      group.steps.push(step);
      group.first ||= step;
      if (step.tool === "coding.agentStart") {
        const input = json(step.inputJson), output = json(step.outputJson);
        if (input?.task) group.task = String(input.task);
        if (Array.isArray(input?.writePaths)) group.paths = input.writePaths;
        if (Array.isArray(output?.writePaths)) group.paths = output.writePaths;
        const result = output?.result ?? output?.Result;
        const status = String(output?.status ?? output?.Status ?? "").toLowerCase();
        if (terminal.has(status)) {
          group.status = status;
          group.result = typeof result === "string" ? result : "";
        } else if (["failed", "cancelled", "denied"].includes(step.status)) {
          group.status = step.status;
          group.result = String(output?.error || step.detail || "");
        }
      }
    }
    // Older clients persisted final results in agentWait rather than an owned receipt.
    for (const step of steps.filter(item => ["coding.agentWait", "coding.agentCancel"].includes(item.tool))) {
      for (const result of json(step.outputJson)?.agents || []) {
        if (!result.agentId) continue;
        const group = ensure(String(result.agentId));
        if (terminal.has(String(result.status).toLowerCase())) group.status = String(result.status).toLowerCase();
        if (typeof result.result === "string") group.result = result.result;
        if (Array.isArray(result.writePaths)) group.paths = result.writePaths;
        group.first ||= step;
      }
    }
    for (const group of groups.values()) {
      if (!terminal.has(group.status) && terminal.has(String(message.status).toLowerCase())) group.status = "interrupted";
    }
    return [...groups.values()];
  }

  function mainSteps(message, steps) {
    const groups = groupsForMessage(message, steps);
    if (!groups.length) return steps;
    const output = [];
    const emitted = new Set();
    const lastIndices = new Map(groups.map(group => [group.id,
      Math.max(steps.findIndex(step => step.id === group.first?.id), ...group.steps.map(child => steps.findIndex(step => step.id === child.id)))]));
    for (const [index, step] of steps.entries()) {
      const group = groups.find(item => item.first?.id === step.id);
      if (group && !emitted.has(group.id)) {
        emitted.add(group.id);
        output.push({ ...step, id: `${group.id}-notice-start`, tool: "assistant.agentNotice", agentNotice: {
          kind: "start", task: group.task, paths: group.paths, status: "completed" }, status: "completed" });
      }
      if (!isChild(step) && !["coding.agentStart", "coding.agentWait", "coding.agentCancel"].includes(step.tool)) output.push(step);
      for (const finished of groups.filter(item => terminal.has(item.status) && lastIndices.get(item.id) === index)) {
        output.push({ ...step, id: `${finished.id}-notice-result`, tool: "assistant.agentNotice",
          contentOffset: Math.max(step.contentOffset || 0, ...finished.steps.map(item => item.contentOffset || 0)),
          agentNotice: { kind: "result", result: finished.result, status: finished.status }, status: finished.status });
      }
    }
    return output;
  }

  function renderNotice(step, options) {
    const data = step.agentNotice;
    const section = node("section", `agent-notice agent-notice--${data.kind}`);
    section.dataset.timelineKey = `tool-${step.id}`;
    section.dataset.stepId = String(step.id);
    const heading = node("div", "agent-notice__heading");
    heading.append(node("strong", "", data.kind === "start" ? "Subagent gestartet" : "Subagent-Ergebnis"));
    if (data.kind === "result") heading.append(node("span", `agent-state agent-state--${data.status}`, labels[data.status] || data.status));
    const open = node("button", "agent-notice__open", "Subagent öffnen");
    open.type = "button";
    open.addEventListener("click", () => controller?.select("subagent"));
    heading.append(open);
    section.append(heading);
    const text = data.kind === "start" ? data.task || "Ein Teilauftrag wurde delegiert."
      : data.result || (data.status === "interrupted" ? "Der Teilauftrag wurde nicht abgeschlossen." : "Kein Ergebnistext empfangen.");
    const body = node("div", "message-content agent-notice__body");
    body.append(options.renderMarkdown(text));
    section.append(body);
    if (data.kind === "start" && data.paths?.length) section.append(node("p", "agent-notice__scope", `Schreibbereich: ${data.paths.join(", ")}`));
    return section;
  }

  function renderGroup(group, options, previous) {
    const conversation = node("section", "subagent-conversation");
    conversation.dataset.timelineKey = `${group.messageId}/${group.id}`;
    conversation.setAttribute("aria-label", "Subagent-Verlauf");
    const task = node("article", "message user");
    task.dataset.timelineKey = "task";
    task.setAttribute("aria-label", "Auftrag an den Subagenten");
    const taskBody = node("div", "message-body");
    const taskContent = node("div", "message-content");
    taskContent.append(options.renderMarkdown(group.task || "Delegierter Teilauftrag"));
    options.enhanceCodeBlocks?.(taskContent);
    if (group.paths.length) taskContent.append(node("p", "agent-notice__scope", `Schreibbereich: ${group.paths.join(", ")}`));
    taskBody.append(taskContent);
    task.append(taskBody);
    conversation.append(task);

    const article = node("article", "message assistant");
    article.dataset.timelineKey = "answer";
    article.append(node("div", "avatar", "AI"));
    const body = node("div", "message-body");
    const createdAt = group.first?.startedAt || group.source.createdAt || group.source.updatedAt;
    const time = options.timeLabel?.(createdAt);
    const meta = node("div", "message-meta", time ? `Subagent - ${time}` : "Subagent");
    if (!terminal.has(group.status)) {
      const spinner = node("span", "message-status-spinner");
      spinner.setAttribute("aria-hidden", "true");
      meta.append(spinner);
    }
    meta.append(node("span", `message-status ${group.status}`, labels[group.status] || "Arbeitet"));
    body.append(meta);
    const detailSteps = group.steps.filter(step => step.tool !== "coding.agentStart").map(step => ({ ...step, contentOffset: 0 }));
    // Earlier builds stored content progress on the lifecycle receipt itself.
    if (!detailSteps.some(step => step.tool === "assistant.narration")) {
      const legacy = group.steps.find(step => step.tool === "coding.agentStart" && step.explanation
        && !/^Serverwerkzeug|^Subagent GPU1/.test(step.explanation));
      if (legacy) detailSteps.push({ ...legacy, id: `${legacy.id}-legacy-text`, tool: "assistant.narration", detail: legacy.explanation, contentOffset: 0 });
    }
    const result = terminal.has(group.status) ? group.result || "Kein Ergebnistext empfangen." : "";
    const lastNarration = detailSteps.findLast(step => step.tool === "assistant.narration");
    const finalText = result.trim() === String(lastNarration?.detail || "").trim() ? "" : result;
    const message = { ...group.source, id: group.messageId, content: finalText
      || (!detailSteps.length ? "Der Subagent bereitet den Teilauftrag vor …" : ""), status: group.status };
    body.append(globalThis.goCodingTimeline.render(message, detailSteps, { ...options,
      previousTimeline: previous?.querySelector(".coding-timeline"), liveStatus: null }));
    article.append(body);
    conversation.append(article);
    return conversation;
  }

  function create(options) {
    const { mainTab, subagentTab, mainPanel, subagentPanel, subagentList, composer } = options.elements;
    const choices = new Map(), positions = new Map();
    let sessionId = null, active = "main", currentState = null;
    const storageKey = id => `go.assistant.agent-tab.v1:${id}`;
    const read = id => {
      if (choices.has(id)) return choices.get(id);
      try { return globalThis.localStorage?.getItem(storageKey(id)) === "subagent" ? "subagent" : "main"; }
      catch { return "main"; }
    };
    function saveScroll() {
      if (sessionId && active === "subagent") positions.set(sessionId, { top: subagentPanel.scrollTop || 0, left: subagentPanel.scrollLeft || 0 });
    }
    function show() {
      const child = active === "subagent";
      mainPanel.hidden = child;
      subagentPanel.hidden = !child;
      if (composer) composer.hidden = child;
      mainTab.setAttribute("aria-selected", String(!child));
      subagentTab.setAttribute("aria-selected", String(child));
      mainTab.tabIndex = child ? -1 : 0;
      subagentTab.tabIndex = child ? 0 : -1;
    }
    function select(value) {
      const next = value === "subagent" ? "subagent" : "main";
      if (next === active) return;
      saveScroll();
      if (active === "main") { options.saveMainScroll?.(); options.pauseMainScroll?.(); }
      active = next;
      if (sessionId) {
        choices.set(sessionId, active);
        try { globalThis.localStorage?.setItem(storageKey(sessionId), active); } catch { /* Storage is optional. */ }
      }
      show();
      if (currentState) refresh(currentState);
      if (active === "main") { options.renderMain?.(); options.restoreMainScroll?.(); }
      else {
        const position = positions.get(sessionId);
        subagentPanel.scrollTop = position?.top || 0;
        subagentPanel.scrollLeft = position?.left || 0;
      }
    }
    function refresh(state) {
      currentState = state;
      const nextSession = String(state.activeSessionId || "");
      const changed = nextSession !== sessionId;
      if (changed) { saveScroll(); sessionId = nextSession; active = read(sessionId); }
      show();
      const groups = state.messages.flatMap(message => groupsForMessage(message, options.steps(message)));
      const count = groups.filter(group => !terminal.has(group.status)).length;
      subagentTab.setAttribute("aria-label", count ? `Subagent, ${count} aktiv` : "Subagent");
      subagentTab.dataset.running = String(count > 0);
      const previousTop = subagentPanel.scrollTop || 0;
      const follow = (subagentPanel.scrollHeight || 0) - (subagentPanel.clientHeight || 0) - previousTop < 48;
      const existing = new Map([...subagentList.children].map(item => [item.dataset.timelineKey, item]));
      let index = 0;
      for (const group of groups) {
        const old = existing.get(`${group.messageId}/${group.id}`);
        const next = renderGroup(group, options.timeline, old);
        const article = old ? globalThis.goCodingTimeline.reconcile(old, next) : next;
        if (subagentList.children[index] !== article) subagentList.insertBefore(article, subagentList.children[index] || null);
        index++;
      }
      while (subagentList.children.length > index) subagentList.lastChild.remove();
      if (!groups.length) {
        const empty = node("div", "subagent-empty");
        empty.dataset.timelineKey = "empty";
        empty.append(node("h2", "", "Noch kein Subagent gestartet"), node("p", "", "Sobald der Hauptagent einen Teilauftrag delegiert, erscheinen hier dessen Denkprozess, Arbeitsplan, Werkzeuge und Ergebnis."));
        subagentList.replaceChildren(empty);
      }
      if (active === "subagent") {
        subagentPanel.scrollTop = changed ? positions.get(sessionId)?.top || 0
          : follow ? Math.max(0, (subagentPanel.scrollHeight || 0) - (subagentPanel.clientHeight || 0)) : previousTop;
      }
      return active;
    }
    for (const [tab, value] of [[mainTab, "main"], [subagentTab, "subagent"]]) {
      tab.addEventListener("click", () => select(value));
      tab.addEventListener("keydown", event => {
        if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        const next = event.key === "Home" ? "main" : event.key === "End" ? "subagent" : active === "main" ? "subagent" : "main";
        select(next);
        (next === "main" ? mainTab : subagentTab).focus();
      });
    }
    subagentPanel.addEventListener("scroll", saveScroll);
    return { refresh, select, get activeTab() { return active; } };
  }

  globalThis.goAgentTabs = { agentId, isChild, groupsForMessage, mainSteps, renderNotice,
    attach(options) { controller = create(options); return controller; },
    refresh(state) { return controller?.refresh(state) || "main"; },
    select(value) { controller?.select(value); },
    get activeTab() { return controller?.activeTab || "main"; } };
})();
