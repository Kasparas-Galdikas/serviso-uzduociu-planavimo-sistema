(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const tasks = schedule.tasks;

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
    lastPreviewOk: false,
    busy: false,
  };

  const clearPreview = () => {
    document.querySelectorAll("td.is-drop-range, td.is-drop-range-invalid").forEach((td) => {
      td.classList.remove("is-drop-range");
      td.classList.remove("is-drop-range-invalid");
    });
  };

  const clearDragState = () => {
    dragState.taskId = null;
    dragState.version = null;
    dragState.durationSlots = null;
    dragState.lastPreviewOk = false;
    dragState.busy = false;
    document.querySelectorAll(".task-card.is-dragging").forEach((el) => el.classList.remove("is-dragging"));
    document.querySelectorAll(".schedule-slot.is-over").forEach((el) => el.classList.remove("is-over"));
    clearPreview();
  };

  const parseIntSafe = (val) => {
    const n = Number.parseInt(String(val || ""), 10);
    return Number.isFinite(n) ? n : null;
  };

  const getColSpan = (td) => {
    const span = parseIntSafe(td?.getAttribute?.("colspan"));
    return span && span > 0 ? span : 1;
  };

  const isEmptySlotCell = (td) => td?.classList?.contains("empty-slot") && !!td.querySelector?.(".schedule-slot");

  const isOnlyThisTaskInCell = (td, taskId) => {
    if (!td || !taskId) return false;
    const cards = td.querySelectorAll?.(".task-card");
    if (!cards || !cards.length) return false;
    return Array.from(cards).every((c) => String(c.getAttribute("data-task-id") || "") === String(taskId));
  };

  const applyPreviewRange = (startSlotEl, durationSlots, taskId) => {
    clearPreview();
    dragState.lastPreviewOk = false;

    if (!startSlotEl || !durationSlots || durationSlots <= 0) return false;
    const startTd = startSlotEl.closest("td");
    if (!startTd) return false;

    let remaining = durationSlots;
    let td = startTd;
    const touched = [];
    let ok = true;

    while (td && remaining > 0) {
      const span = getColSpan(td);
      const allowed = isEmptySlotCell(td) || isOnlyThisTaskInCell(td, taskId);
      if (!allowed) ok = false;
      touched.push(td);
      remaining -= span;
      td = td.nextElementSibling;
    }

    if (remaining > 0) ok = false;

    touched.forEach((cell) => {
      cell.classList.add(ok ? "is-drop-range" : "is-drop-range-invalid");
    });

    dragState.lastPreviewOk = ok;
    return ok;
  };

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleDragDropInit === "1") return;
    document.documentElement.dataset.scheduleDragDropInit = "1";

    tables.addEventListener("dragstart", (evt) => {
      const handle = evt.target?.closest?.(".task-drag-handle");
      if (!handle) return;
      const card = handle.closest?.(".task-card");
      if (!card) return;

      if (!isDayView() || !isEditModeEnabled() || (card.dataset.editable || "") !== "true") {
        evt.preventDefault();
        return;
      }

      const taskId = card.getAttribute("data-task-id");
      const version = card.getAttribute("data-task-version");
      const durationMinutes = parseIntSafe(card.getAttribute("data-duration-minutes"));
      if (!taskId || !version) return;

      dragState.taskId = taskId;
      dragState.version = version;
      dragState.durationSlots =
        durationMinutes && durationMinutes > 0
          ? Math.max(1, Math.ceil(durationMinutes / 30))
          : 1;
      dragState.busy = false;
      dragState.lastPreviewOk = false;

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
      logEvent("ui.drag.start", { taskId, durationSlots: dragState.durationSlots || 1 });
    });

    tables.addEventListener("dragend", () => {
      logEvent("ui.drag.end", { taskId: dragState.taskId || "none" });
      clearDragState();
    });

    tables.addEventListener("dragover", (evt) => {
      const slot = evt.target?.closest?.(".schedule-slot");
      if (!slot) return;

      if (!isDayView()) return;
      if (!isEditModeEnabled()) return;
      if (!dragState.taskId || !dragState.version || dragState.busy) return;

      applyPreviewRange(slot, dragState.durationSlots || 1, dragState.taskId);
      evt.preventDefault();
      evt.dataTransfer.dropEffect = "move";
    });

    tables.addEventListener("dragenter", (evt) => {
      const slot = evt.target?.closest?.(".schedule-slot");
      if (!slot) return;
      if (!isDayView() || !isEditModeEnabled()) return;
      if (!dragState.taskId || !dragState.version || dragState.busy) return;
      slot.classList.add("is-over");
      applyPreviewRange(slot, dragState.durationSlots || 1, dragState.taskId);
    });

    tables.addEventListener("dragleave", (evt) => {
      const slot = evt.target?.closest?.(".schedule-slot");
      if (!slot) return;
      slot.classList.remove("is-over");
    });

    tables.addEventListener("drop", async (evt) => {
      const slot = evt.target?.closest?.(".schedule-slot");
      if (!slot) return;

      if (!isDayView()) return;
      if (!isEditModeEnabled()) return;
      if (!dragState.taskId || !dragState.version || dragState.busy) return;

      evt.preventDefault();
      slot.classList.remove("is-over");
      const previewOk = applyPreviewRange(slot, dragState.durationSlots || 1, dragState.taskId);
      if (!previewOk) {
        logEvent("ui.drag.drop.blocked.preview", { taskId: dragState.taskId || "none" }, "warn");
        alert("Uzduotis netelpa i laisva laiko tarpa.");
        clearDragState();
        return;
      }

      const taskId = dragState.taskId;
      const expectedVersion = parseIntSafe(dragState.version);
      const targetTechnicianId = parseIntSafe(slot.dataset.techId);
      const targetSlotStartMinutes = parseIntSafe(slot.dataset.slotStartMinutes);
      const targetDate = slot.dataset.date || "";

      if (!expectedVersion || !targetTechnicianId || targetSlotStartMinutes == null || !targetDate) {
        logEvent("ui.drag.drop.invalid", { taskId }, "warn");
        clearDragState();
        return;
      }

      dragState.busy = true;
      logEvent("ui.drag.drop", { taskId, targetTechnicianId, targetSlotStartMinutes, targetDate });

      try {
        const url = `/tasks/${taskId}/move`;
        const body = JSON.stringify({
          targetTechnicianId,
          targetDate,
          targetSlotStartMinutes,
          expectedVersion,
        });

        const resp =
          (await logger?.fetchWithTrace?.(
            url,
            { method: "PATCH", headers: { "Content-Type": "application/json" }, body },
            { event: "ui.task.move", taskId },
          )) ||
          (await fetch(url, {
            method: "PATCH",
            headers: {
              "X-Requested-With": "XMLHttpRequest",
              "Content-Type": "application/json",
            },
            body,
          }));

        const ok = resp.ok;

        if (resp.status === 409) {
          const msg = (await resp.json().catch(() => null))?.message || "Persidengia uzduotys arba uzduotis pasikeite.";
          alert(msg);
        } else if (resp.status === 422) {
          const msg = (await resp.json().catch(() => null))?.message || "Netinkamas laiko langas.";
          alert(msg);
        } else if (resp.status === 403) {
          const msg = (await resp.json().catch(() => null))?.message || "Neturite teisiu perkelti sios uzduoties.";
          alert(msg);
        } else if (!resp.ok) {
          alert("Nepavyko perkelti uzduoties.");
        }

        const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
        if (!query.has("view")) query.set("view", "day");
        await table?.fetchAndRender?.(query, { updateUrl: false });
        if (ok) {
          await tasks?.refreshOpenTask?.(taskId);
        }
      } catch (err) {
        logEvent("ui.drag.drop.error", { taskId, error: err?.message }, "error");
        alert("Nepavyko perkelti uzduoties.");
        const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
        if (!query.has("view")) query.set("view", "day");
        await table?.fetchAndRender?.(query, { updateUrl: false });
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
