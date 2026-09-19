(function () {
  "use strict";
  const names = { none: "Aus", on: "Ein", minimal: "Minimal", low: "Niedrig", medium: "Mittel", high: "Hoch", xhigh: "Sehr hoch", max: "Maximum", ultra: "Ultra" };
  const normalized = value => typeof value === "string" ? value.trim().toLowerCase() : "";
  function options(profile) {
    const levels = [...new Set((Array.isArray(profile?.levels) ? profile.levels : []).map(normalized)
      .filter(value => /^[a-z][a-z0-9_-]{0,31}$/.test(value) && value !== "auto"))];
    return levels.map(value => ({ value, label: names[value] || value }));
  }
  function selection(profile, choices = options(profile)) {
    const supported = value => choices.some(option => option.value === value);
    const preferred = normalized(profile?.selected), modelDefault = normalized(profile?.defaultLevel);
    return supported(preferred) ? preferred : supported(modelDefault) ? modelDefault : choices[0]?.value || null;
  }
  if (typeof module === "object") module.exports = { options, selection };
  if (typeof document === "undefined") return;
  const button = document.getElementById("reasoning-button");
  if (!button) return;
  const menu = document.getElementById("reasoning-menu");
  const list = document.getElementById("reasoning-options");
  const label = document.getElementById("reasoning-label");
  const detail = document.getElementById("reasoning-detail");
  let modelId = "", role = "general", profile = null, pending = null, running = false;
  function close(focus = false) {
    menu.hidden = true; button.setAttribute("aria-expanded", "false");
    if (focus) button.focus();
  }
  function render() {
    const choices = options(profile), selected = selection(profile, choices);
    button.disabled = running || Boolean(pending) || !modelId || !profile?.available || !choices.length;
    if (running || !modelId || !pending && (!profile?.available || !choices.length)) close();
    label.textContent = "Reasoning";
    button.title = selected ? `Reasoning: ${names[selected] || selected}` : "Keine wählbare Reasoning-Stufe verfügbar";
    detail.textContent = pending ? "Modellinformationen werden geladen …" : profile?.detail
      || (choices.length ? "Auswahl wird für dieses Modell gespeichert." : "Das Modell bietet keine wählbaren Reasoning-Stufen.");
    list.replaceChildren();
    for (const option of choices) {
      const item = document.createElement("button");
      item.type = "button"; item.className = "service-option reasoning-option";
      item.setAttribute("role", "menuitemradio");
      item.setAttribute("aria-checked", String(option.value === selected));
      item.textContent = option.label;
      item.disabled = running || Boolean(pending) || !profile?.available;
      item.addEventListener("click", () => {
        if (item.disabled) return;
        pending = globalThis.goBridge.post("reasoning.set", { modelId, role, effort: option.value });
        render();
      });
      list.append(item);
    }
  }
  function refresh() {
    if (!modelId) return;
    pending = globalThis.goBridge.post("reasoning.get", { modelId, role });
    render();
  }
  button.addEventListener("click", event => {
    event.stopPropagation();
    if (button.disabled) return;
    if (!menu.hidden) return close();
    const tools = document.getElementById("tools-menu");
    if (tools) tools.hidden = true;
    document.getElementById("tools-button")?.setAttribute("aria-expanded", "false");
    menu.hidden = false; button.setAttribute("aria-expanded", "true"); refresh();
  });
  menu.addEventListener("click", event => event.stopPropagation());
  document.addEventListener("click", () => close());
  document.getElementById("tools-button")?.addEventListener("click", () => close());
  document.addEventListener("keydown", event => {
    if (menu.hidden) return;
    if (event.key === "Escape") { event.preventDefault(); close(true); return; }
    const items = [...list.querySelectorAll("button")].filter(item => !item.disabled);
    if (!items.length) return;
    const index = items.indexOf(document.activeElement);
    const next = event.key === "ArrowDown" ? (index + 1) % items.length
      : event.key === "ArrowUp" ? (index - 1 + items.length) % items.length
      : event.key === "Home" ? 0 : event.key === "End" ? items.length - 1 : -1;
    if (next >= 0) { event.preventDefault(); items[next].focus(); }
  });
  globalThis.addEventListener("go:host-message", event => {
    const message = event.detail, data = message.payload || {};
    if (message.type === "state.snapshot") {
      running = Boolean(data.isRunning);
      const changed = modelId !== (data.reasoningModelId || "") || role !== (data.reasoningRole || "general");
      modelId = data.reasoningModelId || ""; role = data.reasoningRole || "general";
      if (changed) { profile = null; pending = null; close(); refresh(); }
      else if (!profile?.available && !pending && !running) refresh();
      if (running) close();
      render();
    } else if (message.type === "reasoning.snapshot" && message.requestId === pending && data.modelId === modelId && data.role === role) {
      const wasSetting = profile && data.selected !== profile.selected;
      profile = data; pending = null; render();
      if (wasSetting) close(true);
      else if (!menu.hidden) list.querySelector('[aria-checked="true"]')?.focus();
    } else if (message.type === "host.error" && message.requestId === pending) {
      pending = null; profile = { ...profile, selected: null, levels: [], available: false, detail: "Modellinformationen konnten nicht geladen werden." }; render();
    } else if (message.type === "chat.started") { running = true; close(); render(); }
    else if (["chat.completed", "chat.failed", "chat.cancelled"].includes(message.type)) { running = false; render(); }
  });
  render();
})();
