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

  const clearChooser = () => {
    selection.chooserEl?.remove?.();
    selection.chooserEl = null;
  };

  const clearSelection = (reason) => {
    clearChooser();
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
    if (reason) logEvent("ui.slot.selection.clear", { reason });
  };

  // Use shared DOM utility for reading slot data (stable load order guaranteed)
  const readSlotData = dom.readSlotData;

  // Sync current selection start/end to the panel form if it is already open in create mode.
  // Called after every resize/extend so the panel stays in step without resetting other fields.
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

  const applySelectionRange = (startTd, durationSlots) => {
    const { slotSizeMinutes } = grid.getConfig();

    if (!startTd || !durationSlots || durationSlots <= 0) return false;
    if (!isEmptySlotCell(startTd)) return false;

    let remaining = durationSlots;
    let td = startTd;
    const touched = [];
    let ok = true;

    while (td && remaining > 0) {
      const span = getColSpan(td);
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
    return true;
  };

  const canApplyRange = (candidateStartTd, candidateDurationSlots) => {
    if (!candidateStartTd || !candidateDurationSlots || candidateDurationSlots <= 0) return false;
    if (!isEmptySlotCell(candidateStartTd)) return false;

    let remaining = candidateDurationSlots;
    let td = candidateStartTd;
    while (td && remaining > 0) {
      const span = getColSpan(td);
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
        const lastTouchedTd = getLastTdInRange(selection.startTd, selection.durationSlots || 1);
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
    const endTd = getLastTdInRange(startTd, selection.durationSlots || 1) || startTd;

    // Use shared slotOverlay.positionFromTds (stable load order)
    slotOverlay.positionFromTds(selection.barEl, selection.barContainerEl, startTd, endTd);

    // Update range label via slotOverlay (stable load order)
    slotOverlay.updateRangeLabel(selection.barEl, selection.startMinutes ?? 0, selection.endMinutes ?? 0);
  };

  const stepExtend = (dir) => {
    if (!selection.active || !selection.startTd) return;
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

    const startTd = selection.startTd;
    const endTd = getLastTdInRange(startTd, selection.durationSlots || 1) || startTd;
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
    return isManager && status === "draft";
  };

  const createTaskInSelection = async () => {
    if (!selection.active) return;
    if (!canCreateInCurrentContext()) {
      alert("Uzduotis galima kurti tik juodrascio plane.");
      return;
    }

    await schedule.tasks?.openTaskPanel?.({
      technicianId: selection.techId,
      targetDate: selection.date,
      startMinutes: selection.startMinutes,
      endMinutes: selection.endMinutes,
    });

    logEvent("ui.task.create.form.open", { techId: selection.techId, date: selection.date });
  };

  // Export the createTask function so tasks.js can call the API
  const submitCreateTask = async (formData) => {
    // Use task.api module if available
    if (taskApi?.createTask) {
      const result = await taskApi.createTask({
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
          "/api/tasks",
          { method: "POST", headers: { "Content-Type": "application/json" }, body },
          { event: "ui.task.create", techId: formData.technicianId, date: formData.targetDate },
        )) ||
        (await fetch("/api/tasks", {
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
      return;
    }
    // Panel not open yet — open fresh create mode
    schedule.tasks?.openTaskPanel?.({
      technicianId: selection.techId,
      targetDate: selection.date,
      startMinutes: selection.startMinutes,
      endMinutes: selection.endMinutes,
    });
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



