(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  const logger = window.scheduleLogger;
  if (logger) logger.initTrace();
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const normalizeView = (view) =>
    (view || "").toLowerCase() === "week" ? "week" : "day";

  const parseDate = (val) => (val ? new Date(`${val}T00:00:00`) : null);
  const formatDate = (date) => (date ? date.toISOString().slice(0, 10) : "");

  const getQuery = () => new URLSearchParams(window.location.search || "");

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
      const fallbackRoot =
        document.querySelector('[data-schedule-filters="true"]') || document;
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
  };

  const logFetchError = async (resp, url) => {
    try {
      const text = await resp.text();
      logEvent(
        "ui.fetch.error",
        { url, status: resp.status, statusText: resp.statusText, body: text },
        "error",
      );
    } catch (err) {
      logEvent(
        "ui.fetch.error",
        { url, status: resp.status, statusText: resp.statusText },
        "error",
      );
    }
  };

  const notifyTableUpdated = () => {
    document.dispatchEvent(new CustomEvent("schedule:table:updated"));
    logEvent("ui.table.updated");
  };

  const fetchTable = async (query, { button, skipHistory } = {}) => {
    button?.classList.add("disabled");
    button?.setAttribute("aria-busy", "true");

    const url = `/Schedule/Table?${query.toString()}`;
    try {
      const response = await (logger?.fetchWithTrace(
        url,
        {},
        {
          event: "ui.table.fetch",
          view: query.get("view"),
          date: query.get("date"),
          location: query.get("location"),
          status: query.get("status"),
	        },
      ) ||
        fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } }));
      if (!response.ok) {
        await logFetchError(response, url);
        return;
      }

      const html = await response.text();
      tables.innerHTML = html;
      logEvent("ui.table.fetch.ok", { url, status: response.status, length: html.length });

      if (!skipHistory) {
        writeUrl(query, { replace: true });
      }
      notifyTableUpdated();
    } catch (err) {
      logEvent("ui.table.fetch.error", { url, error: err?.message }, "error");
    } finally {
      button?.classList.remove("disabled");
      button?.removeAttribute("aria-busy");
    }
  };

  const switchViewUI = (root, view) => {
    const normalized = normalizeView(view);

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
      input.setAttribute(
        "title",
        `Galima rinktis nuo ${minStr} (nepersidengiant su aktyviu planu)`,
      );
      if (input.value) {
        const current = parseDate(input.value);
        if (current && current < nextFreeDate) {
          input.value = minStr;
        }
      }
    });
  };

  const syncUIFromQuery = (root, query) => {
    const view = normalizeView(query.get("view") || tables.dataset.currentView || "day");
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
    const query = getQuery();
    document
      .querySelectorAll('[data-schedule-filters="true"]')
      .forEach((root) => syncUIFromQuery(root, query));
  };

  const readFiltersFromUI = (root) => {
    const pendingView = normalizeView(root.dataset.pendingView || "day");
    const dayDateInput = root.querySelector('[data-filter="dayDate"]');
    const weekDateInput = root.querySelector('[data-filter="weekDate"]');
    const locationInput = root.querySelector('[data-filter="location"]');
    const statusInput = root.querySelector('[data-filter="status"]');

    const dateVal =
      pendingView === "week" ? weekDateInput?.value || "" : dayDateInput?.value || "";
    const locVal = locationInput?.value || "";
    const statusVal = statusInput?.value || "";

    return { view: pendingView, date: dateVal, location: locVal, status: statusVal };
  };

  const applyFromRoot = async (root, { button } = {}) => {
    const baseQuery = getQuery();
    const currentRole = baseQuery.get("role") || "";

    const { view, date, location, status } = readFiltersFromUI(root);
    const query = new URLSearchParams();
    query.set("view", view);
    if (currentRole) query.set("role", currentRole);
    if (date) query.set("date", date);
    if (location) query.set("location", location);
    if (status) query.set("status", status);

    logEvent("ui.filter.apply", {
      view,
      date: date || "none",
      location: location || "all",
      status: status || "all",
    });

    await fetchTable(query, { button });
    syncAllFromQuery();
  };

  const initFilterRoot = (root) => {
    if (!root || root.dataset.filtersInit === "1") return;
    root.dataset.filtersInit = "1";

    clampDates(root);

    // Edit mode toggle (enables/disables edit actions)
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
        const nextView = normalizeView(btn.dataset.viewMode);
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

    // Modal-specific behaviors
    const isModalRoot = (root.dataset.scheduleFiltersRoot || "") === "modal";
    if (isModalRoot) {
      root.addEventListener("shown.bs.modal", () => syncAllFromQuery());
    }
  };

  const bindTaskClickOnce = () => {
    if (document.documentElement.dataset.scheduleTaskClickInit === "1") return;
    document.documentElement.dataset.scheduleTaskClickInit = "1";

    const clearActiveCards = () => {
      document.querySelectorAll(".task-card.is-active").forEach((el) => {
        el.classList.remove("is-active");
      });
    };

    const setActiveCard = (card) => {
      if (!card) return;
      clearActiveCards();
      card.classList.add("is-active");
    };

    const populateTaskModal = (modalEl, data, editable) => {
      const setVal = (selector, value) => {
        const el = modalEl.querySelector(selector);
        if (el) el.value = value || "";
      };

      setVal("#taskDetailTitle", data.title);
      setVal("#taskDetailLocation", data.location);
      setVal("#taskDetailStart", data.startAt ? data.startAt.substring(0, 16) : "");
      setVal("#taskDetailEnd", data.endAt ? data.endAt.substring(0, 16) : "");
      setVal("#taskDetailPriority", data.priority);
      setVal("#taskDetailStatus", data.status);

      modalEl.dataset.taskId = data.id;
      modalEl.dataset.editable = editable ? "true" : "false";

      const inputs = modalEl.querySelectorAll("input, select, textarea");
      inputs.forEach((el) => {
        if (editable) {
          el.removeAttribute("disabled");
        } else {
          el.setAttribute("disabled", "disabled");
        }
      });

      const saveBtn = modalEl.querySelector("#taskDetailSave");
      if (saveBtn) {
        saveBtn.classList.toggle("d-none", !editable);
        saveBtn.onclick = async () => {
          await saveTask(modalEl);
        };
      }
    };

    const saveTask = async (modalEl) => {
      const taskId = modalEl.dataset.taskId;
      if (!taskId) return;
      const title = modalEl.querySelector("#taskDetailTitle")?.value || "";
      const location = modalEl.querySelector("#taskDetailLocation")?.value || "";
      const startAt = modalEl.querySelector("#taskDetailStart")?.value || "";
      const endAt = modalEl.querySelector("#taskDetailEnd")?.value || "";
      const priority = modalEl.querySelector("#taskDetailPriority")?.value || "";
      const status = modalEl.querySelector("#taskDetailStatus")?.value || "";

      const saveBtn = modalEl.querySelector("#taskDetailSave");
      const setBusy = (state) => {
        if (saveBtn) {
          saveBtn.disabled = state;
          saveBtn.setAttribute("aria-busy", state ? "true" : "false");
        }
      };

      setBusy(true);
      try {
        const resp =
          (await logger?.fetchWithTrace(
            `/tasks/${taskId}`,
            {
              method: "PATCH",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ title, location, startAt, endAt, priority, status }),
            },
            { event: "ui.task.save", taskId },
          )) ||
          (await fetch(`/tasks/${taskId}`, {
            method: "PATCH",
            headers: {
              "X-Requested-With": "XMLHttpRequest",
              "Content-Type": "application/json",
            },
            body: JSON.stringify({ title, location, startAt, endAt, priority, status }),
          }));

        if (resp.status === 409 || resp.status === 403) {
          alert("Planas pasikeite. Uzduoties redaguoti negalima.");
        } else if (!resp.ok) {
          throw new Error("Nepavyko issaugoti uzduoties");
        } else {
          const query = getQuery();
          if (!query.has("view")) query.set("view", "day");
          await fetchTable(query, { skipHistory: true });
          const bsPanel = bootstrap.Offcanvas.getInstance(modalEl);
          bsPanel?.hide();
        }

        logEvent("ui.task.saved", { taskId });
      } catch (err) {
        logEvent("ui.task.save.error", { taskId, error: err?.message }, "error");
        alert("Nepavyko issaugoti uzduoties.");
      } finally {
        setBusy(false);
      }
    };

    document.addEventListener("click", async (evt) => {
      const card = evt.target?.closest?.(".task-card");
      if (!card) return;

      const taskId = card.getAttribute("data-task-id");
      if (!taskId) return;

      try {
        setActiveCard(card);

        const resp =
          (await logger?.fetchWithTrace(
            `/tasks/${taskId}`,
            {},
            { event: "ui.task.fetch", taskId },
          )) ||
          (await fetch(`/tasks/${taskId}`, {
            headers: { "X-Requested-With": "XMLHttpRequest" },
          }));
        if (!resp.ok) {
          await logFetchError(resp, `/tasks/${taskId}`);
          throw new Error("Nepavyko gauti uzduoties duomenu");
        }

        const data = await resp.json();
        const detailPanel = document.getElementById("taskDetailModal");
        if (!detailPanel) return;

        populateTaskModal(detailPanel, data, data.isEditable);
        const panel = bootstrap.Offcanvas.getOrCreateInstance(detailPanel);
        panel.show();
        logEvent("ui.task.opened", { taskId, editable: data.isEditable });
      } catch (err) {
        clearActiveCards();
        logEvent("ui.task.fetch.error", { taskId, error: err?.message }, "error");
        alert("Nepavyko atidaryti uzduoties duomenu.");
      }
    });
  };

  const initNavOnce = () => {
    if (document.documentElement.dataset.scheduleNavInit === "1") return;
    document.documentElement.dataset.scheduleNavInit = "1";

    tables.addEventListener("click", async (evt) => {
      const navBtn = evt.target?.closest?.("[data-nav]");
      if (!navBtn) return;
      evt.preventDefault();

      const baseQuery = getQuery();
      const query = new URLSearchParams();

      const navView = normalizeView(navBtn.dataset.view || baseQuery.get("view") || "day");
      query.set("view", navView);

      const roleVal = navBtn.dataset.role || baseQuery.get("role") || "";
      if (roleVal) query.set("role", roleVal);

      const targetDate = navBtn.dataset.targetDate || "";
      if (targetDate) query.set("date", targetDate);

      const loc = navBtn.dataset.location || "";
      if (loc) query.set("location", loc);
      const status = navBtn.dataset.status || "";
      if (status) query.set("status", status);

      logEvent("ui.nav.click", {
        view: navView,
        date: targetDate || "none",
        location: loc || "all",
        status: status || "all",
      });

      writeUrl(query, { replace: false });
      await fetchTable(query, { button: navBtn, skipHistory: true });
      syncAllFromQuery();
    });

    window.addEventListener("popstate", async () => {
      const query = getQuery();
      if (!query.has("view")) query.set("view", "day");
      await fetchTable(query, { skipHistory: true });
      syncAllFromQuery();
    });

    document.addEventListener("schedule:table:updated", () => syncAllFromQuery());
  };

  const initTriggerVisibilityOnce = () => {
    if (document.documentElement.dataset.scheduleTriggerInit === "1") return;
    document.documentElement.dataset.scheduleTriggerInit = "1";

    const detailPanel = document.getElementById("taskDetailModal");
    const filterTrigger = document.querySelector(".filter-console-trigger");
    if (detailPanel && filterTrigger) {
      const hideTrigger = () => filterTrigger.classList.add("d-none");
      const showTrigger = () => filterTrigger.classList.remove("d-none");
      detailPanel.addEventListener("show.bs.offcanvas", hideTrigger);
      detailPanel.addEventListener("hidden.bs.offcanvas", showTrigger);
    }

    if (detailPanel) {
      const root = document.documentElement;
      const supportsPush = () => window.matchMedia("(min-width: 992px)").matches;

      const applyPush = () => {
        if (!supportsPush()) return;
        const rect = detailPanel.getBoundingClientRect();
        const rawWidth = rect.width || 0;
        const push = Math.min(Math.max(rawWidth, 0), 560);
        root.style.setProperty("--offcanvas-push", `${push}px`);
        root.classList.add("offcanvas-push-open");
      };

      const clearPush = () => {
        root.classList.remove("offcanvas-push-open");
        root.style.setProperty("--offcanvas-push", "0px");
      };

      detailPanel.addEventListener("shown.bs.offcanvas", () => {
        requestAnimationFrame(() => applyPush());
      });
      detailPanel.addEventListener("hidden.bs.offcanvas", clearPush);

      window.addEventListener("resize", () => {
        if (root.classList.contains("offcanvas-push-open")) {
          applyPush();
        } else if (!supportsPush()) {
          clearPush();
        }
      });

      detailPanel.addEventListener("hidden.bs.offcanvas", () => {
        document.querySelectorAll(".task-card.is-active").forEach((el) => {
          el.classList.remove("is-active");
        });
      });
    }
  };

  ensureUrlDefaults();

  document
    .querySelectorAll('[data-schedule-filters="true"]')
    .forEach((root) => initFilterRoot(root));

  initNavOnce();
  bindTaskClickOnce();
  initTriggerVisibilityOnce();
  syncAllFromQuery();
})();
