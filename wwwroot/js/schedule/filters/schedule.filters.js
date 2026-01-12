(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const parseDate = (val) => (val ? new Date(`${val}T00:00:00`) : null);
  const formatDate = (date) => (date ? date.toISOString().slice(0, 10) : "");

  const switchViewUI = (root, view) => {
    const normalized = state?.normalizeView?.(view) || "day";

    const viewTargets = root.querySelectorAll("[data-view-target]");
    viewTargets.forEach((el) => {
      el.classList.toggle("d-none", (el.dataset.viewTarget || "") !== normalized);
    });

    const viewModeButtons = root.querySelectorAll("[data-view-mode]");
    viewModeButtons.forEach((btn) => {
      const isActive = (btn.dataset.viewMode || "") === normalized;
      btn.classList.toggle("btn-primary", isActive);
      btn.classList.toggle("btn-outline-secondary", !isActive);
      btn.classList.toggle("active", isActive);
    });
  };

  const clampDates = (root) => {
    const nextFreeStr = tables.dataset.firstFree;
    const nextFreeDate = parseDate(nextFreeStr);
    if (!nextFreeDate) return;

    const minStr = formatDate(nextFreeDate);
    const inputs = [
      root.querySelector('[data-filter="dayDate"]'),
      root.querySelector('[data-filter="weekDate"]'),
    ].filter(Boolean);

    inputs.forEach((input) => {
      input.min = minStr;
      input.setAttribute("title", `Galima rinktis nuo ${minStr} (nepersidengiant su aktyviu planu)`);
      if (input.value) {
        const current = parseDate(input.value);
        if (current && current < nextFreeDate) {
          input.value = minStr;
        }
      }
    });
  };

  const syncUIFromQuery = (root, query) => {
    const view = state?.getView?.(query) || "day";
    root.dataset.pendingView = view;
    switchViewUI(root, view);

    const dateParam = query.get("date") || "";
    if (dateParam) {
      const dayDateInput = root.querySelector('[data-filter="dayDate"]');
      const weekDateInput = root.querySelector('[data-filter="weekDate"]');
      if (view === "week") {
        if (weekDateInput) weekDateInput.value = dateParam;
      } else {
        if (dayDateInput) dayDateInput.value = dateParam;
      }
    }

    const locationInput = root.querySelector('[data-filter="location"]');
    if (locationInput) {
      locationInput.value = query.get("location") || "";
    }

    const statusInput = root.querySelector('[data-filter="status"]');
    if (statusInput) {
      const statusParam = query.get("status");
      statusInput.value = statusParam || "Visi";
    }
  };

  const syncAllFromQuery = () => {
    const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
    document
      .querySelectorAll('[data-schedule-filters="true"]')
      .forEach((root) => syncUIFromQuery(root, query));
  };

  const readFiltersFromUI = (root) => {
    const pendingView = state?.normalizeView?.(root.dataset.pendingView || "day") || "day";
    const dayDateInput = root.querySelector('[data-filter="dayDate"]');
    const weekDateInput = root.querySelector('[data-filter="weekDate"]');
    const locationInput = root.querySelector('[data-filter="location"]');
    const statusInput = root.querySelector('[data-filter="status"]');

    const dateVal = pendingView === "week" ? weekDateInput?.value || "" : dayDateInput?.value || "";
    const locVal = locationInput?.value || "";
    const statusVal = statusInput?.value || "";

    return { view: pendingView, date: dateVal, location: locVal, status: statusVal };
  };

  const applyFromRoot = async (root, { button } = {}) => {
    const baseQuery = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
    const currentRole = baseQuery.get("role") || "";
    const technicianId = baseQuery.get("technicianId") || "";

    const { view, date, location, status } = readFiltersFromUI(root);
    const query = new URLSearchParams();
    query.set("view", view);
    if (currentRole) query.set("role", currentRole);
    if (date) query.set("date", date);
    if (location) query.set("location", location);
    if (status) query.set("status", status);
    if (technicianId) query.set("technicianId", technicianId);

    logEvent("ui.filter.apply", {
      view,
      date: date || "none",
      location: location || "all",
      status: status || "all",
      technicianId: technicianId || "all",
    });

    await table?.fetchAndRender?.(query, { button, updateUrl: true, replaceUrl: true });
    syncAllFromQuery();
  };

  const initFilterRoot = (root) => {
    if (!root || root.dataset.filtersInit === "1") return;
    root.dataset.filtersInit = "1";

    clampDates(root);

    const editToggle = root.querySelector("[data-edit-toggle]");
    const editActions = root.querySelectorAll("[data-edit-action]");
    if (editToggle && editActions.length) {
      const syncEdit = () => {
        const enabled = editToggle.checked;
        editActions.forEach((btn) => {
          btn.disabled = !enabled;
          btn.classList.toggle("disabled", !enabled);
        });
      };
      syncEdit();
      editToggle.addEventListener("change", syncEdit);
      root.addEventListener("shown.bs.modal", syncEdit);
    }

    const viewModeButtons = root.querySelectorAll("[data-view-mode]");
    viewModeButtons.forEach((btn) => {
      btn.addEventListener("click", () => {
        const nextView = state?.normalizeView?.(btn.dataset.viewMode) || "day";
        root.dataset.pendingView = nextView;
        switchViewUI(root, nextView);
      });
    });

    const applyBtn = root.querySelector("[data-filter-apply]");
    if (applyBtn) {
      applyBtn.addEventListener("click", (e) => {
        e.preventDefault();
        applyFromRoot(root, { button: applyBtn });
      });
    }

    const isModalRoot = (root.dataset.scheduleFiltersRoot || "") === "modal";
    if (isModalRoot) {
      root.addEventListener("shown.bs.modal", () => syncAllFromQuery());
    }
  };

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleFiltersInit === "1") return;
    document.documentElement.dataset.scheduleFiltersInit = "1";

    document.querySelectorAll('[data-schedule-filters="true"]').forEach((root) => initFilterRoot(root));

    document.addEventListener("schedule:view:changed", () => syncAllFromQuery());
    document.addEventListener("schedule:table:updated", () => syncAllFromQuery());

    syncAllFromQuery();
    logEvent("ui.filters.init");
  };

  schedule.filters = schedule.filters || {};
  schedule.filters.initOnce = initOnce;
  schedule.filters.syncAllFromQuery = syncAllFromQuery;
})();

