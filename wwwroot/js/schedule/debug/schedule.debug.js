(() => {
  const drawer = document.getElementById("scheduleDebugDrawer");
  if (!drawer || !window.scheduleLogger) return;
  const logger = window.scheduleLogger;
  logger.initTrace();

  const SERVER_KEY = "schedule:showServer";
  const safeSet = (k, v) => {
    try {
      sessionStorage.setItem(k, v);
    } catch {}
  };
  const safeGet = (k) => {
    try {
      return sessionStorage.getItem(k);
    } catch {
      return null;
    }
  };

  const list = drawer.querySelector("[data-log-list]");
  const traceBadge = drawer.querySelector("[data-trace-id]");
  const refreshBtn = drawer.querySelector("[data-refresh-logs]");
  const copyBtn = drawer.querySelector("[data-copy-trace]");
  const serverToggle = drawer.querySelector("[data-toggle-server]");
  const status = drawer.querySelector("[data-status]");
  const clearBtn = drawer.querySelector("[data-clear-logs]");
  const state = { showServer: safeGet(SERVER_KEY) === "true" };

  const render = () => {
    const entries = scheduleLogger.getBuffer();
    if (traceBadge) {
      const ctx = scheduleLogger.getContext();
      traceBadge.textContent = ctx.traceId || "n/a";
    }

    if (!list) return;
    list.innerHTML = "";

    const items = entries
      .filter((e) => state.showServer || !(e.event || "").startsWith("server."))
      .slice(-100)
      .reverse();

    items.forEach((e) => {
      const li = document.createElement("li");
      li.className = "list-group-item py-2 px-3 small";
      const ts = new Date(e.timestamp).toLocaleTimeString();
      li.innerHTML = `<div class="fw-semibold">${e.event}</div>
        <div class="text-muted">${ts} • ${e.level} • span=${e.spanId || "-"}</div>
        <pre class="mb-0 small bg-light p-2 rounded">${JSON.stringify(e.data || {}, null, 2)}</pre>`;
      list.appendChild(li);
    });
  };

  const copyTrace = async () => {
    const ctx = scheduleLogger.getContext();
    if (!ctx.traceId) return;
    await navigator.clipboard.writeText(ctx.traceId);
    if (status) {
      status.textContent = "TraceId copied";
      setTimeout(() => (status.textContent = ""), 1500);
    }
  };

  const pullServer = async () => {
    const ctx = scheduleLogger.getContext();
    if (!ctx.traceId || !state.showServer) return;
    serverToggle?.setAttribute("aria-busy", "true");
    try {
      const resp = await fetch(`/debug/trace/${ctx.traceId}`, {
        headers: { "X-Trace-Id": ctx.traceId, "X-Requested-With": "XMLHttpRequest" },
      });
      if (!resp.ok) throw new Error("Server logs unavailable");
      const data = await resp.json();
      data.forEach((d) => {
        scheduleLogger.log((d.level || "info").toLowerCase(), `server.${d.eventId || "log"}`, {
          spanId: d.spanId,
          category: d.category,
          message: d.message,
          level: d.level,
          scope: d.scope,
        });
      });
      render();
    } catch (err) {
      scheduleLogger.log("error", "debug.pullServer.failed", { error: err?.message });
      render();
    } finally {
      serverToggle?.removeAttribute("aria-busy");
    }
  };

  refreshBtn?.addEventListener("click", render);
  copyBtn?.addEventListener("click", copyTrace);
  serverToggle?.addEventListener("change", () => {
    state.showServer = !!serverToggle.checked;
    safeSet(SERVER_KEY, state.showServer ? "true" : "false");
    if (state.showServer) {
      pullServer();
    } else {
      render();
    }
  });
  clearBtn?.addEventListener("click", () => {
    scheduleLogger.clearBuffer();
    render();
    if (status) {
      status.textContent = "Logs cleared";
      setTimeout(() => (status.textContent = ""), 1200);
    }
  });

  if (serverToggle) {
    serverToggle.checked = state.showServer;
  }

  render();
  if (state.showServer) {
    pullServer();
  }
})();

