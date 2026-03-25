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
  let _activeTypeTab = ITEM_TYPE.Permanent;

  function getItems() { return _items; }

  async function loadItemsFromApi() {
    try {
      const resp = await fetch("/api/backlog", { credentials: "same-origin" });
      if (!resp.ok) return;
      const data = await resp.json();
      _items = data.map(normalizeServerItem);
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

    // Show backlog-only type field
    panel.querySelector("#backlogTypeField")?.classList.remove("d-none");

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

    // Hide backlog-only type field and reset it
    const typeField = panel.querySelector("#backlogTypeField");
    if (typeField) typeField.classList.add("d-none");
    const typeSelect = panel.querySelector("#backlogItemType");
    if (typeSelect) typeSelect.value = "";

    // Restore save buttons
    panel.querySelector("#taskDetailSave")?.classList.remove("d-none");
    panel.querySelector("#backlogSaveBtn")?.classList.add("d-none");

    // Hide the tab bar
    panel.querySelector("#backlogTabBar")?.classList.add("d-none");

    // Ensure list container is hidden
    const listContent = document.getElementById("backlogPanelContent");
    if (listContent) listContent.style.display = "none";
  }

  // ── Badge ────────────────────────────────────────────────────────────────

  function updateBadge() {
    const count = getItems().filter((i) => i.itemType === ITEM_TYPE.Temporary).length;
    const badgeDesktop = document.getElementById("navBacklogBadge");
    const badgeMobile = document.getElementById("navBacklogBadgeMobile");
    const panelBadge = document.getElementById("backlogCountBadge");

    [badgeDesktop, badgeMobile].forEach((el) => {
      if (!el) return;
      el.textContent = count;
      el.classList.toggle("d-none", count === 0);
    });

    if (panelBadge) panelBadge.textContent = count;
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

  const PRIORITY_LABELS = { "0": "Žemas", "1": "Vidutinis", "2": "Aukštas" };
  const PRIORITY_CLASSES = { "0": "bg-secondary", "1": "bg-warning text-dark", "2": "bg-danger" };
  const SKILL_LABELS = { Basic: "Bazinis", Advanced: "Pažengęs", Certified: "Sertifikuotas" };

  function escapeHtml(str) {
    if (!str) return "";
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
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
      card.setAttribute("draggable", "true");
      card.dataset.backlogId = item.id;

      const priorityBadge = item.priority !== ""
        ? `<span class="badge ${PRIORITY_CLASSES[item.priority] || "bg-secondary"}">${escapeHtml(PRIORITY_LABELS[item.priority] || item.priority)}</span>`
        : "";
      const skillBadge = item.skillLevel
        ? `<span class="badge bg-light text-dark border">${escapeHtml(SKILL_LABELS[item.skillLevel] || item.skillLevel)}</span>`
        : "";

      const hasDetails = !!(item.instructions || item.objectName);
      const detailsHtml = hasDetails ? `
        <div class="backlog-card__details" id="backlog-details-${escapeHtml(item.id)}" hidden>
          ${item.objectName ? `<div class="backlog-card__location"><i class="bi bi-geo-alt me-1" aria-hidden="true"></i>${escapeHtml(item.objectName)}${item.locationText ? ` · ${escapeHtml(item.locationText)}` : ""}</div>` : ""}
          ${item.instructions ? `<div class="backlog-card__instructions">${escapeHtml(item.instructions)}</div>` : ""}
        </div>` : "";

      card.innerHTML = `
        <div class="backlog-card__row">
          <div class="backlog-card__title">${escapeHtml(item.title) || "<em class='text-muted'>Be pavadinimo</em>"}</div>
          <div class="backlog-card__badges">${priorityBadge}${skillBadge}</div>
          <div class="backlog-card__date">${escapeHtml(new Date(item.createdAt).toLocaleDateString("lt-LT"))}</div>
          ${hasDetails ? `<button type="button" class="backlog-card__toggle" data-toggle-id="${escapeHtml(item.id)}" aria-label="Rodyti detales" aria-expanded="false"><i class="bi bi-chevron-right" aria-hidden="true"></i></button>` : ""}
          <button type="button" class="backlog-card__edit" data-edit-id="${escapeHtml(item.id)}" aria-label="Redaguoti">
            <i class="bi bi-pencil" aria-hidden="true"></i>
          </button>
          <button type="button" class="backlog-card__delete" data-delete-id="${escapeHtml(item.id)}" aria-label="Pašalinti iš eilės">
            <i class="bi bi-x-lg" aria-hidden="true"></i>
          </button>
        </div>
        ${detailsHtml}
      `;

      setupCardDrag(card, item);

      // Prevent drag from firing when interacting with action buttons
      card.querySelector(".backlog-card__toggle")?.addEventListener("mousedown", (e) => e.stopPropagation());
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

    const dto = {
      title,
      priority: priorityRaw !== "" ? Number(priorityRaw) : null,
      itemType: Number(itemTypeRaw),
      skillLevel: panel.querySelector("#taskDetailSkill")?.value ?? "",
      instructions: panel.querySelector("#taskDetailInstructions")?.value?.trim() ?? "",
      objectId: objectId && objectId !== "" ? objectId : "",
      objectName: objectText && objectText !== "– Pasirinkite –" ? objectText : "",
      objectAddress: addressValue && addressValue !== "" ? addressValue : "",
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
      if (result.validationErrors && modal?.displayValidationErrors) {
        modal.displayValidationErrors(panel, result.validationErrors, FIELD_MAP);
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

  function clearSchedulingContext() {
    _schedulingFromBacklogId = null;
    _schedulingFromBacklogItemType = null;
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
      renderList();
    });
    document.getElementById("backlogTypeTabPerm")?.addEventListener("click", () => {
      _activeTypeTab = ITEM_TYPE.Permanent;
      document.getElementById("backlogTypeTabPerm")?.classList.add("active");
      document.getElementById("backlogTypeTabTemp")?.classList.remove("active");
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
      ?.addEventListener("hide.bs.offcanvas", clearSchedulingContext);

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
  window.schedule.backlog = { openBacklogPanel };
})();
