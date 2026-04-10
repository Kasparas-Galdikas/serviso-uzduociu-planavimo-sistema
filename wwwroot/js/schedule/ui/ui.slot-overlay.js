/**
 * ui.slot-overlay.js
 *
 * Shared overlay logic for slot selection bars and task resize bars.
 * Provides consistent TD-boundary positioning and drag handle behavior.
 *
 * Dependencies: schedule.grid (optional for formatRange)
 * Dependents: schedule.selection, task.resize
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};
  window.schedule.ui = window.schedule.ui || {};

  const grid = () => window.schedule.grid;

  // ========== Helpers ==========

  const formatRange = (startMinutes, endMinutes) => {
    const g = grid();
    if (g?.formatRange) return g.formatRange(startMinutes, endMinutes);
    const fmt = (m) => `${String(Math.floor(m / 60)).padStart(2, "0")}:${String(m % 60).padStart(2, "0")}`;
    return `${fmt(startMinutes)}–${fmt(endMinutes)}`;
  };

  // ========== Overlay Factory ==========

  /**
   * Create an overlay bar element with optional handles.
   *
   * @param {Object} options
   * @param {string} [options.className] - Additional CSS classes (e.g., "task-resize-bar")
   * @param {boolean} [options.showRange=false] - Whether to show the time range label
   * @param {string} [options.hitLabel] - ARIA label for the hit area
   * @param {string} [options.leftLabel="Anksčiau"] - ARIA label for left handle
   * @param {string} [options.rightLabel="Vėliau"] - ARIA label for right handle
   * @returns {HTMLElement} The overlay element
   */
  const create = (options = {}) => {
    const {
      className = "",
      showRange = false,
      hitLabel = "Keisti laiką",
      leftLabel = "Anksčiau",
      rightLabel = "Vėliau",
    } = options;

    const overlay = document.createElement("div");
    overlay.className = `slot-selection-bar ${className}`.trim();

    const rangeHtml = showRange
      ? `<div class="slot-selection-range" data-slot-range aria-hidden="true"></div>`
      : "";

    overlay.innerHTML = `
      <button type="button" class="slot-selection-hit" data-slot-hit aria-label="${hitLabel}"></button>
      <button type="button" class="slot-selection-handle" data-slot-handle="left" aria-label="${leftLabel}" title="${leftLabel}">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟨</span>
      </button>
      ${rangeHtml}
      <button type="button" class="slot-selection-handle" data-slot-handle="right" aria-label="${rightLabel}" title="${rightLabel}">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟩</span>
      </button>
    `;

    overlay.style.position = "absolute";
    return overlay;
  };

  // ========== Positioning ==========

  /**
   * Position an overlay using TD boundaries (start and end cells).
   *
   * @param {HTMLElement} overlay - The overlay element
   * @param {HTMLElement} container - The scroll container (table-responsive)
   * @param {HTMLElement} startTd - The starting TD cell
   * @param {HTMLElement} endTd - The ending TD cell (can be same as startTd)
   * @param {Object} [offsets] - Optional pixel offsets
   * @param {number} [offsets.leftNudge=1] - Pixels to add to left position
   * @param {number} [offsets.widthShrink=2] - Pixels to subtract from width
   */
  const positionFromTds = (overlay, container, startTd, endTd, offsets = {}) => {
    if (!overlay || !container || !startTd) return;
    const actualEndTd = endTd || startTd;

    const {
      leftNudge = 1,
      widthShrink = 2,
      expandToContent = false,
      minVisualWidth = 0,
    } = offsets;

    const containerRect = container.getBoundingClientRect();
    const startRect = startTd.getBoundingClientRect();
    const endRect = actualEndTd.getBoundingClientRect();

    let left = startRect.left - containerRect.left + container.scrollLeft + leftNudge;
    const top = startRect.top - containerRect.top + container.scrollTop;
    const baseWidth = Math.max(1, endRect.right - startRect.left - widthShrink);
    const height = Math.max(1, startRect.height);

    overlay.style.height = `${height}px`;

    let width = baseWidth;
    if (expandToContent) {
      overlay.style.width = `${baseWidth}px`;
      const contentWidth = Math.ceil(overlay.scrollWidth || 0);
      const desiredWidth = Math.max(baseWidth, minVisualWidth || 0, contentWidth);
      if (desiredWidth > baseWidth) {
        const maxLeft = Math.max(0, (container.scrollWidth || container.clientWidth || 0) - desiredWidth);
        left = Math.min(Math.max(0, left - ((desiredWidth - baseWidth) / 2)), maxLeft);
        width = desiredWidth;
      }
    }

    overlay.style.left = `${left}px`;
    overlay.style.top = `${top}px`;
    overlay.style.width = `${width}px`;
  };

  /**
   * Update the range label in an overlay.
   *
   * @param {HTMLElement} overlay - The overlay element
   * @param {number} startMinutes - Start time in minutes
   * @param {number} endMinutes - End time in minutes
   */
  const updateRangeLabel = (overlay, startMinutes, endMinutes) => {
    const label = overlay?.querySelector?.("[data-slot-range]");
    if (label) {
      label.textContent = formatRange(startMinutes, endMinutes);
    }
  };

  // ========== Drag Handle Wiring ==========

  /**
   * Attach pointer-based drag behavior to resize handles.
   *
   * @param {HTMLElement} overlay - The overlay element containing handles
   * @param {Object} callbacks
   * @param {Function} callbacks.onDragStart - Called when drag begins: (dir, event) => void
   * @param {Function} callbacks.onDragMove - Called on move: (dir, event) => void
   * @param {Function} callbacks.onDragEnd - Called when drag ends: (dir, event, changed) => Promise<void>|void
   * @param {Function} [callbacks.onChange] - Optional: called to check if values changed
   */
  const attachDragHandles = (overlay, callbacks) => {
    const { onDragStart, onDragMove, onDragEnd, onChange } = callbacks;

    const attachToHandle = (handle, dir) => {
      if (!handle) return;
      handle.style.cursor = "ew-resize";

      handle.addEventListener("pointerdown", (e) => {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        handle.setPointerCapture(e.pointerId);

        overlay.classList.add("is-dragging");
        overlay.style.pointerEvents = "none";

        onDragStart?.(dir, e);

        const move = (ev) => {
          onDragMove?.(dir, ev);
        };

        const up = async (ev) => {
          handle.releasePointerCapture(ev.pointerId);
          handle.removeEventListener("pointermove", move);
          handle.removeEventListener("pointerup", up);
          handle.removeEventListener("pointercancel", up);

          overlay.classList.remove("is-dragging");
          overlay.style.pointerEvents = "";

          const changed = onChange?.() ?? true;
          await onDragEnd?.(dir, ev, changed);
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", up);
        handle.addEventListener("pointercancel", up);
      });
    };

    attachToHandle(overlay.querySelector('[data-slot-handle="left"]'), "left");
    attachToHandle(overlay.querySelector('[data-slot-handle="right"]'), "right");
  };

  /**
   * Wire the hit area to close/dismiss the overlay.
   *
   * @param {HTMLElement} overlay - The overlay element
   * @param {Function} onHit - Callback when hit area is clicked
   */
  const attachHitClose = (overlay, onHit) => {
    const hitEl = overlay?.querySelector?.("[data-slot-hit]");
    if (!hitEl) return;
    hitEl.addEventListener("click", (e) => {
      e.stopPropagation();
      onHit?.();
    });
  };

  /**
   * Wire outside click to dismiss the overlay.
   *
   * @param {HTMLElement} overlay - The overlay element
   * @param {HTMLElement[]} excludeEls - Elements that should NOT trigger dismiss
   * @param {Function} onOutsideClick - Callback when clicking outside
   * @returns {Function} Cleanup function to remove the listener
   */
  const attachOutsideClickDismiss = (overlay, excludeEls, onOutsideClick) => {
    const handler = (evt) => {
      if (overlay.contains(evt.target)) return;
      for (const el of excludeEls) {
        if (el?.contains?.(evt.target)) return;
      }
      onOutsideClick?.();
    };
    document.addEventListener("pointerdown", handler, true);
    return () => document.removeEventListener("pointerdown", handler, true);
  };

  // ========== TD Navigation Helpers ==========

  /**
   * Get the index of a TD within its parent row.
   *
   * @param {HTMLElement} td - The TD element
   * @returns {number|null} Index or null if not found
   */
  const getTdIndexInRow = (td) => {
    const row = td?.parentElement;
    if (!row) return null;
    const tds = Array.from(row.querySelectorAll("td"));
    const idx = tds.indexOf(td);
    return idx >= 0 ? idx : null;
  };

  /**
   * Get a TD at a specific index in a row.
   *
   * @param {HTMLElement} row - The TR element
   * @param {number} index - The index
   * @returns {HTMLElement|null} The TD or null
   */
  const getTdAtIndexInRow = (row, index) => {
    if (!row || index == null) return null;
    const tds = Array.from(row.querySelectorAll("td"));
    if (index < 0 || index >= tds.length) return null;
    return tds[index];
  };

  /**
   * Walk forward through TDs counting colspans until we've covered N slots.
   *
   * @param {HTMLElement} fromTd - Starting TD
   * @param {number} slots - Number of slots to cover
   * @returns {HTMLElement} The last TD that covers the slot range
   */
  const getEndTdForSlots = (fromTd, slots) => {
    let remaining = slots;
    let td = fromTd;
    while (td && remaining > 0) {
      const span = parseInt(td.getAttribute("colspan") || "1", 10);
      remaining -= span;
      if (remaining <= 0) return td;
      td = td.nextElementSibling;
    }
    return fromTd;
  };

  /**
   * Get the last TD in a range starting from startTd covering durationSlots.
   *
   * @param {HTMLElement} startTd - Starting TD
   * @param {number} durationSlots - Number of slots
   * @returns {HTMLElement|null} The last TD or null
   */
  const getLastTdInRange = (startTd, durationSlots) => {
    let remaining = durationSlots || 1;
    let td = startTd;
    while (td && remaining > 0) {
      const span = parseInt(td.getAttribute("colspan") || "1", 10);
      remaining -= span;
      if (remaining <= 0) return td;
      td = td.nextElementSibling;
    }
    return null;
  };

  // ========== Cleanup ==========

  /**
   * Remove an overlay and its event listeners.
   *
   * @param {HTMLElement} overlay - The overlay to remove
   * @param {Function} [cleanupFn] - Optional additional cleanup
   */
  const destroy = (overlay, cleanupFn) => {
    overlay?.remove?.();
    cleanupFn?.();
  };

  // ========== Exports ==========

  window.schedule.ui.slotOverlay = {
    // Factory
    create,
    // Positioning
    positionFromTds,
    updateRangeLabel,
    // Event wiring
    attachDragHandles,
    attachHitClose,
    attachOutsideClickDismiss,
    // TD navigation
    getTdIndexInRow,
    getTdAtIndexInRow,
    getEndTdForSlots,
    getLastTdInRange,
    // Cleanup
    destroy,
    // Helpers
    formatRange,
  };
})();
