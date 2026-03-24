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
  let lastHistoryState = {}; // Track previous history to detect new entries

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

  function formatClock(isoString) {
    if (!isoString) return "";
    try {
      const d = new Date(isoString);
      return d.toLocaleTimeString("lt-LT", {
        hour: "2-digit",
        minute: "2-digit",
      });
    } catch {
      return isoString;
    }
  }

  function tryParseDetails(detailsJson) {
    if (!detailsJson) return null;
    try {
      return JSON.parse(detailsJson);
    } catch {
      return null;
    }
  }

  function renderDetails(item) {
    const details = tryParseDetails(item.detailsJson);
    if (!details) return "";

    const rows = [];

    if (item.actionType === "move") {
      const oldTime = formatClock(details.oldStartAt);
      const newTime = formatClock(details.newStartAt);
      if (oldTime && newTime) {
        rows.push(`<div><span class="text-muted">Laikas:</span> ${escapeHtml(oldTime)} &rarr; ${escapeHtml(newTime)}</div>`);
      }
    }

    if (item.actionType === "reschedule") {
      const oldStart = formatClock(details.oldStartAt);
      const oldEnd = formatClock(details.oldEndAt);
      const newStart = formatClock(details.newStartAt);
      const newEnd = formatClock(details.newEndAt);
      if (oldStart && oldEnd && newStart && newEnd) {
        rows.push(`<div><span class="text-muted">Laiko langas:</span> ${escapeHtml(oldStart)}-${escapeHtml(oldEnd)} &rarr; ${escapeHtml(newStart)}-${escapeHtml(newEnd)}</div>`);
      }
    }

    if (details.repairApplied) {
      rows.push('<div><span class="text-muted">Pastaba:</span> pritaikytas automatinis taisymas</div>');
    }

    if (!rows.length) return "";
    return `<div class="schedule-history-item__details small text-muted">${rows.join("")}</div>`;
  }

  // Map "failure" → "error" to match existing CSS class names
  const OUTCOME_CSS = { success: "outcome-success", failure: "outcome-error", warning: "outcome-warning" };

  function getDateGroup(isoString) {
    if (!isoString) return "Negrūpuota";
    try {
      const d = new Date(isoString);
      const today = new Date();
      const yesterday = new Date(today);
      yesterday.setDate(yesterday.getDate() - 1);
      
      const dateStr = d.toLocaleDateString("lt-LT", { year: "numeric", month: "2-digit", day: "2-digit" });
      const todayStr = today.toLocaleDateString("lt-LT", { year: "numeric", month: "2-digit", day: "2-digit" });
      const yesterdayStr = yesterday.toLocaleDateString("lt-LT", { year: "numeric", month: "2-digit", day: "2-digit" });
      
      if (dateStr === todayStr) return "Šiandien";
      if (dateStr === yesterdayStr) return "Vakar";
      return dateStr;
    } catch {
      return "Negrūpuota";
    }
  }

  function renderEntry(item) {
    const icon = OUTCOME_ICONS[item.outcome] || OUTCOME_ICONS.failure;
    const outcomeClass = OUTCOME_CSS[item.outcome] || "outcome-error";
    const actionLabel = escapeHtml(ACTION_LABELS[item.actionType] || item.actionType);
    const actor = escapeHtml(item.actorDisplayName || "—");
    const role = escapeHtml(ROLE_LABELS[item.actorRole] || item.actorRole || "");
    const time = escapeHtml(formatTime(item.occurredAt));
    const summary = escapeHtml(item.summary);
    const details = renderDetails(item);

    return `<div class="schedule-history-item ${outcomeClass}">
  <div class="schedule-history-item__head">
    <span class="schedule-history-item__badge">${icon}</span>
    <span class="schedule-history-item__action">${actionLabel}</span>
    <span class="schedule-history-item__time">${time}</span>
    <button type="button" class="schedule-history-item__toggle" data-history-toggle aria-expanded="false" aria-label="Rodyti detales">
      <i class="bi bi-chevron-right" aria-hidden="true"></i>
    </button>
  </div>
  <div class="schedule-history-item__body" hidden>
    <p class="schedule-history-item__message">${summary}</p>
    ${details}
    <div class="schedule-history-item__meta">
      <span>${actor}</span>${role ? `<span class="ms-1 text-muted">(${role})</span>` : ""}
    </div>
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

      // Group items by date
      const grouped = {};
      items.forEach(item => {
        const dateGroup = getDateGroup(item.occurredAt);
        if (!grouped[dateGroup]) grouped[dateGroup] = [];
        grouped[dateGroup].push(item);
      });

      // Detect which outcome types received new entries
      const newOutcomeTypes = new Set();
      
      Object.entries(grouped).forEach(([dateGroup, entries]) => {
        const oldDateState = lastHistoryState[dateGroup] || {};
        
        // Check each outcome type for count increases
        ["success", "failure", "warning"].forEach(outcome => {
          const currentCount = entries.filter(e => e.outcome === outcome).length;
          const oldCount = oldDateState[outcome] || 0;
          
          if (currentCount > oldCount) {
            newOutcomeTypes.add(outcome);
          }
        });
      });

      // Render grouped sections
      const today = "Šiandien";
      const dateOrder = ["Šiandien", "Vakar"];
      const orderedDates = [
        ...dateOrder.filter(d => grouped[d]),
        ...Object.keys(grouped).filter(d => !dateOrder.includes(d))
      ];

      let html = "";
      orderedDates.forEach((dateGroup, idx) => {
        const entries = grouped[dateGroup];
        const isToday = dateGroup === today;
        const collapseId = `history-group-${idx}`;
        
        // Separate entries by outcome
        const successEntries = entries.filter(e => e.outcome === "success");
        const failureEntries = entries.filter(e => e.outcome === "failure");
        const warningEntries = entries.filter(e => e.outcome === "warning");
        
        html += `
          <div class="schedule-history-group mb-3">
            <div class="schedule-history-group__trigger" role="button" tabindex="0" data-bs-toggle="collapse" data-bs-target="#${collapseId}" aria-expanded="${isToday ? 'true' : 'false'}" aria-controls="${collapseId}">
              <strong class="schedule-history-group__title">${escapeHtml(dateGroup)}</strong>
              <small class="text-muted">${entries.length} veiksm${entries.length === 1 ? 'as' : 'ai'}</small>
              <i class="bi bi-chevron-down schedule-history-group__icon" aria-hidden="true"></i>
            </div>
            <div class="collapse ${isToday ? 'show' : ''}" id="${collapseId}">
              <div class="schedule-history-group__content">
        `;
        
        // Success section - expand only if it has new entries
        const showSuccess = newOutcomeTypes.has("success");
        html += `
          <div class="schedule-history-subsection">
            <div class="schedule-history-subsection__trigger" role="button" tabindex="0" data-bs-toggle="collapse" data-bs-target="#${collapseId}-success" aria-expanded="${showSuccess}" aria-controls="${collapseId}-success">
              <i class="bi bi-check-circle-fill text-success"></i>
              <strong class="schedule-history-subsection__title">Atlikti veiksmai</strong>
              <small class="text-muted">${successEntries.length}</small>
              <i class="bi bi-chevron-down schedule-history-subsection__icon" aria-hidden="true"></i>
            </div>
            <div class="collapse ${showSuccess ? 'show' : ''}" id="${collapseId}-success">
              <div class="schedule-history-subsection__content">
                ${successEntries.length > 0 
                  ? successEntries.map(renderEntry).join('') 
                  : '<div class="schedule-history-empty-message">Įrašų šiuo metu nėra</div>'
                }
              </div>
            </div>
          </div>
        `;
        
        // Failure section - expand only if it has new entries
        const showFailure = newOutcomeTypes.has("failure");
        html += `
          <div class="schedule-history-subsection">
            <div class="schedule-history-subsection__trigger" role="button" tabindex="0" data-bs-toggle="collapse" data-bs-target="#${collapseId}-failure" aria-expanded="${showFailure}" aria-controls="${collapseId}-failure">
              <i class="bi bi-x-circle-fill text-danger"></i>
              <strong class="schedule-history-subsection__title">Klaidos</strong>
              <small class="text-muted">${failureEntries.length}</small>
              <i class="bi bi-chevron-down schedule-history-subsection__icon" aria-hidden="true"></i>
            </div>
            <div class="collapse ${showFailure ? 'show' : ''}" id="${collapseId}-failure">
              <div class="schedule-history-subsection__content">
                ${failureEntries.length > 0 
                  ? failureEntries.map(renderEntry).join('') 
                  : '<div class="schedule-history-empty-message">Įrašų šiuo metu nėra</div>'
                }
              </div>
            </div>
          </div>
        `;
        
        // Warning section - expand only if it has new entries
        const hasWarnings = warningEntries.length > 0 || entries.some(e => !["success", "failure"].includes(e.outcome));
        const showWarning = newOutcomeTypes.has("warning") && hasWarnings;
        
        if (hasWarnings) {
          html += `
            <div class="schedule-history-subsection">
              <div class="schedule-history-subsection__trigger" role="button" tabindex="0" data-bs-toggle="collapse" data-bs-target="#${collapseId}-warning" aria-expanded="${showWarning}" aria-controls="${collapseId}-warning">
                <i class="bi bi-exclamation-triangle-fill text-warning"></i>
                <strong class="schedule-history-subsection__title">Įspėjimai</strong>
                <small class="text-muted">${warningEntries.length}</small>
                <i class="bi bi-chevron-down schedule-history-subsection__icon" aria-hidden="true"></i>
              </div>
              <div class="collapse ${showWarning ? 'show' : ''}" id="${collapseId}-warning">
                <div class="schedule-history-subsection__content">
                  ${warningEntries.length > 0 
                    ? warningEntries.map(renderEntry).join('') 
                    : '<div class="schedule-history-empty-message">Įrašų šiuo metu nėra</div>'
                  }
                </div>
              </div>
            </div>
          `;
        }
        
        html += `
              </div>
            </div>
          </div>
        `;
      });

      listEl.innerHTML = html;

      // Update history state for next comparison - track counts per outcome type per date group
      lastHistoryState = {};
      Object.entries(grouped).forEach(([dateGroup, entries]) => {
        lastHistoryState[dateGroup] = {};
        ["success", "failure", "warning"].forEach(outcome => {
          lastHistoryState[dateGroup][outcome] = entries.filter(e => e.outcome === outcome).length;
        });
      });
    } catch {
      if (loadingEl) loadingEl.classList.add("d-none");
      listEl.innerHTML = '<div class="small text-danger">Nepavyko įkelti istorijos.</div>';
    }
  }

  function openHistoryPanel() {
    const panel = window.schedule?.panel;
    if (panel?.show && panel?.showContent) {
      panel.show();
      panel.showContent("history");
      panelOpen = true;
      currentPlanId = null; // re-resolve on each open in case plan changed
      loadHistory();
    }
  }

  function init() {
    const panel = document.getElementById("taskDetailModal");
    if (!panel) return;

    panel.addEventListener("show.bs.offcanvas", () => {
      const isHistoryView = panel.querySelector("#historyPanelContent")?.style.display !== "none";
      if (isHistoryView) {
        panelOpen = true;
        currentPlanId = null; // re-resolve on each open in case plan changed
        loadHistory();
      }
    });

    panel.addEventListener("hide.bs.offcanvas", () => {
      panelOpen = false;
    });

    document.addEventListener("schedule:action-completed", () => {
      if (panelOpen) {
        loadHistory();
      }
    });

    // Expand/collapse individual history entries
    document.getElementById("historyPanelContent")?.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-history-toggle]");
      if (!btn) return;
      const item = btn.closest(".schedule-history-item");
      if (!item) return;
      const body = item.querySelector(".schedule-history-item__body");
      if (!body) return;
      const expanded = btn.getAttribute("aria-expanded") === "true";
      body.hidden = expanded;
      btn.setAttribute("aria-expanded", String(!expanded));
      const icon = btn.querySelector("i");
      if (icon) {
        icon.classList.toggle("bi-chevron-right", expanded);
        icon.classList.toggle("bi-chevron-down", !expanded);
      }
      item.classList.toggle("is-expanded", !expanded);
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }

  // Export openHistoryPanel globally
  window.schedule = window.schedule || {};
  window.schedule.history = {
    openHistoryPanel,
  };
})();
