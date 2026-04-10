/**
 * schedule.grid.js
 *
 * Grid configuration and time calculation utilities.
 * Provides a single source of truth for slot sizes, day boundaries,
 * and time formatting functions used across schedule modules.
 *
 * Dependencies: None (reads from #scheduleTables data attributes)
 * Dependents: schedule.selection, schedule.tasks, schedule.dragdrop, schedule.resize
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};

  /**
   * Safely parse an integer from any value.
   * @param {*} val - Value to parse
   * @returns {number|null} Parsed integer or null if invalid
   */
  const parseIntSafe = (val) => {
    const n = Number.parseInt(String(val || ""), 10);
    return Number.isFinite(n) ? n : null;
  };

  /**
   * Get grid configuration from the schedule tables element.
   * @returns {{ slotSizeMinutes: number, dayStartMinutes: number, dayEndMinutes: number }}
   */
  const getConfig = () => {
    const tables = document.getElementById("scheduleTables");
    if (!tables) {
      return {
        slotSizeMinutes: 30,
        dayStartMinutes: 8 * 60,  // 08:00
        dayEndMinutes: 20 * 60,   // 20:00
      };
    }
    return {
      slotSizeMinutes: parseIntSafe(tables.dataset.slotSizeMinutes) || 30,
      dayStartMinutes: parseIntSafe(tables.dataset.dayStartMinutes) || 8 * 60,
      dayEndMinutes: parseIntSafe(tables.dataset.dayEndMinutes) || 20 * 60,
    };
  };

  /**
   * Convert minutes since midnight to HH:MM format.
   * @param {number} minutes - Minutes since midnight
   * @returns {string} Formatted time string (e.g., "08:30")
   */
  const minutesToTime = (minutes) => {
    const m = Math.max(0, minutes | 0);
    const h = String(Math.floor(m / 60)).padStart(2, "0");
    const min = String(m % 60).padStart(2, "0");
    return `${h}:${min}`;
  };

  /**
   * Format a time range as "HH:MM–HH:MM".
   * @param {number} startMinutes - Start time in minutes since midnight
   * @param {number} endMinutes - End time in minutes since midnight
   * @returns {string} Formatted range (e.g., "08:30–09:00")
   */
  const formatRange = (startMinutes, endMinutes) => {
    return `${minutesToTime(startMinutes)}–${minutesToTime(endMinutes)}`;
  };

  /**
   * Get the client's timezone offset in minutes.
   * Positive values indicate timezones ahead of UTC.
   * @returns {number} Timezone offset in minutes
   */
  const getTimezoneOffset = () => {
    return -new Date().getTimezoneOffset();
  };

  /**
   * Calculate the number of slots for a given duration.
   * @param {number} durationMinutes - Duration in minutes
   * @returns {number} Number of slots (minimum 1)
   */
  const calculateSlots = (durationMinutes) => {
    const { slotSizeMinutes } = getConfig();
    return Math.max(1, Math.round(durationMinutes / slotSizeMinutes));
  };

  /**
   * Round minutes up to the next slot boundary.
   * @param {number} minutes - Minutes to round
   * @returns {number} Rounded minutes
   */
  const roundToNextSlot = (minutes) => {
    const { slotSizeMinutes } = getConfig();
    return Math.ceil(minutes / slotSizeMinutes) * slotSizeMinutes;
  };

  /**
   * Round minutes down to the previous slot boundary.
   * @param {number} minutes - Minutes to round
   * @returns {number} Rounded minutes
   */
  const roundToPrevSlot = (minutes) => {
    const { slotSizeMinutes } = getConfig();
    return Math.floor(minutes / slotSizeMinutes) * slotSizeMinutes;
  };

  /**
   * Check if a given date string represents today.
   * @param {string} dateStr - Date string in YYYY-MM-DD format
   * @returns {boolean} True if the date is today
   */
  const isToday = (dateStr) => {
    const now = new Date();
    const taskDate = new Date(dateStr + "T00:00:00");
    return now.toDateString() === taskDate.toDateString();
  };

  /**
   * Get current time in minutes since midnight.
   * @returns {number} Current time in minutes
   */
  const getCurrentTimeMinutes = () => {
    const now = new Date();
    return now.getHours() * 60 + now.getMinutes();
  };

  /**
   * Calculate the minimum allowed start time for a task on a given date.
   * For today, this is the next slot boundary after current time.
   * For other days, this is the day start time.
   * @param {string} dateStr - Date string in YYYY-MM-DD format
   * @returns {number} Minimum start time in minutes
   */
  const getMinStartMinutes = (dateStr) => {
    const { dayStartMinutes } = getConfig();
    if (isToday(dateStr)) {
      const tables = document.getElementById("scheduleTables");
      const server = tables ? parseIntSafe(tables.dataset.earliestStartMinutes) : null;
      return server ?? roundToNextSlot(getCurrentTimeMinutes());
    }
    return dayStartMinutes;
  };

  /**
   * Get the backend-computed earliest creatable placement for the currently selected schedule date.
   * @returns {{ targetDate: string, startMinutes: number, endMinutes: number } | null}
   */
  const getEarliestCreatablePlacement = () => {
    const tables = document.getElementById("scheduleTables");
    if (!tables) return null;

    const targetDate = String(tables.dataset.earliestCreatableDate || "").trim();
    const startMinutes = parseIntSafe(tables.dataset.earliestCreatableStartMinutes);
    const endMinutes = parseIntSafe(tables.dataset.earliestCreatableEndMinutes);

    if (!/^\d{4}-\d{2}-\d{2}$/.test(targetDate) || startMinutes == null || endMinutes == null) {
      return null;
    }

    return {
      targetDate,
      startMinutes,
      endMinutes,
    };
  };

  // Export module
  window.schedule.grid = {
    parseIntSafe,
    getConfig,
    minutesToTime,
    formatRange,
    getTimezoneOffset,
    calculateSlots,
    roundToNextSlot,
    roundToPrevSlot,
    isToday,
    getCurrentTimeMinutes,
    getMinStartMinutes,
    getEarliestCreatablePlacement,
  };
})();
