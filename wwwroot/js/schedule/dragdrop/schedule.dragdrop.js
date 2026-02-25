(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const tasks = schedule.tasks;
  const selection = schedule.selection;
  const refresh = schedule.refresh;

  // Import from shared modules
  const grid = schedule.grid;
  const dom = schedule.ui?.dom;
  const taskApi = schedule.tasks?.api;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const isEditModeEnabled = () => {
    const toggles = document.querySelectorAll("[data-edit-toggle]");
    if (!toggles.length) return false;
    return Array.from(toggles).some((t) => t.checked);
  };

  const isDayView = () => (state?.getView?.() || "day") === "day";

  const dragState = {
    taskId: null,
    version: null,
    durationSlots: null,
    travelSlots: 0,
    baseTravelSlots: 0,
    lastPreviewOk: false,
    busy: false,
    originTd: null,
    originStartMinutes: null,
    previewData: null,
    previewDebounceTimer: null,
    lastPreviewSlot: null,
    lastPreviewMinutes: null,
    lastPreviewRow: null,
    highlightedCells: [],
    highlightedTravelCells: [],
    scroll: {
      container: null,
      rafId: null,
      velocityY: 0,
      lastClientY: null,
    },
  };

  const clearPreview = () => {
    // First clear tracked cells (fast path)
    for (const td of dragState.highlightedCells) {
      td.classList.remove("is-drop-range", "is-drop-range-invalid");
    }
    dragState.highlightedCells = [];

    // Fallback: clear any stray cells that might have been missed
    document.querySelectorAll("td.is-drop-range, td.is-drop-range-invalid").forEach((td) => {
      td.classList.remove("is-drop-range", "is-drop-range-invalid");
    });
  };

  const clearTravelPreview = () => {
    for (const td of dragState.highlightedTravelCells) {
      td.classList.remove("is-drop-travel", "is-drop-travel-invalid", "is-drop-travel-start");
    }
    dragState.highlightedTravelCells = [];

    document
      .querySelectorAll("td.is-drop-travel, td.is-drop-travel-invalid, td.is-drop-travel-start")
      .forEach((td) => td.classList.remove("is-drop-travel", "is-drop-travel-invalid", "is-drop-travel-start"));
  };

  const clearPreviewColors = () => {
    document.querySelectorAll(".task-card.preview-normal, .task-card.preview-load, .task-card.preview-conflict").forEach((card) => {
      card.classList.remove("preview-normal");
      card.classList.remove("preview-load");
      card.classList.remove("preview-conflict");
    });
  };

  const applyPreviewCardColors = (previewData) => {
    if (!previewData || !previewData.previewStates) {
      logEvent("ui.drag.preview.apply", { status: "no-data" }, "warn");
      return;
    }

    clearPreviewColors();

    const stateMap = {};
    previewData.previewStates.forEach((state) => {
      stateMap[String(state.technicianId)] = state.loadState;
      logEvent("ui.drag.preview.state", {
        technicianId: state.technicianId,
        loadState: state.loadState,
        hasConflict: state.hasConflict,
        dailyLimitExceeded: state.isDailyLimitExceeded,
        weeklyLimitExceeded: state.isWeeklyLimitExceeded,
        switchLimitExceeded: state.isSwitchLimitExceeded,
        dailyHours: state.dailyHours,
        dailyLimit: state.dailyLimit,
        dailyUtil: `${(state.dailyUtilization * 100).toFixed(0)}%`,
        weeklyHours: state.weeklyHours,
        weeklyLimit: state.weeklyLimit,
        weeklyUtil: `${(state.weeklyUtilization * 100).toFixed(0)}%`,
        switches: state.switchCount
      });
    });

    let appliedCount = 0;
    document.querySelectorAll(".task-card").forEach((card) => {
      const techId = card.getAttribute("data-tech-id");
      if (!techId) return;

      const loadState = stateMap[techId];
      if (!loadState) return;

      const cssClass = loadState === "Conflict"
        ? "preview-conflict"
        : loadState === "Load"
        ? "preview-load"
        : "preview-normal";

      card.classList.add(cssClass);
      appliedCount++;
    });

    logEvent("ui.drag.preview.apply", {
      totalStates: previewData.previewStates.length,
      appliedCount: appliedCount,
      states: previewData.previewStates.map(s => `tech${s.technicianId}:${s.loadState}`).join(", ")
    });
  };

  const fetchDragPreview = async (slot) => {
    if (!slot || !dragState.taskId) return;

    const technicianId = parseIntSafe(slot.dataset.techId);
    const startMinutes = parseIntSafe(slot.dataset.slotStartMinutes);
    const targetDate = slot.dataset.date || "";

    if (!technicianId || startMinutes == null || !targetDate) return;

    // Use task.api module if available
    if (taskApi?.fetchDragPreview) {
      const result = await taskApi.fetchDragPreview({
        taskId: parseIntSafe(dragState.taskId),
        technicianId,
        targetDate,
        startMinutes,
        durationSlots: dragState.durationSlots || 1,
      });

      if (result.success && result.previewStates) {
        dragState.previewData = { previewStates: result.previewStates };
        applyPreviewCardColors(dragState.previewData);

        const state = result.previewStates.find((s) => Number(s.technicianId) === Number(technicianId));
        if (state && Number.isFinite(state.requiredInboundTravelMinutes)) {
          dragState.travelSlots = Math.max(1, Math.ceil(Number(state.requiredInboundTravelMinutes) / 30));
        } else {
          dragState.travelSlots = dragState.baseTravelSlots || 0;
        }

        const row = slot.closest("tr");
        if (row) {
          applyTravelPreviewRangeByMinutes(
            row,
            startMinutes,
            dragState.travelSlots || 0,
            dragState.taskId,
            technicianId,
            targetDate
          );
        }
      }
      return;
    }

    // Fallback: direct fetch
    const timezoneOffset = -new Date().getTimezoneOffset();
    const durationMinutes = (dragState.durationSlots || 1) * 30;

    const localDate = new Date(targetDate + "T00:00:00");
    const targetStartAt = new Date(localDate.getTime() + startMinutes * 60000 + timezoneOffset * 60000);
    const targetEndAt = new Date(targetStartAt.getTime() + durationMinutes * 60000);

    try {
      const body = JSON.stringify({
        taskId: parseIntSafe(dragState.taskId),
        targetTechnicianId: technicianId,
        targetStartAt: targetStartAt.toISOString(),
        targetEndAt: targetEndAt.toISOString(),
      });

      const resp =
        (await logger?.fetchWithTrace?.(
          "/api/tasks/preview/drag",
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body,
          },
          { event: "ui.drag.preview", taskId: dragState.taskId }
        )) ||
        (await fetch("/api/tasks/preview/drag", {
          method: "POST",
          headers: {
            "X-Requested-With": "XMLHttpRequest",
            "Content-Type": "application/json",
          },
          body,
        }));

      if (resp.ok) {
        const data = await resp.json();
        dragState.previewData = data;
        applyPreviewCardColors(data);

        const states = Array.isArray(data?.previewStates) ? data.previewStates : [];
        const state = states.find((s) => Number(s.technicianId) === Number(technicianId));
        if (state && Number.isFinite(state.requiredInboundTravelMinutes)) {
          dragState.travelSlots = Math.max(1, Math.ceil(Number(state.requiredInboundTravelMinutes) / 30));
        } else {
          dragState.travelSlots = dragState.baseTravelSlots || 0;
        }

        const row = slot.closest("tr");
        if (row) {
          applyTravelPreviewRangeByMinutes(
            row,
            startMinutes,
            dragState.travelSlots || 0,
            dragState.taskId,
            technicianId,
            targetDate
          );
        }
      } else {
        logEvent("ui.drag.preview.error", { status: resp.status }, "warn");
      }
    } catch (err) {
      logEvent("ui.drag.preview.error", { error: err?.message }, "error");
    }
  };

  const scheduleDragPreview = (slot) => {
    if (!slot || dragState.busy) return;

    if (dragState.lastPreviewSlot === slot) return;
    dragState.lastPreviewSlot = slot;

    if (dragState.previewDebounceTimer) {
      clearTimeout(dragState.previewDebounceTimer);
    }

    dragState.previewDebounceTimer = setTimeout(() => {
      fetchDragPreview(slot);
      dragState.previewDebounceTimer = null;
    }, 150);
  };

  const getScrollContainer = () =>
    tables.querySelector(".table-responsive") || tables.closest(".table-responsive") || null;

  const onWheelDuringDrag = (evt) => {
    if (!dragState.taskId || dragState.busy) return;
    const container = dragState.scroll.container || getScrollContainer();
    if (!container) return;

    // Enable natural mouse wheel / trackpad scrolling while HTML5 dragging.
    // Browsers often block default scroll during drag; we apply it manually.
    const dy = Number(evt.deltaY || 0);
    const dx = Number(evt.deltaX || 0);
    if (!dy && !dx) return;

    dragState.scroll.container = container;
    if (dy) container.scrollTop += dy;
    if (dx) container.scrollLeft += dx;

    evt.preventDefault();
  };

  const stopAutoScroll = () => {
    if (dragState.scroll.rafId) {
      cancelAnimationFrame(dragState.scroll.rafId);
    }
    dragState.scroll.rafId = null;
    dragState.scroll.velocityY = 0;
    dragState.scroll.lastClientY = null;
    dragState.scroll.container = null;
  };

  const tickAutoScroll = () => {
    const container = dragState.scroll.container;
    const vy = dragState.scroll.velocityY || 0;
    if (!container || vy === 0) {
      dragState.scroll.rafId = null;
      return;
    }

    container.scrollTop += vy;
    dragState.scroll.rafId = requestAnimationFrame(tickAutoScroll);
  };

  const updateAutoScroll = (clientY) => {
    const container = dragState.scroll.container;
    if (!container) return;

    const rect = container.getBoundingClientRect();
    const threshold = 56; // px from edge
    const maxSpeed = 22; // px per frame

    const distTop = clientY - rect.top;
    const distBottom = rect.bottom - clientY;

    let velocityY = 0;
    if (distTop >= 0 && distTop < threshold) {
      const t = 1 - distTop / threshold;
      velocityY = -Math.max(2, Math.round(maxSpeed * t));
    } else if (distBottom >= 0 && distBottom < threshold) {
      const t = 1 - distBottom / threshold;
      velocityY = Math.max(2, Math.round(maxSpeed * t));
    }

    dragState.scroll.velocityY = velocityY;
    dragState.scroll.lastClientY = clientY;
    if (velocityY !== 0 && !dragState.scroll.rafId) {
      dragState.scroll.rafId = requestAnimationFrame(tickAutoScroll);
    }
  };

  const clearDragState = () => {
    dragState.taskId = null;
    dragState.version = null;
    dragState.durationSlots = null;
    dragState.travelSlots = 0;
    dragState.baseTravelSlots = 0;
    dragState.lastPreviewOk = false;
    dragState.busy = false;
    dragState.originTd?.classList?.remove?.("is-drag-origin");
    dragState.originTd = null;
    dragState.originStartMinutes = null;
    dragState.previewData = null;
    dragState.lastPreviewSlot = null;
    dragState.lastPreviewMinutes = null;
    dragState.lastPreviewRow = null;
    if (dragState.previewDebounceTimer) {
      clearTimeout(dragState.previewDebounceTimer);
      dragState.previewDebounceTimer = null;
    }
    stopAutoScroll();
    document.querySelectorAll(".task-card.is-dragging").forEach((el) => el.classList.remove("is-dragging"));
    document.querySelectorAll(".schedule-slot.is-over").forEach((el) => el.classList.remove("is-over"));
    clearPreview();
    clearTravelPreview();
    clearPreviewColors();
    dragState.highlightedCells = [];
    dragState.highlightedTravelCells = [];
  };

  // Use shared grid utility (with fallback)
  const parseIntSafe = (val) => {
    if (grid?.parseIntSafe) return grid.parseIntSafe(val);
    const n = Number.parseInt(String(val || ""), 10);
    return Number.isFinite(n) ? n : null;
  };

  const getSlotMinutesFromX = (row, clientX, techId, date) => {
    if (!row) return null;

    const allSlots = row.querySelectorAll(`.schedule-slot[data-tech-id="${techId}"][data-date="${date}"]`);
    if (!allSlots.length) return null;

    // Get all slot positions and find which one the cursor is closest to
    let bestSlot = null;
    let bestDistance = Infinity;

    for (const slot of allSlots) {
      const rect = slot.getBoundingClientRect();
      const slotCenter = rect.left + rect.width / 2;
      const distance = Math.abs(clientX - slotCenter);

      // If cursor is within the slot bounds, use this slot
      if (clientX >= rect.left && clientX <= rect.right) {
        return parseIntSafe(slot.dataset.slotStartMinutes);
      }

      if (distance < bestDistance) {
        bestDistance = distance;
        bestSlot = slot;
      }
    }

    // If cursor is not directly over any slot, check task cards too
    const allCells = row.querySelectorAll("td.schedule-cell");
    for (const cell of allCells) {
      const rect = cell.getBoundingClientRect();
      if (clientX >= rect.left && clientX <= rect.right) {
        // Cursor is over this cell
        const card = cell.querySelector(".task-card");
        if (card) {
          const cardStart = parseIntSafe(card.dataset.startMinutes);
          const cardDuration = parseIntSafe(card.dataset.durationMinutes);
          if (cardStart != null && cardDuration != null) {
            // Calculate which slot within this task cell based on X position
            const slotWidth = rect.width / (cardDuration / 30);
            const offsetX = clientX - rect.left;
            const slotIndex = Math.floor(offsetX / slotWidth);
            return cardStart + slotIndex * 30;
          }
        }
      }
    }

    return bestSlot ? parseIntSafe(bestSlot.dataset.slotStartMinutes) : null;
  };

  const applyPreviewRangeByMinutes = (row, startMinutes, durationSlots, taskId, techId, date) => {
    if (!row || startMinutes == null || !durationSlots || durationSlots <= 0) {
      clearPreview();
      dragState.lastPreviewOk = false;
      dragState.lastPreviewRow = null;
      return false;
    }

    // If row changed, clear all previous highlights first
    const rowChanged = dragState.lastPreviewRow !== row;
    if (rowChanged) {
      for (const td of dragState.highlightedCells) {
        td.classList.remove("is-drop-range", "is-drop-range-invalid");
      }
      dragState.highlightedCells = [];
      dragState.lastPreviewRow = row;
    }

    const slotSize = 30;
    const newTouched = [];
    let ok = true;

    for (let i = 0; i < durationSlots; i++) {
      const targetMinutes = startMinutes + i * slotSize;

      // First try to find an empty slot at this time
      const slot = row.querySelector(
        `.schedule-slot[data-tech-id="${techId}"][data-date="${date}"][data-slot-start-minutes="${targetMinutes}"]`
      );

      if (slot) {
        const td = slot.closest("td");
        if (td && !newTouched.includes(td)) {
          newTouched.push(td);
        }
      } else {
        // No empty slot - check if this time falls within our own task (allowed)
        const ownTaskCard = row.querySelector(`.task-card[data-task-id="${taskId}"]`);
        if (ownTaskCard) {
          const cardStart = parseIntSafe(ownTaskCard.dataset.startMinutes);
          const cardDuration = parseIntSafe(ownTaskCard.dataset.durationMinutes);
          if (cardStart != null && cardDuration != null) {
            const cardEnd = cardStart + cardDuration;
            if (targetMinutes >= cardStart && targetMinutes < cardEnd) {
              const td = ownTaskCard.closest("td");
              if (td && !newTouched.includes(td)) {
                newTouched.push(td);
              }
              continue;
            }
          }
        }

        // Also allow occupying travel blocks reserved for this same task.
        const ownTravelCards = row.querySelectorAll(`.travel-slot-card[data-travel-for-task-id="${taskId}"]`);
        if (ownTravelCards && ownTravelCards.length > 0) {
          let coveredByOwnTravel = false;
          for (const travelCard of ownTravelCards) {
            const travelStart = parseIntSafe(travelCard.dataset.startMinutes);
            const travelDuration = parseIntSafe(travelCard.dataset.durationMinutes);
            if (travelStart == null || travelDuration == null) continue;
            const travelEnd = travelStart + travelDuration;
            if (targetMinutes >= travelStart && targetMinutes < travelEnd) {
              const td = travelCard.closest("td");
              if (td && !newTouched.includes(td)) {
                newTouched.push(td);
              }
              coveredByOwnTravel = true;
              break;
            }
          }
          if (coveredByOwnTravel) {
            continue;
          }
        }

        // Time slot is occupied by another task
        ok = false;
      }
    }

    if (newTouched.length === 0) ok = false;

    // Clear only cells that are no longer in the new set (same row)
    if (!rowChanged) {
      const newSet = new Set(newTouched);
      for (const td of dragState.highlightedCells) {
        if (!newSet.has(td)) {
          td.classList.remove("is-drop-range", "is-drop-range-invalid");
        }
      }
    }

    // Apply new classes
    const className = ok ? "is-drop-range" : "is-drop-range-invalid";
    const oldClassName = ok ? "is-drop-range-invalid" : "is-drop-range";
    for (const cell of newTouched) {
      cell.classList.remove(oldClassName);
      cell.classList.add(className);
    }

    // Update tracked cells
    dragState.highlightedCells = newTouched;
    dragState.lastPreviewOk = ok;
    return ok;
  };

  const applyTravelPreviewRangeByMinutes = (row, taskStartMinutes, travelSlots, taskId, techId, date) => {
    clearTravelPreview();
    if (!row || !Number.isFinite(taskStartMinutes) || !travelSlots || travelSlots <= 0) {
      return true;
    }

    const touched = [];
    let ok = true;
    const travelStartMinutes = taskStartMinutes - (travelSlots * 30);

    for (let i = 0; i < travelSlots; i++) {
      const minute = travelStartMinutes + (i * 30);

      const slot = row.querySelector(
        `.schedule-slot[data-tech-id="${techId}"][data-date="${date}"][data-slot-start-minutes="${minute}"]`
      );

      if (slot) {
        const td = slot.closest("td");
        if (td && !touched.includes(td)) touched.push(td);
        continue;
      }

      const ownTaskCard = row.querySelector(`.task-card[data-task-id="${taskId}"]`);
      if (ownTaskCard) {
        const cardStart = parseIntSafe(ownTaskCard.dataset.startMinutes);
        const cardDuration = parseIntSafe(ownTaskCard.dataset.durationMinutes);
        if (cardStart != null && cardDuration != null) {
          const cardEnd = cardStart + cardDuration;
          if (minute >= cardStart && minute < cardEnd) {
            const td = ownTaskCard.closest("td");
            if (td && !touched.includes(td)) touched.push(td);
            continue;
          }
        }
      }

      const ownTravelCards = row.querySelectorAll(`.travel-slot-card[data-travel-for-task-id="${taskId}"]`);
      let coveredByOwnTravel = false;
      for (const travelCard of ownTravelCards) {
        const travelStart = parseIntSafe(travelCard.dataset.startMinutes);
        const travelDuration = parseIntSafe(travelCard.dataset.durationMinutes);
        if (travelStart == null || travelDuration == null) continue;
        const travelEnd = travelStart + travelDuration;
        if (minute >= travelStart && minute < travelEnd) {
          const td = travelCard.closest("td");
          if (td && !touched.includes(td)) touched.push(td);
          coveredByOwnTravel = true;
          break;
        }
      }
      if (coveredByOwnTravel) {
        continue;
      }

      ok = false;
    }

    const className = ok ? "is-drop-travel" : "is-drop-travel-invalid";
    touched.forEach((td, idx) => {
      td.classList.add(className);
      if (idx === touched.length - 1) {
        td.classList.add("is-drop-travel-start");
      }
    });

    dragState.highlightedTravelCells = touched;
    return ok;
  };

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleDragDropInit === "1") return;
    document.documentElement.dataset.scheduleDragDropInit = "1";

    // Capture wheel events so we can apply manual scrolling while dragging.
    // Some browsers suppress wheel events during native HTML5 drag; listening on window maximizes our chance (might still not work if broswer denies it).
    tables.addEventListener("wheel", onWheelDuringDrag, { passive: false, capture: true });
    window.addEventListener("wheel", onWheelDuringDrag, { passive: false, capture: true });

    tables.addEventListener("dragstart", (evt) => {
      const handle = evt.target?.closest?.(".task-drag-handle");
      if (!handle) return;
      const card = handle.closest?.(".task-card");
      if (!card) return;

      selection?.clearSelection?.("task.drag.start");

      if (!isDayView() || !isEditModeEnabled() || (card.dataset.editable || "") !== "true") {
        evt.preventDefault();
        return;
      }

      const taskId = card.getAttribute("data-task-id");
      const version = card.getAttribute("data-task-version");
      const durationMinutes = parseIntSafe(card.getAttribute("data-duration-minutes"));
      const travelDurationMinutes = parseIntSafe(card.getAttribute("data-travel-duration-minutes")) || 0;
      if (!taskId || !version) return;

      dragState.taskId = taskId;
      dragState.version = version;
      dragState.durationSlots =
        durationMinutes && durationMinutes > 0
          ? Math.max(1, Math.ceil(durationMinutes / 30))
          : 1;
      dragState.travelSlots = travelDurationMinutes > 0
        ? Math.max(1, Math.ceil(travelDurationMinutes / 30))
        : 0;
      dragState.baseTravelSlots = dragState.travelSlots;
      dragState.originStartMinutes = parseIntSafe(card.getAttribute("data-start-minutes"));
      dragState.busy = false;
      dragState.lastPreviewOk = false;
      dragState.scroll.container = getScrollContainer();

      evt.dataTransfer.effectAllowed = "move";
      evt.dataTransfer.setData("application/x-schedule-task-id", taskId);
      evt.dataTransfer.setData("application/x-schedule-task-version", version);
      evt.dataTransfer.setData("text/plain", taskId);

      try {
        evt.dataTransfer.setDragImage(card, 16, 16);
      } catch {
        // ignore
      }

      card.classList.add("is-dragging");
      dragState.originTd = card.closest("td");
      dragState.originTd?.classList?.add?.("is-drag-origin");
      logEvent("ui.drag.start", { taskId, durationSlots: dragState.durationSlots || 1 });
    });

    tables.addEventListener("dragend", () => {
      logEvent("ui.drag.end", { taskId: dragState.taskId || "none" });
      selection?.clearSelection?.("task.drag.end");
      clearDragState();
    });

    tables.addEventListener("dragover", (evt) => {
      if (!isDayView()) return;
      if (!isEditModeEnabled()) return;
      if (!dragState.taskId || !dragState.version || dragState.busy) return;

      updateAutoScroll(evt.clientY);
      evt.preventDefault();
      evt.dataTransfer.dropEffect = "move";

      // Use screen position to find the element under the cursor
      const el = document.elementFromPoint(evt.clientX, evt.clientY);
      const cell = el?.closest?.("td.schedule-cell");
      const row = el?.closest?.("tr");
      if (!cell || !row) return;

      // Get techId and date from slot or task card in this row
      const slot = cell.querySelector(".schedule-slot");
      const card = cell.querySelector(".task-card");
      const refEl = slot || card;
      if (!refEl) return;

      const techId = refEl.dataset.techId;
      const date = refEl.dataset.date || slot?.dataset?.date;
      if (!techId || !date) return;

      // Calculate slot minutes based on cursor X position
      const slotMinutes = getSlotMinutesFromX(row, evt.clientX, techId, date);
      if (slotMinutes == null) return;

      // Only update if position AND row unchanged
      const rowChanged = dragState.lastPreviewRow !== row;
      if (!rowChanged && dragState.lastPreviewMinutes === slotMinutes) return;
      dragState.lastPreviewMinutes = slotMinutes;

      applyPreviewRangeByMinutes(row, slotMinutes, dragState.durationSlots || 1, dragState.taskId, techId, date);
      applyTravelPreviewRangeByMinutes(row, slotMinutes, dragState.travelSlots || 0, dragState.taskId, techId, date);

      // Schedule API preview call
      const previewSlot = row.querySelector(`.schedule-slot[data-tech-id="${techId}"][data-date="${date}"][data-slot-start-minutes="${slotMinutes}"]`);
      if (previewSlot) {
        scheduleDragPreview(previewSlot);
      }
    });

    tables.addEventListener("dragenter", (evt) => {
      if (!isDayView() || !isEditModeEnabled()) return;
      if (!dragState.taskId || !dragState.version || dragState.busy) return;

      const el = document.elementFromPoint(evt.clientX, evt.clientY);
      const cell = el?.closest?.("td.schedule-cell");
      const row = el?.closest?.("tr");
      if (!cell || !row) return;

      const slot = cell.querySelector(".schedule-slot");
      if (slot) slot.classList.add("is-over");

      updateAutoScroll(evt.clientY);
    });

    tables.addEventListener("dragleave", (evt) => {
      const slot = evt.target?.closest?.(".schedule-slot");
      if (!slot) return;
      slot.classList.remove("is-over");
    });

    tables.addEventListener("drop", async (evt) => {
      if (!isDayView()) return;
      if (!isEditModeEnabled()) return;

      evt.preventDefault();
      selection?.clearSelection?.("task.drag.drop");
      dragState.scroll.velocityY = 0;

      // Find the cell and row from cursor position
      const el = document.elementFromPoint(evt.clientX, evt.clientY);
      const cell = el?.closest?.("td.schedule-cell");
      const row = el?.closest?.("tr");

      if (!cell || !row) {
        clearDragState();
        return;
      }

      // Get techId and date from slot or task card
      const slot = cell.querySelector(".schedule-slot");
      const card = cell.querySelector(".task-card");
      const refEl = slot || card;

      if (slot) slot.classList.remove("is-over");

      if (!refEl) {
        clearDragState();
        return;
      }

      const techId = refEl.dataset.techId;
      const date = refEl.dataset.date || slot?.dataset?.date;

      if (!techId || !date) {
        clearDragState();
        return;
      }

      // Calculate slot minutes based on cursor X position
      const startMinutes = getSlotMinutesFromX(row, evt.clientX, techId, date);
      const technicianId = parseIntSafe(techId);
      const targetDate = date;

      if (!technicianId || startMinutes == null || !targetDate) {
        clearDragState();
        return;
      }

      /**
       * ============================================================
       * 🆕 CREATE MODE (no taskId → create new task)
       * ============================================================
       */
      if (!dragState.taskId) {
        logEvent("ui.drag.drop.create", {
          technicianId,
          targetDate,
          startMinutes,
        });

        await schedule.tasks?.openTaskPanel?.({
          technicianId,
          targetDate,
          startMinutes,
          endMinutes: startMinutes + 30, // default slot length
        });

        clearDragState();
        return;
      }

      /**
       * ============================================================
       * ✏️ MOVE / EDIT MODE (existing task)
       * ============================================================
       */
      if (!dragState.version || dragState.busy) {
        clearDragState();
        return;
      }

      const previewOk = applyPreviewRangeByMinutes(
        row,
        startMinutes,
        dragState.durationSlots || 1,
        dragState.taskId,
        techId,
        date
      );

      applyTravelPreviewRangeByMinutes(
        row,
        startMinutes,
        dragState.travelSlots || 0,
        dragState.taskId,
        techId,
        date
      );

      if (!previewOk) {
        logEvent(
          "ui.drag.drop.blocked.preview",
          { taskId: dragState.taskId },
          "warn"
        );
        alert("Užduotis netelpa į laisvą laiko tarpą.");
        clearDragState();
        return;
      }

      const taskId = dragState.taskId;
      const expectedVersion = parseIntSafe(dragState.version);

      if (!expectedVersion) {
        clearDragState();
        return;
      }

      dragState.busy = true;

      logEvent("ui.drag.drop.move", {
        taskId,
        technicianId,
        startMinutes,
        targetDate,
      });

      try {
        // Use task.api module if available
        if (taskApi?.moveTask) {
          const result = await taskApi.moveTask(taskId, {
            targetTechnicianId: technicianId,
            targetDate,
            targetSlotStartMinutes: startMinutes,
          }, expectedVersion);

          if (!result.success) {
            alert(result.error || "Nepavyko perkelti užduoties.");
          } else {
            const query =
              state?.getQuery?.() ||
              new URLSearchParams(window.location.search || "");
            if (!query.has("view")) query.set("view", "day");

            await table?.fetchAndRender?.(query, {
              updateUrl: false,
              preserveScroll: true,
            });

            await tasks?.refreshOpenTask?.(taskId);
          }
        } else {
          // Fallback: direct fetch
          const timezoneOffset = -new Date().getTimezoneOffset();

          const body = JSON.stringify({
            targetTechnicianId: technicianId,
            targetDate,
            targetSlotStartMinutes: startMinutes,
            version: expectedVersion,
            timezoneOffsetMinutes: timezoneOffset,
          });

          const resp =
            (await logger?.fetchWithTrace?.(
              `/api/tasks/${taskId}/move`,
              {
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body,
              },
              { event: "ui.task.move", taskId }
            )) ||
            (await fetch(`/api/tasks/${taskId}/move`, {
              method: "PATCH",
              headers: {
                "X-Requested-With": "XMLHttpRequest",
                "Content-Type": "application/json",
              },
              body,
            }));

          if (resp.status === 409 || resp.status === 422 || resp.status === 403) {
            const msg =
              (await resp.json().catch(() => null))?.message ||
              "Nepavyko perkelti užduoties.";
            alert(msg);
          } else if (!resp.ok) {
            throw new Error("Nepavyko perkelti užduoties.");
          } else {
            const query =
              state?.getQuery?.() ||
              new URLSearchParams(window.location.search || "");
            if (!query.has("view")) query.set("view", "day");

            await table?.fetchAndRender?.(query, {
              updateUrl: false,
              preserveScroll: true,
            });

            await tasks?.refreshOpenTask?.(taskId);
          }
        }
      } catch (err) {
        logEvent(
          "ui.drag.drop.error",
          { taskId, error: err?.message },
          "error"
        );
        alert("Nepavyko perkelti užduoties.");

        const query =
          state?.getQuery?.() ||
          new URLSearchParams(window.location.search || "");
        if (!query.has("view")) query.set("view", "day");

        await table?.fetchAndRender?.(query, {
          updateUrl: false,
          preserveScroll: true,
        });
      } finally {
        clearDragState();
      }
    });


    document.addEventListener("schedule:view:changed", () => {
      if (!isDayView()) clearDragState();
    });

    logEvent("ui.dragdrop.init");
  };

  schedule.dragdrop = schedule.dragdrop || {};
  schedule.dragdrop.initOnce = initOnce;
})();
