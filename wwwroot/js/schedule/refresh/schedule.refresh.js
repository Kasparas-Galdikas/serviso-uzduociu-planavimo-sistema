(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  /**
   * Reusable partial refresh system for schedule updates.
   * Provides optimistic UI updates and targeted DOM manipulation to avoid full table re-renders.
   */

  // ============================================================================
  // TASK CARD OPERATIONS
  // ============================================================================

  /**
   * Find a task card element by ID
   * @param {string|number} taskId
   * @returns {HTMLElement|null}
   */
  const findTaskCard = (taskId) => {
    if (!taskId) return null;
    return document.querySelector(`.task-card[data-task-id="${CSS.escape(String(taskId))}"]`);
  };

  /**
   * Find the cell (td) containing a task card
   * @param {string|number} taskId
   * @returns {HTMLElement|null}
   */
  const findTaskCell = (taskId) => {
    const card = findTaskCard(taskId);
    return card?.closest("td") || null;
  };

  /**
   * Find a specific schedule slot cell by technician, date, and time
   * @param {number} technicianId
   * @param {string} date - YYYY-MM-DD format
   * @param {number} slotStartMinutes - Minutes from start of day
   * @returns {HTMLElement|null}
   */
  const findSlotCell = (technicianId, date, slotStartMinutes) => {
    // Data attributes are on the .schedule-slot div, not the td
    const slotSelector = `.schedule-slot[data-tech-id="${technicianId}"][data-date="${date}"][data-slot-start-minutes="${slotStartMinutes}"]`;
    const slot = document.querySelector(slotSelector);
    const cell = slot?.closest("td") || null;

    if (!cell) {
      logEvent("refresh.findSlotCell.failed", {
        technicianId,
        date,
        slotStartMinutes,
        selector: slotSelector,
        slotFound: !!slot
      }, "warn");
    }

    return cell;
  };

  /**
   * Extract task data attributes from a card element
   * @param {HTMLElement} card
   * @returns {Object}
   */
  const extractTaskData = (card) => {
    if (!card) return null;
    return {
      id: card.getAttribute("data-task-id"),
      version: card.getAttribute("data-task-version"),
      durationMinutes: parseInt(card.getAttribute("data-duration-minutes")) || 30,
      editable: card.getAttribute("data-editable") === "true",
    };
  };

  // ============================================================================
  // OPTIMISTIC UPDATE HELPERS
  // ============================================================================

  /**
   * Create a snapshot of current DOM state for rollback
   * @param {HTMLElement[]} elements
   * @returns {Map<HTMLElement, {html: string, parent: HTMLElement}>}
   */
  const createSnapshot = (elements) => {
    const snapshot = new Map();
    elements.forEach((el) => {
      if (el) {
        snapshot.set(el, {
          html: el.outerHTML,
          parent: el.parentElement,
          nextSibling: el.nextElementSibling,
        });
      }
    });
    return snapshot;
  };

  /**
   * Rollback DOM to a previous snapshot
   * @param {Map} snapshot
   */
  const rollbackSnapshot = (snapshot) => {
    snapshot.forEach((state, el) => {
      try {
        if (state.parent && state.html) {
          const tempDiv = document.createElement("div");
          tempDiv.innerHTML = state.html;
          const restored = tempDiv.firstElementChild;

          if (el.parentElement) {
            el.replaceWith(restored);
          } else if (state.nextSibling) {
            state.parent.insertBefore(restored, state.nextSibling);
          } else {
            state.parent.appendChild(restored);
          }
        }
      } catch (err) {
        logEvent("refresh.rollback.error", { error: err?.message }, "error");
      }
    });
  };

  /**
   * Add optimistic loading state to an element
   * @param {HTMLElement} element
   * @param {string} className - Class to add during loading (default: 'is-updating')
   */
  const addLoadingState = (element, className = "is-updating") => {
    if (!element) return;
    element.classList.add(className);
    element.style.pointerEvents = "none";
  };

  /**
   * Remove optimistic loading state from an element
   * @param {HTMLElement} element
   * @param {string} className
   */
  const removeLoadingState = (element, className = "is-updating") => {
    if (!element) return;
    element.classList.remove(className);
    element.style.pointerEvents = "";
  };

  // ============================================================================
  // TASK CARD UPDATES
  // ============================================================================

  /**
   * Update a task card's version attribute after successful API call
   * @param {string|number} taskId
   * @param {number} newVersion
   * @returns {boolean} Success
   */
  const updateTaskVersion = (taskId, newVersion) => {
    const card = findTaskCard(taskId);
    if (!card || newVersion == null) return false;

    card.setAttribute("data-task-version", String(newVersion));
    logEvent("refresh.version.updated", { taskId, version: newVersion });
    return true;
  };

  /**
   * Update task card's display properties (title, time, etc.) without full re-render
   * @param {string|number} taskId
   * @param {Object} updates - Properties to update {title, location, startTime, endTime}
   * @returns {boolean} Success
   */
  const updateTaskCardContent = (taskId, updates) => {
    const card = findTaskCard(taskId);
    if (!card) return false;

    try {
      // Update title
      if (updates.title !== undefined) {
        const titleEl = card.querySelector(".task-card-title");
        if (titleEl) titleEl.textContent = updates.title;
      }

      // Update location
      if (updates.location !== undefined) {
        const locationEl = card.querySelector(".task-card-location");
        if (locationEl) locationEl.textContent = updates.location;
      }

      // Update time display
      if (updates.startTime !== undefined || updates.endTime !== undefined) {
        const timeEl = card.querySelector(".task-card-time");
        if (timeEl && updates.startTime && updates.endTime) {
          timeEl.textContent = `${updates.startTime} - ${updates.endTime}`;
        }
      }

      // Update version if provided
      if (updates.version !== undefined) {
        card.setAttribute("data-task-version", String(updates.version));
      }

      logEvent("refresh.card.content.updated", { taskId, updates });
      return true;
    } catch (err) {
      logEvent("refresh.card.content.error", { taskId, error: err?.message }, "error");
      return false;
    }
  };

  // ============================================================================
  // TASK MOVEMENT (Optimistic Drag & Drop)
  // ============================================================================

  /**
   * Optimistically move a task card to a new location in the DOM
   * Returns a rollback function in case the server request fails
   *
   * @param {string|number} taskId
   * @param {number} targetTechnicianId
   * @param {string} targetDate - YYYY-MM-DD
   * @param {number} targetSlotStartMinutes
   * @returns {{success: boolean, rollback: Function, card: HTMLElement}}
   */
  const optimisticallyMoveTask = (taskId, targetTechnicianId, targetDate, targetSlotStartMinutes) => {
    const card = findTaskCard(taskId);
    const originCell = card?.closest("td");
    const targetCell = findSlotCell(targetTechnicianId, targetDate, targetSlotStartMinutes);

    if (!card || !originCell || !targetCell) {
      logEvent("refresh.move.failed", {
        taskId,
        reason: "elements_not_found",
        cardFound: !!card,
        originCellFound: !!originCell,
        targetCellFound: !!targetCell,
        targetTechnicianId,
        targetDate,
        targetSlotStartMinutes
      }, "warn");
      return { success: false, rollback: () => {}, card: null };
    }

    // Save references for rollback (don't use innerHTML - it breaks event listeners!)
    const originCellWasEmpty = originCell.classList.contains("empty-slot");
    const targetCellWasEmpty = targetCell.classList.contains("empty-slot");

    try {
      // Add loading state (no spinner, just visual feedback)
      card.classList.add("task-card--moving");
      card.style.pointerEvents = "none";

      // Remove .schedule-slot div from target cell if it exists (for empty cells)
      const targetSlot = targetCell.querySelector(".schedule-slot");
      if (targetSlot) {
        targetSlot.remove();
      }

      // Move the card DOM element directly to the cell
      targetCell.appendChild(card);

      // Update origin cell state - restore empty slot if no cards remain
      const remainingCards = originCell.querySelectorAll(".task-card");
      if (remainingCards.length === 0) {
        originCell.classList.add("empty-slot");
        // Create empty slot div for origin cell
        const emptySlot = document.createElement("div");
        emptySlot.className = "schedule-slot";
        emptySlot.setAttribute("role", "button");
        emptySlot.setAttribute("tabindex", "0");
        // Copy data attributes from cell to empty slot (if they exist on cell)
        const cellDate = originCell.getAttribute("data-date");
        const cellTechId = originCell.getAttribute("data-tech-id");
        const cellSlotStart = originCell.getAttribute("data-slot-start-minutes");
        if (cellDate) emptySlot.setAttribute("data-date", cellDate);
        if (cellTechId) emptySlot.setAttribute("data-tech-id", cellTechId);
        if (cellSlotStart) emptySlot.setAttribute("data-slot-start-minutes", cellSlotStart);
        originCell.appendChild(emptySlot);
      }

      // Update target cell state
      targetCell.classList.remove("empty-slot");
      targetCell.classList.remove("is-drop-range", "is-drop-range-invalid");

      logEvent("refresh.move.optimistic", { taskId, targetTechnicianId, targetSlotStartMinutes });

      // Return rollback function
      const rollback = () => {
        logEvent("refresh.move.rollback", { taskId });

        // Add X icon to card before animating back
        card.classList.remove("task-card--moving");
        card.classList.add("task-card--rollback");

        // Animate rollback after a brief moment to show X icon
        setTimeout(() => {
          // Remove .schedule-slot div from origin cell if it exists
          const originSlot = originCell.querySelector(".schedule-slot");
          if (originSlot) {
            originSlot.remove();
          }

          // Move card back to origin cell
          originCell.appendChild(card);

          // Remove rollback state
          card.classList.remove("task-card--rollback");
          card.style.pointerEvents = "";

          // Restore origin cell state
          if (originCellWasEmpty) {
            originCell.classList.add("empty-slot");
          } else {
            originCell.classList.remove("empty-slot");
          }

          // Restore target cell state - add empty slot if needed
          const targetRemainingCards = targetCell.querySelectorAll(".task-card");
          if (targetRemainingCards.length === 0) {
            if (targetCellWasEmpty) {
              targetCell.classList.add("empty-slot");
              // Create empty slot div for target cell
              const emptySlot = document.createElement("div");
              emptySlot.className = "schedule-slot";
              emptySlot.setAttribute("role", "button");
              emptySlot.setAttribute("tabindex", "0");
              const cellDate = targetCell.getAttribute("data-date");
              const cellTechId = targetCell.getAttribute("data-tech-id");
              const cellSlotStart = targetCell.getAttribute("data-slot-start-minutes");
              if (cellDate) emptySlot.setAttribute("data-date", cellDate);
              if (cellTechId) emptySlot.setAttribute("data-tech-id", cellTechId);
              if (cellSlotStart) emptySlot.setAttribute("data-slot-start-minutes", cellSlotStart);
              targetCell.appendChild(emptySlot);
            }
          }

          logEvent("refresh.rollback.complete", { taskId });
        }, 150);
      };

      return { success: true, rollback, card };
    } catch (err) {
      logEvent("refresh.move.error", { taskId, error: err?.message }, "error");
      // Restore on error by moving card back
      const originSlot = originCell.querySelector(".schedule-slot");
      if (originSlot) {
        originSlot.remove();
      }
      originCell.appendChild(card);
      card.classList.remove("task-card--moving");
      card.style.pointerEvents = "";
      if (originCellWasEmpty) originCell.classList.add("empty-slot");
      if (targetCellWasEmpty) targetCell.classList.add("empty-slot");
      return { success: false, rollback: () => {}, card: null };
    }
  };

  /**
   * Finalize a task move after successful server response
   * @param {string|number} taskId
   * @param {number} newVersion
   * @returns {boolean} Success
   */
  const finalizeTaskMove = (taskId, newVersion) => {
    const card = findTaskCard(taskId);
    if (!card) return false;

    try {
      // Remove loading state
      removeLoadingState(card, "task-card--moving");

      // Update version
      if (newVersion != null) {
        card.setAttribute("data-task-version", String(newVersion));
      }

      logEvent("refresh.move.finalized", { taskId, version: newVersion });
      return true;
    } catch (err) {
      logEvent("refresh.move.finalize.error", { taskId, error: err?.message }, "error");
      return false;
    }
  };

  // ============================================================================
  // PARTIAL FETCH OPERATIONS
  // ============================================================================

  /**
   * Fetch and replace a specific task card from the server
   * Useful when you need the server's authoritative HTML for a task
   *
   * @param {string|number} taskId
   * @returns {Promise<boolean>} Success
   */
  const refreshTaskCard = async (taskId) => {
    const card = findTaskCard(taskId);
    if (!card) return false;

    const originCell = card.closest("td");
    if (!originCell) return false;

    try {
      addLoadingState(card, "task-card--refreshing");

      // Fetch just this task's card HTML
      // You'll need to add this endpoint to your backend: GET /api/tasks/{id}/card
      const resp = await fetch(`/api/tasks/${taskId}/card`, {
        headers: { "X-Requested-With": "XMLHttpRequest" },
      });

      if (!resp.ok) {
        throw new Error(`Failed to fetch task card: ${resp.status}`);
      }

      const html = await resp.text();
      const tempDiv = document.createElement("div");
      tempDiv.innerHTML = html;
      const newCard = tempDiv.querySelector(".task-card");

      if (newCard) {
        card.replaceWith(newCard);
        logEvent("refresh.card.fetched", { taskId });
        return true;
      } else {
        throw new Error("Invalid HTML response");
      }
    } catch (err) {
      logEvent("refresh.card.fetch.error", { taskId, error: err?.message }, "error");
      removeLoadingState(card, "task-card--refreshing");
      return false;
    }
  };

  /**
   * Fetch and replace specific cells in the schedule table
   * Useful for updating multiple time slots after an operation
   *
   * @param {Array<{technicianId, date, slotStartMinutes}>} cells
   * @returns {Promise<boolean>} Success
   */
  const refreshCells = async (cells) => {
    if (!cells || cells.length === 0) return true;

    try {
      // For now, this would require a backend endpoint that returns cell HTML
      // Future enhancement: POST /Schedule/Cells with array of cell identifiers
      logEvent("refresh.cells.pending", { count: cells.length }, "warn");
      return false;
    } catch (err) {
      logEvent("refresh.cells.error", { error: err?.message }, "error");
      return false;
    }
  };

  // ============================================================================
  // FALLBACK TO FULL REFRESH
  // ============================================================================

  /**
   * Fallback to full table refresh when partial refresh isn't possible
   * Preserves scroll position and active states
   *
   * @param {URLSearchParams} query
   * @returns {Promise<boolean>} Success
   */
  const fallbackToFullRefresh = async (query) => {
    const table = schedule.table;
    if (!table?.fetchAndRender) {
      logEvent("refresh.fallback.failed", { reason: "no_table_module" }, "error");
      return false;
    }

    // Capture current scroll position
    const container = tables.querySelector(".table-responsive") || tables.closest(".table-responsive");
    const scrollTop = container?.scrollTop || 0;
    const scrollLeft = container?.scrollLeft || 0;

    // Capture active task
    const activeCard = document.querySelector(".task-card.is-active");
    const activeTaskId = activeCard?.getAttribute("data-task-id") || null;

    try {
      logEvent("refresh.fallback.start", { query: query.toString() });
      const success = await table.fetchAndRender(query, { updateUrl: false });

      if (success) {
        // Restore scroll position
        if (container) {
          container.scrollTop = scrollTop;
          container.scrollLeft = scrollLeft;
        }

        // Restore active state
        if (activeTaskId) {
          const newActiveCard = findTaskCard(activeTaskId);
          if (newActiveCard) {
            newActiveCard.classList.add("is-active");
          }
        }

        logEvent("refresh.fallback.success");
      }

      return success;
    } catch (err) {
      logEvent("refresh.fallback.error", { error: err?.message }, "error");
      return false;
    }
  };

  // ============================================================================
  // PUBLIC API
  // ============================================================================

  schedule.refresh = {
    // Element finders
    findTaskCard,
    findTaskCell,
    findSlotCell,
    extractTaskData,

    // Optimistic updates
    createSnapshot,
    rollbackSnapshot,
    addLoadingState,
    removeLoadingState,

    // Task card operations
    updateTaskVersion,
    updateTaskCardContent,

    // Task movement
    optimisticallyMoveTask,
    finalizeTaskMove,

    // Partial fetch
    refreshTaskCard,
    refreshCells,

    // Fallback
    fallbackToFullRefresh,
  };

  logEvent("ui.refresh.module.init");
})();
