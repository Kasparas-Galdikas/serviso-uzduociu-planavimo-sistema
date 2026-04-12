/**
 * task.resize.js
 *
 * Inline task time editing via drag handles on the schedule grid.
 * Handles both expanding and shrinking tasks within TD constraints.
 *
 * Dependencies: ui.slot-overlay, schedule.grid, schedule.tasks.api, schedule.table
 * Dependents: schedule.tasks (main orchestrator)
 */
(() => {
  "use strict";

  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  window.schedule.tasks = window.schedule.tasks || {};

  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const grid = schedule.grid;
  const toast = schedule.ui?.toast;
  const taskApi = schedule.tasks?.api;

  // Import slot overlay utilities (with fallbacks for load order)
  const getSlotOverlay = () => schedule.ui?.slotOverlay;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);
  const showError = (message, fallback = "Nepavyko pakeisti laiko.") =>
    toast?.showError?.(message || fallback) || alert(message || fallback);
  const showWarning = (message, fallback = "Nepavyko pakeisti laiko.") =>
    toast?.showWarning?.(message || fallback) || alert(message || fallback);
  const showSuccess = (message, fallback = "Uzduoties laikas pakeistas.") =>
    toast?.showSuccess?.(message || fallback) || alert(message || fallback);

  // Helper: format time range using grid utility (stable load order)
  const formatRange = grid.formatRange;

  /**
   * Start inline resize mode for a task card.
   * Creates an overlay with drag handles for adjusting task start/end times.
   */
  const startInlineResize = (card, { taskId, version, date, startMinutes, durationMinutes, applyTimeUpdate: customApplyTimeUpdate = null }) => {
    if (!tables) return;
    const slotOverlay = getSlotOverlay();

    const slotSize = parseInt(tables.dataset.slotSizeMinutes || "30", 10);
    const dayStart = parseInt(tables.dataset.dayStartMinutes || "480", 10);
    const dayEnd = parseInt(tables.dataset.dayEndMinutes || "1200", 10);

    // Calculate minimum start time — use server-provided value when available
    const now = new Date();
    const taskDate = new Date(date + "T00:00:00");
    const isToday = now.toDateString() === taskDate.toDateString();
    const serverEarliest = parseInt(tables.dataset.earliestStartMinutes || "", 10);
    const minStartMinutes = isToday
      ? (Number.isFinite(serverEarliest) ? serverEarliest : Math.ceil((now.getHours() * 60 + now.getMinutes()) / slotSize) * slotSize)
      : dayStart;

    let currentStart = startMinutes;
    let currentEnd = startMinutes + durationMinutes;
    let currentVersion = version;

    // Use table-responsive container for positioning
    const container = tables.querySelector(".table-responsive") || tables.closest(".table-responsive");
    if (!container) return;

    // Find the starting TD cell
    const startTd = card.closest("td");
    if (!startTd) return;
    const row = startTd.parentElement;
    if (!row) return;

    // Get all TDs in the row
    const allTds = Array.from(row.querySelectorAll("td"));
    const startTdIndex = allTds.indexOf(startTd);
    if (startTdIndex < 0) return;

    // Calculate how many slots the task spans
    const durationSlots = Math.max(1, Math.round(durationMinutes / slotSize));

    // Find the end TD using shared helper (stable load order)
    let endTd = slotOverlay.getEndTdForSlots(startTd, durationSlots);

    // Create overlay element using shared factory (stable load order)
    const overlay = slotOverlay.create({
      className: "task-resize-bar",
      showRange: false,
      hitLabel: "Keisti laiką"
    });

    overlay.style.zIndex = "8";

    // Apply card styling to overlay
    const cardClasses = card.className;
    const taskCardClassMatch = cardClasses.match(/task-(normal|load|conflict|travel)/);
    if (taskCardClassMatch) {
      overlay.classList.add(`task-${taskCardClassMatch[1]}`);
    }

    // Position overlay using shared helper (stable load order)
    slotOverlay.positionFromTds(overlay, container, startTd, endTd);

    // Track cleanup function for outside click
    let removeOutsideListener = null;

    // Cleanup function
    const teardown = () => {
      slotOverlay.destroy(overlay, removeOutsideListener);
    };

    // Wire outside click dismiss using shared helper (stable load order)
    removeOutsideListener = slotOverlay.attachOutsideClickDismiss(overlay, [card], teardown);

    // API call to update task time
    const applyTimeUpdate = async (newStart, newEnd) => {
      if (typeof customApplyTimeUpdate === "function") {
        return await customApplyTimeUpdate(newStart, newEnd, {
          card,
          taskId,
          version: currentVersion,
          date,
        });
      }

      if (taskApi?.updateTaskTime) {
        const result = await taskApi.updateTaskTime(taskId, {
          targetDate: date,
          startMinutes: newStart,
          endMinutes: newEnd,
        }, currentVersion);

        if (!result.success) {
          (result.errorType === "conflict" || result.errorType === "forbidden" ? showWarning : showError)(result.error, "Nepavyko pakeisti laiko.");
          if (result.errorType === "conflict" || result.errorType === "forbidden") {
            teardown();
          }
          return false;
        }

        if (result.version) {
          currentVersion = result.version;
          card.setAttribute("data-task-version", String(currentVersion));
        }

        // Update detail panel times + version via modal module (uses field cache, syncs version)
        schedule.tasks?.modal?.updatePanelTimes?.(taskId, result.startAt, result.endAt, result.version);

        const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
        await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
        showSuccess(result.message, "Uzduoties laikas pakeistas.");
        document.dispatchEvent(new CustomEvent("schedule:action-completed", {
          detail: {
            action: "time",
            primaryTaskId: Number(taskId),
            taskIds: [Number(taskId)],
            version: Number(result.version || currentVersion || 0),
          },
        }));
        return true;
      }

      // Fallback: direct fetch
      return await applyTimeUpdateFallback(taskId, date, newStart, newEnd, currentVersion, card, teardown);
    };

    // Helper to get TD index in row (stable load order)
    const getTdIndexInRow = slotOverlay.getTdIndexInRow;

    // Track current end TD index for resize (start index derived from startTd)
    let currentEndTdIndex = getTdIndexInRow(endTd);
    const baseStart = startMinutes;
    const baseEnd = startMinutes + durationMinutes;

    const updateOverlayFromTds = (newStartTd, newEndTd) => {
      // Update time display on card
      const taskTimeEl = card.querySelector(".task-time");
      if (taskTimeEl) {
        const dragHandle = taskTimeEl.querySelector(".task-drag-handle");
        const handleHtml = dragHandle ? dragHandle.outerHTML : "";
        taskTimeEl.innerHTML = `${handleHtml}${formatRange(currentStart, currentEnd)}`;
      }

      // Recalculate overlay position from actual TD boundaries (stable load order)
      slotOverlay.positionFromTds(overlay, container, newStartTd, newEndTd);
      overlay.style.transform = "none";
    };

    // Drag state tracking for change detection
    let dragBaseStart = currentStart;
    let dragBaseEnd = currentEnd;

    // Attach drag behavior using shared helper (stable load order)
    // Note: State cleanup (pointercancel, reset overlay styles) is handled by slotOverlay.attachDragHandles()
    slotOverlay.attachDragHandles(overlay, {
      onDragStart: (dir, e) => {
        dragBaseStart = currentStart;
        dragBaseEnd = currentEnd;
      },
      onDragMove: (dir, ev) => {
        handleDragMove(dir, ev, row, startTd, getTdIndexInRow, slotOverlay.getEndTdForSlots, slotSize, dayStart, dayEnd, minStartMinutes, updateOverlayFromTds, container, overlay);
      },
      onDragEnd: async (dir, ev, changed) => {
        if (currentStart !== dragBaseStart || currentEnd !== dragBaseEnd) {
          await applyTimeUpdate(currentStart, currentEnd);
        }
      }
    });

    // Wire hit area close (stable load order)
    function handleDragMove(dir, ev, row, origStartTd, getTdIdx, getEndTd, slotSz, dayStartMin, dayEndMin, minStartMin, updateFn, containerEl, overlayEl) {
      const el = document.elementFromPoint(ev.clientX, ev.clientY);
      const hoveredTd = el?.closest?.("td");
      if (!hoveredTd) return;

      const hoveredRow = hoveredTd.parentElement;
      if (!hoveredRow || hoveredRow !== row) return;

      const hoveredIdx = getTdIdx(hoveredTd);
      if (hoveredIdx == null) return;

      const isOverTaskTd = hoveredTd === origStartTd;
      const baseStartTdIdx = getTdIdx(origStartTd);
      const baseEndTdIdx = currentEndTdIndex;

      let newStartMinutes = dragBaseStart;
      let newEndMinutes = dragBaseEnd;

      if (isOverTaskTd) {
        // Shrinking: calculate slot position within the task TD based on mouse X
        const tdRect = origStartTd.getBoundingClientRect();
        const tdWidth = tdRect.width;
        const mouseXInTd = ev.clientX - tdRect.left;
        const totalTaskSlots = Math.round((dragBaseEnd - dragBaseStart) / slotSz);
        const slotWidthInTd = tdWidth / totalTaskSlots;

        if (dir === "left") {
          const slotsFromLeft = Math.round(mouseXInTd / slotWidthInTd);
          const clampedSlots = Math.max(0, Math.min(slotsFromLeft, totalTaskSlots - 1));
          newStartMinutes = dragBaseStart + clampedSlots * slotSz;
        } else {
          const slotsFromLeft = Math.round(mouseXInTd / slotWidthInTd);
          const clampedSlots = Math.max(1, Math.min(slotsFromLeft, totalTaskSlots));
          newEndMinutes = dragBaseStart + clampedSlots * slotSz;
        }
      } else {
        // Expanding: use the hovered empty slot TD
        const slotEl = hoveredTd.querySelector(".schedule-slot");
        if (!slotEl?.dataset?.slotStartMinutes) return;

        const hoveredSlotStart = parseInt(slotEl.dataset.slotStartMinutes, 10);

        if (dir === "left") {
          if (hoveredIdx >= baseStartTdIdx) return;
          newStartMinutes = hoveredSlotStart;
        } else {
          if (hoveredIdx <= baseEndTdIdx) return;
          newEndMinutes = hoveredSlotStart + slotSz;
        }
      }

      // Apply time constraints
      newStartMinutes = Math.max(dayStartMin, Math.max(minStartMin, newStartMinutes));
      newEndMinutes = Math.min(dayEndMin, newEndMinutes);
      if (newEndMinutes - newStartMinutes < slotSz) return;

      // Update state
      currentStart = newStartMinutes;
      currentEnd = newEndMinutes;

      // Recalculate overlay position
      const newDurationSlots = Math.round((currentEnd - currentStart) / slotSz);
      const offsetFromOriginalStart = (currentStart - baseStart) / slotSz;

      const origStartRect = origStartTd.getBoundingClientRect();
      const origSlotWidth = origStartRect.width / Math.round((baseEnd - baseStart) / slotSz);

      if (!isOverTaskTd) {
        // Find actual start/end TDs for the new range
        let actualStartTd = origStartTd;
        let actualEndTd = origStartTd;

        if (dir === "left" && hoveredIdx < baseStartTdIdx) {
          actualStartTd = hoveredTd;
          currentStartTdIndex = hoveredIdx;
        } else if (dir === "right" && hoveredIdx > baseEndTdIdx) {
          actualEndTd = hoveredTd;
          currentEndTdIndex = hoveredIdx;
        }

        updateFn(actualStartTd, actualEndTd);
      } else {
        // Shrinking within same TD - use pixel-based positioning
        const containerRect = containerEl.getBoundingClientRect();
        const newLeft = origStartRect.left - containerRect.left + containerEl.scrollLeft + 1 + (offsetFromOriginalStart * origSlotWidth);
        const newWidth = Math.max(origSlotWidth, newDurationSlots * origSlotWidth - 2);

        overlayEl.style.left = `${newLeft}px`;
        overlayEl.style.width = `${newWidth}px`;
        overlayEl.style.transform = "none";

        // Update time display
        const taskTimeEl = card.querySelector(".task-time");
        if (taskTimeEl) {
          const dragHandle = taskTimeEl.querySelector(".task-drag-handle");
          const handleHtml = dragHandle ? dragHandle.outerHTML : "";
          taskTimeEl.innerHTML = `${handleHtml}${formatRange(currentStart, currentEnd)}`;
        }
      }
    }

    // Wire hit area close button (stable load order)
    slotOverlay.attachHitClose(overlay, teardown);

    // Append to container
    container.appendChild(overlay);

    logEvent("ui.task.resize.start", { taskId, startMinutes, durationMinutes });
  };

  // ========== Exports ==========

  schedule.tasks.resize = {
    startInlineResize,
  };
})();
