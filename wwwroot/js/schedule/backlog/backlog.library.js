(() => {
  "use strict";

  window.schedule = window.schedule || {};
  const backlog = window.schedule.backlog = window.schedule.backlog || {};
  const state = backlog.state;

  backlog.api = backlog.api || {};

  backlog.api.loadItemsFromApi = async (itemType, page) => {
    try {
      const resp = await fetch(`/api/backlog?itemType=${itemType}&page=${page}${state.priorityParam()}`, { credentials: "same-origin" });
      if (!resp.ok) return null;
      return await resp.json();
    } catch {
      return null;
    }
  };

  backlog.api.normalizeServerItem = (raw) => ({
    id: String(raw.id),
    title: raw.title || "",
    priority: raw.priority ?? "",
    itemType: raw.itemType ?? state.ITEM_TYPE.Temporary,
    skillLevel: raw.skillLevel ?? "",
    instructions: raw.instructions ?? "",
    objectName: raw.objectName ?? "",
    locationText: raw.objectAddress ?? "",
    objectId: raw.objectId ?? null,
    dueDate: raw.dueDate ? raw.dueDate.substring(0, 10) : null,
    createdAt: raw.createdAt || new Date().toISOString(),
  });

  backlog.api.parseApiResponse = async (resp) => {
    if (resp.ok) return { ok: true, data: await resp.json() };
    let body = {};
    try { body = await resp.json(); } catch { }
    if (resp.status === 422 || resp.status === 400) {
      return { ok: false, validationErrors: body.errors ?? null, error: body.title ?? body.message ?? null };
    }
    return { ok: false, error: body.message ?? null };
  };

  backlog.api.apiAddItem = async (dto) => {
    const resp = await fetch("/api/backlog", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(dto),
    });
    const result = await backlog.api.parseApiResponse(resp);
    if (!result.ok) return result;
    const mapped = backlog.api.normalizeServerItem(result.data.item);
    state._items.unshift(mapped);
    return { ok: true, item: mapped, message: result.data.message };
  };

  backlog.api.apiUpdateItem = async (id, dto) => {
    const resp = await fetch(`/api/backlog/${id}`, {
      method: "PUT",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(dto),
    });
    const result = await backlog.api.parseApiResponse(resp);
    if (!result.ok) return result;
    const mapped = backlog.api.normalizeServerItem(result.data.item);
    const idx = state._items.findIndex((i) => i.id === String(id));
    if (idx !== -1) state._items[idx] = mapped;
    return { ok: true, item: mapped, message: result.data.message };
  };

  backlog.api.apiRemoveItem = async (id) => {
    const resp = await fetch(`/api/backlog/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!resp.ok) throw new Error("backlog.delete.failed");
    const { message } = await resp.json();
    state._items = state._items.filter((i) => i.id !== String(id));
    return message;
  };

  backlog.api.getItemById = (id) => state._items.find((i) => i.id === String(id)) ?? null;

  backlog.list = backlog.list || {};

  backlog.list.updateBadge = () => {
    const tempCount = state._totalCounts[state.ITEM_TYPE.Temporary];
    const permCount = state._totalCounts[state.ITEM_TYPE.Permanent];

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
  };

  backlog.list.exitSelectionMode = () => {
    if (!state._selectionMode) return;
    state._selectionMode = false;
    state.clearSelection();
    document.getElementById("backlogPanelContent")?.classList.remove("is-selecting");
    if (state._hasMore) backlog.list.setupScroll();
  };

  backlog.list.enterSelectionMode = () => {
    state._selectionMode = true;
    state.clearSelection();
    if (state._isBulkMode && state._bulkQueue.length) {
      state.replaceSelectionOrder(
        state._bulkQueue
          .map((item) => String(item?.id ?? ""))
          .filter(Boolean)
      );
    }
    state._scroll?.disconnect();
    document.getElementById("backlogPanelContent")?.classList.add("is-selecting");
    backlog.list.updateBulkBar();
  };

  backlog.list.updateBulkBar = () => {
    const countEl = document.getElementById("backlogBulkCount");
    const deleteBtn = document.getElementById("backlogBulkDelete");
    const scheduleBtn = document.getElementById("backlogBulkSchedule");
    const selectAllCb = document.getElementById("backlogSelectAll");
    const n = state._selectedIds.size;
    const total = state._items.length;
    if (countEl) countEl.textContent = n === 0 ? "Nepasirinkta" : `${n} / ${total} pasirinkta`;
    if (deleteBtn) deleteBtn.disabled = n === 0;
    if (scheduleBtn) scheduleBtn.disabled = n === 0;
    if (selectAllCb) {
      if (n === 0) { selectAllCb.checked = false; selectAllCb.indeterminate = false; }
      else if (n >= total) { selectAllCb.checked = true; selectAllCb.indeterminate = false; }
      else { selectAllCb.checked = false; selectAllCb.indeterminate = true; }
    }
  };

  backlog.list.toggleSelectedItem = (id, { selected } = {}) => {
    const normalizedId = String(id ?? "");
    if (!normalizedId) return;
    const shouldSelect = typeof selected === "boolean" ? selected : !state._selectedIds.has(normalizedId);
    if (shouldSelect) state.selectBacklogItem(normalizedId);
    else state.unselectBacklogItem(normalizedId);
  };

  backlog.list.handleBulkDelete = async () => {
    if (state._selectedIds.size === 0) return;
    const ids = state.getSelectedIdsInOrder();
    for (const id of ids) {
      try {
        await backlog.api.apiRemoveItem(id);
      } catch {
        window.schedule?.ui?.toast?.showError?.("Nepavyko istrinti dalies sablonu.");
        break;
      }
    }
    backlog.list.exitSelectionMode();
    backlog.list.renderList();
    backlog.list.updateBadge();
  };

  backlog.list.buildBacklogCard = (item) => {
    const tpl = document.getElementById("backlogCardTpl");
    const card = tpl.content.cloneNode(true).firstElementChild;
    card.dataset.backlogId = item.id;
    card.setAttribute("draggable", state._selectionMode ? "false" : "true");
    if (state._selectionMode && state._selectedIds.has(String(item.id))) card.classList.add("is-selected");
    const checkbox = card.querySelector(".backlog-card__checkbox");
    if (checkbox) checkbox.checked = state._selectionMode && state._selectedIds.has(String(item.id));

    const titleEl = card.querySelector(".backlog-card__title");
    if (titleEl) {
      if (item.title) titleEl.textContent = item.title;
      else {
        const em = document.createElement("em");
        em.className = "text-muted";
        em.textContent = "Be pavadinimo";
        titleEl.appendChild(em);
      }
    }

    const priEl = card.querySelector(".backlog-card__badge-priority");
    if (priEl) {
      if (item.priority !== "") {
        priEl.className = `badge ${state.PRIORITY_CLASSES[item.priority] || "bg-secondary"}`;
        priEl.textContent = state.PRIORITY_LABELS[item.priority] || String(item.priority);
      } else priEl.remove();
    }

    const dueEl = card.querySelector(".backlog-card__badge-duedate");
    if (dueEl) {
      const dueDom = state.buildDueDateBadge(item.dueDate);
      if (dueDom) dueEl.replaceWith(dueDom); else dueEl.remove();
    }

    const createdLabel = new Date(item.createdAt).toLocaleDateString("lt-LT");
    const dateEl = card.querySelector(".backlog-card__date");
    if (dateEl) dateEl.textContent = createdLabel;

    const hasDetails = !!(item.instructions || item.objectName || item.skillLevel);
    const detailsEl = card.querySelector(".backlog-card__details");
    const toggleBtn = card.querySelector(".backlog-card__toggle");
    const footerEl = card.querySelector(".backlog-card__footer");
    if (hasDetails) {
      if (detailsEl) detailsEl.id = `backlog-details-${item.id}`;
      if (item.skillLevel && detailsEl) {
        const b = detailsEl.querySelector(".backlog-card__detail--skill");
        if (b) {
          b.removeAttribute("hidden");
          const v = b.querySelector(".backlog-card__skill-value");
          if (v) v.textContent = state.SKILL_LABELS[item.skillLevel] || item.skillLevel;
        }
      }
      if (item.objectName && detailsEl) {
        const b = detailsEl.querySelector(".backlog-card__detail--object");
        if (b) {
          b.removeAttribute("hidden");
          const v = b.querySelector(".backlog-card__object-value");
          if (v) v.textContent = item.locationText ? `${item.objectName} · ${item.locationText}` : item.objectName;
        }
      }
      if (item.instructions && detailsEl) {
        const b = detailsEl.querySelector(".backlog-card__detail--instructions");
        if (b) {
          b.removeAttribute("hidden");
          const v = b.querySelector(".backlog-card__instructions-value");
          if (v) v.textContent = item.instructions;
        }
      }
      if (toggleBtn) toggleBtn.setAttribute("data-toggle-id", item.id);
      if (footerEl) footerEl.textContent = createdLabel;
    } else {
      detailsEl?.remove();
      toggleBtn?.remove();
      footerEl?.remove();
    }
    card.querySelector(".backlog-card__schedule")?.setAttribute("data-schedule-id", item.id);
    card.querySelector(".backlog-card__edit")?.setAttribute("data-edit-id", item.id);
    card.querySelector(".backlog-card__delete")?.setAttribute("data-delete-id", item.id);
    backlog.scheduleBridge.setupCardDrag(card, item);
    card.querySelector(".backlog-card__toggle")?.addEventListener("mousedown", (e) => e.stopPropagation());
    card.querySelector(".backlog-card__schedule")?.addEventListener("mousedown", (e) => e.stopPropagation());
    card.querySelector(".backlog-card__edit")?.addEventListener("mousedown", (e) => e.stopPropagation());
    card.querySelector(".backlog-card__delete")?.addEventListener("mousedown", (e) => e.stopPropagation());
    return card;
  };

  backlog.list.renderList = () => {
    const listEl = document.getElementById("backlogItemsList");
    const emptyEl = document.getElementById("backlogListEmpty");
    if (!listEl) return;
    listEl.innerHTML = "";
    if (state._items.length === 0) {
      if (emptyEl) emptyEl.classList.remove("d-none");
      return;
    }
    if (emptyEl) emptyEl.classList.add("d-none");
    state._items.forEach((item) => listEl.appendChild(backlog.list.buildBacklogCard(item)));
  };

  backlog.list.appendCards = (newItems) => {
    const listEl = document.getElementById("backlogItemsList");
    const emptyEl = document.getElementById("backlogListEmpty");
    if (!listEl || newItems.length === 0) return;
    if (emptyEl) emptyEl.classList.add("d-none");
    newItems.forEach((item) => listEl.appendChild(backlog.list.buildBacklogCard(item)));
  };

  backlog.list.setupScroll = () => {
    const sentinelEl = document.getElementById("backlogScrollSentinel");
    if (!sentinelEl || !window.schedule?.infra?.pagination) return;
    state._scroll?.disconnect();
    state._scroll = window.schedule.infra.pagination.createInfiniteScroll({
      sentinelEl,
      onLoadMore: async () => {
        if (!state._hasMore) return false;
        state._page++;
        const data = await backlog.api.loadItemsFromApi(state._activeTypeTab, state._page);
        if (!data) return false;
        const newItems = (data.items ?? []).map(backlog.api.normalizeServerItem);
        state._items = state._items.concat(newItems);
        state._hasMore = data.hasMore ?? false;
        backlog.list.appendCards(newItems);
        return state._hasMore;
      },
    });
    state._scroll.observe();
  };

  backlog.list.resetPriorityFilter = () => {
    state._priority = null;
    document.querySelectorAll("#backlogPriorityFilter [data-backlog-priority]").forEach((b) => {
      b.classList.toggle("active", b.dataset.backlogPriority === "");
    });
  };

  backlog.list.syncTypeTabButtons = () => {
    const isTemporary = state._activeTypeTab === state.ITEM_TYPE.Temporary;
    document.getElementById("backlogTypeTabTemp")?.classList.toggle("active", isTemporary);
    document.getElementById("backlogTypeTabPerm")?.classList.toggle("active", !isTemporary);
  };

  backlog.list.loadTypeTab = async (itemType) => {
    state._scroll?.disconnect();
    state._page = 1;
    state._hasMore = false;
    state._items = [];
    state._activeTypeTab = itemType;
    backlog.list.syncTypeTabButtons();
    const data = await backlog.api.loadItemsFromApi(itemType, 1);
    if (data) {
      state._items = (data.items ?? []).map(backlog.api.normalizeServerItem);
      state._hasMore = data.hasMore ?? false;
      state._localNow = data.localNow ?? state._localNow;
      state._totalCounts[itemType] = data.totalCount ?? 0;
    }
    backlog.list.renderList();
    backlog.list.updateBadge();
    backlog.list.setupScroll();
  };

  backlog.list.openBacklogList = async () => {
    state._scroll?.disconnect();
    state._page = 1;
    state._hasMore = false;
    state._items = [];
    backlog.list.syncTypeTabButtons();
    const [tempData, permData] = await Promise.all([
      backlog.api.loadItemsFromApi(state.ITEM_TYPE.Temporary, 1),
      backlog.api.loadItemsFromApi(state.ITEM_TYPE.Permanent, 1),
    ]);
    state._totalCounts[state.ITEM_TYPE.Temporary] = tempData?.totalCount ?? 0;
    state._totalCounts[state.ITEM_TYPE.Permanent] = permData?.totalCount ?? 0;
    const data = state._activeTypeTab === state.ITEM_TYPE.Temporary ? tempData : permData;
    if (data) {
      state._items = (data.items ?? []).map(backlog.api.normalizeServerItem);
      state._hasMore = data.hasMore ?? false;
      state._localNow = data.localNow ?? state._localNow;
    }
    backlog.list.renderList();
    backlog.list.updateBadge();
    backlog.list.setupScroll();
  };

  backlog.form = backlog.form || {};

  backlog.form.handleSave = async () => {
    const panel = state.getPanelEl();
    if (!panel) return;
    const objectEl = panel.querySelector("#taskDetailObject");
    const locationEl = panel.querySelector("#taskDetailLocation");
    const objectId = objectEl?.value ?? "";
    const objectText = objectEl?.options?.[objectEl.selectedIndex]?.text ?? "";
    const addressValue = locationEl?.value ?? "";
    const modal = window.schedule?.tasks?.modal;
    const formData = modal?.collectFormData?.(panel);
    const itemTypeRaw = panel.querySelector("#backlogItemType")?.value ?? "";
    let allValid = formData ? (modal?.validateBacklogFormData?.(panel, formData) ?? true) : true;
    if (itemTypeRaw === "") {
      modal?.setFieldError?.(panel, "#backlogItemType", "Sablono tipas yra privalomas.");
      allValid = false;
    }
    if (!allValid) return;
    const dto = {
      title: panel.querySelector("#taskDetailTitle")?.value?.trim() ?? "",
      priority: panel.querySelector("#taskDetailPriority")?.value !== "" ? Number(panel.querySelector("#taskDetailPriority")?.value) : null,
      itemType: Number(itemTypeRaw),
      skillLevel: panel.querySelector("#taskDetailSkill")?.value ?? "",
      instructions: panel.querySelector("#taskDetailInstructions")?.value?.trim() ?? "",
      objectId: objectId && objectId !== "" ? objectId : "",
      objectName: objectText && objectText !== "– Pasirinkite –" ? objectText : "",
      objectAddress: addressValue && addressValue !== "" ? addressValue : "",
      dueDate: (panel.querySelector("#backlogDueDate")?.value ?? "") || null,
    };
    let result;
    if (state.editingBacklogId) result = await backlog.api.apiUpdateItem(state.editingBacklogId, dto);
    else result = await backlog.api.apiAddItem(dto);
    if (!result.ok) {
      if (result.validationErrors) {
        modal?.displayValidationErrors?.(panel, result.validationErrors, {
          Title: "#taskDetailTitle",
          SkillLevel: "#taskDetailSkill",
          ObjectName: "#taskDetailObject",
          ObjectAddress: "#taskDetailLocation",
          DueDate: "#backlogDueDate",
          ItemType: "#backlogItemType",
        });
      } else {
        window.schedule?.ui?.toast?.showError?.(result.error || "Nepavyko issaugoti sablono.");
      }
      return;
    }
    backlog.form.cancelEdit();
    window.schedule?.ui?.toast?.showSuccess?.(result.message || "Issaugota.");
    backlog.list.updateBadge();
    backlog.scheduleBridge.switchBacklogTab("list");
  };

  backlog.form.startEdit = async (id) => {
    const item = backlog.api.getItemById(id);
    if (!item) return;
    state.editingBacklogId = id;
    const panel = state.getPanelEl();
    state._isBulkMode = false;
    state._bulkConfirmPhase = false;
    window.schedule?.panel?.setBacklogTab?.("create");
    window.schedule?.panel?.activateVariant?.("backlog-create");
    const set = (sel, val) => { const el = panel?.querySelector(sel); if (el) el.value = val ?? ""; };
    set("#taskDetailTitle", item.title);
    set("#taskDetailPriority", item.priority);
    set("#backlogItemType", item.itemType);
    set("#taskDetailSkill", item.skillLevel);
    set("#taskDetailInstructions", item.instructions);
    set("#backlogDueDate", item.dueDate ?? "");
    const modal = window.schedule?.tasks?.modal;
    if (modal) {
      await modal.populateObjectDropdown(panel, item.objectId || null, item.objectName || null, item.locationText || null);
      await modal.populateAddressDropdown(panel, item.locationText || null);
      modal.ensureObjectAddressBinding(panel);
      backlog.form.syncObjectFromAddress(panel);
    }
    const btn = document.getElementById("backlogSaveBtn");
    if (btn) btn.innerHTML = '<i class="bi bi-check-lg me-1" aria-hidden="true"></i>Atnaujinti';
    backlog.scheduleBridge.switchBacklogTab("create", false);
  };

  backlog.form.cancelEdit = () => {
    if (!state.editingBacklogId) return;
    state.editingBacklogId = null;
    const btn = document.getElementById("backlogSaveBtn");
    if (btn) btn.innerHTML = '<i class="bi bi-inbox me-1" aria-hidden="true"></i>Itraukti i eile';
  };

  backlog.form.syncObjectFromAddress = (panel) => {
    const objEl = panel.querySelector("#taskDetailObject");
    const addrEl = panel.querySelector("#taskDetailLocation");
    if (objEl?.value || !addrEl?.value) return;
    const linkedId = addrEl.selectedOptions?.[0]?.dataset?.objectId;
    if (linkedId) window.schedule?.ui?.select2?.setValue?.(objEl, linkedId);
  };

  backlog.form.scheduleFromBacklogUnscheduled = async (item) => {
    const openPanel = window.schedule?.tasks?.openTaskPanel;
    if (typeof openPanel !== "function") {
      window.schedule?.ui?.toast?.showError?.("Nepavyko atidaryti planavimo lango.");
      return;
    }
    const placement = state.getEarliestCreatableRangeForScheduleDate?.() || {};
    state._schedulingFromBacklogId = item.id;
    state._schedulingFromBacklogItemType = item.itemType;
    await openPanel({
      targetDate: placement.targetDate,
      startMinutes: placement.startMinutes,
      endMinutes: placement.endMinutes,
      allowEmptySchedule: false,
    });
    await backlog.form.applyPrefill(item);
  };

  backlog.form.applyPrefill = async (item) => {
    const panel = state.getPanelEl();
    if (!panel) return;
    const set = (id, val) => { const el = panel.querySelector(id); if (el && val != null) el.value = val; };
    set("#taskDetailTitle", item.title);
    set("#taskDetailPriority", item.priority);
    set("#taskDetailSkill", item.skillLevel);
    const instrEl = panel.querySelector("#taskDetailInstructions");
    if (instrEl && item.instructions) instrEl.value = item.instructions;
    const modal = window.schedule?.tasks?.modal;
    if (modal && (item.objectId || item.objectName || item.locationText)) {
      await modal.populateObjectDropdown(panel, item.objectId || null, item.objectName || null, item.locationText || null);
      await modal.populateAddressDropdown(panel, item.locationText || null);
      modal.ensureObjectAddressBinding(panel);
      backlog.form.syncObjectFromAddress(panel);
      const locationEl = panel.querySelector("#taskDetailLocation");
      if (locationEl) locationEl.dispatchEvent(new Event("change", { bubbles: true }));
    }
  };

  backlog.form.deleteItem = async (id) => {
    try {
      await backlog.api.apiRemoveItem(id);
      backlog.list.renderList();
      backlog.list.updateBadge();
    } catch {
      window.schedule?.ui?.toast?.showError?.("Nepavyko istrinti sablono.");
    }
  };
})();
