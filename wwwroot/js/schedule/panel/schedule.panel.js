(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const selection = schedule.selection;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const VARIANTS = {
    "task-edit": {
      legacyType: "task",
      panelMode: "task",
      views: ["task-form"],
      sections: ["planning", "parameters", "asset", "manager", "technician"],
      fields: ["assignee", "start", "end"],
      regions: { "backlog-tabs": false, "bulk-progress": false, footer: true },
      footer: "task",
      header: { titleText: "Užduoties informacija", subtitle: "code", historySubtitle: false },
    },
    "task-create": {
      legacyType: "task",
      panelMode: "task",
      views: ["task-form"],
      sections: ["planning", "parameters", "asset", "manager", "technician"],
      fields: ["assignee", "start", "end"],
      regions: { "backlog-tabs": false, "bulk-progress": false, footer: true },
      footer: "task",
      header: { titleText: "Nauja užduotis", subtitle: "hidden", historySubtitle: false },
    },
    "backlog-create": {
      legacyType: "backlog",
      panelMode: "backlog",
      datasetMode: "backlog",
      views: ["task-form"],
      sections: ["planning", "parameters", "asset", "manager"],
      fields: ["backlog-type", "backlog-due-date"],
      regions: { "backlog-tabs": true, "bulk-progress": false, footer: true },
      footer: "backlog",
      header: { titleText: "Darbų šablonai", subtitle: "hidden", historySubtitle: false },
    },
    "backlog-list": {
      legacyType: "backlog",
      panelMode: "backlog",
      datasetMode: "backlog",
      views: ["backlog-list"],
      sections: [],
      fields: [],
      regions: { "backlog-tabs": true, "bulk-progress": false, footer: false },
      footer: "hidden",
      header: { titleText: "Darbų šablonai", subtitle: "hidden", historySubtitle: false },
    },
    "backlog-bulk-edit": {
      legacyType: "backlog",
      panelMode: "backlog",
      datasetMode: "backlog",
      views: ["task-form"],
      sections: ["planning", "parameters", "asset", "manager", "technician"],
      fields: ["assignee", "start", "end", "plan-mode"],
      regions: { "backlog-tabs": false, "bulk-progress": true, footer: true },
      footer: "bulk",
      header: { titleText: "Darbų šablonai", subtitle: "hidden", historySubtitle: false },
    },
    "backlog-bulk-confirm": {
      legacyType: "backlog",
      panelMode: "backlog",
      datasetMode: "backlog",
      views: ["bulk-confirm"],
      sections: [],
      fields: [],
      regions: { "backlog-tabs": false, "bulk-progress": false, footer: true },
      footer: "bulk",
      header: { titleText: "Plano santrauka", subtitle: "hidden", historySubtitle: false },
    },
    history: {
      legacyType: "history",
      panelMode: "history",
      datasetMode: "history",
      views: ["history"],
      sections: [],
      fields: [],
      regions: { "backlog-tabs": false, "bulk-progress": false, footer: false },
      footer: "hidden",
      header: { title: "history", subtitle: "hidden", historySubtitle: true },
    },
    "tasks-panel": {
      legacyType: "tasks-panel",
      panelMode: "tasks-panel",
      datasetMode: "tasks-panel",
      views: ["tasks-panel"],
      sections: [],
      fields: [],
      regions: { "backlog-tabs": false, "bulk-progress": false, footer: false },
      footer: "hidden",
      header: { titleText: "Suplanuoti darbai", subtitle: "hidden", historySubtitle: false },
    },
    technician: {
      legacyType: "technician",
      panelMode: "technician",
      datasetMode: "technician",
      views: ["technician"],
      sections: [],
      fields: [],
      regions: { "backlog-tabs": false, "bulk-progress": false, footer: false },
      footer: "hidden",
      header: { titleText: "Techniko neaktyvumas", subtitle: "hidden", historySubtitle: false },
    },
  };

  const FOOTER_ACTIONS = {
    task: ["close", "task-save"],
    backlog: ["close", "backlog-save"],
    bulk: ["bulk-back", "bulk-next"],
    hidden: [],
  };

  const VIEW_ALIASES = {
    task: () => (getPanelEl()?.dataset.mode === "create" ? "task-create" : "task-edit"),
    backlog: () => resolveBacklogVariant(),
    history: () => "history",
    "tasks-panel": () => "tasks-panel",
    technician: () => "technician",
  };

  const getPanelEl = () => document.getElementById("taskDetailModal");

  const getVariantElements = (panelEl) => ({
    views: Array.from(panelEl.querySelectorAll("[data-panel-view]")),
    regions: Array.from(panelEl.querySelectorAll("[data-panel-region]")),
    fields: Array.from(panelEl.querySelectorAll("[data-panel-field]")),
    sections: Array.from(panelEl.querySelectorAll("[data-panel-section]")),
    footerActions: Array.from(panelEl.querySelectorAll("[data-panel-footer-action]")),
    body: panelEl.querySelector("[data-panel-body]"),
    headerTitle: panelEl.querySelector("#taskDetailLabel"),
    headerSubtitle: panelEl.querySelector("#taskDetailCode"),
    historySubtitle: panelEl.querySelector("#taskDetailHistorySubtitle"),
  });

  const clearActiveCards = () => {
    schedule.ui?.dom?.clearActiveCards?.();
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

  const resolveBacklogVariant = () => {
    const backlogState = schedule.backlog?.state;
    if (backlogState?._isBulkMode) {
      return backlogState._bulkConfirmPhase ? "backlog-bulk-confirm" : "backlog-bulk-edit";
    }

    const active = getPanelEl()?.dataset.backlogTab;
    return active === "create" ? "backlog-create" : "backlog-list";
  };

  const resolveVariantName = (variantOrType) => {
    if (VARIANTS[variantOrType]) return variantOrType;
    const resolver = VIEW_ALIASES[variantOrType];
    return resolver ? resolver() : "task-edit";
  };

  const setVisible = (el, visible) => {
    if (!el) return;
    el.style.display = visible ? "" : "none";
  };

  const toggleHiddenClass = (el, hidden) => {
    if (!el) return;
    el.classList.toggle("d-none", hidden);
  };

  const applyHeader = (elements, config) => {
    const { headerTitle, headerSubtitle, historySubtitle } = elements;
    const header = config.header || {};

    if (headerTitle) {
      if (header.title === "task") {
        headerTitle.textContent = headerTitle.dataset.taskTitle || "Užduoties informacija";
      } else if (header.title === "history") {
        headerTitle.textContent = headerTitle.dataset.historyTitle || "Veiksmų istorija";
      } else if (header.titleText) {
        headerTitle.textContent = header.titleText;
      }
    }

    if (headerSubtitle) {
      if (header.subtitle === "code") {
        headerSubtitle.classList.remove("d-none");
      } else {
        headerSubtitle.classList.add("d-none");
      }
    }

    if (historySubtitle) {
      historySubtitle.classList.toggle("d-none", !header.historySubtitle);
    }
  };

  const applyViews = (elements, config) => {
    const visibleViews = new Set(config.views || []);
    elements.views.forEach((el) => {
      const visible = visibleViews.has(el.dataset.panelView);
      setVisible(el, visible);
      toggleHiddenClass(el, !visible);
    });
  };

  const applyRegions = (elements, config) => {
    const regionConfig = config.regions || {};
    elements.regions.forEach((el) => {
      const name = el.dataset.panelRegion;
      const visible = regionConfig[name] === true;
      if (name === "footer") {
        toggleHiddenClass(el, !visible);
        return;
      }
      toggleHiddenClass(el, !visible);
    });
  };

  const applyFieldsAndSections = (elements, config) => {
    const visibleFields = new Set(config.fields || []);
    const visibleSections = new Set(config.sections || []);

    elements.fields.forEach((el) => {
      toggleHiddenClass(el, !visibleFields.has(el.dataset.panelField));
    });

    elements.sections.forEach((el) => {
      toggleHiddenClass(el, !visibleSections.has(el.dataset.panelSection));
    });
  };

  const applyFooter = (elements, config) => {
    const visibleActions = new Set(FOOTER_ACTIONS[config.footer] || []);
    elements.footerActions.forEach((el) => {
      toggleHiddenClass(el, !visibleActions.has(el.dataset.panelFooterAction));
    });
  };

  const resetBodyScroll = (elements, config) => {
    if (!elements?.body) return;
    const visibleViews = new Set(config.views || []);
    if (!visibleViews.has("task-form") && !visibleViews.has("bulk-confirm")) return;
    elements.body.scrollTop = 0;
  };

  const clearVariantSpecificState = (panelEl) => {
    schedule.tasks?.modal?.clearFieldErrors?.(panelEl);
  };

  const activateVariant = (variantOrType) => {
    const panelEl = getPanelEl();
    if (!panelEl) return null;

    const variantName = resolveVariantName(variantOrType);
    const config = VARIANTS[variantName];
    if (!config) return null;

    clearVariantSpecificState(panelEl);

    panelEl.dataset.panelVariant = variantName;
    panelEl.dataset.panelMode = config.panelMode;
    if (variantName === "backlog-create") panelEl.dataset.backlogTab = "create";
    if (variantName === "backlog-list") panelEl.dataset.backlogTab = "list";
    if (config.datasetMode) {
      panelEl.dataset.mode = config.datasetMode;
    } else if (config.panelMode !== "task") {
      delete panelEl.dataset.mode;
    }

    const elements = getVariantElements(panelEl);
    applyViews(elements, config);
    applyRegions(elements, config);
    applyFieldsAndSections(elements, config);
    applyFooter(elements, config);
    applyHeader(elements, config);
    resetBodyScroll(elements, config);

    panelEl.dispatchEvent(new CustomEvent("schedule:panel:mode", {
      detail: { type: config.legacyType, variant: variantName },
    }));
    panelEl.dispatchEvent(new CustomEvent("schedule:panel:variant", {
      detail: { variant: variantName, type: config.legacyType },
    }));

    return variantName;
  };

  const syncCurrentVariant = () => {
    const panelEl = getPanelEl();
    if (!panelEl) return null;
    const activeVariant = panelEl.dataset.panelVariant || resolveVariantName(panelEl.dataset.panelMode || "task");
    return activateVariant(activeVariant);
  };

  const setBacklogTab = (tabName) => {
    const panelEl = getPanelEl();
    if (!panelEl) return;
    panelEl.dataset.backlogTab = tabName === "create" ? "create" : "list";
  };

  const initOnce = () => {
    if (document.documentElement.dataset.schedulePanelInit === "1") return;
    document.documentElement.dataset.schedulePanelInit = "1";

    const detailPanel = getPanelEl();
    const filterTrigger = document.querySelector(".filter-console-trigger");

    if (detailPanel && filterTrigger) {
      const hideTrigger = () => filterTrigger.classList.add("d-none");
      const showTrigger = () => filterTrigger.classList.remove("d-none");
      detailPanel.addEventListener("show.bs.offcanvas", () => selection?.clearSelection?.("panel.open"));
      detailPanel.addEventListener("show.bs.offcanvas", hideTrigger);
      detailPanel.addEventListener("hidden.bs.offcanvas", showTrigger);
      detailPanel.addEventListener("hidden.bs.offcanvas", () => {
        schedule.tasks?.modal?.clearFieldCache?.();
      });
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
        syncCurrentVariant();
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

    activateVariant("task-edit");
    logEvent("ui.panel.init");
  };

  schedule.panel = {
    initOnce,
    show,
    hide,
    closeForNonDailyContext,
    showContent: activateVariant,
    activateVariant,
    syncCurrentVariant,
    getActiveVariant: () => getPanelEl()?.dataset.panelVariant || null,
    setBacklogTab,
  };
})();
