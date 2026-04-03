(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;

  if (document.documentElement.dataset.scheduleInit === "1") return;
  document.documentElement.dataset.scheduleInit = "1";

  schedule.state?.ensureUrlDefaults?.();

  schedule.panel?.initOnce?.();
  schedule.table?.initNavOnce?.();
  schedule.tasks?.initOnce?.();
  schedule.dragdrop?.initOnce?.();
  schedule.selection?.initOnce?.();
  schedule.filters?.initOnce?.();
  schedule.nowLine?.initOnce?.();
  schedule.unavailability?.initOnce?.();

  schedule.state?.emitViewChanged?.(schedule.state?.getQuery?.());
})();
