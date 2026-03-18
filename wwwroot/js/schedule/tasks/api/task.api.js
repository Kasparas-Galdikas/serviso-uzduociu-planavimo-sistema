/**
 * task.api.js
 *
 * Task-specific API operations for CRUD and related entities.
 * Wraps schedule.api with task-specific endpoints and data transformations.
 *
 * Dependencies: schedule.api, schedule.grid
 * Dependents: task.panel, task.form, schedule.selection
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};
  window.schedule.tasks = window.schedule.tasks || {};

  const api = window.schedule.api;
  const grid = window.schedule.grid;
  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);
  const readJsonSafe = async (resp) => await resp.json().catch(() => ({}));

  // Cached promises for dropdown data
  let techniciansPromise = null;
  let objectsPromise = null;

  /**
   * Fetch a single task by ID.
   * @param {string|number} taskId - The task ID
   * @returns {Promise<Object>} The task data
   * @throws {Error} If the fetch fails
   */
  const fetchTask = async (taskId) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const resp = await (api?.get || fetch)(`/api/tasks/${taskId}`, {
      headers: { "X-Timezone-Offset": String(timezoneOffset) }
    }, { event: "api.task.fetch", taskId });

    if (!resp.ok) {
      logEvent("api.task.fetch.error", { taskId, status: resp.status }, "error");
      throw new Error("Nepavyko gauti uÃ…Â¾duoties duomenÃ…Â³");
    }

    const data = await resp.json();
    logEvent("api.task.fetch.ok", { taskId });
    return data;
  };

  /**
   * Create a new task.
   * @param {Object} taskData - The task data
   * @returns {Promise<{ success: boolean, id?: number, version?: number, error?: string, validationErrors?: Object }>}
   */
  const createTask = async (taskData) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      technicianId: taskData.technicianId,
      targetDate: taskData.targetDate,
      startMinutes: taskData.startMinutes,
      endMinutes: taskData.endMinutes,
      title: taskData.title,
      location: taskData.location || "",
      priority: taskData.priority,
      status: taskData.status ?? 0,
      timezoneOffsetMinutes: timezoneOffset,
      skillLevel: taskData.skillLevel,
      objectName: taskData.objectName,
      objectAddress: taskData.objectAddress,
      instructions: taskData.instructions,
    };

    try {
      const resp = await (api?.post
        ? api.post("/api/tasks", body, {}, { event: "api.task.create", techId: taskData.technicianId })
        : fetch("/api/tasks", {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (resp.status === 409) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Laiko langas uÃ…Â¾imtas.", errorType: "conflict"};
      }

      if (resp.status === 400) {
        const errorData = await readJsonSafe(resp);
        if (errorData.errors && typeof errorData.errors === "object") {
          return { success: false, validationErrors: errorData.errors, errorType: "validation"};
        }
        return { success: false, error: errorData.message || "Patikrinkite Ã„Â¯vestus duomenis.", errorType: "validation"};
      }

      if (resp.status === 422) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Netinkamas laiko langas.", errorType: "unprocessable"};
      }

      if (resp.status === 403) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Neturite teisiÃ…Â³ kurti uÃ…Â¾duoties.", errorType: "forbidden"};
      }

      if (!resp.ok) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Ã„Â®vyko klaida kuriant uÃ…Â¾duotÃ„Â¯.", errorType: "unknown"};
      }

      const responseData = await readJsonSafe(resp);
      logEvent("api.task.create.ok", { id: responseData?.id });
      return {
        success: true,
        id: responseData?.id,
        version: responseData?.version,
        message: responseData?.message,
      };
    } catch (err) {
      logEvent("api.task.create.error", { error: err?.message }, "error");
      return { success: false, error: err?.message || "Ã„Â®vyko nenumatyta klaida.", errorType: "exception" };
    }
  };

  /**
   * Update a task as a manager (full update).
   * @param {string|number} taskId - The task ID
   * @param {Object} taskData - The updated task data
   * @param {number} version - Current version for optimistic locking
   * @param {{ autoCompact?: boolean }} [options] - Delete behavior options
   * @returns {Promise<{ success: boolean, version?: number, error?: string, validationErrors?: Object }>}
   */
  const updateTask = async (taskId, taskData, version) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      title: taskData.title,
      location: taskData.location,
      startAt: taskData.startAt,
      endAt: taskData.endAt,
      priority: taskData.priority,
      version: parseInt(version) || 0,
      timezoneOffsetMinutes: timezoneOffset,
      technicianId: taskData.technicianId || 0,
      skillLevel: taskData.skillLevel,
      objectName: taskData.objectName,
      objectAddress: taskData.objectAddress,
      instructions: taskData.instructions,
      technicianComment: taskData.technicianComment,
    };

    try {
      const resp = await (api?.patch
        ? api.patch(`/api/tasks/${taskId}`, body, {}, { event: "api.task.update", taskId, role: "manager" })
        : fetch(`/api/tasks/${taskId}`, {
            method: "PATCH",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      return await handleUpdateResponse(resp, taskId);
    } catch (err) {
      logEvent("api.task.update.error", { taskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko iÃ…Â¡saugoti uÃ…Â¾duoties.", errorType: "exception" };
    }
  };

  /**
   * Update a task as a technician (status and comment only).
   * @param {string|number} taskId - The task ID
   * @param {number} status - The new status
   * @param {string} comment - The technician comment
   * @param {number} version - Current version for optimistic locking
   * @param {{ autoCompact?: boolean }} [options] - Delete behavior options
   * @returns {Promise<{ success: boolean, version?: number, error?: string }>}
   */
  const updateTaskAsTechnician = async (taskId, status, comment, version) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      status: parseInt(status) || 0,
      comment: comment || "",
      version: parseInt(version) || 0,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.patch
        ? api.patch(`/api/tasks/${taskId}/technician-update`, body, {}, { event: "api.task.update", taskId, role: "technician" })
        : fetch(`/api/tasks/${taskId}/technician-update`, {
            method: "PATCH",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      return await handleUpdateResponse(resp, taskId);
    } catch (err) {
      logEvent("api.task.update.error", { taskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko iÃ…Â¡saugoti uÃ…Â¾duoties.", errorType: "exception" };
    }
  };

  /**
   * Update task time/position.
   * @param {string|number} taskId - The task ID
   * @param {Object} timeData - Time update data
   * @param {number} version - Current version for optimistic locking
   * @param {{ autoCompact?: boolean }} [options] - Delete behavior options
   * @returns {Promise<{ success: boolean, version?: number, startAt?: string, endAt?: string, error?: string }>}
   */
  const updateTaskTime = async (taskId, timeData, version) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      targetDate: timeData.targetDate,
      targetSlotStartMinutes: timeData.startMinutes,
      targetSlotEndMinutes: timeData.endMinutes,
      version: parseInt(version) || 0,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.patch
        ? api.patch(`/api/tasks/${taskId}/time`, body, {}, { event: "api.task.time", taskId })
        : fetch(`/api/tasks/${taskId}/time`, {
            method: "PATCH",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (resp.status === 409 || resp.status === 403) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message, version: data.version, errorType: resp.status === 409 ? "conflict" : "forbidden"};
      }

      if (!resp.ok) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Nepavyko pakeisti laiko.", errorType: "unknown"};
      }

      const data = await readJsonSafe(resp);
      logEvent("api.task.time.ok", { taskId, newVersion: data?.version });
      return {
        success: true,
        version: data?.version,
        startAt: data?.startAt,
        endAt: data?.endAt,
        message: data?.message,
      };
    } catch (err) {
      logEvent("api.task.time.error", { taskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko pakeisti laiko.", errorType: "exception" };
    }
  };

  /**
   * Delete a task.
   * @param {string|number} taskId - The task ID
   * @param {number} version - Current version for optimistic locking
   * @param {{ autoCompact?: boolean }} [options] - Delete behavior options
   * @returns {Promise<{ success: boolean, error?: string, newVersion?: number }>}
   */
  const deleteTask = async (taskId, version, options = {}) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      version: Number.isFinite(version) ? version : 0,
      autoCompact: options.autoCompact !== false,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.delete
        ? api.delete(`/api/tasks/${taskId}`, body, {}, { event: "api.task.delete", taskId })
        : fetch(`/api/tasks/${taskId}`, {
            method: "DELETE",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (resp.status === 409) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "UÃ…Â¾duotis pasikeitÃ„â€”.", newVersion: data.version, errorType: "conflict"};
      }

      if (resp.status === 403) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Neturite teisiÃ…Â³ iÃ…Â¡trinti.", errorType: "forbidden"};
      }

      if (resp.status === 404) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "UÃ…Â¾duotis nerasta.", errorType: "not_found"};
      }

      if (!resp.ok) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Nepavyko iÃ…Â¡trinti uÃ…Â¾duoties.", errorType: "unknown"};
      }

      const data = await readJsonSafe(resp);
      logEvent("api.task.delete.ok", { taskId });
      return { success: true, message: data?.message };
    } catch (err) {
      logEvent("api.task.delete.error", { taskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko iÃ…Â¡trinti uÃ…Â¾duoties.", errorType: "exception" };
    }
  };

  /**
   * Fetch all technicians for dropdown.
   * @param {boolean} [forceRefresh] - Force refresh the cached data
   * @returns {Promise<Array>}
   */
  const fetchTechnicians = async (forceRefresh = false) => {
    if (techniciansPromise && !forceRefresh) return techniciansPromise;

    techniciansPromise = (async () => {
      try {
        const resp = await (api?.get || fetch)("/api/technicians", {
          headers: { "X-Requested-With": "XMLHttpRequest" }
        });

        if (!resp.ok) {
          console.error("Failed to load technicians");
          return [];
        }
        return await resp.json();
      } catch (err) {
        console.error("Error loading technicians:", err);
        return [];
      }
    })();

    return techniciansPromise;
  };

  /**
   * Fetch all objects for dropdown.
   * @param {boolean} [forceRefresh] - Force refresh the cached data
   * @returns {Promise<Array>}
   */
  const fetchObjects = async (forceRefresh = false) => {
    if (objectsPromise && !forceRefresh) return objectsPromise;

    objectsPromise = (async () => {
      try {
        const resp = await (api?.get || fetch)("/api/objects", {
          headers: { "X-Requested-With": "XMLHttpRequest" }
        });

        if (!resp.ok) {
          console.error("Failed to load objects");
          return [];
        }
        return await resp.json();
      } catch (err) {
        console.error("Error loading objects:", err);
        return [];
      }
    })();

    return objectsPromise;
  };

  /**
   * Clear cached dropdown data.
   */
  const clearCache = () => {
    techniciansPromise = null;
    objectsPromise = null;
  };

  /**
   * Move a task to a new technician/time slot.
   * @param {string|number} taskId - The task ID
   * @param {Object} moveData - Move data { targetTechnicianId, targetDate, targetSlotStartMinutes }
   * @param {number} version - Current version for optimistic locking
   * @param {{ autoCompact?: boolean }} [options] - Delete behavior options
   * @returns {Promise<{ success: boolean, version?: number, error?: string }>}
   */
  const moveTask = async (taskId, moveData, version) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      targetTechnicianId: moveData.targetTechnicianId,
      targetDate: moveData.targetDate,
      targetSlotStartMinutes: moveData.targetSlotStartMinutes,
      version: parseInt(version) || 0,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.patch
        ? api.patch(`/api/tasks/${taskId}/move`, body, {}, { event: "api.task.move", taskId })
        : fetch(`/api/tasks/${taskId}/move`, {
            method: "PATCH",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (resp.status === 409 || resp.status === 422 || resp.status === 403) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Nepavyko perkelti uÃ…Â¾duoties.", errorType: resp.status === 409 ? "conflict" : resp.status === 403 ? "forbidden" : "unprocessable"};
      }

      if (!resp.ok) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Nepavyko perkelti uÃ…Â¾duoties.", errorType: "unknown"};
      }

      const data = await readJsonSafe(resp);
      logEvent("api.task.move.ok", { taskId, newVersion: data?.version });
      return {
        success: true,
        version: data?.version,
        message: data?.message,
      };
    } catch (err) {
      logEvent("api.task.move.error", { taskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko perkelti uÃ…Â¾duoties.", errorType: "exception" };
    }
  };

  /**
   * Swap two tasks on the same technician/day lane.
   * @param {string|number} sourceTaskId - Dragged task ID
   * @param {string|number} targetTaskId - Target task ID
   * @param {number} sourceVersion - Source task version
   * @param {number} targetVersion - Target task version
   * @returns {Promise<{ success: boolean, error?: string, sourceVersion?: number, targetVersion?: number }>}
   */
  const swapTask = async (sourceTaskId, targetTaskId, sourceVersion, targetVersion) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    const body = {
      targetTaskId: parseInt(targetTaskId) || 0,
      sourceVersion: parseInt(sourceVersion) || 0,
      targetVersion: parseInt(targetVersion) || 0,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.patch
        ? api.patch(`/api/tasks/${sourceTaskId}/swap`, body, {}, { event: "api.task.swap", sourceTaskId, targetTaskId })
        : fetch(`/api/tasks/${sourceTaskId}/swap`, {
            method: "PATCH",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (resp.status === 409 || resp.status === 422 || resp.status === 403 || resp.status === 404) {
        const data = await readJsonSafe(resp);
        return {
          success: false,
          error: data.message || "Nepavyko sukeisti uzduociu.",
          sourceVersion: data.sourceVersion,
          targetVersion: data.targetVersion,
          errorType: resp.status === 409 ? "conflict" : resp.status === 403 ? "forbidden" : resp.status === 404 ? "not_found" : "unprocessable",
          
        };
      }

      if (!resp.ok) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Nepavyko sukeisti uzduociu.", errorType: "unknown"};
      }

      const data = await readJsonSafe(resp);
      logEvent("api.task.swap.ok", { sourceTaskId, targetTaskId });
      return {
        success: true,
        sourceVersion: data?.sourceVersion,
        targetVersion: data?.targetVersion,
        message: data?.message,
      };
    } catch (err) {
      logEvent("api.task.swap.error", { sourceTaskId, targetTaskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko sukeisti uzduociu.", errorType: "exception" };
    }
  };

  /**
   * Smart-swap two tasks and allow minimal local chain adjustments.
   * @param {string|number} sourceTaskId - Dragged task ID
   * @param {string|number} targetTaskId - Target task ID
   * @param {number} sourceVersion - Source task version
   * @param {number} targetVersion - Target task version
   * @returns {Promise<{ success: boolean, error?: string, sourceVersion?: number, targetVersion?: number }>}
   */
  const swapTaskSmart = async (sourceTaskId, targetTaskId, sourceVersion, targetVersion) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    const body = {
      targetTaskId: parseInt(targetTaskId) || 0,
      sourceVersion: parseInt(sourceVersion) || 0,
      targetVersion: parseInt(targetVersion) || 0,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.patch
        ? api.patch(`/api/tasks/${sourceTaskId}/swap-smart`, body, {}, { event: "api.task.swap.smart", sourceTaskId, targetTaskId })
        : fetch(`/api/tasks/${sourceTaskId}/swap-smart`, {
            method: "PATCH",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (resp.status === 409 || resp.status === 422 || resp.status === 403 || resp.status === 404) {
        const data = await readJsonSafe(resp);
        return {
          success: false,
          error: data.message || "Nepavyko atlikti ismanaus sukeitimo.",
          sourceVersion: data.sourceVersion,
          targetVersion: data.targetVersion,
          errorType: resp.status === 409 ? "conflict" : resp.status === 403 ? "forbidden" : resp.status === 404 ? "not_found" : "unprocessable",
          
        };
      }

      if (!resp.ok) {
        const data = await readJsonSafe(resp);
        return { success: false, error: data.message || "Nepavyko atlikti ismanaus sukeitimo.", errorType: "unknown"};
      }

      const data = await readJsonSafe(resp);
      logEvent("api.task.swap.smart.ok", { sourceTaskId, targetTaskId });
      return {
        success: true,
        sourceVersion: data?.sourceVersion,
        targetVersion: data?.targetVersion,
        message: data?.message,
      };
    } catch (err) {
      logEvent("api.task.swap.smart.error", { sourceTaskId, targetTaskId, error: err?.message }, "error");
      return { success: false, error: err?.message || "Nepavyko atlikti ismanaus sukeitimo.", errorType: "exception" };
    }
  };

  /**
   * Fetch drag preview (workload states).
   * @param {Object} previewData - Preview request data
   * @returns {Promise<{ success: boolean, previewStates?: Array, error?: string }>}
   */
  const fetchDragPreview = async (previewData) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const body = {
      taskId: previewData.taskId,
      targetTechnicianId: previewData.technicianId,
      targetDate: previewData.targetDate,
      targetSlotStartMinutes: previewData.startMinutes,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.post
        ? api.post("/api/tasks/preview/drag", body, {}, { event: "api.drag.preview", taskId: previewData.taskId })
        : fetch("/api/tasks/preview/drag", {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (!resp.ok) {
        logEvent("api.drag.preview.error", { status: resp.status }, "warn");
        return { success: false, error: "Preview failed" };
      }

      const data = await resp.json();
      return { success: true, previewStates: data.previewStates };
    } catch (err) {
      logEvent("api.drag.preview.error", { error: err?.message }, "error");
      return { success: false, error: err?.message };
    }
  };

  /**
   * Fetch per-technician travel times for current create/update form context.
   * @param {Object} previewData - { targetDate, startMinutes, endMinutes, objectAddress }
   * @returns {Promise<{ success: boolean, items?: Array, error?: string }>}
   */
  const fetchAssigneeTravelPreview = async (previewData) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    const body = {
      taskId: Number.isFinite(previewData.taskId) ? previewData.taskId : null,
      targetDate: previewData.targetDate,
      startMinutes: previewData.startMinutes,
      endMinutes: previewData.endMinutes,
      objectAddress: previewData.objectAddress,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.post
        ? api.post("/api/tasks/preview/assignee-travel", body, {}, { event: "api.assignee.preview" })
        : fetch("/api/tasks/preview/assignee-travel", {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (!resp.ok) {
        return { success: false, error: "Preview failed" };
      }

      const data = await resp.json();
      return { success: true, items: Array.isArray(data?.items) ? data.items : [] };
    } catch (err) {
      return { success: false, error: err?.message || "Preview failed" };
    }
  };

  // Private helper to handle update responses
  const handleUpdateResponse = async (resp, taskId) => {
    if (resp.status === 409 || resp.status === 403) {
      const data = await readJsonSafe(resp);
      return {
        success: false,
        error: data.message || (resp.status === 409 ? "Duomenys pasikeitÃ„â€”." : "Neturite teisiÃ…Â³."),
        errorType: resp.status === 409 ? "conflict" : "forbidden",
        
      };
    }

    if (resp.status === 400 || resp.status === 422) {
      const errorData = await readJsonSafe(resp);
      if (errorData.errors && typeof errorData.errors === "object") {
        return { success: false, validationErrors: errorData.errors, errorType: "validation"};
      }
      return { success: false, error: errorData.message || "Patikrinkite Ã„Â¯vestus duomenis.", errorType: "validation"};
    }

    if (!resp.ok) {
      const errorData = await readJsonSafe(resp);
      return { success: false, error: errorData.message || "Ã„Â®vyko klaida iÃ…Â¡saugant uÃ…Â¾duotÃ„Â¯.", errorType: "unknown"};
    }

    const responseData = await readJsonSafe(resp);
    logEvent("api.task.update.ok", { taskId, newVersion: responseData?.version });
    return {
      success: true,
      version: responseData?.version,
      message: responseData?.message,
    };
  };

  /**
   * Dry-run feasibility check for a smart swap (no DB changes).
   * @param {string|number} sourceTaskId
   * @param {string|number} targetTaskId
   * @returns {Promise<{ feasible: boolean, reason: string, requiresSmartAdjustment: boolean, message: string|null }>}
   */
  const fetchSwapPreview = async (sourceTaskId, targetTaskId) => {
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    const body = {
      targetTaskId: parseInt(targetTaskId) || 0,
      timezoneOffsetMinutes: timezoneOffset,
    };

    try {
      const resp = await (api?.post
        ? api.post(`/api/tasks/${sourceTaskId}/swap-smart/preview`, body, {}, { event: "api.task.swap.preview", sourceTaskId, targetTaskId })
        : fetch(`/api/tasks/${sourceTaskId}/swap-smart/preview`, {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/json" },
            body: JSON.stringify(body)
          }));

      if (!resp.ok) {
        return { feasible: false, reason: "Error", requiresSmartAdjustment: false, message: null };
      }

      const data = await resp.json().catch(() => null);
      return {
        feasible: data?.feasible ?? false,
        reason: data?.reason ?? "Unknown",
        requiresSmartAdjustment: data?.requiresSmartAdjustment ?? false,
        message: data?.message ?? null,
      };
    } catch {
      return { feasible: false, reason: "Error", requiresSmartAdjustment: false, message: null };
    }
  };

  // Export module
  window.schedule.tasks.api = {
    fetchTask,
    createTask,
    updateTask,
    updateTaskAsTechnician,
    updateTaskTime,
    deleteTask,
    moveTask,
    swapTask,
    swapTaskSmart,
    fetchSwapPreview,
    fetchDragPreview,
    fetchAssigneeTravelPreview,
    fetchTechnicians,
    fetchObjects,
    clearCache,
  };
})();
