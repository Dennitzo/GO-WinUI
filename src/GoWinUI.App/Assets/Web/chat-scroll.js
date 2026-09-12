"use strict";
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory;
  else root.createGoChatScroll = factory;
})(globalThis, function createGoChatScroll({ scroller, content, button, host = globalThis }) {
  let following = true;
  let frame = 0;
  let animation = null;
  const distance = () => Math.max(0, scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop);
  const updateButton = () => { button.hidden = following || distance() <= 8; };
  const cancelFrame = () => {
    if (frame) host.cancelAnimationFrame(frame);
    frame = 0;
    animation = null;
  };
  function refresh() {
    updateButton();
    if (!following || frame) return;
    frame = host.requestAnimationFrame(() => {
      frame = 0;
      if (following) scroller.scrollTop = scroller.scrollHeight;
      updateButton();
    });
  }
  function pause() {
    cancelFrame();
    following = false;
    updateButton();
  }
  function jump(smooth = true) {
    cancelFrame();
    following = true;
    updateButton();
    if (!smooth || host.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
      refresh();
      return;
    }
    animation = { start: scroller.scrollTop, time: null };
    function advance(now) {
      frame = 0;
      if (!animation || !following) return;
      animation.time ??= now;
      const progress = Math.min(1, (now - animation.time) / 220);
      const bottom = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
      scroller.scrollTop = animation.start + (bottom - animation.start) * (1 - Math.pow(1 - progress, 3));
      if (progress < 1) frame = host.requestAnimationFrame(advance);
      else { animation = null; refresh(); }
    }
    frame = host.requestAnimationFrame(advance);
  }
  scroller.addEventListener("scroll", () => {
    if (!frame && !animation) following = distance() <= 8;
    updateButton();
  }, { passive: true });
  scroller.addEventListener("wheel", event => { if (event.deltaY < 0) pause(); }, { passive: true });
  scroller.addEventListener("touchstart", pause, { passive: true });
  scroller.addEventListener("pointerdown", pause, { passive: true });
  scroller.addEventListener("keydown", event => {
    if (["ArrowUp", "PageUp", "Home"].includes(event.key)) pause();
  });
  button.addEventListener("click", () => jump());
  const observer = host.ResizeObserver ? new host.ResizeObserver(refresh) : null;
  observer?.observe(scroller);
  observer?.observe(content);
  updateButton();
  return {
    refresh, jump,
    get following() { return following; },
    restore(atEnd) { cancelFrame(); following = atEnd; updateButton(); },
  };
});
