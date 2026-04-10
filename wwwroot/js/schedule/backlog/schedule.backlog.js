(() => {
  "use strict";

  window.schedule = window.schedule || {};
  const backlog = window.schedule.backlog = window.schedule.backlog || {};
  const state = backlog.state;

  const init = () => backlog.scheduleBridge?.init?.();

  backlog.openBacklogPanel = (...args) => backlog.scheduleBridge?.openBacklogPanel?.(...args);
  backlog.getSchedulingBacklogId = () => state._schedulingFromBacklogId;
  backlog.initOnce = init;
  backlog._draggingItem = backlog._draggingItem || null;

  Object.defineProperty(backlog, "_isBulkMode", {
    configurable: true,
    enumerable: true,
    get: () => state._isBulkMode,
  });

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
