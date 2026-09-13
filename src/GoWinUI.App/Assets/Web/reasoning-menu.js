(function () {
  "use strict";
  const names = { auto: "Automatisch", none: "Aus", on: "Ein", minimal: "Minimal", low: "Niedrig", medium: "Mittel", high: "Hoch", xhigh: "Sehr hoch", max: "Maximum", ultra: "Ultra" };
  function options(profile) {
    const levels = [...new Set((profile?.levels || []).filter(value => typeof value === "string" && /^[a-z][a-z0-9_-]{0,31}$/.test(value) && value !== "auto"))];
    return ["auto", ...levels].map(value => ({ value, label: names[value] || value }));
  }
  if (typeof module === "object") module.exports = { options };
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
    button.disabled = running || !modelId;
    const selected = profile?.selected || "auto";
    label.textContent = "Reasoning";
    button.title = `Reasoning: ${names[selected] || selected}`;
    detail.textContent = pending ? "Modellinformationen werden geladen …" : profile?.detail
      || (profile?.defaultLevel ? `Automatisch: ${names[profile.defaultLevel] || profile.defaultLevel}. Auswahl wird für dieses Modell gespeichert.` : "Auswahl wird für dieses Modell gespeichert.");
    list.replaceChildren();
    for (const option of options(profile)) {
      const item = document.createElement("button");
      item.type = "button"; item.className = "service-option reasoning-option";
      item.setAttribute("role", "menuitemradio");
      item.setAttribute("aria-checked", String(option.value === selected));
      item.textContent = option.label;
      item.disabled = running || Boolean(pending) || (option.value !== "auto" && !profile?.available);
      item.addEventListener("click", () => {
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
    const items = [...list.querySelectorAll("button:not(:disabled)")];
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
      if (running) close();
      render();
    } else if (message.type === "reasoning.snapshot" && message.requestId === pending && data.modelId === modelId && data.role === role) {
      const wasSetting = profile && data.selected !== profile.selected;
      profile = data; pending = null; render();
      if (wasSetting) close(true);
      else if (!menu.hidden) list.querySelector('[aria-checked="true"]')?.focus();
    } else if (message.type === "host.error" && message.requestId === pending) {
      pending = null; profile = { selected: "auto", levels: [], ...profile, available: false, detail: "Modellinformationen konnten nicht geladen werden. Zum erneuten Prüfen das Menü öffnen." }; render();
    } else if (message.type === "chat.started") { running = true; close(); render(); }
    else if (["chat.completed", "chat.failed", "chat.cancelled"].includes(message.type)) { running = false; render(); }
  });
  render();
})();
