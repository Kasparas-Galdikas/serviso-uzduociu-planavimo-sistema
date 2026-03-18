/**
 * task.delete.js
 *
 * Task deletion functionality including confirmation modal
 * and the delete API call with version control.
 *
 * Dependencies: schedule.tasks.api, schedule.table, schedule.panel
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
  const panel = schedule.panel;
  const dom = schedule.ui?.dom;
  const toast = schedule.ui?.toast;
  const taskApi = schedule.tasks?.api;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);
  const showError = (message, fallback = "Nepavyko istrinti uzduoties.") =>
    toast?.showError?.(message || fallback) || alert(message || fallback);
  const showWarning = (message, fallback = "Patikrinkite uzduoties busena.") =>
    toast?.showWarning?.(message || fallback) || alert(message || fallback);
  const showSuccess = (message, fallback = "Uzduotis istrinta.") =>
    toast?.showSuccess?.(message || fallback) || alert(message || fallback);

  // ========== Helpers ==========

  const clearActiveCards = () => {
    dom?.clearActiveCards?.();
  };

  const getDetailPanel = () => dom?.getDetailPanel?.() || document.getElementById("taskDetailModal");

  const formatMinutes = (minutes) => {
    if (!Number.isFinite(minutes)) return "";
    return schedule.grid?.minutesToTime?.(minutes) ?? "";
  };

  const getDeleteModal = () => document.getElementById("taskDeleteModal");
  const getAutoCompactCheckbox = () => document.getElementById("taskDeleteAutoCompact");

  // ========== Modal Setup ==========

  const setDeleteModalDetails = ({ taskId, sourceEl }) => {
    const modalEl = getDeleteModal();
    if (!modalEl) return;

    const setRowText = (rowKey, spanSelector, value) => {
      const row = modalEl.querySelector(`[data-task-delete-row="${rowKey}"]`);
      const span = modalEl.querySelector(spanSelector);
      const text = value ? String(value).trim() : "";
      if (span) span.textContent = text;
      if (row) row.classList.toggle("d-none", !text);
    };

    setRowText("code", "[data-task-delete-code]", taskId ? `#${taskId}` : "");

    let title = "";
    let location = "";
    let timeRange = "";

    if (sourceEl?.classList?.contains?.("task-card")) {
      title = sourceEl.querySelector(".task-title")?.textContent || "";
      location = sourceEl.querySelector(".task-location")?.textContent || "";
      const startMinutes = parseInt(sourceEl.getAttribute("data-start-minutes") || "", 10);
      const durationMinutes = parseInt(sourceEl.getAttribute("data-duration-minutes") || "", 10);
      if (Number.isFinite(startMinutes) && Number.isFinite(durationMinutes)) {
        const endMinutes = startMinutes + durationMinutes;
        timeRange = `${formatMinutes(startMinutes)} - ${formatMinutes(endMinutes)}`;
      }
    } else if (sourceEl?.id === "taskDetailModal") {
      title = sourceEl.querySelector("#taskDetailTitle")?.value || "";
      const locationEl = sourceEl.querySelector("#taskDetailLocation");
      location = locationEl?.value || "";
      const startAt = sourceEl.querySelector("#taskDetailStart")?.value || "";
      const endAt = sourceEl.querySelector("#taskDetailEnd")?.value || "";
      const startTime = startAt.length >= 16 ? startAt.substring(11, 16) : "";
      const endTime = endAt.length >= 16 ? endAt.substring(11, 16) : "";
      if (startTime && endTime) {
        timeRange = `${startTime} - ${endTime}`;
      }
    }

    setRowText("title", "[data-task-delete-title]", title);
    setRowText("time", "[data-task-delete-time]", timeRange);
    setRowText("location", "[data-task-delete-location]", location);
  };

  const openDeleteModal = ({ taskId, version, sourceEl }) => {
    if (!taskId) return;
    const modalEl = getDeleteModal();
    if (!modalEl) return;
    const autoCompactCheckbox = getAutoCompactCheckbox();
    if (autoCompactCheckbox) autoCompactCheckbox.checked = true;
    modalEl.dataset.taskId = String(taskId);
    if (version != null && !Number.isNaN(version)) {
      modalEl.dataset.version = String(version);
    }
    setDeleteModalDetails({ taskId, sourceEl });
    const modal = window.bootstrap?.Modal?.getOrCreateInstance?.(modalEl);
    modal?.show?.();
  };

  // ========== Version Update ==========

  const updateTaskVersion = (taskId, nextVersion) => {
    if (nextVersion == null) return;
    const detailPanel = getDetailPanel();
    if (detailPanel && String(detailPanel.dataset.taskId || "") == String(taskId)) {
      detailPanel.dataset.version = String(nextVersion);
    }
    const card = document.querySelector(`.task-card[data-task-id="${CSS.escape(String(taskId))}"]`);
    if (card) card.setAttribute("data-task-version", String(nextVersion));
  };

  // ========== Delete Logic ==========

  const deleteTaskAndRefresh = async ({ taskId, version, autoCompact = true }) => {
    // Use task.api module if available
    if (taskApi?.deleteTask) {
      const result = await taskApi.deleteTask(taskId, version, { autoCompact });
      if (!result.success) {
        if (result.newVersion) {
          updateTaskVersion(taskId, result.newVersion);
        }
        (result.errorType === "conflict" || result.errorType === "forbidden" || result.errorType === "not_found" ? showWarning : showError)(result.error, "Nepavyko istrinti uzduoties.");
        document.dispatchEvent(new CustomEvent("schedule:action-completed"));
        return false;
      }

      const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      if (!query.has("view")) query.set("view", "day");
      await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
      panel?.hide?.();
      clearActiveCards();
      showSuccess(result.message || "Uzduotis istrinta.");
      document.dispatchEvent(new CustomEvent("schedule:action-completed"));
      return true;
    }

    // Fallback: direct fetch
    const timezoneOffset = -new Date().getTimezoneOffset();
    const resp = await fetch(`/api/tasks/${taskId}`, {
      method: "DELETE",
      headers: {
        "Content-Type": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      },
      body: JSON.stringify({
        version: Number.isFinite(version) ? version : 0,
        autoCompact,
        timezoneOffsetMinutes: timezoneOffset
      })
    });

    if (resp.status === 409) {
      const body = await resp.json().catch(() => ({}));
      updateTaskVersion(taskId, body?.version);
      showWarning(body.message, "Uzduotis pasikeite. Atnaujinkite lentele.");
      document.dispatchEvent(new CustomEvent("schedule:action-completed"));
      return false;
    }

    if (resp.status === 403) {
      const body = await resp.json().catch(() => ({}));
      showWarning(body.message, "Neturite teisiu istrinti sios uzduoties.");
      document.dispatchEvent(new CustomEvent("schedule:action-completed"));
      return false;
    }

    if (resp.status === 404) {
      const body = await resp.json().catch(() => ({}));
      showWarning(body.message, "Uzduotis nerasta.");
      document.dispatchEvent(new CustomEvent("schedule:action-completed"));
      return false;
    }

    if (!resp.ok) {
      const body = await resp.json().catch(() => ({}));
      showError(body.message, "Nepavyko istrinti uzduoties.");
      document.dispatchEvent(new CustomEvent("schedule:action-completed"));
      return false;
    }

    const body = await resp.json().catch(() => ({}));

    const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
    if (!query.has("view")) query.set("view", "day");
    await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
    panel?.hide?.();
    clearActiveCards();
    showSuccess(body.message || "Uzduotis istrinta.");
    document.dispatchEvent(new CustomEvent("schedule:action-completed"));
  };

  // ========== Event Wiring ==========

  const wireDeleteModal = () => {
    const modalEl = getDeleteModal();
    if (!modalEl || modalEl.dataset.bound === "1") return;
    modalEl.dataset.bound = "1";
    const confirmBtn = modalEl.querySelector("#taskDeleteConfirm");
    if (!confirmBtn) return;

    confirmBtn.addEventListener("click", async () => {
      const taskId = modalEl.dataset.taskId;
      const version = parseInt(modalEl.dataset.version || "0", 10);
      const autoCompact = getAutoCompactCheckbox()?.checked !== false;
      if (!taskId) {
        showError("Nepavyko nustatyti uzduoties.");
        return;
      }

      confirmBtn.disabled = true;
      confirmBtn.setAttribute("aria-busy", "true");

      try {
        logEvent("ui.task.delete.requested", { taskId, version, autoCompact });
        const ok = await deleteTaskAndRefresh({ taskId, version, autoCompact });
        if (ok) {
          logEvent("ui.task.delete.succeeded", { taskId });
          const modal = window.bootstrap?.Modal?.getInstance?.(modalEl);
          modal?.hide?.();
        } else {
          logEvent("ui.task.delete.rejected", { taskId });
        }
      } catch (err) {
        const errorMsg = err?.message || "Nepavyko istrinti uzduoties.";
        logEvent("ui.task.delete.error", { taskId, error: errorMsg }, "error");
        showError(errorMsg, "Nepavyko istrinti uzduoties.");
        document.dispatchEvent(new CustomEvent("schedule:action-completed"));
      } finally {
        confirmBtn.disabled = false;
        confirmBtn.removeAttribute("aria-busy");
      }
    });
  };

  const wireDetailDeleteButton = () => {
    const detailPanel = getDetailPanel();
    if (!detailPanel) return;
    const deleteBtn = detailPanel.querySelector("#taskDetailDelete");
    if (!deleteBtn || deleteBtn.dataset.bound === "1") return;
    deleteBtn.dataset.bound = "1";
    deleteBtn.addEventListener("click", (evt) => {
      evt.preventDefault();
      const taskId = detailPanel.dataset.taskId;
      const version = parseInt(detailPanel.dataset.version || "0", 10);
      openDeleteModal({ taskId, version, sourceEl: detailPanel });
    });
  };

  // ========== Exports ==========

  schedule.tasks.delete = {
    openDeleteModal,
    deleteTaskAndRefresh,
    updateTaskVersion,
    wireDeleteModal,
    wireDetailDeleteButton,
  };
})();