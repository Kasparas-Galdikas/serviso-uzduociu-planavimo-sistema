(() => {
  "use strict";

  window.schedule = window.schedule || {};
  const backlog = window.schedule.backlog = window.schedule.backlog || {};

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const ITEM_TYPE = window.ScheduleEnums?.BacklogItemType ?? { Temporary: 0, Permanent: 1 };
  const PRIORITY = window.ScheduleEnums?.TaskPriority ?? { Low: 0, Medium: 1, High: 2 };
  const PRIORITY_LABELS = {
    [PRIORITY.Low]: "Zemas",
    [PRIORITY.Medium]: "Vidutinis",
    [PRIORITY.High]: "Aukstas",
  };
  const PRIORITY_CLASSES = {
    [PRIORITY.Low]: "bg-secondary",
    [PRIORITY.Medium]: "bg-warning text-dark",
    [PRIORITY.High]: "bg-danger",
  };
  const SKILL_LABELS = {
    Basic: "Bazinis",
    Advanced: "Pazenges",
    Certified: "Sertifikuotas",
  };

  const state = backlog.state = backlog.state || {
    ITEM_TYPE,
    PRIORITY,
    PRIORITY_LABELS,
    PRIORITY_CLASSES,
    SKILL_LABELS,
    logEvent,
    _items: [],
    _localNow: null,
    _activeTypeTab: ITEM_TYPE.Permanent,
    _selectionMode: false,
    _selectedIds: new Set(),
    _selectedOrder: [],
    _page: 1,
    _hasMore: false,
    _scroll: null,
    _totalCounts: { [ITEM_TYPE.Temporary]: 0, [ITEM_TYPE.Permanent]: 0 },
    _priority: null,
    _bulkMaterializeSyncToken: 0,
    _draggingBacklogId: null,
    editingBacklogId: null,
    _schedulingFromBacklogId: null,
    _schedulingFromBacklogItemType: null,
    _bulkQueue: [],
    _bulkQueueIdx: 0,
    _isBulkMode: false,
    _bulkPlanMode: "rankinis",
    _bulkScheduledData: [],
    _bulkConfirmPhase: false,
    _bulkConfirmStatuses: [],
    _bulkPreviewRefreshTimer: null,
    _bulkAutoPlanTimer: null,
    _bulkAutoPersistTimer: null,
    _bulkMaterializeSyncTimer: null,
    _bulkSavePromise: null,
    _bulkSaveQueued: false,
    _bulkQueuedSaveOptions: null,
    _bulkNavInFlight: false,
    _bulkTransitionInFlight: false,
    _restoringSnapshot: false,
    _autoPlanningInProgress: false,
    _bulkAutoPlanInFlight: null,
    _bulkAutoPlanPromise: null,
    _bulkPendingPersistRequest: null,
    _bulkAutoPlanRequestSeq: 0,
    _bulkPrecomputedSuggestions: null,
    _bulkPrecomputePromise: null,
    _bulkPrecomputeAbortCtrl: null,
    _bulkDraftPlanId: null,
    _bulkDraftPeriodStart: "",
    _bulkDraftPeriodEnd: "",
  };

  state.getPanelEl = () => document.getElementById("taskDetailModal");

  state.formatLocalInputValue = (date) => {
    const pad = (n) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  };

  state.getRoundedLocalNowRange = () => {
    if (!state._localNow) return "";
    const slotSize = window.schedule?.grid?.getConfig?.().slotSizeMinutes ?? 30;
    const start = new Date(state._localNow);
    if (isNaN(start)) return "";

    start.setSeconds(0, 0);
    const remainder = start.getMinutes() % slotSize;
    if (remainder !== 0) {
      start.setMinutes(start.getMinutes() + (slotSize - remainder));
    }

    const end = new Date(start);
    end.setMinutes(end.getMinutes() + slotSize);

    return {
      start: state.formatLocalInputValue(start),
      end: state.formatLocalInputValue(end),
    };
  };

  state.getActiveScheduleDate = () => {
    const query = window.schedule?.state?.getQuery?.();
    const rawDate = query?.get?.("date") || "";
    if (/^\d{4}-\d{2}-\d{2}$/.test(rawDate)) return rawDate;

    const now = new Date();
    const pad = (n) => String(n).padStart(2, "0");
    return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  };

  state.getEarliestCreatableRangeForScheduleDate = (requestedDate = state.getActiveScheduleDate()) => {
    const grid = window.schedule?.grid;
    const serverPlacement = grid?.getEarliestCreatablePlacement?.();
    if (serverPlacement) {
      return {
        targetDate: serverPlacement.targetDate,
        startMinutes: serverPlacement.startMinutes,
        endMinutes: serverPlacement.endMinutes,
        start: state.toLocalDateTimeValue(serverPlacement.targetDate, serverPlacement.startMinutes),
        end: state.toLocalDateTimeValue(serverPlacement.targetDate, serverPlacement.endMinutes),
      };
    }

    const config = grid?.getConfig?.() || {
      slotSizeMinutes: 30,
      dayStartMinutes: 8 * 60,
    };
    const targetDate = /^\d{4}-\d{2}-\d{2}$/.test(requestedDate || "") ? requestedDate : state.getActiveScheduleDate();
    const startMinutes = config.dayStartMinutes || 8 * 60;
    const endMinutes = startMinutes + (config.slotSizeMinutes || 30);

    return {
      targetDate,
      startMinutes,
      endMinutes,
      start: state.toLocalDateTimeValue(targetDate, startMinutes),
      end: state.toLocalDateTimeValue(targetDate, endMinutes),
    };
  };

  state.priorityParam = () => state._priority !== null ? `&priority=${state._priority}` : "";

  state.escapeHtml = (str) => {
    if (!str) return "";
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  };

  state.buildDueDateBadge = (dueDate) => {
    if (!dueDate) return null;
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const due = new Date(`${dueDate}T00:00:00`);
    due.setHours(0, 0, 0, 0);
    const diffDays = Math.round((due - today) / 86400000);
    const label = due.toLocaleDateString("lt-LT");
    const span = document.createElement("span");
    span.className = "badge";
    if (diffDays < 0) {
      span.classList.add("bg-danger", "text-white");
      span.textContent = `Veluoja ${Math.abs(diffDays)}d.`;
    } else if (diffDays <= 3) {
      span.classList.add("bg-warning", "text-dark");
      span.textContent = `Iki ${label}`;
    } else {
      span.classList.add("bg-light", "text-muted", "border");
      span.textContent = `Iki ${label}`;
    }
    return span;
  };

  state.normalizeDateTimeLocalValue = (value) => {
    if (!value) return "";
    const trimmed = String(value).trim();
    if (!trimmed) return "";
    if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(trimmed)) return trimmed;
    const date = new Date(trimmed);
    if (Number.isNaN(date.getTime())) return "";
    const pad = (n) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  };

  state.parseDatetimeComponents = (val) => {
    const normalized = state.normalizeDateTimeLocalValue(val);
    if (!normalized || !normalized.includes("T")) return null;
    const [datePart, timePart] = normalized.split("T");
    const [h, m] = timePart.split(":").map(Number);
    if (Number.isNaN(h) || Number.isNaN(m)) return null;
    return { date: datePart, minutes: h * 60 + m };
  };

  state.toLocalDateTimeValue = (date, minutes) =>
    `${date}T${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;

  state.setBulkDraftContext = ({ draftPlanId = null, periodStart = "", periodEnd = "" } = {}) => {
    state._bulkDraftPlanId = Number.isFinite(Number(draftPlanId)) ? Number(draftPlanId) : null;
    state._bulkDraftPeriodStart = /^\d{4}-\d{2}-\d{2}$/.test(periodStart || "") ? periodStart : "";
    state._bulkDraftPeriodEnd = /^\d{4}-\d{2}-\d{2}$/.test(periodEnd || "") ? periodEnd : "";
  };

  state.clearBulkDraftContext = () => state.setBulkDraftContext({});

  state.getBulkDraftContext = () => ({
    draftPlanId: state._bulkDraftPlanId,
    periodStart: state._bulkDraftPeriodStart,
    periodEnd: state._bulkDraftPeriodEnd,
  });

  state.isBulkDraftRangeCovered = (startDate, endDate = startDate) => {
    const normalizedStart = /^\d{4}-\d{2}-\d{2}$/.test(startDate || "") ? startDate : "";
    const normalizedEnd = /^\d{4}-\d{2}-\d{2}$/.test(endDate || "") ? endDate : "";
    if (!normalizedStart || !normalizedEnd) return false;
    if (!state._bulkDraftPeriodStart || !state._bulkDraftPeriodEnd) return false;
    return normalizedStart >= state._bulkDraftPeriodStart && normalizedEnd <= state._bulkDraftPeriodEnd;
  };

  state.clearSchedulingContext = () => {
    state._schedulingFromBacklogId = null;
    state._schedulingFromBacklogItemType = null;
  };

  state.clearSelection = () => {
    state._selectedIds.clear();
    state._selectedOrder = [];
  };

  state.selectBacklogItem = (id) => {
    const normalizedId = String(id ?? "");
    if (!normalizedId) return false;
    if (!state._selectedIds.has(normalizedId)) {
      state._selectedIds.add(normalizedId);
      state._selectedOrder.push(normalizedId);
      return true;
    }
    return false;
  };

  state.unselectBacklogItem = (id) => {
    const normalizedId = String(id ?? "");
    if (!normalizedId) return false;
    const hadItem = state._selectedIds.delete(normalizedId);
    if (hadItem) {
      state._selectedOrder = state._selectedOrder.filter((entry) => entry !== normalizedId);
    }
    return hadItem;
  };

  state.replaceSelectionOrder = (ids = []) => {
    const normalized = [];
    const seen = new Set();
    (ids || []).forEach((id) => {
      const normalizedId = String(id ?? "");
      if (!normalizedId || seen.has(normalizedId)) return;
      seen.add(normalizedId);
      normalized.push(normalizedId);
    });
    state._selectedIds = new Set(normalized);
    state._selectedOrder = normalized;
  };

  state.getSelectedIdsInOrder = () => {
    const ordered = state._selectedOrder.filter((id) => state._selectedIds.has(id));
    if (ordered.length === state._selectedIds.size) return ordered.slice();
    return [...state._selectedIds].map((id) => String(id));
  };

  state.getDefaultBulkSaveOptions = () => ({
    refreshSchedule: true,
    silent: true,
  });

  state.mergeBulkSaveOptions = (next = {}) => {
    const base = state._bulkQueuedSaveOptions || {};
    state._bulkQueuedSaveOptions = {
      refreshSchedule: base.refreshSchedule === true || next.refreshSchedule === true,
      silent: base.silent !== false && next.silent !== false,
    };
  };

  state.isBulkOperationBusy = () =>
    state._bulkTransitionInFlight || state._bulkNavInFlight || !!state._bulkSavePromise || !!state._bulkAutoPlanInFlight;

  state.applyDraftBulkFooterState = ({ confirmPhase = state._bulkConfirmPhase, busy = false } = {}) => {
    const panel = state.getPanelEl();
    if (!panel) return;

    panel.dataset.bulkMode = "true";

    const backBtn = document.getElementById("taskBulkBack");
    const nextBtn = document.getElementById("taskBulkNext");
    if (backBtn) {
      const isFirstQueueItem = !confirmPhase && state._bulkQueueIdx === 0;
      backBtn.disabled = busy;
      backBtn.innerHTML = isFirstQueueItem
        ? "Atšaukti"
        : '<i class="bi bi-arrow-left me-1" aria-hidden="true"></i>Atgal';
    }
    if (nextBtn) {
      const hasConfirmErrors = confirmPhase && Array.isArray(state._bulkConfirmStatuses)
        ? state._bulkConfirmStatuses.some((item) => item?.status === "error")
        : false;
      nextBtn.disabled = confirmPhase ? (busy || hasConfirmErrors) : false;
      const isLastItem = !confirmPhase && state._bulkQueue.length > 0
        && state._bulkQueueIdx === state._bulkQueue.length - 1;
      nextBtn.innerHTML = confirmPhase
        ? "Patvirtinti"
        : isLastItem
          ? 'Baigti <i class="bi bi-arrow-right ms-1" aria-hidden="true"></i>'
          : 'Kitas <i class="bi bi-arrow-right ms-1" aria-hidden="true"></i>';
    }
  };

  state.resetBulkState = () => {
    state._bulkQueue = [];
    state._bulkQueueIdx = 0;
    state._isBulkMode = false;
    state._bulkPlanMode = "rankinis";
    state._bulkScheduledData = [];
    state._bulkConfirmPhase = false;
    state._bulkConfirmStatuses = [];
    if (state._bulkPreviewRefreshTimer) {
      clearTimeout(state._bulkPreviewRefreshTimer);
      state._bulkPreviewRefreshTimer = null;
    }
    if (state._bulkAutoPlanTimer) {
      clearTimeout(state._bulkAutoPlanTimer);
      state._bulkAutoPlanTimer = null;
    }
    if (state._bulkAutoPersistTimer) {
      clearTimeout(state._bulkAutoPersistTimer);
      state._bulkAutoPersistTimer = null;
    }
    if (state._bulkMaterializeSyncTimer) {
      clearTimeout(state._bulkMaterializeSyncTimer);
      state._bulkMaterializeSyncTimer = null;
    }
    state._bulkSavePromise = null;
    state._bulkSaveQueued = false;
    state._bulkQueuedSaveOptions = null;
    state._bulkNavInFlight = false;
    state._bulkTransitionInFlight = false;
    state._bulkAutoPlanInFlight = null;
    state._bulkAutoPlanPromise = null;
    state._bulkPendingPersistRequest = null;
    state._bulkAutoPlanRequestSeq = 0;
    state._bulkPrecomputedSuggestions = null;
    state._bulkPrecomputePromise = null;
    if (state._bulkPrecomputeAbortCtrl) {
      state._bulkPrecomputeAbortCtrl.abort();
      state._bulkPrecomputeAbortCtrl = null;
    }
    state.clearBulkDraftContext();
    const panel = state.getPanelEl();
    if (!panel) return;
    delete panel.dataset.bulkMode;
    const planModeEl = panel.querySelector("#taskPlanMode");
    if (planModeEl) planModeEl.value = "";
    panel.querySelectorAll("input[name='taskPlanModeRadio']").forEach((r) => { r.checked = false; });
    document.getElementById("taskBulkBack")?.removeAttribute("disabled");
    document.getElementById("taskBulkNext")?.removeAttribute("disabled");
    if ((window.schedule?.panel?.getActiveVariant?.() || "").startsWith("backlog-bulk")) {
      window.schedule?.panel?.activateVariant?.("backlog-create");
    }
  };
})();
