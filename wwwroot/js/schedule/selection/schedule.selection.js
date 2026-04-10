(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;

  // Import from shared modules
  const grid = schedule.grid;
  const dom = schedule.ui?.dom;
  const slotOverlay = window.schedule.ui?.slotOverlay;
  const taskApi = schedule.tasks?.api;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const isDayView = () => (state?.getView?.() || "day") === "day";

  // Use shared DOM utilities (with fallbacks for load order)
  const isEmptySlotCell = (td) => dom?.isEmptySlotCell?.(td) ??
    (td?.classList?.contains("empty-slot") && !!td.querySelector?.(".schedule-slot"));
  const getColSpan = (td) => dom?.getColSpan?.(td) ?? (() => {
    const span = parseInt(td?.getAttribute?.("colspan") || "1", 10);
    return span && span > 0 ? span : 1;
  })();

  // Use shared slot overlay utilities (stable load order guaranteed)
  const getTdIndexInRow = slotOverlay.getTdIndexInRow;
  const getTdAtIndexInRow = slotOverlay.getTdAtIndexInRow;
  const getLastTdInRange = slotOverlay.getLastTdInRange;

  const selection = {
    active: false,
    techId: null,
    date: null,
    startMinutes: null,
    endMinutes: null,
    durationSlots: 1,
    startTd: null,
    chooserEl: null,
    isResizing: false,
    suppressClickUntilTs: 0,
    barEl: null,
    barContainerEl: null,
    barPointerEventsBeforeResize: null,
  };

  const getLogicalColSpan = (td) => {
    const original = parseInt(td?.dataset?.selectionOriginColspan || "", 10);
    if (Number.isFinite(original) && original > 0) return original;
    return getColSpan(td);
  };

  const clearSelectionPlacement = () => {
    document.querySelectorAll("td[data-selection-origin-colspan]").forEach((td) => {
      const originalColspan = td.dataset.selectionOriginColspan;
      if (originalColspan) td.setAttribute("colspan", originalColspan);
      else td.removeAttribute("colspan");
      td.removeAttribute("data-selection-origin-colspan");
      td.removeAttribute("data-selection-token");
      td.querySelectorAll(".slot-selection-bar.selection-inline").forEach((el) => el.remove());
      td.querySelectorAll(".schedule-slot[data-selection-slot-hidden='1']").forEach((slot) => {
        slot.style.display = "";
        slot.removeAttribute("data-selection-slot-hidden");
      });
    });

    document.querySelectorAll("td[data-selection-hidden-by]").forEach((td) => {
      td.style.display = "";
      td.removeAttribute("data-selection-hidden-by");
    });
  };

  const getSelectionRangeTds = (startTd, slots) => {
    if (!startTd || !slots || slots <= 0) return [];
    let remaining = slots;
    let td = startTd;
    const touched = [];

    while (td && remaining > 0) {
      touched.push(td);
      remaining -= getLogicalColSpan(td);
      td = td.nextElementSibling;
    }

    return remaining <= 0 ? touched : [];
  };

  const getSelectionLastTdInRange = (startTd, slots) => {
    const cells = getSelectionRangeTds(startTd, slots);
    return cells.length ? cells[cells.length - 1] : null;
  };

  const setSelectionColumnMinWidth = (td, minWidthPx) => {
    if (!td || !Number.isFinite(minWidthPx) || minWidthPx <= 0) return;
    const tdIndex = getTdIndexInRow(td);
    if (tdIndex == null) return;

    if (!td.dataset.selectionOriginMinWidth) {
      td.dataset.selectionOriginMinWidth = td.style.minWidth || "";
    }
    td.style.minWidth = `${minWidthPx}px`;

    document.querySelectorAll(".schedule-table thead tr").forEach((row) => {
      const th = row.children?.[tdIndex + 1];
      if (!th?.classList?.contains?.("time-header")) return;
      if (!th.dataset.selectionOriginMinWidth) {
        th.dataset.selectionOriginMinWidth = th.style.minWidth || "";
      }
      th.style.minWidth = `${minWidthPx}px`;
    });
  };

  const clearSelectionColumnMinWidths = () => {
    document.querySelectorAll("td[data-selection-origin-min-width]").forEach((td) => {
      td.style.minWidth = td.dataset.selectionOriginMinWidth || "";
      td.removeAttribute("data-selection-origin-min-width");
    });
    document.querySelectorAll(".schedule-table .time-header[data-selection-origin-min-width]").forEach((th) => {
      th.style.minWidth = th.dataset.selectionOriginMinWidth || "";
      th.removeAttribute("data-selection-origin-min-width");
    });
  };

  const placeSelectionBarInCells = (cells, overlay) => {
    if (!Array.isArray(cells) || !cells.length || !overlay) return false;
    const startTd = cells[0];
    if (!startTd || startTd.dataset.selectionToken) return false;
    if (cells.some((td, idx) => idx > 0 && td.dataset.selectionHiddenBy)) return false;

    const totalColspan = cells.reduce((sum, td) => sum + getLogicalColSpan(td), 0);
    startTd.dataset.selectionOriginColspan = String(getLogicalColSpan(startTd));
    startTd.dataset.selectionToken = "active";
    startTd.setAttribute("colspan", String(Math.max(1, totalColspan)));

    const startSlot = startTd.querySelector(".schedule-slot");
    if (startSlot) {
      startSlot.style.display = "none";
      startSlot.setAttribute("data-selection-slot-hidden", "1");
    }

    for (let i = 1; i < cells.length; i++) {
      cells[i].dataset.selectionHiddenBy = "active";
      cells[i].style.display = "none";
    }

    overlay.classList.add("selection-inline");
    overlay.style.left = "";
    overlay.style.top = "";
    overlay.style.width = "";
    overlay.style.height = "";
    startTd.appendChild(overlay);
    return true;
  };

  const clearChooser = () => {
    selection.chooserEl?.remove?.();
    selection.chooserEl = null;
  };

  const clearSelection = (reason) => {
    clearChooser();
    clearSelectionPlacement();
    clearSelectionColumnMinWidths();
    slotOverlay.destroy(selection.barEl, () => {
      selection.barEl = null;
      selection.barContainerEl = null;
      selection.active = false;
      selection.techId = null;
      selection.date = null;
      selection.startMinutes = null;
      selection.endMinutes = null;
      selection.durationSlots = 1;
      selection.startTd = null;
    });
    document.dispatchEvent(new CustomEvent("schedule:selection:cleared", { detail: { reason } }));
    if (reason) logEvent("ui.slot.selection.clear", { reason });
  };

  // Use shared DOM utility for reading slot data (stable load order guaranteed)
  const readSlotData = dom.readSlotData;

  // Sync current selection start/end to the panel form if it is already open in create mode.
  // Called after every resize/extend so the panel stays in step without resetting other fields.
  // Lightweight — only sets values, no events, so drag is never interrupted.
  const syncSelectionToPanel = () => {
    const modal = document.getElementById("taskDetailModal");
    if (!modal?.classList?.contains("show")) return;
    if (modal.dataset.mode !== "create") return;

    const { startMinutes, endMinutes, date } = selection;
    if (startMinutes == null || endMinutes == null || !date) return;

    modal.dataset.startMinutes = startMinutes;
    modal.dataset.endMinutes = endMinutes;

    const startTime = grid.minutesToTime(startMinutes);
    const endTime = grid.minutesToTime(endMinutes);
    const startInput = modal.querySelector("#taskDetailStart");
    const endInput = modal.querySelector("#taskDetailEnd");
    if (startInput) startInput.value = `${date}T${startTime}`;
    if (endInput) endInput.value = `${date}T${endTime}`;
  };

  const applySelectionToTaskPanel = () => {
    const modal = document.getElementById("taskDetailModal");
    if (!modal?.classList?.contains("show")) return false;

    const { startMinutes, endMinutes, date, techId } = selection;
    if (startMinutes == null || endMinutes == null || !date) return false;

    const startTime = grid.minutesToTime(startMinutes);
    const endTime = grid.minutesToTime(endMinutes);
    const startInput = modal.querySelector("#taskDetailStart");
    const endInput = modal.querySelector("#taskDetailEnd");
    const assigneeEl = modal.querySelector("#taskDetailAssignee");

    if (startInput) {
      startInput.value = `${date}T${startTime}`;
      startInput.dispatchEvent(new Event("input", { bubbles: true }));
    }
    if (endInput) {
      endInput.value = `${date}T${endTime}`;
      endInput.dispatchEvent(new Event("input", { bubbles: true }));
    }
    if (assigneeEl && techId) {
      if (window.jQuery && window.jQuery.fn.select2) {
        window.jQuery(assigneeEl).val(String(techId)).trigger("change");
      } else {
        assigneeEl.value = String(techId);
        assigneeEl.dispatchEvent(new Event("change", { bubbles: true }));
      }
    }

    modal.dataset.targetDate = date;
    modal.dataset.startMinutes = String(startMinutes);
    modal.dataset.endMinutes = String(endMinutes);
    return true;
  };

  const getActiveTaskId = () => {
    const modal = document.getElementById("taskDetailModal");
    const modalTaskId = modal?.dataset?.taskId || "";
    if (modalTaskId) return modalTaskId;
    return document.querySelector(".task-card.is-active")?.getAttribute("data-task-id") || "";
  };

  const isReusableDraftPlanningPanel = () => {
    if (!state?.isDraftMode?.()) return false;
    const modal = document.getElementById("taskDetailModal");
    if (!modal?.classList?.contains("show")) return false;
    const variant = modal.dataset.panelVariant || "";
    if (variant === "history" || variant === "tasks-panel" || variant === "technician") return false;
    return !!(modal.dataset.taskId || modal.dataset.mode === "create" || window.schedule?.backlog?.getSchedulingBacklogId?.());
  };

  const getActiveDraftTask = async () => {
    if (!state?.isDraftMode?.()) return null;
    const taskId = getActiveTaskId();
    if (!taskId) return null;
    const taskData = await taskApi?.fetchTask?.(taskId).catch(() => null);
    if (!taskData?.id) return null;
    if (String(taskData.planStatus || "").toLowerCase() !== "draft") return null;
    return taskData;
  };

  // Called once when user finalizes a selection (click on hit area with panel already open).
  // Updates assignee from drawn slot and triggers route evaluation.
  const finalizeSelectionToPanel = () => {
    const modal = document.getElementById("taskDetailModal");
    if (!modal?.classList?.contains("show")) return;
    if (modal.dataset.mode !== "create") return;

    if (selection.techId) {
      const assigneeEl = modal.querySelector("#taskDetailAssignee");
      if (assigneeEl && window.jQuery) {
        window.jQuery(assigneeEl).val(String(selection.techId)).trigger("change");
      }
    }

    const startInput = modal.querySelector("#taskDetailStart");
    if (startInput) startInput.dispatchEvent(new Event("input", { bubbles: true }));
  };

  const applySelectionRange = (startTd, durationSlots) => {
    const { slotSizeMinutes } = grid.getConfig();
    clearSelectionPlacement();
    clearSelectionColumnMinWidths();

    if (!startTd || !durationSlots || durationSlots <= 0) return false;
    if (!isEmptySlotCell(startTd)) return false;

    let remaining = durationSlots;
    let td = startTd;
    const touched = [];
    let ok = true;

    while (td && remaining > 0) {
      const span = getLogicalColSpan(td);
      const allowed = isEmptySlotCell(td);
      if (!allowed) ok = false;
      touched.push(td);
      remaining -= span;
      td = td.nextElementSibling;
    }

    if (remaining > 0) ok = false;
    if (!ok) return false;

    const slotEl = startTd.querySelector(".schedule-slot");
    const base = readSlotData(slotEl);
    if (!base) return false;

    const startMinutes = base.startMinutes;
    const endMinutes = startMinutes + durationSlots * slotSizeMinutes;

    selection.active = true;
    selection.techId = base.techId;
    selection.date = base.date;
    selection.startMinutes = startMinutes;
    selection.endMinutes = endMinutes;
    selection.durationSlots = durationSlots;
    selection.startTd = startTd;

    ensureBar();
    renderBar();
    syncHandleDisabled();
    syncSelectionToPanel();
    document.dispatchEvent(new CustomEvent("schedule:selection:changed", {
      detail: {
        techId: selection.techId,
        date: selection.date,
        startMinutes,
        endMinutes,
        durationSlots,
      },
    }));
    return true;
  };

  const canApplyRange = (candidateStartTd, candidateDurationSlots) => {
    if (!candidateStartTd || !candidateDurationSlots || candidateDurationSlots <= 0) return false;
    if (!isEmptySlotCell(candidateStartTd)) return false;

    let remaining = candidateDurationSlots;
    let td = candidateStartTd;
    while (td && remaining > 0) {
      const span = getLogicalColSpan(td);
      if (!isEmptySlotCell(td)) return false;
      remaining -= span;
      td = td.nextElementSibling;
    }
    return remaining <= 0;
  };

  const syncHandleDisabled = () => {
    if (!selection.active || !selection.startTd) return;
    const { slotSizeMinutes, dayStartMinutes, dayEndMinutes } = grid.getConfig();
    const leftHandle = selection.barEl?.querySelector?.('[data-slot-handle="left"]');
    const rightHandle = selection.barEl?.querySelector?.('[data-slot-handle="right"]');

    const nextStart = (selection.startMinutes ?? 0) - slotSizeMinutes;
    const nextEnd = (selection.endMinutes ?? 0) + slotSizeMinutes;

    const leftTd = selection.startTd.previousElementSibling;
    const canLeft =
      nextStart >= dayStartMinutes &&
      canApplyRange(leftTd, (selection.durationSlots || 1) + 1) &&
      (() => {
        const s = leftTd?.querySelector?.(".schedule-slot");
        const d = s && readSlotData(s);
        return !!d && d.techId === selection.techId && d.date === selection.date;
      })();

    const canRight =
      nextEnd <= dayEndMinutes &&
      canApplyRange(selection.startTd, (selection.durationSlots || 1) + 1) &&
      (() => {
        const lastTouchedTd = getSelectionLastTdInRange(selection.startTd, selection.durationSlots || 1);
        const nextTd = lastTouchedTd?.nextElementSibling;
        const s = nextTd?.querySelector?.(".schedule-slot");
        const d = s && readSlotData(s);
        return !!d && d.techId === selection.techId && d.date === selection.date && isEmptySlotCell(nextTd);
      })();

    if (leftHandle) leftHandle.classList.toggle("is-disabled", !canLeft);
    if (rightHandle) rightHandle.classList.toggle("is-disabled", !canRight);
  };

  const ensureBar = () => {
    if (selection.barEl && selection.barContainerEl) return;
    const container = tables.querySelector(".table-responsive") || tables.closest(".table-responsive");
    if (!container) return;
    selection.barContainerEl = container;

    // Use shared slotOverlay.create() (stable load order)
    const bar = slotOverlay.create({
      className: "",
      showRange: true,
      hitLabel: "Veiksmai: kurti uzduoti arba priskirti",
      leftLabel: "Keisti pradzios laika",
      rightLabel: "Keisti pabaigos laika",
    });

    container.appendChild(bar);
    selection.barEl = bar;
  };

  const renderBar = () => {
    if (!selection.active || !selection.startTd) return;
    if (!selection.barEl || !selection.barContainerEl) ensureBar();
    if (!selection.barEl || !selection.barContainerEl) return;

    const startTd = selection.startTd;
    clearSelectionColumnMinWidths();
    if ((selection.durationSlots || 1) === 1) {
      setSelectionColumnMinWidth(startTd, 110);
      // Force layout after the column/header min-width change so overlay sizing
      // uses the expanded slot width instead of the previous narrow width.
      void startTd.offsetWidth;
    }

    slotOverlay.updateRangeLabel(selection.barEl, selection.startMinutes ?? 0, selection.endMinutes ?? 0);

    const selectedCells = getSelectionRangeTds(startTd, selection.durationSlots || 1);
    if (!selectedCells.length) return;

    clearSelectionPlacement();
    if (!selection.barEl.isConnected && selection.barContainerEl) {
      selection.barEl.classList.remove("selection-inline");
      selection.barContainerEl.appendChild(selection.barEl);
    }
    const endTd = getSelectionLastTdInRange(startTd, selection.durationSlots || 1) || startTd;
    slotOverlay.positionFromTds(selection.barEl, selection.barContainerEl, startTd, endTd);
  };

  const stepExtend = (dir) => {
    if (!selection.active || !selection.startTd) return;
    clearSelectionPlacement();
    const startTd = selection.startTd;
    const nextDuration = (selection.durationSlots || 1) + 1;

    if (dir === "left") {
      const leftTd = startTd.previousElementSibling;
      if (!leftTd) return;
      const slotEl = leftTd.querySelector(".schedule-slot");
      const d = slotEl && readSlotData(slotEl);
      if (!d || d.techId !== selection.techId || d.date !== selection.date) return;
      if (!canApplyRange(leftTd, nextDuration)) return;
      applySelectionRange(leftTd, nextDuration);
    } else if (dir === "right") {
      if (!canApplyRange(startTd, nextDuration)) return;
      const endTd = getLastTdInRange(startTd, nextDuration);
      const slotEl = endTd?.querySelector?.(".schedule-slot");
      const d = slotEl && readSlotData(slotEl);
      if (!d || d.techId !== selection.techId || d.date !== selection.date) return;
      applySelectionRange(startTd, nextDuration);
    }
  };

  const startResize = (dir, pointerEvent) => {
    if (!selection.active || !selection.startTd) return;
    if (!dir) return;

    clearSelectionPlacement();
    const startTd = selection.startTd;
    const endTd = getSelectionLastTdInRange(startTd, selection.durationSlots || 1) || startTd;
    const startIdx = getTdIndexInRow(startTd);
    const endIdx = getTdIndexInRow(endTd);
    if (startIdx == null || endIdx == null) return;

    selection.isResizing = true;
    selection.suppressClickUntilTs = Date.now() + 250;
    clearChooser();
    selection.barPointerEventsBeforeResize = selection.barEl?.style?.pointerEvents ?? "";
    if (selection.barEl) selection.barEl.style.pointerEvents = "none";

    const onMove = (e) => {
      if (!selection.isResizing) return;
      if (!isDayView()) return;

      const el = document.elementFromPoint(e.clientX, e.clientY);
      const td = el?.closest?.("td");
      if (!td) return;

      const row = td.parentElement;
      if (!row || row !== startTd.parentElement) return;

      const idx = getTdIndexInRow(td);
      if (idx == null) return;

      if (dir === "left") {
        if (idx > endIdx) return;
        const candidateStartTd = getTdAtIndexInRow(row, idx);
        if (!candidateStartTd || !isEmptySlotCell(candidateStartTd)) return;
        const candidateDuration = endIdx - idx + 1;
        if (candidateDuration <= 0) return;

        const extending = idx < startIdx;
        if (extending) {
          const slotEl = candidateStartTd.querySelector(".schedule-slot");
          const d = slotEl && readSlotData(slotEl);
          if (!d || d.techId !== selection.techId || d.date !== selection.date) return;
          if (!canApplyRange(candidateStartTd, candidateDuration)) return;
        }
        applySelectionRange(candidateStartTd, candidateDuration);
      } else {
        if (idx < startIdx) return;
        const candidateDuration = idx - startIdx + 1;
        if (candidateDuration <= 0) return;
        const lastCandidateTd = getTdAtIndexInRow(row, idx);
        if (!lastCandidateTd || !isEmptySlotCell(lastCandidateTd)) return;
        const extending = idx > endIdx;
        if (extending) {
          if (!canApplyRange(startTd, candidateDuration)) return;
          const slotEl = lastCandidateTd.querySelector(".schedule-slot");
          const d = slotEl && readSlotData(slotEl);
          if (!d || d.techId !== selection.techId || d.date !== selection.date) return;
        }
        applySelectionRange(startTd, candidateDuration);
      }

      syncHandleDisabled();
    };

    const onUp = () => {
      selection.isResizing = false;
      document
        .querySelectorAll(".slot-selection-handle.is-dragging")
        .forEach((el) => el.classList.remove("is-dragging"));
      if (selection.barEl) selection.barEl.style.pointerEvents = selection.barPointerEventsBeforeResize || "";
      selection.barPointerEventsBeforeResize = null;
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      renderBar();
      syncHandleDisabled();
      logEvent("ui.slot.selection.resize.end", { dir });
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp, { once: true });
    logEvent("ui.slot.selection.resize.start", { dir });
    pointerEvent?.preventDefault?.();
    pointerEvent?.stopPropagation?.();
  };


  const syncCreateRangeFromPanel = ({ technicianId, targetDate, startMinutes, endMinutes }) => {
    if (!isDayView()) return { ok: false, reason: "not-day" };

    const techIdNum = Number.parseInt(String(technicianId || ""), 10);
    const start = Number.parseInt(String(startMinutes || ""), 10);
    const end = Number.parseInt(String(endMinutes || ""), 10);
    if (!Number.isFinite(techIdNum) || !targetDate || !Number.isFinite(start) || !Number.isFinite(end)) {
      return { ok: false, reason: "invalid-input" };
    }
    if (end <= start) return { ok: false, reason: "invalid-range" };

    const { slotSizeMinutes } = grid.getConfig();
    if (start % slotSizeMinutes !== 0 || end % slotSizeMinutes !== 0) {
      return { ok: false, reason: "unaligned" };
    }

    const durationSlots = Math.max(1, Math.round((end - start) / slotSizeMinutes));
    const slot = tables.querySelector(
      `.schedule-slot[data-tech-id="${techIdNum}"][data-date="${targetDate}"][data-slot-start-minutes="${start}"]`
    );
    const td = slot?.closest?.("td");
    if (!td || !isEmptySlotCell(td)) {
      return { ok: false, reason: "slot-unavailable" };
    }
    if (!canApplyRange(td, durationSlots)) {
      return { ok: false, reason: "range-unavailable" };
    }

    const applied = applySelectionRange(td, durationSlots);
    if (!applied) {
      return { ok: false, reason: "apply-failed" };
    }

    clearChooser();
    return { ok: true };
  };
  const canCreateInCurrentContext = () => {
    const isManager = (tables.dataset.isManager || "false") === "true";
    const status = (tables.dataset.planStatus || "").toLowerCase();
    return isManager && (status === "draft" || status === "active");
  };

  const createTaskInSelection = async () => {
    if (!selection.active) return;
    if (!canCreateInCurrentContext()) {
      alert("Uzduotis galima kurti tik juodrascio plane.");
      return;
    }

    if (isReusableDraftPlanningPanel()) {
      applySelectionToTaskPanel();
      logEvent("ui.task.draft-plan.panel.reused", {
        taskId: getActiveTaskId() || null,
        techId: selection.techId,
        date: selection.date,
      });
      return;
    }

    const activeDraftTask = await getActiveDraftTask();
    if (activeDraftTask?.id) {
      await schedule.tasks?.openTaskPanel?.({ taskId: activeDraftTask.id });
      applySelectionToTaskPanel();
      logEvent("ui.task.draft-plan.task.reused", {
        taskId: activeDraftTask.id,
        techId: selection.techId,
        date: selection.date,
      });
      return;
    }

    const backlogTaskId = window.schedule?.backlog?.getSchedulingBacklogId?.();
    const backlogItem = backlogTaskId
      ? window.schedule?.backlog?.api?.getItemById?.(backlogTaskId)
      : null;

    await schedule.tasks?.openTaskPanel?.({
      technicianId: selection.techId,
      targetDate: selection.date,
      startMinutes: selection.startMinutes,
      endMinutes: selection.endMinutes,
    });

    if (state?.isDraftMode?.() && backlogItem) {
      await window.schedule?.backlog?.form?.applyPrefill?.(backlogItem);
    }

    logEvent("ui.task.create.form.open", { techId: selection.techId, date: selection.date });
  };

  // Export the createTask function so tasks.js can call the API
  const submitCreateTask = async (formData) => {
    const createFn = state?.isDraftMode?.() ? taskApi?.createDraftTask : taskApi?.createTask;
    // Use task.api module if available
    if (createFn) {
      const result = await createFn({
        technicianId: formData.technicianId,
        targetDate: formData.targetDate,
        startMinutes: formData.startMinutes,
        endMinutes: formData.endMinutes,
        title: formData.title,
        location: formData.location || "",
        priority: formData.priority,
        status: formData.status,
        skillLevel: formData.skillLevel,
        objectName: formData.objectName,
        objectAddress: formData.objectAddress,
        instructions: formData.instructions,
        sourceBacklogItemId: formData.sourceBacklogItemId ?? null,
      });

      if (result.success) {
        const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
        if (!query.has("view")) query.set("view", "day");
        clearSelection("create.ok");
        await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });

        if (result.id) {
          const card = document.querySelector(`.task-card[data-task-id="${CSS.escape(String(result.id))}"]`);
          card?.scrollIntoView?.({ behavior: "smooth", block: "center" });
          card?.click?.();
        }
      }

      return result;
    }

    // Fallback: direct fetch
    const timezoneOffset = -new Date().getTimezoneOffset();
    const createUrl = state?.isDraftMode?.() ? "/api/tasks/draft" : "/api/tasks";

    const body = JSON.stringify({
      technicianId: formData.technicianId,
      targetDate: formData.targetDate,
      startMinutes: formData.startMinutes,
      endMinutes: formData.endMinutes,
      title: formData.title,
      location: formData.location || "",
      priority: formData.priority,
      status: formData.status,
      timezoneOffsetMinutes: timezoneOffset,
      skillLevel: formData.skillLevel,
      objectName: formData.objectName,
      objectAddress: formData.objectAddress,
      instructions: formData.instructions,
    });

    try {
      const resp =
        (await logger?.fetchWithTrace?.(
          createUrl,
          { method: "POST", headers: { "Content-Type": "application/json" }, body },
          { event: "ui.task.create", techId: formData.technicianId, date: formData.targetDate },
        )) ||
        (await fetch(createUrl, {
          method: "POST",
          headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
          body,
        }));

      if (resp.status === 409) {
        const msg = (await resp.json().catch(() => null))?.message || "Laiko langas uzimtas.";
        return { success: false, error: msg, errorType: "conflict" };
      }

      if (resp.status === 400) {
        const errorData = await resp.json().catch(() => ({}));
        if (errorData.errors && typeof errorData.errors === "object") {
          return { success: false, validationErrors: errorData.errors, errorType: "validation" };
        }
        const msg = errorData.message || "Patikrinkite įvestus duomenis.";
        return { success: false, error: msg, errorType: "validation" };
      }

      if (resp.status === 422) {
        const msg = (await resp.json().catch(() => null))?.message || "Netinkamas laiko langas.";
        return { success: false, error: msg, errorType: "unprocessable" };
      }

      if (resp.status === 403) {
        const msg = (await resp.json().catch(() => null))?.message || "Neturite teisiu kurti uzduoties.";
        return { success: false, error: msg, errorType: "forbidden" };
      }

      if (!resp.ok) {
        return { success: false, error: "Įvyko klaida kuriant užduotį.", errorType: "unknown" };
      }

      const responseData = await resp.json().catch(() => null);
      const id = responseData?.id;

      const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      if (!query.has("view")) query.set("view", "day");
      clearSelection("create.ok");
      await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });

      if (id) {
        const card = document.querySelector(`.task-card[data-task-id="${CSS.escape(String(id))}"]`);
        card?.scrollIntoView?.({ behavior: "smooth", block: "center" });
        card?.click?.();
      }

      return {
        success: true,
        id,
        version: responseData?.version,
        message: responseData?.message,
      };
    } catch (err) {
      logEvent("ui.task.create.error", { error: err?.message }, "error");
      return { success: false, error: err?.message || "Įvyko nenumatyta klaida.", errorType: "exception" };
    }
  };

  const showChooserAt = (anchorEl) => {
    if (!selection.active || !anchorEl) return;
    clearChooser();

    const rect = anchorEl.getBoundingClientRect();
    const chooser = document.createElement("div");
    chooser.className = "card shadow-sm p-2";
    chooser.style.position = "fixed";
    chooser.style.left = `${Math.max(8, rect.left)}px`;
    chooser.style.top = `${Math.min(window.innerHeight - 80, rect.bottom + 6)}px`;
    chooser.style.zIndex = "1080";
    chooser.style.minWidth = "180px";

    const createDisabled = !canCreateInCurrentContext();
    chooser.innerHTML = `
      <div class="d-grid gap-1">
        <button type="button" class="btn btn-sm btn-primary" data-slot-action="create" ${
          createDisabled ? "disabled" : ""
        }>+ Kurti uzduoti</button>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-slot-action="assign" disabled>⇄ Priskirti esama</button>
        <button type="button" class="btn btn-sm btn-outline-danger" data-slot-action="cancel">✕ Atsaukti</button>
      </div>
    `;

    chooser.addEventListener("click", async (e) => {
      const btn = e.target?.closest?.("[data-slot-action]");
      if (!btn) return;
      e.preventDefault();
      e.stopPropagation();

      const action = btn.dataset.slotAction;
      if (action === "cancel") {
        clearSelection("chooser.cancel");
        return;
      }
      if (action === "create") {
        await createTaskInSelection();
        clearChooser();
        return;
      }
    });

    document.body.appendChild(chooser);
    selection.chooserEl = chooser;
  };

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleSelectionInit === "1") return;
    document.documentElement.dataset.scheduleSelectionInit = "1";

    document.addEventListener("schedule:view:changed", () => clearSelection("view.changed"));
    document.addEventListener("schedule:table:updated", () => clearSelection("table.updated"));

    // Any modal open implies a context switch away from "create in empty slot".
    document.addEventListener("show.bs.modal", () => clearSelection("modal.open"), true);

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape") clearSelection("esc");
    });

    document.addEventListener("click", (evt) => {
      if (!selection.active) return;
      if (evt.target?.closest?.("#scheduleTables")) return;
      if (evt.target?.closest?.(".slot-selection-bar")) return;
      // Don't clear selection when interacting with the task detail panel (offcanvas)
      if (evt.target?.closest?.("#taskDetailModal")) return;
      if (evt.target?.closest?.(".offcanvas")) return;
      // Don't clear selection when interacting with Select2 dropdowns
      if (evt.target?.closest?.(".select2-container")) return;
      if (evt.target?.closest?.(".select2-dropdown")) return;
      clearSelection("outside.click");
    });

    tables.addEventListener("pointerdown", (evt) => {
      const handle = evt.target?.closest?.("[data-slot-handle]");
      if (!handle) return;
      if (handle.classList.contains("is-disabled")) return;
      const dir = handle.dataset.slotHandle;
      handle.classList.add("is-dragging");
      startResize(dir, evt);
    });

    tables.addEventListener("pointerdown", (evt) => {
      const bar = evt.target?.closest?.(".slot-selection-bar");
      if (!bar) return;
      if (!selection.active) return;
      if (evt.target?.closest?.("[data-slot-handle]")) return;

      const rect = bar.getBoundingClientRect();
      const edgeZonePx = 28;
      const leftZone = evt.clientX - rect.left;
      const rightZone = rect.right - evt.clientX;

      let dir = null;
      if (leftZone >= 0 && leftZone <= edgeZonePx) dir = "left";
      else if (rightZone >= 0 && rightZone <= edgeZonePx) dir = "right";
      else return;

      const handle = bar.querySelector(`[data-slot-handle="${dir}"]`);
      if (handle?.classList?.contains?.("is-disabled")) return;
      handle?.classList?.add?.("is-dragging");
      startResize(dir, evt);
    });

    tables.addEventListener("click", (evt) => {
  if (selection.isResizing) return;
  if (Date.now() < (selection.suppressClickUntilTs || 0)) return;
  if (evt.target?.closest?.("[data-slot-handle]")) return;

  const hit = evt.target?.closest?.("[data-slot-hit]");
  if (hit && selection.active) {
    const modal = document.getElementById("taskDetailModal");
    // Panel already open in create mode — sync times without resetting the form
    if (modal?.classList?.contains("show") && modal.dataset.mode === "create") {
      syncSelectionToPanel();
      finalizeSelectionToPanel();
      return;
    }
    // Panel not open yet — open fresh create mode
    if (modal?.classList?.contains("show") && modal.dataset.panelMode === "technician") {
      return;
    }
    void createTaskInSelection();
    return;
  }

  const card = evt.target?.closest?.(".task-card");
  if (card) {
    // OPEN PANEL IN EDIT MODE
    const taskId = card.getAttribute("data-task-id");
    if (taskId) {
      schedule.tasks?.openTaskPanel?.({ taskId });
    }
    return;
  }

  const slot = evt.target?.closest?.(".schedule-slot");
  if (!slot) return;
  if (!isDayView()) return;

  const td = slot.closest("td");
  if (!td || !isEmptySlotCell(td)) return;

  const d = readSlotData(slot);
  if (!d) return;

  clearChooser();
  const ok = applySelectionRange(td, 1);
  if (ok) {
    logEvent("ui.slot.selected", {
      techId: d.techId,
      date: d.date,
      startMinutes: d.startMinutes,
    });
  }
});


    tables.addEventListener("keydown", (evt) => {
      const slot = evt.target?.closest?.(".schedule-slot");
      if (!slot) return;
      if (evt.key !== "Enter" && evt.key !== " ") return;
      evt.preventDefault();
      slot.click();
    });

    tables.addEventListener("contextmenu", (evt) => {
      const bar = evt.target?.closest?.(".slot-selection-bar");
      if (!bar) return;
      if (!selection.active) return;
      evt.preventDefault();
      evt.stopPropagation();
      showChooserAt(bar);
    });

    document.addEventListener("click", (evt) => {
      const handle = evt.target?.closest?.("[data-slot-handle]");
      if (!handle) return;
      if (selection.isResizing) return;
      if (Date.now() < (selection.suppressClickUntilTs || 0)) return;
      if (handle.classList.contains("is-disabled")) return;

      const dir = handle.dataset.slotHandle;
      if (dir === "left") stepExtend("left");
      if (dir === "right") stepExtend("right");
      syncHandleDisabled();
      evt.preventDefault();
      evt.stopPropagation();
    });

    const onContainerScrollOrResize = () => {
      if (!selection.active) return;
      renderBar();
    };

    window.addEventListener("resize", onContainerScrollOrResize);
    tables.addEventListener("scroll", onContainerScrollOrResize, true);

    logEvent("ui.slot.selection.init");
  };

  schedule.selection = {
    initOnce,
    clearSelection,
    submitCreateTask,
    syncCreateRangeFromPanel,
  };
})();



