/**
 * ui.dom.js
 *
 * Shared DOM query and manipulation utilities for schedule modules.
 * Provides consistent patterns for finding task cards, slots, and cells.
 *
 * Dependencies: None
 * Dependents: schedule.selection, schedule.tasks, schedule.dragdrop, schedule.refresh
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};
  window.schedule.ui = window.schedule.ui || {};

  /**
   * Safely escape a value for use in CSS selectors.
   * @param {string|number} value - Value to escape
   * @returns {string} Escaped string safe for CSS selectors
   */
  const escapeSelector = (value) => {
    return CSS.escape(String(value));
  };

  /**
   * Find a task card by its ID.
   * @param {string|number} taskId - The task ID
   * @returns {HTMLElement|null} The task card element or null
   */
  const findTaskCard = (taskId) => {
    if (!taskId) return null;
    return document.querySelector(`.task-card[data-task-id="${escapeSelector(taskId)}"]`);
  };

  /**
   * Find all task cards in the schedule.
   * @returns {NodeListOf<HTMLElement>} All task card elements
   */
  const findAllTaskCards = () => {
    return document.querySelectorAll(".task-card");
  };

  /**
   * Find the table cell containing a task card.
   * @param {string|number} taskId - The task ID
   * @returns {HTMLTableCellElement|null} The containing TD or null
   */
  const findTaskCell = (taskId) => {
    const card = findTaskCard(taskId);
    return card?.closest("td") || null;
  };

  /**
   * Find a schedule slot by technician, date, and start time.
   * @param {number} techId - Technician ID
   * @param {string} date - Date in YYYY-MM-DD format
   * @param {number} startMinutes - Slot start time in minutes
   * @returns {HTMLElement|null} The slot element or null
   */
  const findSlot = (techId, date, startMinutes) => {
    return document.querySelector(
      `.schedule-slot[data-tech-id="${escapeSelector(techId)}"][data-date="${escapeSelector(date)}"][data-slot-start-minutes="${escapeSelector(startMinutes)}"]`
    );
  };

  /**
   * Find the table cell containing a slot.
   * @param {number} techId - Technician ID
   * @param {string} date - Date in YYYY-MM-DD format
   * @param {number} startMinutes - Slot start time in minutes
   * @returns {HTMLTableCellElement|null} The containing TD or null
   */
  const findSlotCell = (techId, date, startMinutes) => {
    const slot = findSlot(techId, date, startMinutes);
    return slot?.closest("td") || null;
  };

  /**
   * Check if a table cell is an empty slot (available for task creation).
   * @param {HTMLTableCellElement} td - The table cell to check
   * @returns {boolean} True if the cell is an empty slot
   */
  const isEmptySlotCell = (td) => {
    return td?.classList?.contains("empty-slot") && !!td.querySelector?.(".schedule-slot");
  };

  /**
   * Get the colspan of a table cell.
   * @param {HTMLTableCellElement} td - The table cell
   * @returns {number} The colspan value (minimum 1)
   */
  const getColSpan = (td) => {
    const span = parseInt(td?.getAttribute?.("colspan") || "1", 10);
    return span && span > 0 ? span : 1;
  };

  /**
   * Get all TDs in a table row.
   * @param {HTMLTableRowElement} row - The table row
   * @returns {HTMLTableCellElement[]} Array of TD elements
   */
  const getRowCells = (row) => {
    if (!row) return [];
    return Array.from(row.querySelectorAll("td"));
  };

  /**
   * Get the index of a TD within its row.
   * @param {HTMLTableCellElement} td - The table cell
   * @returns {number|null} The index or null if not found
   */
  const getTdIndexInRow = (td) => {
    if (!td) return null;
    const row = td.parentElement;
    if (!row) return null;
    const cells = getRowCells(row);
    const idx = cells.indexOf(td);
    return idx >= 0 ? idx : null;
  };

  /**
   * Get the TD at a specific index in a row.
   * @param {HTMLTableRowElement} row - The table row
   * @param {number} index - The index to retrieve
   * @returns {HTMLTableCellElement|null} The TD or null if out of bounds
   */
  const getTdAtIndex = (row, index) => {
    if (!row || index == null) return null;
    const cells = getRowCells(row);
    return (index >= 0 && index < cells.length) ? cells[index] : null;
  };

  /**
   * Read slot data from a schedule-slot element.
   * @param {HTMLElement} slotEl - The slot element
   * @returns {{ techId: number, startMinutes: number, date: string }|null}
   */
  const readSlotData = (slotEl) => {
    if (!slotEl?.dataset) return null;
    const techId = parseInt(slotEl.dataset.techId, 10);
    const startMinutes = parseInt(slotEl.dataset.slotStartMinutes, 10);
    const date = slotEl.dataset.date || null;
    if (!Number.isFinite(techId) || !Number.isFinite(startMinutes) || !date) {
      return null;
    }
    return { techId, startMinutes, date };
  };

  /**
   * Get the schedule tables container element.
   * @returns {HTMLElement|null} The #scheduleTables element or null
   */
  const getTablesContainer = () => {
    return document.getElementById("scheduleTables");
  };

  /**
   * Get the scrollable table container.
   * @returns {HTMLElement|null} The .table-responsive element or null
   */
  const getScrollContainer = () => {
    const tables = getTablesContainer();
    return tables?.querySelector(".table-responsive") || tables?.closest(".table-responsive") || null;
  };

  /**
   * Set the active state on a task card.
   * @param {HTMLElement} card - The task card element
   */
  const setActiveCard = (card) => {
    clearActiveCards();
    if (card) {
      card.classList.add("is-active");
    }
  };

  /**
   * Clear active state from all task cards.
   */
  const clearActiveCards = () => {
    document.querySelectorAll(".task-card.is-active").forEach((el) => {
      el.classList.remove("is-active");
    });
  };

  /**
   * Set active state on a task card by ID.
   * @param {string|number} taskId - The task ID
   */
  const setActiveCardByTaskId = (taskId) => {
    if (!taskId) return;
    const card = findTaskCard(taskId);
    if (card) setActiveCard(card);
  };

  /**
   * Create an element with attributes and children.
   * @param {string} tag - The HTML tag name
   * @param {Object} [attrs] - Attributes to set
   * @param {(string|HTMLElement)[]} [children] - Child elements or text
   * @returns {HTMLElement} The created element
   */
  const createElement = (tag, attrs = {}, children = []) => {
    const el = document.createElement(tag);

    Object.entries(attrs).forEach(([key, value]) => {
      if (key === "className") {
        el.className = value;
      } else if (key === "dataset") {
        Object.entries(value).forEach(([dataKey, dataVal]) => {
          el.dataset[dataKey] = dataVal;
        });
      } else if (key.startsWith("on") && typeof value === "function") {
        el.addEventListener(key.slice(2).toLowerCase(), value);
      } else {
        el.setAttribute(key, value);
      }
    });

    children.forEach((child) => {
      if (typeof child === "string") {
        el.appendChild(document.createTextNode(child));
      } else if (child instanceof HTMLElement) {
        el.appendChild(child);
      }
    });

    return el;
  };

  // Export module
  window.schedule.ui.dom = {
    escapeSelector,
    findTaskCard,
    findAllTaskCards,
    findTaskCell,
    findSlot,
    findSlotCell,
    isEmptySlotCell,
    getColSpan,
    getRowCells,
    getTdIndexInRow,
    getTdAtIndex,
    readSlotData,
    getTablesContainer,
    getScrollContainer,
    setActiveCard,
    clearActiveCards,
    setActiveCardByTaskId,
    createElement,
    getDetailPanel: () => document.getElementById("taskDetailModal"),
  };
})();