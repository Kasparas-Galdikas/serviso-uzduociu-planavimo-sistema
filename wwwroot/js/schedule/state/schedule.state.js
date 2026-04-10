(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const normalizeView = (view) => ((view || "").toLowerCase() === "week" ? "week" : "day");
  const getQuery = () => new URLSearchParams(window.location.search || "");
  const cloneQuery = (query) => new URLSearchParams((query || "").toString());
  const isDraftMode = (query = getQuery()) =>
    String((query instanceof URLSearchParams ? query : cloneQuery(query)).get("planScope") || "").toLowerCase() === "draft";

  const getView = (query = getQuery()) =>
    normalizeView(query.get("view") || tables.dataset.currentView || "day");

  const emit = (eventName, detail) => {
    document.dispatchEvent(new CustomEvent(eventName, { detail }));
  };

  const emitViewChanged = (query = getQuery()) => {
    const q = cloneQuery(query);
    emit("schedule:view:changed", { view: getView(q), query: q });
  };

  const emitDraftModeChanged = (query = getQuery()) => {
    const q = cloneQuery(query);
    emit("schedule:draft-mode-changed", { enabled: isDraftMode(q), query: q });
  };

  const emitTableUpdated = (query = getQuery()) => {
    const q = cloneQuery(query);
    emit("schedule:table:updated", { view: getView(q), query: q });
  };

  const writeUrl = (query, { replace } = { replace: true }) => {
    const nextUrl = new URL(window.location.href);
    nextUrl.pathname = "/Schedule";
    nextUrl.search = query.toString();
    if (replace) {
      window.history.replaceState({}, "", nextUrl.toString());
    } else {
      window.history.pushState({}, "", nextUrl.toString());
    }
  };

  const ensureUrlDefaults = () => {
    const query = getQuery();
    let changed = false;

    if (!query.has("view")) {
      query.set("view", normalizeView(tables.dataset.currentView || "day"));
      changed = true;
    }

    if (!query.has("date")) {
      const view = normalizeView(query.get("view"));
      const fallbackRoot = document.querySelector('[data-schedule-filters="true"]') || document;
      const dateInput =
        view === "week"
          ? fallbackRoot.querySelector('[data-filter="weekDate"]')
          : fallbackRoot.querySelector('[data-filter="dayDate"]');
      const val = dateInput?.value;
      if (val) {
        query.set("date", val);
        changed = true;
      }
    }

    if (changed) {
      writeUrl(query, { replace: true });
    }

    emitViewChanged(query);
    emitDraftModeChanged(query);
    return query;
  };

  const setQuery = (query, { replace } = { replace: true }) => {
    writeUrl(query, { replace });
    emitViewChanged(query);
    emitDraftModeChanged(query);
  };

  const assertDayView = (reason) => {
    const view = getView();
    if (view === "day") return true;
    logEvent("ui.guard.blocked", { reason: reason || "unknown", view, query: getQuery().toString() });
    return false;
  };

  schedule.state = {
    normalizeView,
    getQuery,
    getView,
    isDraftMode,
    ensureUrlDefaults,
    setQuery,
    emitViewChanged,
    emitDraftModeChanged,
    emitTableUpdated,
    assertDayView,
  };
})();

