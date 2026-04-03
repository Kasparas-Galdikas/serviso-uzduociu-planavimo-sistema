(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  let currentTechnicianId = null;
  let currentTechnicianName = "";
  let editingPeriodId = null;
  let editingDraft = null;
  let previewDebounceTimer = null;

  const getPanelEl = () => document.getElementById("taskDetailModal");
  const getToast = () => schedule.ui?.toast;
  const getTable = () => schedule.table;
  const getStartInput = () => document.getElementById("unavailStart");
  const getEndInput = () => document.getElementById("unavailEnd");
  const getPeriodsList = () => document.getElementById("unavailPeriodsList");
  const getPreviewResult = () => document.getElementById("unavailPreviewResult");
  const getPreviewList = () => document.getElementById("unavailPreviewList");
  const getConfirmButton = () => document.getElementById("unavailConfirmBtn");
  const getEditingRow = () => editingPeriodId
    ? getPeriodsList()?.querySelector?.(`.unavail-period-row[data-period-id="${editingPeriodId}"]`)
    : null;
  const getGridConfig = () => schedule.grid?.getConfig?.() || {
    slotSizeMinutes: Number.parseInt(tables.dataset.slotSizeMinutes || "30", 10) || 30,
    dayStartMinutes: Number.parseInt(tables.dataset.dayStartMinutes || "480", 10) || 480,
    dayEndMinutes: Number.parseInt(tables.dataset.dayEndMinutes || "1200", 10) || 1200,
  };

  const getCurrentTargetDate = () => {
    const query = new URLSearchParams(window.location.search || "");
    const fromQuery = query.get("date");
    if (fromQuery) return fromQuery;

    const firstSlot = tables.querySelector(".schedule-slot[data-date]");
    if (firstSlot?.dataset?.date) {
      return firstSlot.dataset.date;
    }

    return new Date().toISOString().slice(0, 10);
  };

  const isTechnicianPanelOpen = () => {
    const panel = getPanelEl();
    return !!panel?.classList?.contains("show") && panel.dataset.panelMode === "technician";
  };

  const getTechnicianNameById = (technicianId) => {
    const button = document.querySelector(`.tech-name-btn[data-technician-id="${technicianId}"]`);
    return button?.dataset?.technicianName || "";
  };

  const clearEditingHighlights = () => {
    getPeriodsList()
      ?.querySelectorAll(".unavail-period-row.is-editing")
      ?.forEach((row) => row.classList.remove("is-editing"));
  };

  const resetEditingState = () => {
    editingPeriodId = null;
    editingDraft = null;
    clearEditingHighlights();
    if (getConfirmButton()) {
      getConfirmButton().textContent = "Patvirtinti";
    }
  };

  const setBusy = (button, isBusy, busyText = "Vykdoma...") => {
    if (!button) return;
    if (isBusy) {
      if (!button.dataset.originalText) {
        button.dataset.originalText = button.textContent || "";
      }
      button.disabled = true;
      button.setAttribute("aria-busy", "true");
      button.textContent = busyText;
      return;
    }

    button.disabled = false;
    button.removeAttribute("aria-busy");
    if (button.dataset.originalText) {
      button.textContent = button.dataset.originalText;
    }
  };

  const resetFieldError = (inputId, errorId) => {
    const input = document.getElementById(inputId);
    const errorEl = document.getElementById(errorId);
    input?.classList?.remove("is-invalid");
    if (errorEl) {
      errorEl.textContent = "";
      errorEl.classList.add("d-none");
    }
  };

  const setFieldError = (inputId, errorId, message) => {
    const input = document.getElementById(inputId);
    const errorEl = document.getElementById(errorId);
    input?.classList?.add("is-invalid");
    if (errorEl) {
      errorEl.textContent = message;
      errorEl.classList.remove("d-none");
    }
  };

  const clearFieldErrors = () => {
    resetFieldError("unavailStart", "unavailStartError");
    resetFieldError("unavailEnd", "unavailEndError");
  };

  const clearPreview = () => {
    if (previewDebounceTimer) {
      clearTimeout(previewDebounceTimer);
      previewDebounceTimer = null;
    }

    if (getPreviewResult()) {
      getPreviewResult().style.display = "none";
    }

    if (getPreviewList()) {
      getPreviewList().innerHTML = "";
    }
  };

  const formatPeriodLabel = (value) => {
    if (!value) return "";
    const [datePart, timePart = ""] = String(value).split("T");
    return `${datePart} ${timePart.slice(0, 5)}`.trim();
  };

  const formatRangeLabel = (start, end) => `${formatPeriodLabel(start)} - ${formatPeriodLabel(end)}`;
  const formatMinutesLabel = (minutes) => {
    const hours = String(Math.floor(minutes / 60)).padStart(2, "0");
    const mins = String(minutes % 60).padStart(2, "0");
    return `${hours}:${mins}`;
  };

  const formatPreviewTaskTime = (value) => {
    if (!value) return "";

    const date = new Date(value);
    const pad = (part) => String(part).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
  };

  const showEmptyState = () => {
    const listEl = getPeriodsList();
    if (!listEl) return;

    listEl.innerHTML = `
      <div class="unavail-empty-state">
        <i class="bi bi-calendar2-x" aria-hidden="true"></i>
        <div class="unavail-empty-state__title">Neaktyvumo periodu nėra</div>
        <div class="unavail-empty-state__text">Pasirinkite laiką grafike arba iveskite periodą ranka.</div>
      </div>
    `;
  };

  const buildPreviewRow = (task) => `
    <div class="unavail-period-row unavail-period-row--preview">
      <div class="unavail-period-row__content">
        <div class="unavail-period-row__range">${task.title}</div>
        <div class="unavail-period-row__meta">${formatPreviewTaskTime(task.startAt)} - ${formatPreviewTaskTime(task.endAt)}</div>
      </div>
    </div>
  `;

  const renderPreviewTasks = (tasks) => {
    const previewList = getPreviewList();
    const previewResult = getPreviewResult();
    if (!previewList || !previewResult) return;

    previewList.innerHTML = tasks.length === 0
      ? `
        <div class="unavail-period-row unavail-period-row--preview unavail-period-row--empty">
          <div class="unavail-period-row__content">
            <div class="unavail-period-row__range">Šiame laiko periode paveiktų užduočių nėra.</div>
          </div>
        </div>
      `
      : tasks.map((task) => buildPreviewRow(task)).join("");

    previewResult.style.display = "";
  };

  const validatePeriodValues = (startVal, endVal, options = {}) => {
    const { fieldPrefix = null } = options;

    if (fieldPrefix === "top") {
      clearFieldErrors();
    }

    if (!startVal) {
      if (fieldPrefix === "top") {
        setFieldError("unavailStart", "unavailStartError", "Iveskite pradžios data ir laiką.");
      }
      return false;
    }

    if (!endVal) {
      if (fieldPrefix === "top") {
        setFieldError("unavailEnd", "unavailEndError", "Iveskite pabaigos data ir laiką.");
      }
      return false;
    }

    if (endVal <= startVal) {
      if (fieldPrefix === "top") {
        setFieldError("unavailEnd", "unavailEndError", "Pabaiga turi buti velesnė nei pradžia.");
      }
      return false;
    }

    const { slotSizeMinutes, dayStartMinutes, dayEndMinutes } = getGridConfig();
    const toMinutes = (raw) => {
      const [, timePart = ""] = raw.split("T");
      const [hours, minutes] = timePart.split(":").map((part) => Number.parseInt(part || "0", 10));
      return hours * 60 + minutes;
    };
    const startDate = startVal.split("T")[0] || "";
    const endDate = endVal.split("T")[0] || "";
    const startMinutes = toMinutes(startVal);
    const endMinutes = toMinutes(endVal);

    if (startDate !== endDate) {
      if (fieldPrefix === "top") {
        setFieldError("unavailEnd", "unavailEndError", "Neaktyvumo periodas turi tilpti i vienos dienos darbo langą.");
      }
      return false;
    }

    if (startMinutes % slotSizeMinutes !== 0) {
      if (fieldPrefix === "top") {
        setFieldError("unavailStart", "unavailStartError", "Pradžios laikas turi atitikti 30 min. žingsnį.");
      }
      return false;
    }

    if (endMinutes % slotSizeMinutes !== 0) {
      if (fieldPrefix === "top") {
        setFieldError("unavailEnd", "unavailEndError", "Pabaigos laikas turi atitikti 30 min. žingsnį.");
      }
      return false;
    }

    if (startMinutes < dayStartMinutes || endMinutes > dayEndMinutes) {
      if (fieldPrefix === "top") {
        setFieldError(
          "unavailEnd",
          "unavailEndError",
          `Neaktyvumo periodas turi tilpti i darbo laiko langą (${formatMinutesLabel(dayStartMinutes)}-${formatMinutesLabel(dayEndMinutes)}).`
        );
      }
      return false;
    }

    return true;
  };

  const validateTopPeriod = () => validatePeriodValues(
    getStartInput()?.value || "",
    getEndInput()?.value || "",
    { fieldPrefix: "top" }
  );

  const buildRowActions = (periodId, isEditing) => {
    if (isEditing) {
      return `
        <button type="button" class="unavail-inline-btn unavail-inline-btn--confirm" data-action="confirm-edit" data-period-id="${periodId}">Patvirtinti</button>
        <button type="button" class="unavail-inline-btn unavail-inline-btn--cancel" data-action="cancel-edit" data-period-id="${periodId}">Atšaukti</button>
      `;
    }

    return `
      <button type="button" class="unavail-edit-btn" data-action="edit" data-period-id="${periodId}" aria-label="Redaguoti neaktyvumo periodą">
        <i class="bi bi-pencil" aria-hidden="true"></i>
      </button>
      <button type="button" class="unavail-remove-btn" data-action="delete" data-period-id="${periodId}" aria-label="Pašalinti neaktyvumo periodą">
        <i class="bi bi-x-lg" aria-hidden="true"></i>
      </button>
    `;
  };

  const buildEditingRowContent = (period) => {
    const draftStart = editingDraft?.start || period.periodStart;
    const draftEnd = editingDraft?.end || period.periodEnd;
    return `
      <div class="unavail-period-row__editor">
        <input type="datetime-local"
               class="form-control form-control-sm unavail-period-row__input"
               data-edit-field="start"
               data-period-id="${period.id}"
               step="1800"
               value="${draftStart}">
        <span class="unavail-period-row__separator">-</span>
        <input type="datetime-local"
               class="form-control form-control-sm unavail-period-row__input"
               data-edit-field="end"
               data-period-id="${period.id}"
               step="1800"
               value="${draftEnd}">
      </div>
    `;
  };

  const renderPeriods = (periods) => {
    const listEl = getPeriodsList();
    if (!listEl) return;

    if (!Array.isArray(periods) || periods.length === 0) {
      showEmptyState();
      return;
    }

    listEl.innerHTML = periods.map((period) => {
      const isEditing = String(editingPeriodId) === String(period.id);
      return `
        <div class="unavail-period-row ${isEditing ? "is-editing" : ""}"
             data-period-id="${period.id}"
             data-period-start="${period.periodStart}"
             data-period-end="${period.periodEnd}">
          <div class="unavail-period-row__content">
            ${isEditing
              ? buildEditingRowContent(period)
              : `<div class="unavail-period-row__range">${formatRangeLabel(period.periodStart, period.periodEnd)}</div>`}
          </div>
          <div class="unavail-period-row__actions">
            ${buildRowActions(period.id, isEditing)}
          </div>
        </div>
      `;
    }).join("");
  };

  const parseApiError = async (response, fallbackMessage) => {
    const text = await response.text().catch(() => "");
    if (!text) return fallbackMessage;

    try {
      const json = JSON.parse(text);
      if (json?.error || json?.message) {
        return json.error || json.message;
      }
    } catch {
      return text || fallbackMessage;
    }

    return text || fallbackMessage;
  };

  const refreshSchedule = async () => {
    const query = schedule.state?.getQuery?.() || new URLSearchParams(window.location.search || "");
    if (!query.has("view")) {
      query.set("view", tables.dataset.currentView || "day");
    }

    const success = await getTable()?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
    if (!success) {
      logEvent("ui.unavailability.refresh.error");
    }
  };

  const buildRequestBody = (periodStart, periodEnd) => ({
    technicianId: currentTechnicianId,
    periodStart,
    periodEnd,
  });

  const handlePreview = async () => {
    const periodStart = getStartInput()?.value || "";
    const periodEnd = getEndInput()?.value || "";
    if (!currentTechnicianId || !validateTopPeriod()) {
      clearPreview();
      return;
    }

    try {
      const response = await fetch("/api/unavailability/preview", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Requested-With": "XMLHttpRequest",
        },
        body: JSON.stringify(buildRequestBody(periodStart, periodEnd)),
      });

      if (!response.ok) {
        clearPreview();
        const message = await parseApiError(response, "Nepavyko gauti paveiktu uzduociu saraso.");
        logEvent("ui.unavailability.preview.error", { message }, "error");
        return;
      }

      const payload = await response.json();
      const tasks = Array.isArray(payload.affectedTasks) ? payload.affectedTasks : [];
      renderPreviewTasks(tasks);
    } catch (error) {
      clearPreview();
      logEvent("ui.unavailability.preview.error", { error: String(error) }, "error");
    }
  };

  const schedulePreviewRefresh = () => {
    if (!isTechnicianPanelOpen() || !currentTechnicianId || editingPeriodId) {
      clearPreview();
      return;
    }

    const startValue = getStartInput()?.value || "";
    const endValue = getEndInput()?.value || "";
    if (!startValue || !endValue) {
      clearPreview();
      return;
    }

    if (previewDebounceTimer) {
      clearTimeout(previewDebounceTimer);
    }

    previewDebounceTimer = setTimeout(async () => {
      previewDebounceTimer = null;
      await handlePreview();
    }, 250);
  };

  const loadDefaultsFromService = async () => {
    const startInput = getStartInput();
    const endInput = getEndInput();
    if (!startInput || !endInput) return;

    try {
      const targetDate = getCurrentTargetDate();
      const response = await fetch(`/api/unavailability/defaults?targetDate=${encodeURIComponent(targetDate)}`, {
        headers: { "X-Requested-With": "XMLHttpRequest" },
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = await response.json();
      startInput.value = data.periodStart || "";
      endInput.value = data.periodEnd || "";
      startInput.step = "1800";
      endInput.step = "1800";
      const { dayStartMinutes, dayEndMinutes } = getGridConfig();
      const minValue = `${targetDate}T${formatMinutesLabel(dayStartMinutes)}`;
      const maxValue = `${targetDate}T${formatMinutesLabel(dayEndMinutes)}`;
      startInput.min = minValue;
      startInput.max = maxValue;
      endInput.min = minValue;
      endInput.max = maxValue;
      schedulePreviewRefresh();
      logEvent("ui.unavailability.defaults.loaded", { targetDate, technicianId: currentTechnicianId });
    } catch (error) {
      logEvent("ui.unavailability.defaults.error", { error: String(error) }, "error");
    }
  };

  const loadPeriods = async () => {
    const listEl = getPeriodsList();
    if (!listEl || !currentTechnicianId) return;

    listEl.innerHTML = '<span class="text-muted small">Kraunama...</span>';

    try {
      const response = await fetch(`/api/unavailability?technicianId=${currentTechnicianId}`, {
        headers: { "X-Requested-With": "XMLHttpRequest" },
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const periods = await response.json();
      renderPeriods(periods);
    } catch (error) {
      listEl.innerHTML = '<span class="text-danger small">Nepavyko uzkrauti periodu.</span>';
      logEvent("ui.unavailability.load.error", { error: String(error) }, "error");
    }
  };

  const setPanelTechnician = async (technicianId, technicianName, { loadDefaults = true } = {}) => {
    currentTechnicianId = Number(technicianId) || null;
    currentTechnicianName = technicianName || getTechnicianNameById(technicianId) || "";

    const nameEl = document.getElementById("technicianPanelName");
    if (nameEl) {
      nameEl.textContent = currentTechnicianName;
      nameEl.title = currentTechnicianName;
    }

    resetEditingState();
    clearFieldErrors();
    clearPreview();
    await loadPeriods();

    if (loadDefaults) {
      await loadDefaultsFromService();
    }
  };

  const handleCreate = async () => {
    const periodStart = getStartInput()?.value || "";
    const periodEnd = getEndInput()?.value || "";
    if (!currentTechnicianId || !validateTopPeriod()) return;

    const confirmButton = getConfirmButton();
    setBusy(confirmButton, true, "Saugoma...");

    try {
      const response = await fetch("/api/unavailability", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Requested-With": "XMLHttpRequest",
        },
        body: JSON.stringify(buildRequestBody(periodStart, periodEnd)),
      });

      if (!response.ok) {
        const message = await parseApiError(response, "Nepavyko issaugoti neaktyvumo periodo.");
        getToast()?.showError?.(message);
        logEvent("ui.unavailability.create.error", { message, technicianId: currentTechnicianId }, "error");
        return;
      }

      const payload = await response.json();
      const displacedCount = payload.displacedCount ?? 0;
      resetEditingState();
      await Promise.all([loadPeriods(), refreshSchedule()]);
      clearPreview();
      schedulePreviewRefresh();
      getToast()?.showSuccess?.(
        displacedCount > 0
          ? `Neaktyvumas issaugotas. I eile perkelta uzduociu: ${displacedCount}.`
          : "Neaktyvumo periodas issaugotas."
      );
      logEvent("ui.unavailability.create.ok", { technicianId: currentTechnicianId, displacedCount });
    } catch (error) {
      getToast()?.showError?.("Nepavyko issaugoti neaktyvumo periodo.");
      logEvent("ui.unavailability.create.error", { error: String(error) }, "error");
    } finally {
      setBusy(confirmButton, false);
    }
  };

  const handleUpdate = async (periodId) => {
    const periodStart = editingDraft?.start || "";
    const periodEnd = editingDraft?.end || "";
    if (!periodId || !validatePeriodValues(periodStart, periodEnd)) return;

    const confirmButton = getEditingRow()?.querySelector?.('[data-action="confirm-edit"]');
    setBusy(confirmButton, true, "Saugoma...");

    try {
      const response = await fetch(`/api/unavailability/${periodId}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          "X-Requested-With": "XMLHttpRequest",
        },
        body: JSON.stringify(buildRequestBody(periodStart, periodEnd)),
      });

      if (!response.ok) {
        const message = await parseApiError(response, "Nepavyko atnaujinti neaktyvumo periodo.");
        getToast()?.showError?.(message);
        logEvent("ui.unavailability.update.error", { message, periodId }, "error");
        return;
      }

      const payload = await response.json();
      const displacedCount = payload.displacedCount ?? 0;
      resetEditingState();
      await Promise.all([loadPeriods(), refreshSchedule()]);
      clearPreview();
      schedulePreviewRefresh();
      getToast()?.showSuccess?.(
        displacedCount > 0
          ? `Neaktyvumo periodas atnaujintas. I eile perkelta uzduociu: ${displacedCount}.`
          : "Neaktyvumo periodas atnaujintas."
      );
      logEvent("ui.unavailability.update.ok", { periodId, displacedCount });
    } catch (error) {
      getToast()?.showError?.("Nepavyko atnaujinti neaktyvumo periodo.");
      logEvent("ui.unavailability.update.error", { error: String(error), periodId }, "error");
    } finally {
      setBusy(confirmButton, false);
    }
  };

  const handleDelete = async (periodId, row) => {
    try {
      const response = await fetch(`/api/unavailability/${periodId}`, {
        method: "DELETE",
        headers: { "X-Requested-With": "XMLHttpRequest" },
      });

      if (!response.ok) {
        const message = await parseApiError(response, "Nepavyko pasalinti neaktyvumo periodo.");
        throw new Error(message);
      }

      const payload = await response.json();

      if (editingPeriodId && String(editingPeriodId) === String(periodId)) {
        resetEditingState();
      }

      row?.remove?.();
      if (!getPeriodsList()?.querySelector?.("[data-period-id]")) {
        showEmptyState();
      }

      await refreshSchedule();
      schedulePreviewRefresh();

      const restoredCount = payload.restoredCount ?? 0;
      const backlogRemainingCount = payload.backlogRemainingCount ?? 0;
      const message = backlogRemainingCount > 0
        ? `Periodas pasalintas. Atkurta uzduociu: ${restoredCount}, eileje liko: ${backlogRemainingCount}.`
        : restoredCount > 0
          ? `Periodas pasalintas. Atkurta uzduociu: ${restoredCount}.`
          : "Periodas pasalintas.";

      getToast()?.showSuccess?.(message);
      logEvent("ui.unavailability.delete.ok", { periodId, restoredCount, backlogRemainingCount });
    } catch (error) {
      const message = error instanceof Error && error.message
        ? error.message
        : "Nepavyko pasalinti neaktyvumo periodo.";
      getToast()?.showError?.(message);
      logEvent("ui.unavailability.delete.error", { message: String(error), periodId }, "error");
    }
  };

  const handleEditPeriod = async (button) => {
    const row = button?.closest?.(".unavail-period-row");
    const periodStart = row?.dataset?.periodStart || "";
    const periodEnd = row?.dataset?.periodEnd || "";
    if (!periodStart || !periodEnd) return;

    editingPeriodId = row?.dataset?.periodId || null;
    editingDraft = { start: periodStart, end: periodEnd };
    clearPreview();
    await loadPeriods();
    getEditingRow()?.querySelector?.('[data-edit-field="start"]')?.focus?.();
    logEvent("ui.unavailability.edit.prefill", { periodId: editingPeriodId });
  };

  const handleCancelEdit = async () => {
    resetEditingState();
    clearFieldErrors();
    clearPreview();
    await loadDefaultsFromService();
    await loadPeriods();
  };

  const syncRangeFromSelection = async (detail) => {
    if (!isTechnicianPanelOpen() || !detail?.date) return;

    const technicianId = Number(detail.techId) || null;
    if (!technicianId) return;

    const technicianChanged = currentTechnicianId !== technicianId;
    if (technicianChanged) {
      await setPanelTechnician(technicianId, getTechnicianNameById(technicianId), { loadDefaults: false });
    }

    const nextStart = `${detail.date}T${schedule.grid.minutesToTime(detail.startMinutes)}`;
    const nextEnd = `${detail.date}T${schedule.grid.minutesToTime(detail.endMinutes)}`;
    const { dayStartMinutes, dayEndMinutes } = getGridConfig();

    clearPreview();

    if (editingPeriodId) {
      editingDraft = { start: nextStart, end: nextEnd };
      await loadPeriods();
      return;
    }

    const startInput = getStartInput();
    const endInput = getEndInput();
    if (!startInput || !endInput) return;

    startInput.value = nextStart;
    endInput.value = nextEnd;
    startInput.min = `${detail.date}T${formatMinutesLabel(dayStartMinutes)}`;
    startInput.max = `${detail.date}T${formatMinutesLabel(dayEndMinutes)}`;
    endInput.min = `${detail.date}T${formatMinutesLabel(dayStartMinutes)}`;
    endInput.max = `${detail.date}T${formatMinutesLabel(dayEndMinutes)}`;
    clearFieldErrors();
    schedulePreviewRefresh();
    await loadPeriods();
  };

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleUnavailabilityInit === "1") return;
    document.documentElement.dataset.scheduleUnavailabilityInit = "1";
    if (getConfirmButton()) {
      getConfirmButton().textContent = "Patvirtinti";
    }

    tables.addEventListener("click", async (event) => {
      const button = event.target?.closest?.(".tech-name-btn");
      if (!button) return;

      schedule.panel?.showContent?.("technician");
      schedule.panel?.show?.();
      await setPanelTechnician(button.dataset.technicianId, button.dataset.technicianName, { loadDefaults: true });
    });

    document.addEventListener("click", async (event) => {
      if (event.target?.closest?.("#unavailConfirmBtn")) {
        await handleCreate();
        return;
      }

      const actionButton = event.target?.closest?.("[data-action]");
      if (!actionButton) return;

      const action = actionButton.dataset.action;
      const row = actionButton.closest("[data-period-id]");
      const periodId = actionButton.dataset.periodId;

      if (action === "edit") {
        await handleEditPeriod(actionButton);
        return;
      }

      if (action === "confirm-edit") {
        await handleUpdate(periodId);
        return;
      }

      if (action === "cancel-edit") {
        await handleCancelEdit();
        return;
      }

      if (action === "delete") {
        await handleDelete(periodId, row);
      }
    });

    document.addEventListener("input", (event) => {
      if (event.target?.id === "unavailStart" || event.target?.id === "unavailEnd") {
        clearFieldErrors();
        schedulePreviewRefresh();
        return;
      }

      const editField = event.target?.dataset?.editField;
      if (editField && editingPeriodId) {
        editingDraft = editingDraft || { start: "", end: "" };
        editingDraft[editField] = event.target.value || "";
      }
    });

    document.addEventListener("schedule:selection:changed", async (event) => {
      await syncRangeFromSelection(event.detail);
    });

    document.addEventListener("schedule:table:updated", async () => {
      if (!isTechnicianPanelOpen()) return;
      if (currentTechnicianId) {
        await loadPeriods();
        schedulePreviewRefresh();
      }
    });

    logEvent("ui.unavailability.init");
  };

  schedule.unavailability = {
    initOnce,
    loadPeriods,
  };
})();
