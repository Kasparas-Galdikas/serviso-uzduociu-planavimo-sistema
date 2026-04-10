(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

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

  const fetchAndRender = async (query, { button, updateUrl, replaceUrl, preserveScroll = false } = {}) => {
    button?.classList.add("disabled");
    button?.setAttribute("aria-busy", "true");

    // Capture scroll position before refresh
    const scrollContainer = tables.querySelector(".table-responsive") || tables.closest(".table-responsive");
    const scrollTop = preserveScroll && scrollContainer ? scrollContainer.scrollTop : null;
    const scrollLeft = preserveScroll && scrollContainer ? scrollContainer.scrollLeft : null;

    const url = `/Schedule/Table?${query.toString()}`;
    try {
      const response =
        (await logger?.fetchWithTrace(
          url,
          {},
          {
            event: "ui.table.fetch",
            view: query.get("view"),
            date: query.get("date"),
            location: query.get("location"),
            status: query.get("status"),
          },
        )) || (await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } }));

      if (!response.ok) {
        await logFetchError(response, url);
        return false;
      }

      const html = await response.text();
      tables.innerHTML = html;
      if (query?.get) {
        tables.dataset.currentView = query.get("view") || tables.dataset.currentView || "day";
        const infoType = query.get("info") || tables.dataset.informationType || "apkrova";
        tables.dataset.informationType = infoType;
      }
      const earliestCreatableDate = response.headers.get("X-Schedule-Earliest-Creatable-Date");
      const earliestCreatableStart = response.headers.get("X-Schedule-Earliest-Creatable-Start-Minutes");
      const earliestCreatableEnd = response.headers.get("X-Schedule-Earliest-Creatable-End-Minutes");
      if (earliestCreatableDate) tables.dataset.earliestCreatableDate = earliestCreatableDate;
      if (earliestCreatableStart) tables.dataset.earliestCreatableStartMinutes = earliestCreatableStart;
      if (earliestCreatableEnd) tables.dataset.earliestCreatableEndMinutes = earliestCreatableEnd;
      logEvent("ui.table.fetch.ok", { url, status: response.status, length: html.length });

      // Restore scroll position after refresh
      if (preserveScroll && scrollTop !== null && scrollLeft !== null) {
        const newScrollContainer = tables.querySelector(".table-responsive") || tables.closest(".table-responsive");
        if (newScrollContainer) {
          // Use requestAnimationFrame to ensure DOM is fully rendered
          requestAnimationFrame(() => {
            newScrollContainer.scrollTop = scrollTop;
            newScrollContainer.scrollLeft = scrollLeft;
            logEvent("ui.table.scroll.restored", { scrollTop, scrollLeft });
          });
        }
      }

      if (updateUrl && state?.setQuery) {
        state.setQuery(query, { replace: replaceUrl !== false });
      }

      state?.emitTableUpdated?.(query);
      return true;
    } catch (err) {
      logEvent("ui.table.fetch.error", { url, error: err?.message }, "error");
      return false;
    } finally {
      button?.classList.remove("disabled");
      button?.removeAttribute("aria-busy");
    }
  };

  const initNavOnce = () => {
    if (document.documentElement.dataset.scheduleNavInit === "1") return;
    document.documentElement.dataset.scheduleNavInit = "1";

    tables.addEventListener("click", async (evt) => {
      const navBtn = evt.target?.closest?.("[data-nav]");
      if (!navBtn) return;
      evt.preventDefault();

      const baseQuery = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      const query = new URLSearchParams();

      const navView =
        state?.normalizeView?.(navBtn.dataset.view || baseQuery.get("view") || "day") || "day";
      query.set("view", navView);

      const targetDate = navBtn.dataset.targetDate || baseQuery.get("date") || "";
      if (targetDate) query.set("date", targetDate);

      const loc = navBtn.dataset.location ?? baseQuery.get("location") ?? "";
      if (loc) query.set("location", loc);

      const workload = navBtn.dataset.workload ?? baseQuery.get("workload") ?? "";
      if (workload) query.set("workload", workload);

      const info = navBtn.dataset.info ?? baseQuery.get("info") ?? "";
      if (info) query.set("info", info);

      const planScope = navBtn.dataset.planScope ?? baseQuery.get("planScope") ?? "";
      if (planScope) query.set("planScope", planScope);

      const status = navBtn.dataset.status ?? baseQuery.get("status") ?? "";
      if (status) query.set("status", status);

      const technicianId = navBtn.dataset.technicianId || baseQuery.get("technicianId") || "";
      if (technicianId) query.set("technicianId", technicianId);

      logEvent("ui.nav.click", {
        view: navView,
        date: targetDate || "none",
        location: loc || "all",
        workload: workload || "all",
        info: info || "apkrova",
        status: status || "all",
        technicianId: technicianId || "all",
      });

      state?.setQuery?.(query, { replace: false });
      await fetchAndRender(query, { button: navBtn, updateUrl: false });
    });

    window.addEventListener("popstate", async () => {
      const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      if (!query.has("view")) query.set("view", "day");
      state?.emitViewChanged?.(query);
      await fetchAndRender(query, { updateUrl: false });
    });

    // Clear technician filter button
    document.addEventListener("click", async (evt) => {
      const clearBtn = evt.target?.closest?.("[data-clear-technician-filter]");
      if (!clearBtn) return;
      evt.preventDefault();

      const baseQuery = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      const query = new URLSearchParams();

      // Keep all filters except technicianId
      const view = baseQuery.get("view") || "day";
      query.set("view", view);

      const date = baseQuery.get("date") || "";
      if (date) query.set("date", date);

      const loc = baseQuery.get("location") || "";
      if (loc) query.set("location", loc);

      const workload = baseQuery.get("workload") || "";
      if (workload) query.set("workload", workload);

      const planScope = baseQuery.get("planScope") || "";
      if (planScope) query.set("planScope", planScope);

      // Do NOT include technicianId - that's the point of this button

      logEvent("ui.filter.clear-technician", {
        view,
        date: date || "none",
        location: loc || "all",
        workload: workload || "all",
      });

      state?.setQuery?.(query, { replace: false });
      const success = await fetchAndRender(query, { button: clearBtn, updateUrl: true });

      // If successful, remove ALL instances of the clear button (modal + inline)
      if (success) {
        document.querySelectorAll("[data-clear-technician-filter]").forEach((btn) => {
          btn.remove();
        });
      }
    });
  };

  schedule.table = {
    fetchAndRender,
    initNavOnce,
  };
})();
