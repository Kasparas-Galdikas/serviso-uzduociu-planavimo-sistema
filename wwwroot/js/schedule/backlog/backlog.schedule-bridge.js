(() => {
  "use strict";

  window.schedule = window.schedule || {};
  const backlog = window.schedule.backlog = window.schedule.backlog || {};
  const state = backlog.state;

  backlog.scheduleBridge = backlog.scheduleBridge || {};
  backlog.interop = backlog.scheduleBridge;

  backlog.scheduleBridge.applyBacklogMode = () => {
    state.clearSchedulingContext();
    const panel = state.getPanelEl();
    if (!panel) return;
    state._activeTypeTab = state.ITEM_TYPE.Permanent;
    document.getElementById("backlogTypeTabPerm")?.classList.add("active");
    document.getElementById("backlogTypeTabTemp")?.classList.remove("active");
    backlog.form.cancelEdit();
    const set = (id, val) => { const el = panel.querySelector(id); if (el) el.value = val; };
    set("#taskDetailTitle", "");
    set("#taskDetailPriority", "");
    set("#backlogItemType", "");
    set("#taskDetailSkill", "");
    set("#taskDetailInstructions", "");
    set("#backlogDueDate", "");
    const modal = window.schedule?.tasks?.modal;
    if (modal) Promise.all([modal.populateObjectDropdown(panel, null, null, null), modal.populateAddressDropdown(panel, null)]).then(() => modal.ensureObjectAddressBinding(panel));
    window.schedule?.panel?.setBacklogTab?.("list");
    document.getElementById("backlogTabCreate")?.classList.remove("active", "btn-primary");
    document.getElementById("backlogTabCreate")?.classList.add("btn-outline-secondary");
    document.getElementById("backlogTabList")?.classList.add("active", "btn-primary");
    document.getElementById("backlogTabList")?.classList.remove("btn-outline-secondary");
  };

  backlog.scheduleBridge.restoreFromBacklogMode = () => {
    backlog.form.cancelEdit();
  };

  backlog.scheduleBridge.switchBacklogTab = (tabName, render = true) => {
    const btnCreate = document.getElementById("backlogTabCreate");
    const btnList = document.getElementById("backlogTabList");
    const schedulePanel = window.schedule?.panel;
    schedulePanel?.setBacklogTab?.(tabName);
    if (tabName === "create") {
      if (btnCreate) { btnCreate.classList.add("active", "btn-primary"); btnCreate.classList.remove("btn-outline-secondary"); }
      if (btnList) { btnList.classList.remove("active", "btn-primary"); btnList.classList.add("btn-outline-secondary"); }
      schedulePanel?.activateVariant?.("backlog-create");
    } else {
      backlog.form.cancelEdit();
      if (btnCreate) { btnCreate.classList.remove("active", "btn-primary"); btnCreate.classList.add("btn-outline-secondary"); }
      if (btnList) { btnList.classList.add("active", "btn-primary"); btnList.classList.remove("btn-outline-secondary"); }
      schedulePanel?.activateVariant?.("backlog-list");
      if (render) backlog.list.renderList();
    }
  };

  backlog.scheduleBridge.setupCardDrag = (cardEl, item) => {
    cardEl.addEventListener("dragstart", (e) => {
      if (state._selectionMode) { e.preventDefault(); return; }
      state._draggingBacklogId = item.id;
      backlog._draggingItem = { id: item.id, _lastSlotMinutes: null, _lastRow: null };
      e.dataTransfer.effectAllowed = "copy";
      e.dataTransfer.setData("application/x-backlog-id", item.id);
      e.dataTransfer.setData("text/plain", item.id);
      cardEl.classList.add("is-dragging");
    });
    cardEl.addEventListener("dragend", () => {
      state._draggingBacklogId = null;
      backlog._draggingItem = null;
      window.schedule.dragdrop?.clearPreview?.();
      window.schedule.dragdrop?.clearTravelPreview?.();
      cardEl.classList.remove("is-dragging");
    });
  };

  backlog.scheduleBridge.ensureBacklogDraftSandbox = async () => {
    const resp = await fetch("/api/plans/backlog-draft/ensure", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify({}) });
    const data = await resp.json().catch(() => ({}));
    if (!resp.ok) throw new Error(data?.message || "Nepavyko paruosti juodrascio aplinkos.");
    return data;
  };
  backlog.scheduleBridge.setBacklogDraftScheduleView = async (enabled) => {
    const query = window.schedule?.state?.getQuery?.();
    if (!query) return false;
    if (!query.has("view")) query.set("view", "day");
    if (enabled) query.set("planScope", "draft"); else query.delete("planScope");
    return await window.schedule?.table?.fetchAndRender?.(query, { updateUrl: true, preserveScroll: true });
  };
  backlog.scheduleBridge.isBacklogDraftScheduleActive = () => window.schedule?.state?.isDraftMode?.() === true;
  backlog.scheduleBridge.exitBacklogDraftMode = async () => {
    backlog.bulkFlow.hideBulkConfirmView();
    state.clearSchedulingContext();
    state.resetBulkState();
    try { await fetch("/api/plans/discard", { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "XMLHttpRequest" } }); } catch { }
    if (backlog.scheduleBridge.isBacklogDraftScheduleActive()) await backlog.scheduleBridge.setBacklogDraftScheduleView(false);
  };
  backlog.scheduleBridge.openBacklogPanel = () => {
    const schedulePanel = window.schedule?.panel;
    if (!schedulePanel) return;
    const detailPanel = state.getPanelEl();
    if (detailPanel?.classList?.contains("is-collapsed")) {
      detailPanel.classList.remove("is-collapsed");
      detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
    }
    schedulePanel.setBacklogTab?.("list");
    schedulePanel.activateVariant?.("backlog-list");
    schedulePanel.show();
    backlog.list.updateBadge();
  };

  backlog.scheduleBridge.init = () => {
    const panel = state.getPanelEl();
    if (document.documentElement.dataset.scheduleBacklogInit === "1") return;
    document.documentElement.dataset.scheduleBacklogInit = "1";
    backlog.planningSession?.bindPanelListeners?.();
    document.getElementById("backlogTabCreate")?.addEventListener("click", () => backlog.scheduleBridge.switchBacklogTab("create"));
    document.getElementById("backlogTabList")?.addEventListener("click", () => backlog.scheduleBridge.switchBacklogTab("list"));
    document.getElementById("backlogSaveBtn")?.addEventListener("click", backlog.form.handleSave);
    document.getElementById("backlogBulkSchedule")?.addEventListener("click", () => backlog.bulkFlow.handleBulkSchedule());
    document.getElementById("backlogBulkDelete")?.addEventListener("click", () => backlog.list.handleBulkDelete());
    document.getElementById("taskBulkNext")?.addEventListener("click", () => backlog.bulkFlow.onBulkNext());
    document.getElementById("taskBulkBack")?.addEventListener("click", () => backlog.bulkFlow.onBulkBack());
    document.getElementById("backlogSelectToggle")?.addEventListener("click", () => { backlog.list.enterSelectionMode(); backlog.list.renderList(); });
    document.getElementById("backlogSelectCancel")?.addEventListener("click", () => { backlog.list.exitSelectionMode(); backlog.list.renderList(); });
    document.getElementById("backlogSelectAll")?.addEventListener("change", (e) => {
      if (e.target.checked) state._items.forEach((item) => state._selectedIds.add(String(item.id)));
      else state._selectedIds.clear();
      backlog.list.renderList();
      backlog.list.updateBulkBar();
    });
    document.getElementById("backlogItemsList")?.addEventListener("click", (e) => {
      const toggleBtn = e.target.closest("[data-toggle-id]");
      if (toggleBtn) {
        e.stopPropagation();
        const id = toggleBtn.dataset.toggleId;
        const details = document.getElementById(`backlog-details-${id}`);
        if (!details) return;
        const expanded = toggleBtn.getAttribute("aria-expanded") === "true";
        toggleBtn.setAttribute("aria-expanded", String(!expanded));
        details.hidden = expanded;
        toggleBtn.closest(".backlog-card")?.classList.toggle("is-expanded", !expanded);
        const icon = toggleBtn.querySelector("i");
        icon?.classList.toggle("bi-chevron-right", expanded);
        icon?.classList.toggle("bi-chevron-down", !expanded);
        return;
      }
      const editBtn = e.target.closest("[data-edit-id]");
      if (editBtn) return void backlog.form.startEdit(editBtn.dataset.editId);
      const scheduleBtn = e.target.closest("[data-schedule-id]");
      if (scheduleBtn) {
        const item = backlog.api.getItemById(scheduleBtn.dataset.scheduleId);
        if (item) return void backlog.form.scheduleFromBacklogUnscheduled(item);
      }
      const deleteBtn = e.target.closest("[data-delete-id]");
      if (deleteBtn) return void backlog.form.deleteItem(deleteBtn.dataset.deleteId);
      if (state._selectionMode) {
        const card = e.target.closest(".backlog-card");
        if (!card || e.target.closest("button")) return;
        const id = String(card.dataset.backlogId ?? "");
        if (!id) return;
        backlog.list.toggleSelectedItem(id);
        card.classList.toggle("is-selected", state._selectedIds.has(id));
        const checkbox = card.querySelector(".backlog-card__checkbox");
        if (checkbox) checkbox.checked = state._selectedIds.has(id);
        backlog.list.updateBulkBar();
      }
    });
    if (panel) {
      panel.addEventListener("hidden.bs.offcanvas", async () => {
        if (state._isBulkMode || backlog.scheduleBridge.isBacklogDraftScheduleActive()) {
          await backlog.scheduleBridge.exitBacklogDraftMode();
        }
      });
      document.addEventListener("schedule:action-completed", async (e) => {
        if (!state._isBulkMode) return;
        const detail = e.detail || {};
        const taskIds = Array.isArray(detail.taskIds) ? detail.taskIds : [];
        if (!taskIds.length) {
          await backlog.bulkSync.syncAllMaterializedBulkItems();
          return;
        }
        const syncedTasks = await backlog.bulkSync.syncBulkItemsByTaskIds(taskIds);
        if (detail.action !== "update") return;
        const touchedIndexes = syncedTasks
          .map((taskData) => backlog.bulkSync.lockBulkSnapshotFromTask(taskData))
          .filter((idx) => idx >= 0);
        if (!touchedIndexes.length) return;
        await backlog.planningSession.recomputeDownstreamAutoManagedItems(Math.min(...touchedIndexes), { refreshSchedule: true });
      });
      panel.addEventListener("schedule:panel:variant", (e) => {
        const variant = e.detail?.variant;
        if (!variant) return;

        if (variant.startsWith("backlog-bulk")) {
          panel.dataset.bulkMode = "true";
          backlog.bulkState.applyBulkPlanModeToUi();
          state.applyDraftBulkFooterState({ busy: state._bulkNavInFlight || !!state._bulkSavePromise });
          return;
        }

        delete panel.dataset.bulkMode;

        if (variant === "backlog-list") {
          // Don't reset backlog mode while a bulk draft session is active —
          // applyBacklogMode clears the form and clearSchedulingContext, wiping draft state.
          if (state._isBulkMode) return;
          backlog.scheduleBridge.applyBacklogMode();
          backlog.list.openBacklogList();
          return;
        }

        if (variant === "backlog-create") {
          panel.dataset.mode = "backlog";
          return;
        }

        if (state._isBulkMode) {
          return;
        }

        if (backlog.scheduleBridge.isBacklogDraftScheduleActive()) {
          void backlog.scheduleBridge.exitBacklogDraftMode();
          return;
        }

        backlog.scheduleBridge.restoreFromBacklogMode();
      });
    }
  };
})();
