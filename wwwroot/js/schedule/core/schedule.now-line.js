/**
 * schedule.now-line.js
 *
 * Highlights the time-header cell (top and bottom thead rows) that corresponds
 * to the current 30-minute slot.  No column overlay is drawn — only the header
 * label cells are tinted.
 *
 * Only active when:
 *   - The schedule is in "day" view
 *   - The displayed date matches today (local timezone)
 *   - The current time is within the configured work-day window
 *
 * Dependencies: schedule.grid (getCurrentTimeMinutes, isToday, getConfig)
 */
(() => {
  window.schedule = window.schedule || {};

  window.schedule.nowLine = (() => {
    let _tickTimer = null;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    const displayedDate = () => {
      const param = new URLSearchParams(window.location.search).get("date");
      if (param) return param;
      const slot = document.querySelector(".schedule-slot[data-date]");
      return slot ? slot.dataset.date : null;
    };

    // -------------------------------------------------------------------------
    // Core draw / remove
    // -------------------------------------------------------------------------

    const removeLine = () => {
      document.querySelectorAll("th.time-header.is-now")
        .forEach(el => el.classList.remove("is-now"));
    };

    const drawLine = () => {
      removeLine();

      const tables = document.getElementById("scheduleTables");
      if (!tables) return;

      if (tables.dataset.currentView !== "day") return;

      const date = displayedDate();
      if (!date || !window.schedule.grid.isToday(date)) return;

      const cfg = window.schedule.grid.getConfig();
      const now = window.schedule.grid.getCurrentTimeMinutes();
      if (now < cfg.dayStartMinutes || now > cfg.dayEndMinutes) return;

      const scrollEl = tables.querySelector(".table-fixed-head");
      if (!scrollEl) return;
      const table = scrollEl.querySelector("table");
      if (!table) return;

      const minutesIn  = now - cfg.dayStartMinutes;
      const slotIndex  = Math.floor(minutesIn / cfg.slotSizeMinutes);

      // Highlight the matching th in every thead (top sticky + bottom repeat)
      table.querySelectorAll("thead").forEach(thead => {
        const headers = thead.querySelectorAll("th.time-header");
        if (slotIndex >= 0 && slotIndex < headers.length) {
          headers[slotIndex].classList.add("is-now");
        }
      });
    };

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    const initOnce = () => {
      drawLine();

      if (_tickTimer) clearInterval(_tickTimer);
      _tickTimer = setInterval(drawLine, 60_000);

      document.addEventListener("schedule:table:updated", drawLine);
      document.addEventListener("schedule:view:changed",  drawLine);
    };

    return { initOnce, draw: drawLine };
  })();
})();
