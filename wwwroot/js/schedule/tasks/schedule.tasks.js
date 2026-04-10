/**
 * schedule.tasks.js
 *
 * Main orchestrator for task-related functionality.
 * Coordinates between modal, resize, delete, and API modules.
 *
 * Dependencies: task.modal, task.resize, task.delete, task.api, schedule.state, schedule.table, schedule.panel
 * Dependents: schedule.selection, schedule.dragdrop
 */
(() => {
  "use strict";

  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const panel = schedule.panel;
  const selection = schedule.selection;
  const toast = schedule.ui?.toast;

  // Import from sub-modules (with fallbacks for load order)
  const getModal = () => schedule.tasks?.modal;
  const getResize = () => schedule.tasks?.resize;
  const getDelete = () => schedule.tasks?.delete;
  const getTaskApi = () => schedule.tasks?.api;
  const getDom = () => schedule.ui?.dom;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);
  const notifyActionCompleted = (detail = {}) => {
    document.dispatchEvent(new CustomEvent("schedule:action-completed", { detail }));
  };
  const showError = (message, fallback = "Ivyko klaida.") =>
    toast?.showError?.(message || fallback) || alert(message || fallback);
  const showWarning = (message, fallback = "Patikrinkite veiksma.") =>
    toast?.showWarning?.(message || fallback) || alert(message || fallback);
  const showSuccess = (message, fallback = "Veiksmas pavyko.") =>
    toast?.showSuccess?.(message || fallback) || alert(message || fallback);

  // ========== Active Card Management ==========

  const clearActiveCards = () => {
    const dom = getDom();
    dom?.clearActiveCards?.();
  };

  const setActiveCard = (card) => {
    const dom = getDom();
    if (dom?.setActiveCard) {
      dom.setActiveCard(card);
      return;
    }
    if (!card) return;
    clearActiveCards();
    card.classList.add("is-active");
  };

  const setActiveCardByTaskId = (taskId) => {
    const dom = getDom();
    if (dom?.setActiveCardByTaskId) {
      dom.setActiveCardByTaskId(taskId);
      return;
    }
    if (!taskId) return;
    const card = document.querySelector(`.task-card[data-task-id="${CSS.escape(String(taskId))}"]`);
    if (card) setActiveCard(card);
  };

  // ========== Task Fetching ==========

  const fetchTask = async (taskId) => {
    return getTaskApi().fetchTask(taskId);
  };

  const resolveBulkDraftOpenMode = (taskData) => {
    const backlogState = window.schedule?.backlog?.state;
    const bulkSync = window.schedule?.backlog?.bulkSync;
    if (!backlogState?._isBulkMode || !taskData?.id) return { type: "default", idx: -1 };

    const idx = typeof bulkSync?.findBulkIndexByTaskId === "function"
      ? bulkSync.findBulkIndexByTaskId(taskData.id)
      : -1;

    if (idx < 0) return { type: "default", idx: -1 };
    if (Number(taskData.sourceTaskId || 0) > 0) return { type: "default", idx };
    return { type: "bulk-live", idx };
  };

  // ========== Task Save ==========

  const saveTask = async (modalEl) => {
    const modal = getModal();
    const taskApi = getTaskApi();

    const mode = modalEl.dataset.mode || "update";
    const taskId = modalEl.dataset.taskId;
    const viewModel = window.scheduleViewModel || {};
    const viewModelManagerFlag =
      typeof viewModel.isManager === "string"
        ? viewModel.isManager === "true"
        : !!viewModel.isManager;
    const isManager = viewModelManagerFlag || tables?.dataset?.isManager === "true";

    if (mode === "update" && !taskId) {
      showError("Nepavyko nustatyti uzduoties ID.");
      return;
    }

    // Collect and validate form data
    const formData = modal.collectFormData(modalEl);
    const isValid = modal?.validateFormData?.(modalEl, formData, isManager) ?? true;
    if (!isValid) return;

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    const setBusy = (state) => {
      if (saveBtn) {
        saveBtn.disabled = state;
        saveBtn.setAttribute("aria-busy", state ? "true" : "false");
      }
    };

    setBusy(true);
    try {
      let result;

      if (mode === "create") {
        // CREATE MODE
        const placement = modal?.collectCreatePlacement?.(modalEl);
        if (!placement?.ok) {
          modal?.setFieldError?.(modalEl, "#taskDetailStart", placement?.error || "Netinkamas laiko langas.");
          return;
        }

        const createData = {
          technicianId: placement.technicianId,
          targetDate: placement.targetDate,
          startMinutes: placement.startMinutes,
          endMinutes: placement.endMinutes,
          title: formData.title,
          location: formData.location,
          priority: Number.isNaN(formData.priorityVal) ? 1 : formData.priorityVal,
          status: parseInt(formData.status) || 0,
          skillLevel: formData.skillLevel,
          objectName: formData.objectName,
          objectAddress: formData.objectAddress,
          instructions: formData.instructions,
          sourceBacklogItemId: window.schedule?.backlog?.getSchedulingBacklogId?.() ?? null,
        };

        result = await window.schedule?.selection?.submitCreateTask?.(createData);

        if (result?.success) {
          // submitCreateTask already calls card.click() to re-open the panel with the new task.
          // Only hide the panel if there's no new task id to navigate to.
          if (!result.id) {
            const bsModal = window.bootstrap?.Offcanvas?.getInstance?.(modalEl);
            bsModal?.hide?.();
          }
          showSuccess(result.message, "Uzduotis sukurta.");
          document.dispatchEvent(new CustomEvent("schedule:task:created", { detail: { id: result.id } }));
          notifyActionCompleted({
            action: "create",
            primaryTaskId: Number(result.id || 0),
            taskIds: result.id ? [Number(result.id)] : [],
            version: Number(result.version || 0),
          });
        } else if (result?.validationErrors) {
          const fieldMapping = modal?.CREATE_FIELD_MAPPING || {};
          modal?.displayValidationErrors?.(modalEl, result.validationErrors, fieldMapping);
          notifyActionCompleted();
        } else if (result?.error) {
          showWarning(result.error, "Nepavyko sukurti uzduoties.");
          notifyActionCompleted();
        }

      } else {
        // UPDATE MODE
        const version = modalEl.dataset.version || 0;

        if (taskApi?.updateTask && taskApi?.updateTaskAsTechnician) {
          if (isManager) {
            result = await taskApi.updateTask(taskId, {
              title: formData.title,
              location: formData.location,
              startAt: formData.startAt,
              endAt: formData.endAt,
              priority: Number.isNaN(formData.priorityVal) ? 1 : formData.priorityVal,
              technicianId: formData.technicianId || 0,
              skillLevel: formData.skillLevel,
              objectName: formData.objectName,
              objectAddress: formData.objectAddress,
              instructions: formData.instructions,
              technicianComment: formData.technicianComment
            }, version);
          } else {
            result = await taskApi.updateTaskAsTechnician(
              taskId,
              parseInt(formData.status) || 0,
              formData.technicianComment || "",
              version
            );
          }

          if (result?.success) {
            if (result.version) {
              modalEl.dataset.version = result.version;
            }
            const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
            if (!query.has("view")) query.set("view", "day");
            await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
            // Keep the panel open and refresh its content so the filter button doesn't flash.
            // Fall back to hiding only if the panel is no longer showing this task.
            const refreshed = await refreshOpenTask(taskId);
            if (!refreshed) panel?.hide?.();
            showSuccess(result.message, "Uzduotis issaugota.");
            notifyActionCompleted({
              action: "update",
              primaryTaskId: Number(taskId),
              taskIds: [Number(taskId)],
              version: Number(result.version || 0),
            });
          } else if (result?.validationErrors) {
            const fieldMapping = modal?.UPDATE_FIELD_MAPPING || {};
            modal?.displayValidationErrors?.(modalEl, result.validationErrors, fieldMapping);
            notifyActionCompleted();
          } else if (result?.error) {
            showWarning(result.error, "Nepavyko issaugoti uzduoties.");
            notifyActionCompleted();
          }
        }

        logEvent("ui.task.saved", { taskId });
      }
    } catch (err) {
      logEvent("ui.task.save.error", { taskId, error: err?.message }, "error");
      showError("Nepavyko issaugoti uzduoties.");
      notifyActionCompleted();
    } finally {
      setBusy(false);
    }
  };

  // ========== Panel Operations ==========

  const openTaskPanel = async ({
    taskId = null,
    technicianId,
    targetDate,
    startMinutes,
    endMinutes,
    allowEmptySchedule = false,
  }) => {
    const modal = getModal();
    const detailPanel = document.getElementById("taskDetailModal");
    if (!detailPanel) return;

    modal?.ensureObjectAddressBinding?.(detailPanel);

    if (detailPanel?.classList?.contains("is-collapsed")) {
      detailPanel.classList.remove("is-collapsed");
      detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
    }

    if (taskId) {
      // EDIT MODE
      try {
        const data = await fetchTask(taskId);
        const bulkOpenMode = resolveBulkDraftOpenMode(data);
        if (bulkOpenMode.type === "bulk-live") {
          const backlogState = window.schedule?.backlog?.state;
          window.schedule?.backlog?.bulkFlow?.hideBulkConfirmView?.();
          if (backlogState) backlogState._bulkQueueIdx = bulkOpenMode.idx;
          await window.schedule?.backlog?.bulkFlow?.activateBulkQueueItem?.(bulkOpenMode.idx, { skipAutoPlanning: true });
          setActiveCardByTaskId(taskId);
          logEvent("ui.task.panel.open", { mode: "bulk-live", taskId, bulkQueueIdx: bulkOpenMode.idx });
          panel?.show?.();
          return;
        }
        if (modal?.populateTaskModal) {
          await modal.populateTaskModal(detailPanel, data, data.isEditable, {
            onSave: () => saveTask(detailPanel)
          });
        }
        setActiveCardByTaskId(taskId);
        logEvent("ui.task.panel.open", { mode: "edit", taskId });
        panel?.showContent?.("task");
        panel?.show?.();
      } catch (err) {
        logEvent("ui.task.fetch.error", { taskId, error: err?.message }, "error");
        showError("Nepavyko atidaryti uzduoties.");
      }
    } else {
      // CREATE MODE
      if (modal?.prepareCreateMode) {
        await modal.prepareCreateMode(detailPanel, {
          technicianId,
          targetDate,
          startMinutes,
          endMinutes,
          allowEmptySchedule,
          onSave: () => saveTask(detailPanel)
        });
      }
      panel?.showContent?.("task");
      panel?.show?.();
      logEvent("ui.task.panel.open", {
        mode: "create",
        technicianId,
        targetDate,
        startMinutes,
        endMinutes,
        allowEmptySchedule,
      });
    }
  };

  const refreshOpenTask = async (taskId) => {
    const modal = getModal();
    const detailPanel = document.getElementById("taskDetailModal");
    if (!detailPanel) return false;
    if (!detailPanel.classList.contains("show")) return false;
    if (String(detailPanel.dataset.taskId || "") !== String(taskId || "")) return false;

    try {
      const data = await fetchTask(taskId);
      if (modal?.populateTaskModal) {
        await modal.populateTaskModal(detailPanel, data, data.isEditable, {
          onSave: () => saveTask(detailPanel)
        });
      }
      setActiveCardByTaskId(taskId);
      logEvent("ui.task.refresh.ok", { taskId });
      return true;
    } catch (err) {
      logEvent("ui.task.refresh.error", { taskId, error: err?.message }, "error");
      return false;
    }
  };

  // ========== Event Initialization ==========

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleTaskClickInit === "1") return;
    document.documentElement.dataset.scheduleTaskClickInit = "1";

    const deleteModule = getDelete();
    deleteModule?.wireDeleteModal?.();
    deleteModule?.wireDetailDeleteButton?.();

    // Task card click handler
    document.addEventListener("click", async (evt) => {
      if (evt.target?.closest?.(".task-card-action")) return;
      if (evt.target?.closest?.(".task-drag-handle")) return;
      const card = evt.target?.closest?.(".task-card");
      if (!card) return;

      selection?.clearSelection?.("task.open");

      const taskId = card.getAttribute("data-task-id");
      if (!taskId) return;

      if (!state?.assertDayView?.("task.open")) {
        panel?.closeForNonDailyContext?.("week");
        logEvent("ui.task.open.skipped", { taskId, view: state?.getView?.() || "unknown" });
        return;
      }

      try {
        setActiveCard(card);
        await openTaskPanel({ taskId });
      } catch (err) {
        clearActiveCards();
        logEvent("ui.task.fetch.error", { taskId, error: err?.message }, "error");
        showError("Nepavyko atidaryti uzduoties duomenu.");
      }
    });

    // Task card action buttons (delete, edit/resize)
    document.addEventListener("click", (evt) => {
      const action = evt.target?.closest?.(".task-card-action");
      if (!action) return;
      evt.stopPropagation();
      evt.preventDefault();

      const card = action.closest(".task-card");
      if (!card) return;

      const taskId = card.getAttribute("data-task-id");
      const version = parseInt(card.getAttribute("data-task-version") || "0", 10);

      // Delete action can work even if card is not editable
      if (taskId && action.classList.contains("task-card-action-delete")) {
        const deleteModule = getDelete();
        deleteModule?.openDeleteModal?.({ taskId, version, sourceEl: card });
        return;
      }

      // Edit/resize only allowed when card is editable
      if (card.dataset.editable !== "true") return;

      const date = card.getAttribute("data-date");
      const startMinutes = parseInt(card.getAttribute("data-start-minutes") || "0", 10);
      const durationMinutes = parseInt(card.getAttribute("data-duration-minutes") || "0", 10);

      if (action.classList.contains("task-card-action-edit")) {
        const resizeModule = getResize();
        if (taskId) {
          resizeModule?.startInlineResize?.(card, {
            taskId,
            version,
            date,
            startMinutes,
            durationMinutes
          });
        }
      }
    });

    logEvent("ui.tasks.init");
  };

  // ========== Exports ==========

  schedule.tasks = schedule.tasks || {};
  Object.assign(schedule.tasks, {
    initOnce,
    refreshOpenTask,
    setActiveCardByTaskId,
    openTaskPanel,
    saveTask,
    fetchTask,
  });
})();




