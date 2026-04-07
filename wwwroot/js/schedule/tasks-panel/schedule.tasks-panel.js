/**
 * schedule.tasks-panel.js
 *
 * "Suplanuoti darbai" panel — displays scheduled tasks (TaskEntity records
 * linked to plans) with two filter tabs: Suplanuotos (active) / Pasibaigusios (past).
 *
 * Mirrors the structural patterns of schedule.backlog.js.
 * Dependencies: schedule.panel, schedule.state, schedule.table, schedule.tasks,
 *               schedule.infra.pagination
 */
(() => {
  "use strict";

  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;

  // ── State ─────────────────────────────────────────────────────────────────

  let _items = [];
  let _activeTab = "active";
  let _selectionMode = false;
  let _selectedIds = new Set();
  let _page = 1;
  let _hasMore = false;
  let _scroll = null;
  let _priority = null;

  // ── Priority display helpers (same constants as schedule.backlog.js) ──────

  const PRIORITY = window.ScheduleEnums?.TaskPriority ?? { Low: 0, Medium: 1, High: 2 };
  const PRIORITY_LABELS = {
    [PRIORITY.Low]: "Žemas",
    [PRIORITY.Medium]: "Vidutinis",
    [PRIORITY.High]: "Aukštas",
  };
  const PRIORITY_CLASSES = {
    [PRIORITY.Low]: "bg-secondary",
    [PRIORITY.Medium]: "bg-warning text-dark",
    [PRIORITY.High]: "bg-danger",
  };

  // ── Helpers ───────────────────────────────────────────────────────────────

  function buildStartDateBadge(startAt) {
    if (!startAt) return null;
    const d = new Date(startAt);
    if (isNaN(d.getTime())) return null;
    const label = d.toLocaleDateString("lt-LT");
    const todayLabel = new Date().toLocaleDateString("lt-LT");
    const span = document.createElement("span");
    span.className = "badge";
    if (label === todayLabel) {
      span.classList.add("bg-primary", "text-white");
    } else {
      span.classList.add("bg-light", "text-muted", "border");
    }
    span.textContent = label;
    return span;
  }

  // ── API ───────────────────────────────────────────────────────────────────

  function priorityParam() {
    return _priority !== null ? `&priority=${_priority}` : "";
  }

  async function loadItemsFromApi(tab, page) {
    try {
      const resp = await fetch(`/api/tasks-panel?tab=${encodeURIComponent(tab)}&page=${page}${priorityParam()}`, {
        credentials: "same-origin",
      });
      if (!resp.ok) return null;
      return await resp.json();
    } catch {
      return null;
    }
  }

  // ── Rendering ─────────────────────────────────────────────────────────────

  function formatTimeRange(startAt, endAt) {
    if (!startAt) return "";
    const start = new Date(startAt);
    const end = endAt ? new Date(endAt) : null;
    const fmt = (d) => d.toLocaleTimeString("lt-LT", { hour: "2-digit", minute: "2-digit" });
    return end ? `${fmt(start)} – ${fmt(end)}` : fmt(start);
  }

  function buildCard(item) {
    const tpl = document.getElementById("tasksPanelCardTpl");
    const card = tpl.content.cloneNode(true).firstElementChild;

    card.dataset.tasksPanelId = item.id;
    if (_selectionMode && _selectedIds.has(String(item.id))) card.classList.add("is-selected");

    // Checkbox — CSS (.is-selecting) controls visibility; just sync checked state
    const checkbox = card.querySelector(".backlog-card__checkbox");
    if (checkbox) checkbox.checked = _selectionMode && _selectedIds.has(String(item.id));

    // Title
    const titleEl = card.querySelector(".backlog-card__title");
    if (titleEl) {
      titleEl.setAttribute("data-tp-navigate-id", item.id);
      if (item.title) {
        titleEl.textContent = item.title;
      } else {
        const em = document.createElement("em");
        em.className = "text-muted";
        em.textContent = "Be pavadinimo";
        titleEl.appendChild(em);
      }
    }

    // Priority badge
    const priEl = card.querySelector(".backlog-card__badge-priority");
    if (priEl) {
      if (item.priority != null) {
        priEl.className = `badge ${PRIORITY_CLASSES[item.priority] ?? "bg-secondary"}`;
        priEl.textContent = PRIORITY_LABELS[item.priority] ?? String(item.priority);
      } else {
        priEl.remove();
      }
    }

    // Start date badge
    const startEl = card.querySelector(".backlog-card__badge-startdate");
    if (startEl) {
      const startDom = buildStartDateBadge(item.startAt);
      if (startDom) startEl.replaceWith(startDom); else startEl.remove();
    }

    // Date label
    const dateLabel = item.startAt ? new Date(item.startAt).toLocaleDateString("lt-LT") : "";
    const dateEl = card.querySelector(".backlog-card__date");
    if (dateEl) dateEl.textContent = dateLabel;
    const footerEl = card.querySelector(".backlog-card__footer");
    if (footerEl) footerEl.textContent = dateLabel;

    // Toggle button
    const toggleBtn = card.querySelector(".backlog-card__toggle");
    if (toggleBtn) toggleBtn.setAttribute("data-tp-toggle-id", item.id);

    // Edit / delete buttons — only shown in the active tab
    const editBtn = card.querySelector(".backlog-card__edit");
    const deleteBtn = card.querySelector(".backlog-card__delete");
    if (_activeTab === "active") {
      editBtn?.setAttribute("data-tp-edit-id", item.id);
      deleteBtn?.setAttribute("data-tp-delete-id", item.id);
    } else {
      editBtn?.remove();
      deleteBtn?.remove();
    }

    // Details section
    const detailsEl = card.querySelector(".backlog-card__details");
    if (detailsEl) {
      detailsEl.id = `tp-details-${item.id}`;

      if (item.technicianName) {
        const b = detailsEl.querySelector(".backlog-card__detail--technician");
        if (b) {
          b.removeAttribute("hidden");
          const v = b.querySelector(".backlog-card__technician-value");
          if (v) v.textContent = item.technicianName;
        }
      }
      if (item.objectName) {
        const b = detailsEl.querySelector(".backlog-card__detail--object");
        if (b) {
          b.removeAttribute("hidden");
          const v = b.querySelector(".backlog-card__object-value");
          if (v) v.textContent = item.objectName;
        }
      }
      if (item.startAt) {
        const b = detailsEl.querySelector(".backlog-card__detail--time");
        if (b) {
          b.removeAttribute("hidden");
          const v = b.querySelector(".backlog-card__time-value");
          if (v) v.textContent = formatTimeRange(item.startAt, item.endAt);
        }
      }
    }

    return card;
  }

  function renderList() {
    const listEl = document.getElementById("tasksPanelItemsList");
    const emptyEl = document.getElementById("tasksPanelListEmpty");
    if (!listEl) return;

    listEl.innerHTML = "";

    if (_items.length === 0) {
      emptyEl?.classList.remove("d-none");
      return;
    }
    emptyEl?.classList.add("d-none");

    const frag = document.createDocumentFragment();
    _items.forEach((item) => frag.appendChild(buildCard(item)));
    listEl.appendChild(frag);
  }

  function appendCards(newItems) {
    const listEl = document.getElementById("tasksPanelItemsList");
    const emptyEl = document.getElementById("tasksPanelListEmpty");
    if (!listEl) return;

    if (newItems.length === 0) return;

    emptyEl?.classList.add("d-none");

    const frag = document.createDocumentFragment();
    newItems.forEach((item) => frag.appendChild(buildCard(item)));
    listEl.appendChild(frag);
  }

  // ── Selection mode ────────────────────────────────────────────────────────

  function exitSelectionMode() {
    if (!_selectionMode) return;
    _selectionMode = false;
    _selectedIds.clear();
    document.getElementById("tasksPanelContent")?.classList.remove("is-selecting");
    if (_hasMore) setupScroll();
  }

  function enterSelectionMode() {
    _selectionMode = true;
    _selectedIds.clear();
    _scroll?.disconnect();
    document.getElementById("tasksPanelContent")?.classList.add("is-selecting");
    updateBulkBar();
  }

  function updateBulkBar() {
    const countEl = document.getElementById("tasksPanelBulkCount");
    const deleteBtn = document.getElementById("tasksPanelBulkDelete");
    const selectAllCb = document.getElementById("tasksPanelSelectAll");
    const n = _selectedIds.size;
    const total = _items.length;

    if (countEl) countEl.textContent = n === 0 ? "Nepasirinkta" : `${n} / ${total} pasirinkta`;
    if (deleteBtn) deleteBtn.disabled = n === 0;
    if (selectAllCb) {
      selectAllCb.checked = n > 0 && n >= total;
      selectAllCb.indeterminate = n > 0 && n < total;
    }
  }

  // ── Infinite scroll ───────────────────────────────────────────────────────

  function setupScroll() {
    const sentinelEl = document.getElementById("tasksPanelScrollSentinel");
    if (!sentinelEl || !schedule.infra?.pagination) return;

    _scroll?.disconnect();
    _scroll = schedule.infra.pagination.createInfiniteScroll({
      sentinelEl,
      onLoadMore: async () => {
        if (!_hasMore) return false;
        _page++;
        const data = await loadItemsFromApi(_activeTab, _page);
        if (!data) return false;
        const newItems = data.items ?? [];
        _items = _items.concat(newItems);
        _hasMore = data.hasMore ?? false;
        appendCards(newItems);
        return _hasMore;
      },
    });
    _scroll.observe();
  }

  // ── Tab counts ────────────────────────────────────────────────────────────

  function setTabCount(tab, count) {
    const activeCount = document.getElementById("tasksPanelCountActive");
    const pastCount = document.getElementById("tasksPanelCountPast");
    if (tab === "active" && activeCount) activeCount.textContent = count;
    if (tab === "past" && pastCount) pastCount.textContent = count;
  }

  // ── Panel open — fetch both tab counts at once ────────────────────────────

  async function openPanel() {
    exitSelectionMode();
    _scroll?.disconnect();
    _page = 1;
    _hasMore = false;
    _items = [];

    const activeBtn = document.getElementById("tasksPanelTabActive");
    const pastBtn = document.getElementById("tasksPanelTabPast");
    activeBtn?.classList.toggle("active", _activeTab === "active");
    pastBtn?.classList.toggle("active", _activeTab === "past");

    const [activeData, pastData] = await Promise.all([
      loadItemsFromApi("active", 1),
      loadItemsFromApi("past", 1),
    ]);

    setTabCount("active", activeData?.totalCount ?? 0);
    setTabCount("past", pastData?.totalCount ?? 0);

    const data = _activeTab === "active" ? activeData : pastData;
    if (data) {
      _items = data.items ?? [];
      _hasMore = data.hasMore ?? false;
    }

    renderList();
    setupScroll();
  }

  // ── Tab switching ─────────────────────────────────────────────────────────

  function resetPriorityFilter() {
    _priority = null;
    document.querySelectorAll("#tasksPanelPriorityFilter [data-tp-priority]").forEach((b) => {
      b.classList.toggle("active", b.dataset.tpPriority === "");
    });
  }

  async function switchTab(tab) {
    _activeTab = tab;
    exitSelectionMode();
    resetPriorityFilter();

    const activeBtn = document.getElementById("tasksPanelTabActive");
    const pastBtn = document.getElementById("tasksPanelTabPast");
    activeBtn?.classList.toggle("active", tab === "active");
    pastBtn?.classList.toggle("active", tab === "past");

    _scroll?.disconnect();
    _page = 1;
    _hasMore = false;
    _items = [];

    const data = await loadItemsFromApi(tab, 1);
    if (data) {
      _items = data.items ?? [];
      _hasMore = data.hasMore ?? false;
      setTabCount(tab, data.totalCount ?? _items.length);
    }

    renderList();
    setupScroll();
  }

  // ── Navigation: click card title → navigate calendar to task date ─────────

  async function navigateToTask(itemId) {
    const item = _items.find((i) => String(i.id) === String(itemId));
    if (!item?.startAt) return;

    const dateStr = item.startAt.substring(0, 10);

    const stateModule = schedule.state;
    const tableModule = schedule.table;
    if (!stateModule || !tableModule) return;

    const query = stateModule.getQuery();
    query.set("view", "day");
    query.set("date", dateStr);

    const ok = await tableModule.fetchAndRender(query, { updateUrl: true });
    if (!ok) return;

    await schedule.tasks?.openTaskPanel?.({ taskId: String(item.id) });
  }

  // ── Bulk delete ───────────────────────────────────────────────────────────

  async function handleBulkDelete() {
    if (_selectedIds.size === 0) return;
    const ids = [..._selectedIds].map(Number);

    try {
      const resp = await fetch("/api/tasks-panel/bulk-delete", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(ids),
      });
      if (!resp.ok) throw new Error("delete-failed");
      const data = await resp.json();
      _items = _items.filter((i) => !ids.includes(Number(i.id)));
      schedule.ui?.toast?.showSuccess?.(`${data.deleted} užduotis(-ys) ištrinta(-os).`);
    } catch {
      schedule.ui?.toast?.showError?.("Nepavyko ištrinti užduočių.");
    }

    exitSelectionMode();
    renderList();

    // Refresh the calendar to reflect deleted tasks
    const query = schedule.state?.getQuery?.();
    if (query) {
      schedule.table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
    }
  }

  // ── Open panel ────────────────────────────────────────────────────────────

  function openTasksPanel() {
    const schedulePanel = schedule.panel;
    if (!schedulePanel) return;

    const detailPanel = document.getElementById("taskDetailModal");
    if (detailPanel?.classList?.contains("is-collapsed")) {
      detailPanel.classList.remove("is-collapsed");
      detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
    }

    schedulePanel.showContent("tasks-panel");
    schedulePanel.show();
  }

  // ── Init ──────────────────────────────────────────────────────────────────

  function init() {
    const panel = document.getElementById("taskDetailModal");

    // Tab buttons
    document.getElementById("tasksPanelTabActive")?.addEventListener("click", () => switchTab("active"));
    document.getElementById("tasksPanelTabPast")?.addEventListener("click", () => switchTab("past"));

    // Priority filter
    document.getElementById("tasksPanelPriorityFilter")?.addEventListener("click", async (e) => {
      const btn = e.target.closest("[data-tp-priority]");
      if (!btn) return;
      const val = btn.dataset.tpPriority;
      _priority = val === "" ? null : Number(val);
      document.querySelectorAll("#tasksPanelPriorityFilter [data-tp-priority]").forEach((b) => {
        b.classList.toggle("active", b === btn);
      });
      _scroll?.disconnect();
      _page = 1;
      _items = [];
      const data = await loadItemsFromApi(_activeTab, 1);
      if (data) {
        _items = data.items ?? [];
        _hasMore = data.hasMore ?? false;
        setTabCount(_activeTab, data.totalCount ?? 0);
      }
      renderList();
      setupScroll();
    });

    // Enter selection mode
    document.getElementById("tasksPanelSelectToggle")?.addEventListener("click", () => {
      enterSelectionMode();
      renderList();
    });

    // Cancel selection mode
    document.getElementById("tasksPanelSelectCancel")?.addEventListener("click", () => {
      exitSelectionMode();
      renderList();
    });

    // Select all checkbox
    document.getElementById("tasksPanelSelectAll")?.addEventListener("change", (e) => {
      if (e.target.checked) {
        _items.forEach((i) => _selectedIds.add(String(i.id)));
      } else {
        _selectedIds.clear();
      }
      renderList();
      updateBulkBar();
    });

    // Bulk delete
    document.getElementById("tasksPanelBulkDelete")?.addEventListener("click", handleBulkDelete);

    // Delegated events on list container
    const listEl = document.getElementById("tasksPanelItemsList");

    listEl?.addEventListener("click", async (e) => {
      // Chevron toggle → expand/collapse details
      const toggleBtn = e.target.closest("[data-tp-toggle-id]");
      if (toggleBtn) {
        e.stopPropagation();
        const id = toggleBtn.dataset.tpToggleId;
        const details = document.getElementById("tp-details-" + id);
        if (!details) return;
        const expanded = toggleBtn.getAttribute("aria-expanded") === "true";
        toggleBtn.setAttribute("aria-expanded", String(!expanded));
        details.hidden = expanded;
        toggleBtn.closest(".backlog-card")?.classList.toggle("is-expanded", !expanded);
        const icon = toggleBtn.querySelector("i");
        icon?.classList.toggle("bi-chevron-right", expanded);
        icon?.classList.toggle("bi-chevron-down", !expanded);
        return;
      }

      // Card title click → navigate calendar to task date
      if (!_selectionMode) {
        const titleEl = e.target.closest("[data-tp-navigate-id]");
        if (titleEl) {
          e.stopPropagation();
          await navigateToTask(titleEl.dataset.tpNavigateId);
          return;
        }
      }

      // Edit button
      const editBtn = e.target.closest("[data-tp-edit-id]");
      if (editBtn) {
        e.stopPropagation();
        const taskId = editBtn.dataset.tpEditId;
        schedule.panel?.hide?.();
        schedule.tasks?.openTaskPanel?.({ taskId });
        return;
      }

      // Delete button (single) — reuse existing delete modal
      const deleteBtn = e.target.closest("[data-tp-delete-id]");
      if (deleteBtn) {
        e.stopPropagation();
        const taskId = deleteBtn.dataset.tpDeleteId;
        const item = _items.find((i) => String(i.id) === String(taskId));
        const version = item?.version ?? 0;
        const info = item ? {
          title: item.title || "",
          timeRange: formatTimeRange(item.startAt, item.endAt),
          location: item.objectName || "",
        } : null;
        schedule.tasks?.delete?.openDeleteModal?.({ taskId, version, info, sourceEl: null });
        return;
      }

      // Card click in selection mode — toggle selection
      if (_selectionMode) {
        const card = e.target.closest(".backlog-card");
        if (!card || e.target.closest("button")) return;
        const id = String(card.dataset.tasksPanelId ?? "");
        if (!id) return;
        if (_selectedIds.has(id)) {
          _selectedIds.delete(id);
          card.classList.remove("is-selected");
          const cb = card.querySelector(".backlog-card__checkbox");
          if (cb) cb.checked = false;
        } else {
          _selectedIds.add(id);
          card.classList.add("is-selected");
          const cb = card.querySelector(".backlog-card__checkbox");
          if (cb) cb.checked = true;
        }
        updateBulkBar();
      }
    });

    // React to panel mode change — load data when this panel becomes active
    panel?.addEventListener("schedule:panel:mode", (e) => {
      if (e.detail?.type === "tasks-panel") {
        openPanel();
      }
    });
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  schedule.tasksPanel = { openTasksPanel, init };

  init();
})();
