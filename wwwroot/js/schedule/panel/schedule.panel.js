(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const getPanelEl = () => document.getElementById("taskDetailModal");

  const clearActiveCards = () => {
    document.querySelectorAll(".task-card.is-active").forEach((el) => {
      el.classList.remove("is-active");
    });
  };

  const forceClearPushLayout = () => {
    document.documentElement.classList.remove("offcanvas-push-open");
    document.documentElement.style.setProperty("--offcanvas-push", "0px");
  };

  const hide = () => {
    const panelEl = getPanelEl();
    if (!panelEl) return;
    try {
      bootstrap?.Offcanvas?.getInstance?.(panelEl)?.hide();
    } catch {
      // ignore
    }
  };

  const show = () => {
    const panelEl = getPanelEl();
    if (!panelEl) return;
    try {
      bootstrap?.Offcanvas?.getOrCreateInstance?.(panelEl)?.show();
    } catch {
      // ignore
    }
  };

  const closeForNonDailyContext = (view) => {
    if ((view || "").toLowerCase() === "day") return;
    hide();
    clearActiveCards();
    forceClearPushLayout();
  };

  const initOnce = () => {
    if (document.documentElement.dataset.schedulePanelInit === "1") return;
    document.documentElement.dataset.schedulePanelInit = "1";

    const detailPanel = getPanelEl();
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
      const expandedMaxWidth = 560;
      const collapsedCssWidth = 25;
      const collapseThreshold = 60;

      const applyPush = () => {
        if (!supportsPush()) return;
        const isCollapsed = detailPanel.classList.contains("is-collapsed");
        if (isCollapsed) {
          root.classList.remove("offcanvas-push-open");
          root.style.setProperty("--offcanvas-push", "0px");
          return;
        }

        const measuredWidth = detailPanel.getBoundingClientRect().width || 0;
        const push = Math.min(Math.max(measuredWidth, 0), expandedMaxWidth);
        root.style.setProperty("--offcanvas-push", `${push}px`);
        root.classList.add("offcanvas-push-open");
      };

      const clearPush = () => {
        root.classList.remove("offcanvas-push-open");
        root.style.setProperty("--offcanvas-push", "0px");
      };

      const setPanelWidth = (px) => {
        if (!px || Number.isNaN(px)) {
          detailPanel.style.removeProperty("--bs-offcanvas-width");
          return;
        }
        detailPanel.style.setProperty("--bs-offcanvas-width", `${px}px`);
      };

      const getExpandedWidth = () => {
        const stored = Number(detailPanel.dataset.expandedWidth || "");
        if (stored && !Number.isNaN(stored)) return stored;
        return Math.min(520, expandedMaxWidth);
      };

      const collapsePanel = () => {
        const current = detailPanel.getBoundingClientRect().width || getExpandedWidth();
        if (current > collapseThreshold) {
          detailPanel.dataset.expandedWidth = String(Math.min(current, expandedMaxWidth));
        }
        detailPanel.classList.add("is-collapsed");
        setPanelWidth(null);
        detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
      };

      const expandPanel = (width) => {
        detailPanel.classList.remove("is-collapsed");
        const next = Math.min(
          Math.max(width || getExpandedWidth(), collapseThreshold),
          expandedMaxWidth,
        );
        detailPanel.dataset.expandedWidth = String(next);
        setPanelWidth(next);
        detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
      };

      const toggleBtn = detailPanel.querySelector("[data-panel-toggle]");
      const toggleIcon = detailPanel.querySelector("[data-panel-toggle-icon]");
      const syncToggleIcon = () => {
        if (!toggleBtn || !toggleIcon) return;
        const collapsed = detailPanel.classList.contains("is-collapsed");
        toggleBtn.setAttribute("aria-pressed", collapsed ? "true" : "false");
        toggleIcon.textContent = collapsed ? ">" : "<";
      };

      if (toggleBtn) {
        syncToggleIcon();
        toggleBtn.addEventListener("click", (e) => {
          e.preventDefault();
          if (detailPanel.classList.contains("is-collapsed")) {
            expandPanel();
          } else {
            collapsePanel();
          }
        });
      }

      detailPanel.addEventListener("schedule:panel:collapse-changed", () => {
        syncToggleIcon();
        applyPush();
        setTimeout(() => applyPush(), 260);
      });

      const resizeHandle = detailPanel.querySelector("[data-panel-resize]");
      if (resizeHandle) {
        resizeHandle.addEventListener("pointerdown", (e) => {
          if (!supportsPush()) return;
          e.preventDefault();
          e.stopPropagation();

          detailPanel.classList.add("is-resizing");
          resizeHandle.setPointerCapture?.(e.pointerId);

          const startClientX = e.clientX;
          const startWidth = detailPanel.classList.contains("is-collapsed")
            ? collapsedCssWidth
            : detailPanel.getBoundingClientRect().width || getExpandedWidth();

          const onMove = (ev) => {
            const dx = startClientX - ev.clientX;
            const nextWidth = Math.min(
              Math.max(startWidth + dx, collapsedCssWidth),
              expandedMaxWidth,
            );

            if (nextWidth <= collapseThreshold) {
              detailPanel.classList.add("is-collapsed");
              setPanelWidth(null);
            } else {
              detailPanel.classList.remove("is-collapsed");
              detailPanel.dataset.expandedWidth = String(nextWidth);
              setPanelWidth(nextWidth);
            }

            syncToggleIcon();
            applyPush();
          };

          const onUp = () => {
            detailPanel.classList.remove("is-resizing");
            window.removeEventListener("pointermove", onMove);
            window.removeEventListener("pointerup", onUp);

            if (detailPanel.classList.contains("is-collapsed")) {
              setPanelWidth(null);
            } else {
              const w = detailPanel.getBoundingClientRect().width || getExpandedWidth();
              detailPanel.dataset.expandedWidth = String(Math.min(w, expandedMaxWidth));
            }

            detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
          };

          window.addEventListener("pointermove", onMove);
          window.addEventListener("pointerup", onUp);
        });
      }

      detailPanel.addEventListener("shown.bs.offcanvas", () => {
        requestAnimationFrame(() => applyPush());
        setTimeout(() => applyPush(), 260);
        syncToggleIcon();
      });
      detailPanel.addEventListener("hidden.bs.offcanvas", () => {
        clearPush();
        clearActiveCards();
      });

      window.addEventListener("resize", () => {
        if (root.classList.contains("offcanvas-push-open")) {
          applyPush();
        } else if (!supportsPush()) {
          clearPush();
        }
      });
    }

    document.addEventListener("schedule:view:changed", (e) => {
      closeForNonDailyContext(e?.detail?.view);
    });
    document.addEventListener("schedule:table:updated", (e) => {
      closeForNonDailyContext(e?.detail?.view);
    });

    logEvent("ui.panel.init");
  };

  schedule.panel = {
    initOnce,
    show,
    hide,
    closeForNonDailyContext,
  };
})();

