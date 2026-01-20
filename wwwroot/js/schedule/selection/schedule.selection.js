(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const isDayView = () => (state?.getView?.() || "day") === "day";

  const parseIntSafe = (val) => {
    const n = Number.parseInt(String(val || ""), 10);
    return Number.isFinite(n) ? n : null;
  };

  const getGrid = () => {
    const slotSizeMinutes = parseIntSafe(tables.dataset.slotSizeMinutes) || 30;
    const dayStartMinutes = parseIntSafe(tables.dataset.dayStartMinutes) || 8 * 60;
    const dayEndMinutes = parseIntSafe(tables.dataset.dayEndMinutes) || 20 * 60;
    return { slotSizeMinutes, dayStartMinutes, dayEndMinutes };
  };

  const minutesToTime = (m) => {
    const mm = Math.max(0, m | 0);
    const h = String(Math.floor(mm / 60)).padStart(2, "0");
    const min = String(mm % 60).padStart(2, "0");
    return `${h}:${min}`;
  };
  const formatRange = (start, end) => `${minutesToTime(start)}–${minutesToTime(end)}`;

  const isEmptySlotCell = (td) => td?.classList?.contains("empty-slot") && !!td.querySelector?.(".schedule-slot");

  const getColSpan = (td) => {
    const span = parseIntSafe(td?.getAttribute?.("colspan"));
    return span && span > 0 ? span : 1;
  };

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
    selection.barEl?.remove?.();
    selection.barEl = null;
    selection.barContainerEl = null;
    selection.active = false;
    selection.techId = null;
    selection.date = null;
    selection.startMinutes = null;
    selection.endMinutes = null;
    selection.durationSlots = 1;
    selection.startTd = null;
    if (reason) logEvent("ui.slot.selection.clear", { reason });
  };

  const readSlotData = (slotEl) => {
    const techId = parseIntSafe(slotEl?.dataset?.techId);
    const startMinutes = parseIntSafe(slotEl?.dataset?.slotStartMinutes);
    const date = slotEl?.dataset?.date || null;
    if (!techId || startMinutes == null || !date) return null;
    return { techId, startMinutes, date };
  };

  const applySelectionRange = (startTd, durationSlots) => {
    const { slotSizeMinutes } = getGrid();

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

  const getLastTdInRange = (startTd, durationSlots) => {
    let remaining = durationSlots || 1;
    let td = startTd;
    while (td && remaining > 0) {
      remaining -= getColSpan(td);
      if (remaining <= 0) return td;
      td = td.nextElementSibling;
    }
    return null;
  };

  const syncHandleDisabled = () => {
    if (!selection.active || !selection.startTd) return;
    const { slotSizeMinutes, dayStartMinutes, dayEndMinutes } = getGrid();
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

    const bar = document.createElement("div");
    bar.className = "slot-selection-bar";
    bar.innerHTML = `
      <button type="button" class="slot-selection-hit" data-slot-hit aria-label="Veiksmai: kurti uzduoti arba priskirti"></button>
      <button type="button" class="slot-selection-handle" data-slot-handle="left" aria-label="Keisti pradzios laika" title="Keisti pradzios laika">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟨</span>
      </button>
      <div class="slot-selection-range" data-slot-range aria-hidden="true"></div>
      <button type="button" class="slot-selection-handle" data-slot-handle="right" aria-label="Keisti pabaigos laika" title="Keisti pabaigos laika">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟩</span>
      </button>
    `;
    container.appendChild(bar);
    selection.barEl = bar;
  };

  const renderBar = () => {
    if (!selection.active || !selection.startTd) return;
    if (!selection.barEl || !selection.barContainerEl) ensureBar();
    if (!selection.barEl || !selection.barContainerEl) return;

    const startTd = selection.startTd;
    const endTd = getLastTdInRange(startTd, selection.durationSlots || 1) || startTd;

    const container = selection.barContainerEl;
    const containerRect = container.getBoundingClientRect();
    const startRect = startTd.getBoundingClientRect();
    const endRect = endTd.getBoundingClientRect();

    // Nudge by 1px so the bar aligns with table borders (prevents right handle drifting outside the last cell).
    const left = startRect.left - containerRect.left + container.scrollLeft + 1;
    const top = startRect.top - containerRect.top + container.scrollTop;
    const width = Math.max(1, endRect.right - startRect.left - 2);
    const height = Math.max(1, startRect.height);

    selection.barEl.style.left = `${left}px`;
    selection.barEl.style.top = `${top}px`;
    selection.barEl.style.width = `${width}px`;
    selection.barEl.style.height = `${height}px`;

    const label = selection.barEl.querySelector("[data-slot-range]");
    if (label) {
      label.textContent = `${minutesToTime(selection.startMinutes ?? 0)}–${minutesToTime(
        selection.endMinutes ?? 0,
      )}`;
    }
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

  const getTdIndexInRow = (td) => {
    const row = td?.parentElement;
    if (!row) return null;
    const tds = Array.from(row.querySelectorAll("td"));
    const idx = tds.indexOf(td);
    return idx >= 0 ? idx : null;
  };

  const getTdAtIndexInRow = (row, index) => {
    if (!row || index == null) return null;
    const tds = Array.from(row.querySelectorAll("td"));
    if (index < 0 || index >= tds.length) return null;
    return tds[index];
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

    // Open task detail modal in CREATE mode
    const modal = document.getElementById("taskDetailModal");
    if (!modal) return;

    // Clear the form and set to CREATE mode
    const form = {
      technicianId: selection.techId,
      targetDate: selection.date,
      startMinutes: selection.startMinutes,
      endMinutes: selection.endMinutes,
      date: selection.date,
      startTime: minutesToTimeString(selection.startMinutes),
      endTime: minutesToTimeString(selection.endMinutes),
    };

    // Store selection data for later submission
    modal.dataset.mode = "create";
    modal.dataset.technicianId = selection.techId;
    modal.dataset.targetDate = selection.date;
    modal.dataset.startMinutes = selection.startMinutes;
    modal.dataset.endMinutes = selection.endMinutes;
    delete modal.dataset.taskId;
    delete modal.dataset.version;

    // Calculate datetime-local values for form inputs
    const startDateTime = `${selection.date}T${form.startTime}`;
    const endDateTime = `${selection.date}T${form.endTime}`;

    // Populate form fields
    modal.querySelector("#taskDetailTitle").value = "";
    modal.querySelector("#taskDetailLocation").value = "";
    modal.querySelector("#taskDetailStart").value = startDateTime;
    modal.querySelector("#taskDetailEnd").value = endDateTime;
    modal.querySelector("#taskDetailPriority").value = ""; // force user to choose
    modal.querySelector("#taskDetailStatus").value = "0"; // Planned

    // Set title
    modal.querySelector("#taskDetailLabel").textContent = "Nauja užduotis";
    modal.querySelector("#taskDetailCode").textContent = `Laikas: ${form.startTime} - ${form.endTime}`;

    // Enable all inputs
    const inputs = modal.querySelectorAll("input, select, textarea");
    inputs.forEach((el) => el.removeAttribute("disabled"));

    // Show save button
    const saveBtn = modal.querySelector("#taskDetailSave");
    if (saveBtn) {
      saveBtn.classList.remove("d-none");
      saveBtn.textContent = "Sukurti";
    }

    // Open the modal
    const bsModal = window.bootstrap?.Offcanvas?.getOrCreateInstance?.(modal);
    bsModal?.show?.();

    logEvent("ui.task.create.form.open", { techId: selection.techId, date: selection.date });
  };

  // Helper to convert minutes to HH:MM format
  const minutesToTimeString = (minutes) => {
    const h = String(Math.floor(minutes / 60)).padStart(2, "0");
    const m = String(minutes % 60).padStart(2, "0");
    return `${h}:${m}`;
  };

  // Export the createTask function so tasks.js can call the API
  const submitCreateTask = async (formData) => {
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
      // Additional fields
      shift: formData.shift,
      taskType: formData.taskType,
      skillLevel: formData.skillLevel,
      objectName: formData.objectName,
      objectAddress: formData.objectAddress,
      objectType: formData.objectType,
      instructions: formData.instructions,
      safetyRequirements: formData.safetyRequirements,
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
        // ValidationProblem response - parse errors object
        const errorData = await resp.json().catch(() => ({}));
        if (errorData.errors && typeof errorData.errors === "object") {
          // Return validation errors for form to display
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

      // Response includes TaskCreatedOrUpdatedResponseDto with id and version
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

      return { success: true, id };
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
    // ✅ OPEN PANEL IN CREATE MODE
    const { techId, date, startMinutes, endMinutes } = selection;

    schedule.tasks?.openTaskPanel?.({
      technicianId: techId,
      targetDate: date,
      startMinutes,
      endMinutes,
    });

    return;
  }

  const card = evt.target?.closest?.(".task-card");
  if (card) {
    // ✅ OPEN PANEL IN EDIT MODE
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

  // === Inline resize for existing task cards (reuses slot-selection visuals) ===
  const startTaskResize = ({ anchorEl, taskId, version, techId, date, startMinutes, endMinutes, onApply }) => {
    if (!anchorEl || !taskId || !date) return;
    const { slotSizeMinutes, dayStartMinutes, dayEndMinutes } = getGrid();
    let currentStart = startMinutes ?? 0;
    let currentEnd = endMinutes ?? currentStart + slotSizeMinutes;

    const parentCell = anchorEl.closest("td") || anchorEl.parentElement;
    if (!parentCell) return;
    const container = parentCell;
    if (getComputedStyle(container).position === "static") {
      container.style.position = "relative";
    }

    const bar = document.createElement("div");
    bar.className = "slot-selection-bar task-resize-bar";
    bar.innerHTML = `
      <button type="button" class="slot-selection-hit" data-slot-hit aria-label="Keisti laiką"></button>
      <button type="button" class="slot-selection-handle" data-slot-handle="left" aria-label="Anksčiau">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟨</span>
      </button>
      <div class="slot-selection-range" data-slot-range aria-hidden="true">${formatRange(currentStart, currentEnd)}</div>
      <button type="button" class="slot-selection-handle" data-slot-handle="right" aria-label="Vėliau">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟩</span>
      </button>
    `;

    bar.style.position = "absolute";
    bar.style.left = "6px";
    bar.style.right = "6px";
    bar.style.top = "6px";
    bar.style.height = "42px";
    bar.style.zIndex = "8";

    const teardown = () => {
      bar.remove();
    };

    const applyUpdate = async (nextStart, nextEnd) => {
      if (typeof onApply !== "function") return false;
      const ok = await onApply({
        taskId,
        version,
        techId,
        date,
        startMinutes: nextStart,
        endMinutes: nextEnd
      });
      if (ok) {
        currentStart = nextStart;
        currentEnd = nextEnd;
        const label = bar.querySelector("[data-slot-range]");
        if (label) label.textContent = formatRange(currentStart, currentEnd);
      }
      return ok;
    };

    const adjust = async (dir, shrink = false) => {
      let nextStart = currentStart;
      let nextEnd = currentEnd;
      if (dir === "left" && !shrink) {
        if (currentStart - slotSizeMinutes < dayStartMinutes) return;
        nextStart = currentStart - slotSizeMinutes;
      } else if (dir === "left" && shrink) {
        if (currentEnd - slotSizeMinutes <= currentStart + slotSizeMinutes) return;
        nextStart = currentStart + slotSizeMinutes;
      } else if (dir === "right" && !shrink) {
        if (currentEnd + slotSizeMinutes > dayEndMinutes) return;
        nextEnd = currentEnd + slotSizeMinutes;
      } else if (dir === "right" && shrink) {
        if (currentEnd - slotSizeMinutes <= currentStart + slotSizeMinutes) return;
        nextEnd = currentEnd - slotSizeMinutes;
      }

      const ok = await applyUpdate(nextStart, nextEnd);
      if (ok) {
        version = (version || 0) + 1; // optimistic; caller should update actual version
      }
    };

    const attachDrag = (handle, dir) => {
      if (!handle) return;
      handle.style.cursor = "ew-resize";
      handle.addEventListener("pointerdown", (e) => {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        handle.setPointerCapture(e.pointerId);

        const startX = e.clientX;
        const baseStart = currentStart;
        const baseEnd = currentEnd;

        const move = (ev) => {
          const dx = ev.clientX - startX;
          const slotsDelta = Math.round(dx / (slotSizeMinutes * 2)); // rough scale safeguard
          let nextStart = baseStart;
          let nextEnd = baseEnd;
          if (dir === "left") {
            nextStart = baseStart + slotsDelta * slotSizeMinutes;
            nextStart = Math.min(nextStart, nextEnd - slotSizeMinutes);
          } else {
            nextEnd = baseEnd + slotsDelta * slotSizeMinutes;
            nextEnd = Math.max(nextEnd, nextStart + slotSizeMinutes);
          }
          nextStart = Math.max(dayStartMinutes, nextStart);
          nextEnd = Math.min(dayEndMinutes, nextEnd);
          currentStart = nextStart;
          currentEnd = nextEnd;
          const label = bar.querySelector("[data-slot-range]");
          if (label) label.textContent = formatRange(currentStart, currentEnd);
        };

        const up = async (ev) => {
          handle.releasePointerCapture(ev.pointerId);
          handle.removeEventListener("pointermove", move);
          handle.removeEventListener("pointerup", up);
          handle.removeEventListener("pointercancel", up);
          const changed = currentStart !== baseStart || currentEnd !== baseEnd;
          if (changed) {
            await applyUpdate(currentStart, currentEnd);
          }
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", up);
        handle.addEventListener("pointercancel", up);
      });
    };

    attachDrag(bar.querySelector('[data-slot-handle="left"]'), "left");
    attachDrag(bar.querySelector('[data-slot-handle="right"]'), "right");

    // Click to step by one slot (kept for keyboard/mouse users)
    bar.querySelector('[data-slot-handle="left"]')?.addEventListener("click", (e) => {
      e.stopPropagation();
      adjust("left", e.shiftKey || e.altKey);
    });
    bar.querySelector('[data-slot-handle="right"]')?.addEventListener("click", (e) => {
      e.stopPropagation();
      adjust("right", e.shiftKey || e.altKey);
    });

    bar.querySelector("[data-slot-hit]")?.addEventListener("click", (e) => {
      e.stopPropagation();
      teardown();
    });

    container.appendChild(bar);
  };

  schedule.selection = {
    initOnce,
    clearSelection,
    submitCreateTask,
    startTaskResize
  };
})();
