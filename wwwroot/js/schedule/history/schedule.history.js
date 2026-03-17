/**
 * schedule.history.js
 * Populates #scheduleHistoryPanel from GET /api/plans/{planId}/history.
 * Re-fetches when schedule:action-completed is dispatched while the panel is open.
 */
(() => {
  "use strict";

  const ACTION_LABELS = {
    create: "Kūrimas",
    update: "Atnaujinimas",
    reschedule: "Laiko keitimas",
    move: "Perkėlimas",
    swap: "Sukeitimas",
    swap_smart: "Sukeitimas",
    delete: "Trynimas",
    technician_update: "Techniko atnaujinimas",
  };

  const OUTCOME_ICONS = {
    success: '<i class="bi bi-check-circle-fill text-success" aria-hidden="true"></i>',
    failure: '<i class="bi bi-x-circle-fill text-danger" aria-hidden="true"></i>',
    warning: '<i class="bi bi-exclamation-triangle-fill text-warning" aria-hidden="true"></i>',
  };

  const ROLE_LABELS = {
    manager: "Vadovas",
    technician: "Technikas",
  };

  let currentPlanId = null;
  let panelOpen = false;

  function escapeHtml(str) {
    if (!str) return "";
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function formatTime(isoString) {
    if (!isoString) return "";
    try {
      const d = new Date(isoString);
      return d.toLocaleString("lt-LT", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      });
    } catch {
      return isoString;
    }
  }

  // Map "failure" → "error" to match existing CSS class names
  const OUTCOME_CSS = { success: "outcome-success", failure: "outcome-error", warning: "outcome-warning" };

  function renderEntry(item) {
    const icon = OUTCOME_ICONS[item.outcome] || OUTCOME_ICONS.failure;
    const outcomeClass = OUTCOME_CSS[item.outcome] || "outcome-error";
    const actionLabel = escapeHtml(ACTION_LABELS[item.actionType] || item.actionType);
    const actor = escapeHtml(item.actorDisplayName || "—");
    const role = escapeHtml(ROLE_LABELS[item.actorRole] || item.actorRole || "");
    const time = escapeHtml(formatTime(item.occurredAt));
    const summary = escapeHtml(item.summary);

    return `<div class="schedule-history-item ${outcomeClass}">
  <div class="schedule-history-item__head">
    <span class="schedule-history-item__badge">${icon}</span>
    <span class="schedule-history-item__action">${actionLabel}</span>
    <span class="schedule-history-item__time">${time}</span>
  </div>
  <p class="schedule-history-item__message">${summary}</p>
  <div class="schedule-history-item__meta">
    <span>${actor}</span>${role ? `<span class="ms-1 text-muted">(${role})</span>` : ""}
  </div>
</div>`;
  }

  async function resolvePlanId() {
    try {
      const resp = await fetch("/api/plans/state", { credentials: "same-origin" });
      if (!resp.ok) return null;
      const data = await resp.json();
      return data.draftPlanId ?? data.activePlanId ?? null;
    } catch {
      return null;
    }
  }

  async function loadHistory() {
    const listEl = document.getElementById("scheduleHistoryList");
    const loadingEl = document.getElementById("scheduleHistoryLoading");
    const emptyEl = document.getElementById("scheduleHistoryEmpty");
    if (!listEl) return;

    if (loadingEl) loadingEl.classList.remove("d-none");
    if (emptyEl) emptyEl.classList.add("d-none");
    listEl.innerHTML = "";

    const planId = currentPlanId || (await resolvePlanId());
    currentPlanId = planId;

    if (!planId) {
      if (loadingEl) loadingEl.classList.add("d-none");
      if (emptyEl) emptyEl.classList.remove("d-none");
      return;
    }

    try {
      const resp = await fetch(`/api/plans/${planId}/history?limit=50`, { credentials: "same-origin" });
      if (loadingEl) loadingEl.classList.add("d-none");
      if (!resp.ok) {
        listEl.innerHTML = '<div class="small text-danger">Nepavyko įkelti istorijos.</div>';
        return;
      }
      const items = await resp.json();
      if (!items.length) {
        if (emptyEl) emptyEl.classList.remove("d-none");
        return;
      }
      listEl.innerHTML = items.map(renderEntry).join("");
    } catch {
      if (loadingEl) loadingEl.classList.add("d-none");
      listEl.innerHTML = '<div class="small text-danger">Nepavyko įkelti istorijos.</div>';
    }
  }

  function init() {
    const panel = document.getElementById("scheduleHistoryPanel");
    if (!panel) return;

    panel.addEventListener("show.bs.offcanvas", () => {
      panelOpen = true;
      currentPlanId = null; // re-resolve on each open in case plan changed
      loadHistory();
    });

    panel.addEventListener("hide.bs.offcanvas", () => {
      panelOpen = false;
    });

    document.addEventListener("schedule:action-completed", () => {
      if (panelOpen) {
        loadHistory();
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
