/**
 * schedule.backlog.js
 *
 * "Laukiantys darbai" — backlog panel for unscheduled tasks.
 *
 * Reuses the existing task detail form (#taskPanelContent) for the create tab.
 * Scheduling fields (assignee, start, end, asset section) are hidden in backlog mode
 * and restored when the panel switches back to task or history mode.
 *
 * Storage: server-side via /api/backlog (in-memory cache for rendering).
 *
 * Exposes: window.schedule.backlog.openBacklogPanel()
 */
(() => {
  "use strict";

  // ── Enum constants (sourced from server via window.ScheduleEnums) ─────────
  const ITEM_TYPE = window.ScheduleEnums?.BacklogItemType ?? { Temporary: 0, Permanent: 1 };

  // ── Storage (in-memory cache backed by /api/backlog) ─────────────────────

  let _items = [];
  let _localNow = null;

  function localNowEnd() {
    if (!_localNow) return "";
    const slotSize = window.schedule?.grid?.getConfig?.().slotSizeMinutes ?? 30;
    const d = new Date(_localNow);
    if (isNaN(d)) return "";
    d.setMinutes(d.getMinutes() + slotSize);
    const pad = n => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }
  let _activeTypeTab = ITEM_TYPE.Permanent;
  let _selectionMode = false;
  let _selectedIds = new Set();

  function getItems() { return _items; }

  async function loadItemsFromApi() {
    try {
      const resp = await fetch("/api/backlog", { credentials: "same-origin" });
      if (!resp.ok) return;
      const data = await resp.json();
      _items = data.items.map(normalizeServerItem);
      _localNow = data.localNow ?? null;
    } catch {
      // keep existing cache on network error
    }
  }

  function normalizeServerItem(raw) {
    return {
      id: String(raw.id),
      title: raw.title || "",
      priority: raw.priority ?? "",
      itemType: raw.itemType ?? ITEM_TYPE.Temporary,
      skillLevel: raw.skillLevel ?? "",
      instructions: raw.instructions ?? "",
      objectName: raw.objectName ?? "",
      locationText: raw.objectAddress ?? "",
      dueDate: raw.dueDate ? raw.dueDate.substring(0, 10) : null,
      createdAt: raw.createdAt || new Date().toISOString(),
    };
  }

  async function parseApiResponse(resp) {
    if (resp.ok) return { ok: true, data: await resp.json() };
    let body = {};
    try { body = await resp.json(); } catch { /* ignore */ }
    if (resp.status === 422 || resp.status === 400) {
      return { ok: false, validationErrors: body.errors ?? null, error: body.title ?? body.message ?? null };
    }
    return { ok: false, error: body.message ?? null };
  }

  async function apiAddItem(dto) {
    const resp = await fetch("/api/backlog", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(dto),
    });
    const result = await parseApiResponse(resp);
    if (!result.ok) return result;
    const mapped = normalizeServerItem(result.data.item);
    _items.unshift(mapped);
    return { ok: true, item: mapped, message: result.data.message };
  }

  async function apiUpdateItem(id, dto) {
    const resp = await fetch(`/api/backlog/${id}`, {
      method: "PUT",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(dto),
    });
    const result = await parseApiResponse(resp);
    if (!result.ok) return result;
    const mapped = normalizeServerItem(result.data.item);
    const idx = _items.findIndex((i) => i.id === String(id));
    if (idx !== -1) _items[idx] = mapped;
    return { ok: true, item: mapped, message: result.data.message };
  }

  async function apiRemoveItem(id) {
    const resp = await fetch(`/api/backlog/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!resp.ok) throw new Error("backlog.delete.failed");
    const { message } = await resp.json();
    _items = _items.filter((i) => i.id !== String(id));
    return message;
  }

  function getItemById(id) {
    return _items.find((i) => i.id === String(id)) ?? null;
  }

  // ── Panel element helpers ────────────────────────────────────────────────

  const getPanelEl = () => document.getElementById("taskDetailModal");

  // ── Field show/hide (backlog mode restrictions) ──────────────────────────

  function applyBacklogMode() {
    clearSchedulingContext();
    const panel = getPanelEl();
    if (!panel) return;

    // Hide only scheduling-specific fields — who and when are not known yet for backlog
    // All other fields (object, address, skill, priority, instructions) stay visible
    // so backlog creation collects the same information as task creation
    panel.querySelector("#taskFieldAssignee")?.classList.add("d-none");
    panel.querySelector("#taskFieldStart")?.classList.add("d-none");
    panel.querySelector("#taskFieldEnd")?.classList.add("d-none");
    panel.querySelector("#technicianSection")?.classList.add("d-none");

    // Show backlog-only fields
    panel.querySelector("#backlogTypeField")?.classList.remove("d-none");
    panel.querySelector("#backlogDueDateField")?.classList.remove("d-none");

    // Reset active type tab to Permanent on each open
    _activeTypeTab = ITEM_TYPE.Permanent;
    document.getElementById("backlogTypeTabPerm")?.classList.add("active");
    document.getElementById("backlogTypeTabTemp")?.classList.remove("active");

    // Cancel any pending edit and clear form
    cancelEdit();
    const set = (id, val) => { const el = panel.querySelector(id); if (el) el.value = val; };
    set("#taskDetailTitle", "");
    set("#taskDetailPriority", "");
    set("#backlogItemType", "");
    set("#taskDetailSkill", "");
    set("#taskDetailInstructions", "");
    set("#backlogDueDate", "");

    // Reset object/address dropdowns to empty (same as prepareCreateMode does)
    const modal = window.schedule?.tasks?.modal;
    if (modal) {
      Promise.all([
        modal.populateObjectDropdown(panel, null, null, null),
        modal.populateAddressDropdown(panel, null),
      ]).then(() => modal.ensureObjectAddressBinding(panel));
    }

    // Swap save buttons
    panel.querySelector("#taskDetailSave")?.classList.add("d-none");
    panel.querySelector("#backlogSaveBtn")?.classList.remove("d-none");

    // Show the tab bar
    panel.querySelector("#backlogTabBar")?.classList.remove("d-none");

    // Default to list tab
    switchTab("list", false);
  }

  function restoreFromBacklogMode() {
    const panel = getPanelEl();
    if (!panel) return;

    // Restore hidden scheduling fields — task.modal.js re-applies correct restrictions
    panel.querySelector("#taskFieldAssignee")?.classList.remove("d-none");
    panel.querySelector("#taskFieldStart")?.classList.remove("d-none");
    panel.querySelector("#taskFieldEnd")?.classList.remove("d-none");
    // technicianSection and taskSectionAsset are managed by task.modal.js — do not touch

    // Hide backlog-only and bulk-only fields and reset them
    const planModeField = panel.querySelector("#planModeField");
    if (planModeField) planModeField.classList.add("d-none");
    const planModeEl = panel.querySelector("#taskPlanMode");
    if (planModeEl) planModeEl.value = "";
    panel.querySelectorAll("input[name='taskPlanModeRadio']").forEach(r => r.checked = false);
    document.getElementById("taskBulkProgress")?.classList.add("d-none");

    const typeField = panel.querySelector("#backlogTypeField");
    if (typeField) typeField.classList.add("d-none");
    const typeSelect = panel.querySelector("#backlogItemType");
    if (typeSelect) typeSelect.value = "";
    const dueDateField = panel.querySelector("#backlogDueDateField");
    if (dueDateField) dueDateField.classList.add("d-none");
    const dueDateInput = panel.querySelector("#backlogDueDate");
    if (dueDateInput) dueDateInput.value = "";

    // Restore save buttons, hide bulk nav
    panel.querySelector("#taskDetailSave")?.classList.remove("d-none");
    panel.querySelector("#backlogSaveBtn")?.classList.add("d-none");
    document.getElementById("taskFooterClose")?.classList.remove("d-none");
    document.getElementById("taskBulkBack")?.classList.add("d-none");
    document.getElementById("taskBulkNext")?.classList.add("d-none");
    document.getElementById("taskBulkConfirmView")?.classList.add("d-none");

    // Hide the tab bar
    panel.querySelector("#backlogTabBar")?.classList.add("d-none");

    // Ensure list container is hidden
    const listContent = document.getElementById("backlogPanelContent");
    if (listContent) listContent.style.display = "none";
  }

  // ── Badge ────────────────────────────────────────────────────────────────

  function updateBadge() {
    const items = getItems();
    const tempCount = items.filter((i) => i.itemType === ITEM_TYPE.Temporary).length;
    const permCount = items.filter((i) => i.itemType === ITEM_TYPE.Permanent).length;

    const badgeDesktop = document.getElementById("navBacklogBadge");
    const badgeMobile = document.getElementById("navBacklogBadgeMobile");
    [badgeDesktop, badgeMobile].forEach((el) => {
      if (!el) return;
      el.textContent = tempCount;
      el.classList.toggle("d-none", tempCount === 0);
    });

    const tempBadge = document.getElementById("backlogCountTemp");
    const permBadge = document.getElementById("backlogCountPerm");
    if (tempBadge) tempBadge.textContent = tempCount;
    if (permBadge) permBadge.textContent = permCount;
  }

  // ── Trigger button visibility (daily view only) ──────────────────────────

  function setTriggerVisible(visible) {
    const desktop = document.getElementById("navBacklogTriggerDesktop");
    if (desktop) desktop.classList.toggle("d-none", !visible);
    // Mobile button has no id — hide its parent <li> via closest
    document.querySelectorAll(".nav-backlog-mobile-item").forEach((li) => {
      li.classList.toggle("d-none", !visible);
    });
  }

  function isDailyView() {
    const tables = document.getElementById("scheduleTables");
    return (tables?.dataset?.currentView || "day").toLowerCase() === "day";
  }

  // ── List rendering ───────────────────────────────────────────────────────

  const PRIORITY = window.ScheduleEnums?.TaskPriority ?? { Low: 0, Medium: 1, High: 2 };
  const PRIORITY_LABELS = {
    [PRIORITY.Low]: "Žemas",
    [PRIORITY.Medium]: "Vidutinis",
    [PRIORITY.High]: "Aukštas",
  };
  const PRIORITY_CLASSES = {
    [PRIORITY.Low]: "bg-secondary",
    [PRIORITY.Medium]: "bg-warning text-dark",
    [PRIORITY.High]: "bg-danger",
  };
  const SKILL_LABELS = { Basic: "Bazinis", Advanced: "Pažengęs", Certified: "Sertifikuotas" };

  function buildDueDateBadge(dueDate) {
    if (!dueDate) return "";
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const due = new Date(dueDate + "T00:00:00");
    due.setHours(0, 0, 0, 0);
    const diffDays = Math.round((due - today) / 86400000);
    const label = due.toLocaleDateString("lt-LT");
    if (diffDays < 0) {
      return `<span class="badge bg-danger text-white">Vėluoja ${Math.abs(diffDays)}d.</span>`;
    }
    if (diffDays <= 3) {
      return `<span class="badge bg-warning text-dark">Iki ${escapeHtml(label)}</span>`;
    }
    return `<span class="badge bg-light text-muted border">Iki ${escapeHtml(label)}</span>`;
  }

  function escapeHtml(str) {
    if (!str) return "";
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function exitSelectionMode() {
    if (!_selectionMode) return;
    _selectionMode = false;
    _selectedIds.clear();
    document.getElementById("backlogPanelContent")?.classList.remove("is-selecting");
  }

  function enterSelectionMode() {
    _selectionMode = true;
    _selectedIds.clear();
    document.getElementById("backlogPanelContent")?.classList.add("is-selecting");
    updateBulkBar();
  }

  function updateBulkBar() {
    const countEl = document.getElementById("backlogBulkCount");
    const deleteBtn = document.getElementById("backlogBulkDelete");
    const scheduleBtn = document.getElementById("backlogBulkSchedule");
    const selectAllCb = document.getElementById("backlogSelectAll");
    const n = _selectedIds.size;
    const total = getItems().filter(i => i.itemType === _activeTypeTab).length;
    if (countEl) countEl.textContent = n === 0 ? "Nepasirinkta" : `${n} / ${total} pasirinkta`;
    if (deleteBtn) deleteBtn.disabled = n === 0;
    if (scheduleBtn) scheduleBtn.disabled = n === 0;
    if (selectAllCb) {
      if (n === 0) { selectAllCb.checked = false; selectAllCb.indeterminate = false; }
      else if (n >= total) { selectAllCb.checked = true; selectAllCb.indeterminate = false; }
      else { selectAllCb.checked = false; selectAllCb.indeterminate = true; }
    }
  }

  function renderList() {
    const listEl = document.getElementById("backlogItemsList");
    const emptyEl = document.getElementById("backlogListEmpty");
    if (!listEl) return;

    const items = getItems().filter((i) => i.itemType === _activeTypeTab);
    listEl.innerHTML = "";

    if (items.length === 0) {
      if (emptyEl) emptyEl.classList.remove("d-none");
      return;
    }
    if (emptyEl) emptyEl.classList.add("d-none");

    items.forEach((item) => {
      const card = document.createElement("div");
      card.className = "backlog-card";
      if (_selectionMode && _selectedIds.has(String(item.id))) card.classList.add("is-selected");
      card.setAttribute("draggable", _selectionMode ? "false" : "true");
      card.dataset.backlogId = item.id;

      const priorityBadge = item.priority !== ""
        ? `<span class="badge ${PRIORITY_CLASSES[item.priority] || "bg-secondary"}">${escapeHtml(PRIORITY_LABELS[item.priority] || item.priority)}</span>`
        : "";

      const hasDetails = !!(item.instructions || item.objectName || item.skillLevel);
      const detailsHtml = hasDetails ? `
        <div class="backlog-card__details" id="backlog-details-${escapeHtml(item.id)}" hidden>
          ${item.skillLevel ? `<div class="backlog-card__detail-block"><span class="backlog-card__detail-label">Kvalifikacija</span><div class="backlog-card__skill"><i class="bi bi-person-check me-1" aria-hidden="true"></i>${escapeHtml(SKILL_LABELS[item.skillLevel] || item.skillLevel)}</div></div>` : ""}
          ${item.objectName ? `<div class="backlog-card__detail-block"><span class="backlog-card__detail-label">Objektas</span><div class="backlog-card__location"><i class="bi bi-geo-alt me-1" aria-hidden="true"></i>${escapeHtml(item.objectName)}${item.locationText ? ` · ${escapeHtml(item.locationText)}` : ""}</div></div>` : ""}
          ${item.instructions ? `<div class="backlog-card__detail-block"><span class="backlog-card__detail-label">Instrukcijos</span><div class="backlog-card__instructions">${escapeHtml(item.instructions)}</div></div>` : ""}
        </div>` : "";

      const createdLabel = new Date(item.createdAt).toLocaleDateString("lt-LT");

      card.innerHTML = `
        <div class="backlog-card__row">
          ${_selectionMode ? `<input type="checkbox" class="backlog-card__checkbox" aria-label="Pasirinkti" tabindex="-1" ${_selectedIds.has(String(item.id)) ? "checked" : ""}>` : ""}
          <div class="backlog-card__title">${escapeHtml(item.title) || "<em class='text-muted'>Be pavadinimo</em>"}</div>
          <div class="backlog-card__badges">${priorityBadge}${buildDueDateBadge(item.dueDate)}</div>
          <span class="backlog-card__date">${escapeHtml(createdLabel)}</span>
          ${hasDetails ? `<button type="button" class="backlog-card__toggle" data-toggle-id="${escapeHtml(item.id)}" aria-label="Rodyti detales" aria-expanded="false"><i class="bi bi-chevron-right" aria-hidden="true"></i></button>` : ""}
          <button type="button" class="backlog-card__schedule" data-schedule-id="${escapeHtml(item.id)}" aria-label="Planuoti užduotį" title="Planuoti užduotį">
            <i class="bi bi-calendar-plus" aria-hidden="true"></i>
          </button>
          <button type="button" class="backlog-card__edit" data-edit-id="${escapeHtml(item.id)}" aria-label="Redaguoti">
            <i class="bi bi-pencil" aria-hidden="true"></i>
          </button>
          <button type="button" class="backlog-card__delete" data-delete-id="${escapeHtml(item.id)}" aria-label="Pašalinti iš eilės">
            <i class="bi bi-x-lg" aria-hidden="true"></i>
          </button>
        </div>
        ${detailsHtml}
        ${hasDetails ? `<div class="backlog-card__footer">${escapeHtml(createdLabel)}</div>` : ""}
      `;

      setupCardDrag(card, item);

      // Prevent drag from firing when interacting with action buttons
      card.querySelector(".backlog-card__toggle")?.addEventListener("mousedown", (e) => e.stopPropagation());
      card.querySelector(".backlog-card__schedule")?.addEventListener("mousedown", (e) => e.stopPropagation());
      card.querySelector(".backlog-card__edit")?.addEventListener("mousedown", (e) => e.stopPropagation());
      card.querySelector(".backlog-card__delete")?.addEventListener("mousedown", (e) => e.stopPropagation());

      listEl.appendChild(card);
    });
  }

  // ── Tab switching ────────────────────────────────────────────────────────

  /**
   * Switch between create tab (shows task form) and list tab (shows backlog list).
   * @param {string} tabName - "create" | "list"
   * @param {boolean} [render=true] - whether to re-render the list (skip on first open)
   */
  function switchTab(tabName, render = true) {
    const taskContent = document.getElementById("taskPanelContent");
    const listContent = document.getElementById("backlogPanelContent");
    const footer = document.getElementById("taskDetailFooter");
    const btnCreate = document.getElementById("backlogTabCreate");
    const btnList = document.getElementById("backlogTabList");

    if (tabName === "create") {
      if (taskContent) taskContent.style.display = "";
      if (listContent) listContent.style.display = "none";
      if (footer) footer.classList.remove("d-none");
      if (btnCreate) { btnCreate.classList.add("active", "btn-primary"); btnCreate.classList.remove("btn-outline-secondary"); }
      if (btnList) { btnList.classList.remove("active", "btn-primary"); btnList.classList.add("btn-outline-secondary"); }
    } else {
      cancelEdit();
      if (taskContent) taskContent.style.display = "none";
      if (listContent) listContent.style.display = "";
      if (footer) footer.classList.add("d-none");
      if (btnCreate) { btnCreate.classList.remove("active", "btn-primary"); btnCreate.classList.add("btn-outline-secondary"); }
      if (btnList) { btnList.classList.add("active", "btn-primary"); btnList.classList.remove("btn-outline-secondary"); }
      if (render) renderList();
    }
  }

  // ── Save (reads from shared form fields) ────────────────────────────────

  async function handleSave() {
    const panel = getPanelEl();
    if (!panel) return;

    // Read object/address from Select2
    const objectEl = panel.querySelector("#taskDetailObject");
    const locationEl = panel.querySelector("#taskDetailLocation");
    const objectId = objectEl?.value ?? "";
    const objectText = objectEl?.options?.[objectEl.selectedIndex]?.text ?? "";
    const addressValue = locationEl?.value ?? "";

    const modal = window.schedule?.tasks?.modal;
    const formData = modal?.collectFormData?.(panel);
    const itemTypeRaw = panel.querySelector("#backlogItemType")?.value ?? "";

    let allValid = formData ? (modal?.validateFormData?.(panel, formData, true, { skipScheduling: true }) ?? true) : true;

    if (itemTypeRaw === "") {
      modal?.setFieldError?.(panel, "#backlogItemType", "Šablono tipas yra privalomas.");
      allValid = false;
    }

    if (!allValid) return;

    const title = panel.querySelector("#taskDetailTitle")?.value?.trim() ?? "";
    const priorityRaw = panel.querySelector("#taskDetailPriority")?.value ?? "";

    const dueDateRaw = panel.querySelector("#backlogDueDate")?.value ?? "";

    const dto = {
      title,
      priority: priorityRaw !== "" ? Number(priorityRaw) : null,
      itemType: Number(itemTypeRaw),
      skillLevel: panel.querySelector("#taskDetailSkill")?.value ?? "",
      instructions: panel.querySelector("#taskDetailInstructions")?.value?.trim() ?? "",
      objectId: objectId && objectId !== "" ? objectId : "",
      objectName: objectText && objectText !== "– Pasirinkite –" ? objectText : "",
      objectAddress: addressValue && addressValue !== "" ? addressValue : "",
      dueDate: dueDateRaw !== "" ? dueDateRaw : null,
    };

    const FIELD_MAP = {
      Title: "#taskDetailTitle",
      SkillLevel: "#taskDetailSkill",
      ObjectName: "#taskDetailObject",
      ObjectAddress: "#taskDetailLocation",
      Instructions: "#taskDetailInstructions",
      Priority: "#taskDetailPriority",
      ItemType: "#backlogItemType",
    };

    let result;
    try {
      result = editingBacklogId
        ? await apiUpdateItem(editingBacklogId, dto)
        : await apiAddItem(dto);
    } catch {
      window.schedule?.ui?.toast?.showError?.("Nepavyko išsaugoti.");
      return;
    }

    if (!result.ok) {
      if (result.validationErrors) {
        // DueDate errors are business-rule rejections — show as toast, not inline field error
        const dueDateErr = result.validationErrors.DueDate;
        if (dueDateErr) {
          const msg = Array.isArray(dueDateErr) ? dueDateErr[0] : dueDateErr;
          window.schedule?.ui?.toast?.showError?.(msg);
          return;
        }
        if (modal?.displayValidationErrors) {
          modal.displayValidationErrors(panel, result.validationErrors, FIELD_MAP);
        }
      } else {
        window.schedule?.ui?.toast?.showError?.(result.error ?? "Nepavyko išsaugoti.");
      }
      return;
    }

    window.schedule?.ui?.toast?.showSuccess?.(result.message);

    // Clear form fields
    const clear = (id) => { const el = panel.querySelector(id); if (el) el.value = ""; };
    clear("#taskDetailTitle");
    clear("#taskDetailPriority");
    clear("#backlogItemType");
    clear("#taskDetailSkill");
    clear("#taskDetailInstructions");

    updateBadge();
    switchTab("list");
  }

  // ── Drag from backlog to schedule ────────────────────────────────────────

  let draggingBacklogId = null;
  let editingBacklogId = null;
  let _schedulingFromBacklogId = null;
  let _schedulingFromBacklogItemType = null;

  // ── Bulk scheduling queue state ───────────────────────────────────────────
  let _bulkQueue         = [];
  let _bulkQueueIdx      = 0;
  let _isBulkMode        = false;
  let _bulkScheduledData = [];   // form snapshots per queue item
  let _bulkConfirmPhase  = false; // true while confirm view is shown

  function clearSchedulingContext() {
    _schedulingFromBacklogId = null;
    _schedulingFromBacklogItemType = null;
  }

  function resetBulkState() {
    _bulkQueue         = [];
    _bulkQueueIdx      = 0;
    _isBulkMode        = false;
    _bulkScheduledData = [];
    _bulkConfirmPhase  = false;
    const panel = getPanelEl();
    if (!panel) return;
    delete panel.dataset.bulkMode;
    panel.querySelector("#planModeField")?.classList.add("d-none");
    const planModeEl = panel.querySelector("#taskPlanMode");
    if (planModeEl) planModeEl.value = "";
    panel.querySelectorAll("input[name='taskPlanModeRadio']").forEach(r => r.checked = false);
    document.getElementById("taskBulkProgress")?.classList.add("d-none");
    // Restore normal footer buttons
    document.getElementById("taskFooterClose")?.classList.remove("d-none");
    document.getElementById("taskDetailSave")?.classList.remove("d-none");
    document.getElementById("taskBulkBack")?.classList.add("d-none");
    document.getElementById("taskBulkNext")?.classList.add("d-none");
    document.getElementById("taskBulkBack")?.removeAttribute("disabled");
    document.getElementById("taskBulkNext")?.removeAttribute("disabled");
    // Hide confirm view if open
    document.getElementById("taskBulkConfirmView")?.classList.add("d-none");
    document.querySelectorAll("#taskPanelContent .task-section").forEach(s => s.classList.remove("d-none"));
  }

  async function startEdit(id) {
    const item = getItemById(id);
    if (!item) return;
    editingBacklogId = id;

    const panel = getPanelEl();
    const set = (sel, val) => { const el = panel?.querySelector(sel); if (el) el.value = val ?? ""; };
    set("#taskDetailTitle", item.title);
    set("#taskDetailPriority", item.priority);
    set("#backlogItemType", item.itemType);
    set("#taskDetailSkill", item.skillLevel);
    set("#taskDetailInstructions", item.instructions);
    set("#backlogDueDate", item.dueDate ?? "");

    // Prefill object/address dropdowns
    const modal = window.schedule?.tasks?.modal;
    if (modal) {
      await modal.populateObjectDropdown(panel, item.objectId || null, item.objectName || null, item.locationText || null);
      await modal.populateAddressDropdown(panel, item.locationText || null);
      modal.ensureObjectAddressBinding(panel);
      // Binding only fires on user interaction — manually sync object from the selected address
      syncObjectFromAddress(panel);
    }

    const btn = document.getElementById("backlogSaveBtn");
    if (btn) btn.innerHTML = '<i class="bi bi-check-lg me-1" aria-hidden="true"></i>Atnaujinti';

    switchTab("create", false);
  }

  function cancelEdit() {
    if (!editingBacklogId) return;
    editingBacklogId = null;
    const btn = document.getElementById("backlogSaveBtn");
    if (btn) btn.innerHTML = '<i class="bi bi-inbox me-1" aria-hidden="true"></i>\u012etraukti \u012f eil\u0119';
  }

  function setupCardDrag(cardEl, item) {
    cardEl.addEventListener("dragstart", (e) => {
      if (_selectionMode) { e.preventDefault(); return; }
      draggingBacklogId = item.id;
      e.dataTransfer.effectAllowed = "copy";
      e.dataTransfer.setData("application/x-backlog-id", item.id);
      e.dataTransfer.setData("text/plain", item.id);
      cardEl.classList.add("is-dragging");
    });

    cardEl.addEventListener("dragend", () => {
      draggingBacklogId = null;
      cardEl.classList.remove("is-dragging");
      document.querySelectorAll(".schedule-slot.backlog-drop-over").forEach((el) => {
        el.classList.remove("backlog-drop-over");
      });
    });
  }

  function setupDropTargets() {
    const tables = document.getElementById("scheduleTables");
    if (!tables) return;

    tables.addEventListener("dragover", (e) => {
      if (!draggingBacklogId) return;
      const slot = e.target.closest(".schedule-slot");
      if (!slot) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = "copy";
      slot.classList.add("backlog-drop-over");
    });

    tables.addEventListener("dragleave", (e) => {
      if (!draggingBacklogId) return;
      const slot = e.target.closest(".schedule-slot");
      if (!slot || slot.contains(e.relatedTarget)) return;
      slot.classList.remove("backlog-drop-over");
    });

    tables.addEventListener("drop", (e) => {
      if (!draggingBacklogId) return;
      const slot = e.target.closest(".schedule-slot");
      if (!slot) return;
      e.preventDefault();
      slot.classList.remove("backlog-drop-over");

      const id = draggingBacklogId;
      draggingBacklogId = null;

      const item = getItemById(id);
      if (!item) return;

      const techId = slot.dataset.techId;
      const date = slot.dataset.date;
      const startMinutes = parseInt(slot.dataset.slotStartMinutes, 10);
      if (!techId || !date || isNaN(startMinutes)) return;

      scheduleFromBacklog(item, { techId, date, startMinutes, endMinutes: startMinutes + 60 });
    });
  }

  // If address is selected but object isn't, derive object from address's dataset.objectId.
  // ensureObjectAddressBinding only wires future user interactions — call this after initial population.
  function syncObjectFromAddress(panel) {
    const objEl = panel.querySelector("#taskDetailObject");
    const addrEl = panel.querySelector("#taskDetailLocation");
    if (objEl?.value || !addrEl?.value) return;
    const linkedId = addrEl.selectedOptions?.[0]?.dataset?.objectId;
    if (linkedId) window.schedule?.ui?.select2?.setValue?.(objEl, linkedId);
  }

  async function scheduleFromBacklog(item, { techId, date, startMinutes, endMinutes }) {
    const openPanel = window.schedule?.tasks?.openTaskPanel;
    if (typeof openPanel !== "function") {
      window.schedule?.ui?.toast?.showError?.("Nepavyko atidaryti planavimo lango.");
      clearSchedulingContext();
      return;
    }
    _schedulingFromBacklogId = item.id;
    _schedulingFromBacklogItemType = item.itemType;
    await openPanel({ technicianId: techId, targetDate: date, startMinutes, endMinutes });
    await applyPrefill(item);
  }

  // ── Bulk scheduling ───────────────────────────────────────────────────────

  async function handleBulkSchedule() {
    const ids = [..._selectedIds];
    if (ids.length === 0) return;

    _bulkQueue = ids.map(id => _items.find(i => String(i.id) === id)).filter(Boolean);
    if (_bulkQueue.length === 0) return;

    _bulkQueueIdx = 0;
    _isBulkMode   = true;

    const openPanel = window.schedule?.tasks?.openTaskPanel;
    if (typeof openPanel !== "function") {
      window.schedule?.ui?.toast?.showError?.("Nepavyko atidaryti planavimo lango.");
      return;
    }

    await openPanel({});

    const panel = getPanelEl();
    if (!panel) return;

    panel.dataset.bulkMode = "true";

    // Show plan mode toggle, default to rankinis
    panel.querySelector("#planModeField")?.classList.remove("d-none");
    const rankinisRadio = panel.querySelector("#taskPlanModeRankinis");
    if (rankinisRadio) rankinisRadio.checked = true;
    const hidden = panel.querySelector("#taskPlanMode");
    if (hidden) hidden.value = "rankinis";

    // Show scheduling fields and prefill times immediately
    panel.querySelector("#taskFieldAssignee")?.classList.remove("d-none");
    panel.querySelector("#taskFieldStart")?.classList.remove("d-none");
    panel.querySelector("#taskFieldEnd")?.classList.remove("d-none");
    const startEl = panel.querySelector("#taskDetailStart");
    if (startEl) startEl.value = _localNow ?? "";
    const endEl = panel.querySelector("#taskDetailEnd");
    if (endEl) endEl.value = localNowEnd();

    // Show progress indicator
    document.getElementById("taskBulkProgress")?.classList.remove("d-none");

    // Swap footer to bulk navigation
    document.getElementById("taskFooterClose")?.classList.add("d-none");
    document.getElementById("taskDetailSave")?.classList.add("d-none");
    document.getElementById("backlogSaveBtn")?.classList.add("d-none");
    document.getElementById("taskBulkBack")?.classList.remove("d-none");
    document.getElementById("taskBulkNext")?.classList.remove("d-none");

    await startBulkQueueItem(0);
    updateBulkNavButtons();
  }

  function onPlanModeChange(_value) {
    // Both modes use the same fields and prefill — switching only signals intent
    // (automatinis is a UI-only placeholder for now; no field changes needed)
  }

  async function startBulkQueueItem(idx) {
    const item = _bulkQueue[idx];
    if (!item) return;

    _schedulingFromBacklogId       = item.id;
    _schedulingFromBacklogItemType = item.itemType;

    const panel = getPanelEl();
    if (!panel) return;

    // Clear content fields before prefilling next item
    const set = (id, val) => { const el = panel.querySelector(id); if (el) el.value = val ?? ""; };
    set("#taskDetailTitle", "");
    set("#taskDetailPriority", "");
    set("#taskDetailSkill", "");
    set("#taskDetailInstructions", "");
    set("#taskDetailStart", _localNow ?? "");
    set("#taskDetailEnd", localNowEnd());
    // Clear assignee and placement dataset so travel preview can't fall back to previous tech
    panel.dataset.technicianId = "";
    panel.dataset.targetDate = "";
    panel.dataset.startMinutes = "";
    panel.dataset.endMinutes = "";
    if (window.jQuery) {
      const assigneeEl = panel.querySelector("#taskDetailAssignee");
      if (assigneeEl) window.jQuery(assigneeEl).val("").trigger("change");
    }

    const modal = window.schedule?.tasks?.modal;
    if (modal) {
      await Promise.all([
        modal.populateObjectDropdown(panel, null, null, null),
        modal.populateAddressDropdown(panel, null),
      ]);
      modal.ensureObjectAddressBinding(panel);
    }

    await applyPrefill(item);

    // Update progress indicator
    const stepEl  = document.getElementById("taskBulkProgressStep");
    const titleEl = document.getElementById("taskBulkProgressTitle");
    if (stepEl) stepEl.textContent = `${idx + 1} / ${_bulkQueue.length}`;
    if (titleEl) titleEl.textContent = item.title || "Be pavadinimo";
  }

  function finishBulkSchedule() {
    const total = _bulkQueue.length;
    const suffix = total === 1 ? "is suplanuota" : "ys suplanuotos";
    window.schedule?.ui?.toast?.showSuccess?.(`Visos ${total} užduot${suffix}.`);
    resetBulkState();
    exitSelectionMode();
    renderList();
  }

  function updateBulkNavButtons() {
    const backBtn = document.getElementById("taskBulkBack");
    const nextBtn = document.getElementById("taskBulkNext");
    if (!backBtn || !nextBtn) return;

    if (_bulkConfirmPhase) {
      backBtn.disabled = false;
      nextBtn.disabled = false;
      nextBtn.innerHTML = "Patvirtinti";
      return;
    }

    backBtn.disabled = _bulkQueueIdx === 0;
    nextBtn.disabled = false;
    nextBtn.innerHTML = 'Kitas <i class="bi bi-arrow-right ms-1" aria-hidden="true"></i>';
  }

  async function onBulkNext() {
    if (_bulkConfirmPhase) {
      await confirmAndCreateAll();
      return;
    }

    const panel = getPanelEl();
    const modal = window.schedule?.tasks?.modal;
    if (panel && modal) {
      _bulkScheduledData[_bulkQueueIdx] = modal.collectFormData(panel);
    }

    if (_bulkQueueIdx < _bulkQueue.length - 1) {
      _bulkQueueIdx++;
      await startBulkQueueItem(_bulkQueueIdx);
      restoreSchedulingSnapshot(_bulkQueueIdx);
      updateBulkNavButtons();
    } else {
      showBulkConfirmView();
    }
  }

  async function onBulkBack() {
    if (_bulkConfirmPhase) {
      hideBulkConfirmView();
      _bulkConfirmPhase = false;
      _bulkQueueIdx = _bulkQueue.length - 1;
      await startBulkQueueItem(_bulkQueueIdx);
      restoreSchedulingSnapshot(_bulkQueueIdx);
      updateBulkNavButtons();
      return;
    }
    if (_bulkQueueIdx > 0) {
      _bulkQueueIdx--;
      await startBulkQueueItem(_bulkQueueIdx);
      restoreSchedulingSnapshot(_bulkQueueIdx);
      updateBulkNavButtons();
    }
  }

  function restoreSchedulingSnapshot(idx) {
    const snap = _bulkScheduledData[idx];
    if (!snap) return;
    const panel = getPanelEl();
    if (!panel) return;

    if (snap.startAt) panel.querySelector("#taskDetailStart").value = snap.startAt;
    if (snap.endAt) panel.querySelector("#taskDetailEnd").value = snap.endAt;

    const techId = snap.technicianId;
    if (techId && window.jQuery) {
      const assigneeEl = panel.querySelector("#taskDetailAssignee");
      if (assigneeEl) window.jQuery(assigneeEl).val(String(techId)).trigger("change");
    }
  }

  function showBulkConfirmView() {
    _bulkConfirmPhase = true;

    // Hide form sections
    document.querySelectorAll("#taskPanelContent .task-section").forEach(s => s.classList.add("d-none"));
    document.getElementById("taskBulkProgress")?.classList.add("d-none");

    // Render and show confirm list
    renderBulkConfirmCards();
    document.getElementById("taskBulkConfirmView")?.classList.remove("d-none");

    updateBulkNavButtons();
  }

  function hideBulkConfirmView() {
    document.getElementById("taskBulkConfirmView")?.classList.add("d-none");
    document.getElementById("taskBulkConfirmList").innerHTML = "";
    document.querySelectorAll("#taskPanelContent .task-section").forEach(s => s.classList.remove("d-none"));
    document.getElementById("taskBulkProgress")?.classList.remove("d-none");
  }

  function renderBulkConfirmCards(errorsByIndex = {}) {
    const list = document.getElementById("taskBulkConfirmList");
    if (!list) return;
    list.innerHTML = "";

    const panel = getPanelEl();

    _bulkQueue.forEach((item, idx) => {
      const snap = _bulkScheduledData[idx];
      const priorityClass = PRIORITY_CLASSES[item.priority] ?? "bg-secondary";
      const priorityLabel = PRIORITY_LABELS[item.priority] ?? "";

      // Resolve assignee name from current dropdown options
      let assigneeName = "—";
      if (snap?.technicianId && panel) {
        const opt = panel.querySelector(`#taskDetailAssignee option[value="${snap.technicianId}"]`);
        if (opt) assigneeName = escapeHtml(opt.dataset.baseLabel || opt.textContent.trim());
      }

      const formatDt = (val) => {
        if (!val) return "—";
        const d = new Date(val);
        if (isNaN(d)) return val;
        const pad = n => String(n).padStart(2, "0");
        return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
      };

      const errorMsg = errorsByIndex[idx];
      const card = document.createElement("div");
      card.className = "backlog-card";
      if (errorMsg) card.style.borderLeftColor = "#dc3545";
      card.innerHTML = `
        <div class="backlog-card__row">
          <span class="backlog-card__title">${escapeHtml(item.title || "Be pavadinimo")}</span>
          <div class="backlog-card__badges">
            <span class="badge ${priorityClass}">${escapeHtml(priorityLabel)}</span>
          </div>
          <button type="button" class="backlog-card__toggle" data-confirm-toggle="${idx}" aria-expanded="false">
            <i class="bi bi-chevron-right"></i>
          </button>
        </div>
        ${errorMsg ? `<div class="text-danger small mt-1">${escapeHtml(errorMsg)}</div>` : ""}
        <div class="backlog-card__details" id="bulk-confirm-details-${idx}" hidden>
          <div class="backlog-card__detail-block">
            <span class="backlog-card__detail-label">Priskirtas</span>
            <div>${assigneeName}</div>
          </div>
          <div class="backlog-card__detail-block">
            <span class="backlog-card__detail-label">Pradžia</span>
            <div>${formatDt(snap?.startAt)}</div>
          </div>
          <div class="backlog-card__detail-block">
            <span class="backlog-card__detail-label">Pabaiga</span>
            <div>${formatDt(snap?.endAt)}</div>
          </div>
          ${item.objectName ? `<div class="backlog-card__detail-block">
            <span class="backlog-card__detail-label">Objektas</span>
            <div class="backlog-card__location"><i class="bi bi-geo-alt me-1"></i>${escapeHtml(item.objectName)}${item.locationText ? ` · ${escapeHtml(item.locationText)}` : ""}</div>
          </div>` : ""}
        </div>`;
      list.appendChild(card);
    });

    // Chevron toggle for confirm cards
    list.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-confirm-toggle]");
      if (!btn) return;
      const idx = btn.dataset.confirmToggle;
      const details = document.getElementById(`bulk-confirm-details-${idx}`);
      if (!details) return;
      const expanded = btn.getAttribute("aria-expanded") === "true";
      details.hidden = expanded;
      btn.setAttribute("aria-expanded", String(!expanded));
      const icon = btn.querySelector("i");
      if (icon) {
        icon.classList.toggle("bi-chevron-right", expanded);
        icon.classList.toggle("bi-chevron-down", !expanded);
      }
    });
  }

  function parseDatetimeComponents(val) {
    if (!val || !val.includes("T")) return null;
    const [datePart, timePart] = val.split("T");
    const [h, m] = timePart.split(":").map(Number);
    if (isNaN(h) || isNaN(m)) return null;
    return { date: datePart, minutes: h * 60 + m };
  }

  async function confirmAndCreateAll() {
    const nextBtn = document.getElementById("taskBulkNext");
    const backBtn = document.getElementById("taskBulkBack");
    if (nextBtn) nextBtn.disabled = true;
    if (backBtn) backBtn.disabled = true;

    const tzOffset = window.schedule?.grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();

    const items = _bulkQueue.map((item, idx) => {
      const snap = _bulkScheduledData[idx] || {};
      const start = parseDatetimeComponents(snap.startAt);
      const end   = parseDatetimeComponents(snap.endAt);
      return {
        technicianId:         snap.technicianId || 0,
        targetDate:           start?.date || "",
        startMinutes:         start?.minutes ?? 0,
        endMinutes:           end?.minutes ?? 0,
        title:                item.title || "",
        priority:             item.priority ?? 0,
        status:               0,
        skillLevel:           snap.skillLevel || item.skillLevel || "",
        objectName:           snap.objectName || item.objectName || "",
        objectAddress:        snap.objectAddress || item.locationText || "",
        instructions:         snap.instructions || item.instructions || "",
        timezoneOffsetMinutes: tzOffset,
        sourceBacklogItemId:  item.id ? parseInt(item.id, 10) : null,
      };
    });

    let data, ok;
    try {
      const resp = await fetch("/api/tasks/bulk", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ items }),
      });
      data = await resp.json().catch(() => ({}));
      ok = resp.ok && data.allSucceeded;
    } catch {
      data = {};
      ok = false;
    }

    if (!ok) {
      const errorsByIndex = {};
      (data.errors || []).forEach(e => { errorsByIndex[e.index] = e.message; });
      renderBulkConfirmCards(errorsByIndex);
      if (data.errors?.length) {
        window.schedule?.ui?.toast?.showError?.("Kai kurios užduotys turi klaidų.");
      } else {
        window.schedule?.ui?.toast?.showError?.(data.message || "Nepavyko sukurti užduočių.");
      }
      if (nextBtn) nextBtn.disabled = false;
      if (backBtn) backBtn.disabled = false;
      return;
    }

    // Delete temporary backlog items that were successfully scheduled
    for (const item of _bulkQueue) {
      if (item.itemType === ITEM_TYPE.Temporary) {
        try { await apiRemoveItem(item.id); } catch { /* best-effort */ }
      }
    }

    const scheduleState = window.schedule?.state;
    const scheduleTable = window.schedule?.table;
    let refreshOk = false;

    if (scheduleState?.getQuery && scheduleTable?.fetchAndRender) {
      const query = scheduleState.getQuery();
      if (!query.has("view")) query.set("view", "day");
      refreshOk = await scheduleTable.fetchAndRender(query, { updateUrl: false, preserveScroll: true });
    }

    updateBadge();
    finishBulkSchedule();

    const schedulePanel = window.schedule?.panel;
    const panelEl = getPanelEl();
    schedulePanel?.hide?.();
    window.bootstrap?.Offcanvas?.getInstance?.(panelEl)?.hide?.();

    if (!refreshOk) {
      window.schedule?.ui?.toast?.showWarning?.("Užduotys sukurtos, bet nepavyko atnaujinti tvarkaraščio.");
      document.dispatchEvent(new CustomEvent("schedule:table:updated"));
    }
  }

  // Opens the task creation panel pre-filled with content fields only (no slot).
  // User manually picks technician, date, and time before saving.
  async function scheduleFromBacklogUnscheduled(item) {
    const openPanel = window.schedule?.tasks?.openTaskPanel;
    if (typeof openPanel !== "function") {
      window.schedule?.ui?.toast?.showError?.("Nepavyko atidaryti planavimo lango.");
      return;
    }
    _schedulingFromBacklogId = item.id;
    _schedulingFromBacklogItemType = item.itemType;
    await openPanel({});
    await applyPrefill(item);
  }

  async function applyPrefill(item) {
    const panel = getPanelEl();
    if (!panel) return;
    const set = (id, val) => { const el = panel.querySelector(id); if (el && val != null) el.value = val; };
    set("#taskDetailTitle", item.title);
    set("#taskDetailPriority", item.priority);
    set("#taskDetailSkill", item.skillLevel);
    const instrEl = panel.querySelector("#taskDetailInstructions");
    if (instrEl && item.instructions) instrEl.value = item.instructions;

    // Populate object/address if the backlog item had them saved
    const modal = window.schedule?.tasks?.modal;
    if (modal && (item.objectId || item.objectName || item.locationText)) {
      await modal.populateObjectDropdown(panel, item.objectId || null, item.objectName || null, item.locationText || null);
      await modal.populateAddressDropdown(panel, item.locationText || null);
      modal.ensureObjectAddressBinding(panel);
      syncObjectFromAddress(panel);
      const locationEl = panel.querySelector("#taskDetailLocation");
      if (locationEl) locationEl.dispatchEvent(new Event("change", { bubbles: true }));
    }
  }

  // ── Open backlog panel ───────────────────────────────────────────────────

  function openBacklogPanel() {
    const schedulePanel = window.schedule?.panel;
    if (!schedulePanel) return;

    const detailPanel = getPanelEl();
    if (detailPanel?.classList?.contains("is-collapsed")) {
      detailPanel.classList.remove("is-collapsed");
      detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
    }

    schedulePanel.showContent("backlog");
    schedulePanel.show();
    updateBadge();
  }

  // ── Init ─────────────────────────────────────────────────────────────────

  function init() {
    const panel = getPanelEl();

    // Tab switching
    document.getElementById("backlogTabCreate")?.addEventListener("click", () => switchTab("create"));
    document.getElementById("backlogTabList")?.addEventListener("click", () => switchTab("list"));

    // Type sub-tab switching (Laikini / Nuolatiniai)
    document.getElementById("backlogTypeTabTemp")?.addEventListener("click", () => {
      _activeTypeTab = ITEM_TYPE.Temporary;
      document.getElementById("backlogTypeTabTemp")?.classList.add("active");
      document.getElementById("backlogTypeTabPerm")?.classList.remove("active");
      exitSelectionMode();
      renderList();
    });
    document.getElementById("backlogTypeTabPerm")?.addEventListener("click", () => {
      _activeTypeTab = ITEM_TYPE.Permanent;
      document.getElementById("backlogTypeTabPerm")?.classList.add("active");
      document.getElementById("backlogTypeTabTemp")?.classList.remove("active");
      exitSelectionMode();
      renderList();
    });

    // Save via footer button
    document.getElementById("backlogSaveBtn")?.addEventListener("click", handleSave);

    // Enter key in title field submits
    panel?.querySelector?.("#taskDetailTitle")?.addEventListener("keydown", (e) => {
      if (panel.dataset.mode === "backlog" && e.key === "Enter") {
        e.preventDefault();
        handleSave();
      }
    });

    // Schedule buttons — open task creation panel pre-filled with content, no slot (delegated)
    document.getElementById("backlogItemsList")?.addEventListener("click", async (e) => {
      const btn = e.target.closest("[data-schedule-id]");
      if (!btn) return;
      e.stopPropagation();
      const item = getItemById(btn.dataset.scheduleId);
      if (item) await scheduleFromBacklogUnscheduled(item);
    });

    // Edit buttons (delegated on list container)
    document.getElementById("backlogItemsList")?.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-edit-id]");
      if (!btn) return;
      e.stopPropagation();
      startEdit(btn.dataset.editId);
    });

    // Delete buttons (delegated on list container)
    document.getElementById("backlogItemsList")?.addEventListener("click", async (e) => {
      const btn = e.target.closest("[data-delete-id]");
      if (!btn) return;
      try {
        const message = await apiRemoveItem(btn.dataset.deleteId);
        window.schedule?.ui?.toast?.showSuccess?.(message);
      } catch {
        window.schedule?.ui?.toast?.showError?.("Nepavyko pašalinti.");
      }
      updateBadge();
      renderList();
    });

    // Card click selection in bulk mode (delegated on list container)
    document.getElementById("backlogItemsList")?.addEventListener("click", (e) => {
      if (!_selectionMode) return;
      if (e.target.closest("button")) return;
      const card = e.target.closest(".backlog-card");
      if (!card) return;
      const id = card.dataset.backlogId;
      if (_selectedIds.has(id)) {
        _selectedIds.delete(id);
        card.classList.remove("is-selected");
        const cb = card.querySelector(".backlog-card__checkbox");
        if (cb) cb.checked = false;
      } else {
        _selectedIds.add(id);
        card.classList.add("is-selected");
        const cb = card.querySelector(".backlog-card__checkbox");
        if (cb) cb.checked = true;
      }
      updateBulkBar();
    });

    // Enter selection mode
    document.getElementById("backlogSelectToggle")?.addEventListener("click", () => {
      enterSelectionMode();
      renderList();
    });

    // Exit selection mode
    document.getElementById("backlogSelectCancel")?.addEventListener("click", () => {
      exitSelectionMode();
      renderList();
    });

    // Select all checkbox
    document.getElementById("backlogSelectAll")?.addEventListener("change", (e) => {
      const items = getItems().filter(i => i.itemType === _activeTypeTab);
      if (e.target.checked) { items.forEach(i => _selectedIds.add(String(i.id))); }
      else { _selectedIds.clear(); }
      renderList();
      updateBulkBar();
    });

    // Bulk delete
    document.getElementById("backlogBulkDelete")?.addEventListener("click", async () => {
      if (_selectedIds.size === 0) return;
      const ids = [..._selectedIds].map(Number);
      try {
        const resp = await fetch("/api/backlog/bulk-delete", {
          method: "POST",
          credentials: "same-origin",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(ids),
        });
        if (!resp.ok) throw new Error();
        const { deleted } = await resp.json();
        _items = _items.filter(i => !ids.includes(Number(i.id)));
        window.schedule?.ui?.toast?.showSuccess?.(`${deleted} įrašas(-ai) pašalinta.`);
      } catch {
        window.schedule?.ui?.toast?.showError?.("Nepavyko pašalinti.");
      }
      exitSelectionMode();
      updateBadge();
      renderList();
    });

    // Chevron expand/collapse (delegated on list container)
    document.getElementById("backlogItemsList")?.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-toggle-id]");
      if (!btn) return;
      e.stopPropagation();
      const details = document.getElementById("backlog-details-" + btn.dataset.toggleId);
      if (!details) return;
      const expanded = btn.getAttribute("aria-expanded") === "true";
      details.hidden = expanded;
      btn.setAttribute("aria-expanded", String(!expanded));
      btn.closest(".backlog-card")?.classList.toggle("is-expanded", !expanded);
      const icon = btn.querySelector("i");
      if (icon) {
        icon.classList.toggle("bi-chevron-right", expanded);
        icon.classList.toggle("bi-chevron-down", !expanded);
      }
    });

    // React to panel mode changes dispatched by schedule.panel.js
    if (panel) {
      panel.addEventListener("schedule:panel:mode", (e) => {
        const type = e.detail?.type;
        if (type === "backlog") {
          panel.dataset.mode = "backlog";
          applyBacklogMode();
          loadItemsFromApi().then(() => { renderList(); updateBadge(); });
        } else {
          // Do NOT touch dataset.mode here — task.modal.js owns create/update mode
          restoreFromBacklogMode();
        }
      });

    }

    // Drag-to-schedule drop targets
    setupDropTargets();

    // Auto-delete Temporary backlog item after confirmed task creation
    document.addEventListener("schedule:task:created", async () => {
      const id = _schedulingFromBacklogId;
      const type = _schedulingFromBacklogItemType;
      clearSchedulingContext();
      if (id && type === ITEM_TYPE.Temporary) {
        try {
          await apiRemoveItem(id);
        } catch { /* silent — task was created, backlog delete is best-effort */ }
        updateBadge();
        renderList();
      }
    });

    // Abandonment: panel closed without creating a task
    document.getElementById("taskDetailModal")
      ?.addEventListener("hide.bs.offcanvas", () => {
        clearSchedulingContext();
        if (_isBulkMode) resetBulkState();
      });

    // Bulk schedule button
    document.getElementById("backlogBulkSchedule")
      ?.addEventListener("click", () => handleBulkSchedule());

    // Bulk nav buttons
    document.getElementById("taskBulkNext")
      ?.addEventListener("click", () => onBulkNext());
    document.getElementById("taskBulkBack")
      ?.addEventListener("click", () => onBulkBack());

    // Plan mode radio toggle (delegated on panel element)
    getPanelEl()?.addEventListener("change", (e) => {
      if (e.target.name === "taskPlanModeRadio") {
        const hidden = getPanelEl()?.querySelector("#taskPlanMode");
        if (hidden) hidden.value = e.target.value;
        onPlanModeChange(e.target.value);
      }
    });

    // Hide trigger button when switching to weekly view
    document.addEventListener("schedule:view:changed", (e) => {
      setTriggerVisible((e?.detail?.view || "").toLowerCase() === "day");
    });
    document.addEventListener("schedule:table:updated", () => {
      setTriggerVisible(isDailyView());
    });

    // Initial state: hide button if starting on weekly view
    setTriggerVisible(isDailyView());
    updateBadge();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }

  window.schedule = window.schedule || {};
  window.schedule.backlog = {
    openBacklogPanel,
    getSchedulingBacklogId: () => _schedulingFromBacklogId,
  };
})();
