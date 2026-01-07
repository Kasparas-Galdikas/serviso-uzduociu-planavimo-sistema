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

  const fetchAndRender = async (query, { button, updateUrl, replaceUrl } = {}) => {
    button?.classList.add("disabled");
    button?.setAttribute("aria-busy", "true");

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
      logEvent("ui.table.fetch.ok", { url, status: response.status, length: html.length });

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

      state?.setQuery?.(query, { replace: false });
      await fetchAndRender(query, { button: navBtn, updateUrl: false });
    });

    window.addEventListener("popstate", async () => {
      const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      if (!query.has("view")) query.set("view", "day");
      state?.emitViewChanged?.(query);
      await fetchAndRender(query, { updateUrl: false });
    });
  };

  schedule.table = {
    fetchAndRender,
    initNavOnce,
  };
})();

