(() => {
  "use strict";

  window.schedule = window.schedule || {};
  const backlog = window.schedule.backlog = window.schedule.backlog || {};
  const state = backlog.state;

  backlog.bulkState = backlog.bulkState || {};

  // ============================================================================
  // Item State Helpers
  // ============================================================================
  backlog.bulkState.ensurePlanningMetadata = (snapshot = {}) => ({
    ...snapshot,
    _planningRevision: Number(snapshot._planningRevision || 0),
    _appliedPlanningRevision: Number(snapshot._appliedPlanningRevision || 0),
    _lastSavedRevision: Number(snapshot._lastSavedRevision || 0),
    _lastAutoPlanFingerprint: snapshot._lastAutoPlanFingerprint || "",
    _visited: snapshot._visited === true,
    _dirty: snapshot._dirty === true,
    _autoPlanned: snapshot._autoPlanned === true,
    _autoPlanLocked: snapshot._autoPlanLocked === true,
  });
  backlog.bulkState.buildPlanningFingerprint = (snapshot = {}) => {
    const normalized = backlog.bulkState.ensurePlanningMetadata(snapshot);
    return JSON.stringify({
      technicianId: Number(normalized.technicianId || 0),
      startAt: normalized.startAt || "",
      endAt: normalized.endAt || "",
      objectId: normalized.objectId != null ? Number(normalized.objectId) : null,
      objectName: normalized.objectName || "",
      objectAddress: normalized.objectAddress || "",
    });
  };
  backlog.bulkState.getItemState = (idx) =>
    backlog.bulkState.ensurePlanningMetadata(state._bulkScheduledData[idx] || {});
  backlog.bulkState.patchItemState = (idx, patch = {}) => {
    if (idx < 0) return backlog.bulkState.ensurePlanningMetadata({});
    const next = {
      ...backlog.bulkState.getItemState(idx),
      ...patch,
    };
    state._bulkScheduledData[idx] = next;
    return next;
  };
  backlog.bulkState.getCurrentItemState = () =>
    backlog.bulkState.getItemState(state._bulkQueueIdx);
  backlog.bulkState.pickSnapshotMetadata = (base = {}) => {
    const normalized = backlog.bulkState.ensurePlanningMetadata(base);
    return {
      _planningRevision: Number(normalized._planningRevision || 0),
      _appliedPlanningRevision: Number(normalized._appliedPlanningRevision || 0),
      _lastSavedRevision: Number(normalized._lastSavedRevision || 0),
      _lastAutoPlanFingerprint: normalized._lastAutoPlanFingerprint || "",
      _visited: normalized._visited === true,
      _dirty: normalized._dirty === true,
      _autoPlanned: normalized._autoPlanned === true,
      _autoPlanLocked: normalized._autoPlanLocked === true,
    };
  };
  backlog.bulkState.composeSnapshot = ({ base = {}, data = {}, metadata = {} } = {}) => ({
    ...backlog.bulkState.pickSnapshotMetadata(base),
    ...base,
    ...data,
    ...metadata,
  });
  backlog.bulkState.hasPlanningInputsChangedSinceAutoPlan = (idx) => {
    const snapshot = backlog.bulkState.getItemState(idx);
    return backlog.bulkState.buildPlanningFingerprint(snapshot) !== (snapshot._lastAutoPlanFingerprint || "");
  };
  backlog.bulkState.invalidateCurrentItemForAutoPlan = () => {
    backlog.bulkState.persistCurrentBulkSnapshot();
    backlog.bulkState.bumpPlanningRevision(state._bulkQueueIdx);
    backlog.bulkState.markBulkItemDirty(state._bulkQueueIdx, true);
  };
  backlog.bulkState.getSnapshotPlanningRevision = (idx) =>
    Number((state._bulkScheduledData[idx] || {})._planningRevision || 0);
  backlog.bulkState.getSnapshotAppliedRevision = (idx) =>
    Number((state._bulkScheduledData[idx] || {})._appliedPlanningRevision || 0);
  backlog.bulkState.hasVisitedBulkItem = (idx) => {
    const snapshot = backlog.bulkState.getItemState(idx);
    return snapshot._visited === true;
  };
  backlog.bulkState.markBulkItemVisited = (idx) => {
    if (idx < 0) return;
    backlog.bulkState.patchItemState(idx, {
      _visited: true,
    });
  };
  backlog.bulkState.markBulkItemDirty = (idx, dirty = true) => {
    if (idx < 0) return;
    const snapshot = backlog.bulkState.getItemState(idx);
    backlog.bulkState.patchItemState(idx, {
      _dirty: dirty,
      _autoPlanLocked: dirty ? false : snapshot._autoPlanLocked,
    });
  };
  backlog.bulkState.markBulkItemAutoPlanned = (idx) => {
    if (idx < 0) return;
    const snapshot = backlog.bulkState.getItemState(idx);
    backlog.bulkState.patchItemState(idx, {
      _visited: true,
      _dirty: false,
      _autoPlanned: true,
      _autoPlanLocked: true,
      _lastAutoPlanFingerprint: backlog.bulkState.buildPlanningFingerprint(snapshot),
    });
  };
  backlog.bulkState.shouldAutoPlanItemOnActivation = (idx, { skipAutoPlanning = false } = {}) => {
    if (skipAutoPlanning || backlog.bulkState.getBulkPlanMode() !== "automatinis") return false;
    const snapshot = backlog.bulkState.getItemState(idx);
    if (!snapshot._visited) return true;
    if (!snapshot._autoPlanned) return true;
    if (!snapshot._autoPlanLocked) return true;
    if (snapshot._dirty) return true;
    return false;
  };
  backlog.bulkState.bumpPlanningRevision = (idx = state._bulkQueueIdx) => {
    if (idx < 0) return 0;
    const current = backlog.bulkState.getItemState(idx);
    const nextRevision = Number(current._planningRevision || 0) + 1;
    backlog.bulkState.patchItemState(idx, {
      _planningRevision: nextRevision,
    });
    return nextRevision;
  };
  backlog.bulkState.markPlanningApplied = (idx, revision) => {
    if (idx < 0) return;
    const current = backlog.bulkState.getItemState(idx);
    backlog.bulkState.patchItemState(idx, {
      _appliedPlanningRevision: Math.max(Number(current._appliedPlanningRevision || 0), Number(revision || 0)),
    });
  };
  backlog.bulkState.markPlanningSaved = (idx, revision) => {
    if (idx < 0) return;
    const current = backlog.bulkState.getItemState(idx);
    backlog.bulkState.patchItemState(idx, {
      _lastSavedRevision: Math.max(Number(current._lastSavedRevision || 0), Number(revision || 0)),
    });
  };
  backlog.bulkState.isPlanningRevisionApplied = (idx = state._bulkQueueIdx) =>
    backlog.bulkState.getSnapshotAppliedRevision(idx) >= backlog.bulkState.getSnapshotPlanningRevision(idx);
  backlog.bulkState.hasPlanningWorkPending = (idx = state._bulkQueueIdx) => {
    if (idx < 0 || !state._isBulkMode) return false;
    if (state._bulkAutoPlanTimer) return true;
    const inFlight = state._bulkAutoPlanInFlight;
    if (inFlight && inFlight.idx === idx) return true;
    return !backlog.bulkState.isPlanningRevisionApplied(idx);
  };
  backlog.bulkState.persistCurrentBulkSnapshot = () => {
    if (!state._isBulkMode) return;
    const panel = state.getPanelEl();
    const modal = window.schedule?.tasks?.modal;
    if (!panel || !modal) return;
    state._bulkScheduledData[state._bulkQueueIdx] = backlog.bulkState.collectMergedBulkSnapshot(panel, state._bulkQueueIdx);
  };
  backlog.bulkState.mapFormDataToSnapshotFields = ({
    base = {},
    item = null,
    formData = {},
    selectedObjectValue = "",
    selectedObjectText = "",
    selectedAddressValue = "",
    selectedAddressText = "",
    selectedAssigneeText = "",
  } = {}) => ({
    title: formData.title || "",
    priorityRaw: formData.priority || "",
    priorityVal: Number.isNaN(formData.priorityVal) ? null : formData.priorityVal,
    technicianId: formData.technicianId || 0,
    technicianName: selectedAssigneeText || base.technicianName || "",
    startAt: state.normalizeDateTimeLocalValue(formData.startAt),
    endAt: state.normalizeDateTimeLocalValue(formData.endAt),
    skillLevel: formData.skillLevel || "",
    instructions: formData.instructions || "",
    technicianComment: formData.technicianComment || "",
    objectId: selectedObjectValue ? Number(selectedObjectValue) : null,
    objectName: selectedObjectText && selectedObjectText !== "â€“ Pasirinkite â€“" ? selectedObjectText : "",
    addressId: selectedAddressValue ? Number(selectedAddressValue) : null,
    objectAddress: selectedAddressText && selectedAddressText !== "â€“ Pasirinkite â€“" ? selectedAddressText : "",
    draftTaskId: base.draftTaskId || null,
    draftTaskVersion: Number(base.draftTaskVersion || 0),
    sourceBacklogItemId: Number(base.sourceBacklogItemId || item?.id || 0) || null,
    isMaterialized: base.isMaterialized === true,
    isUserLocked: base.isUserLocked === true,
    isAutoManaged: base.isAutoManaged !== false,
  });
  backlog.bulkState.mapTaskDataToSnapshotFields = (taskData, base = {}) => ({
    title: taskData?.title || base.title || "",
    priorityVal: Number.isFinite(taskData?.priority) ? taskData.priority : (base.priorityVal ?? null),
    priorityRaw: taskData?.priority != null ? String(taskData.priority) : (base.priorityRaw || ""),
    technicianId: Number(taskData?.technicianId || base.technicianId || 0),
    technicianName: base.technicianName || "",
    startAt: taskData?.startAt ? taskData.startAt.substring(0, 16) : (base.startAt || ""),
    endAt: taskData?.endAt ? taskData.endAt.substring(0, 16) : (base.endAt || ""),
    skillLevel: taskData?.skillLevel || base.skillLevel || "",
    instructions: taskData?.instructions || base.instructions || "",
    technicianComment: taskData?.technicianComment || base.technicianComment || "",
    objectId: taskData?.objectId ?? base.objectId ?? null,
    objectName: taskData?.objectName || base.objectName || "",
    addressId: base.addressId ?? null,
    objectAddress: taskData?.objectAddress || taskData?.location || base.objectAddress || "",
    sourceBacklogItemId: Number(taskData?.sourceBacklogItemId || base.sourceBacklogItemId || 0) || null,
    draftTaskId: Number(taskData?.id || base.draftTaskId || 0) || null,
    draftTaskVersion: Number(taskData?.version || base.draftTaskVersion || 0),
    isMaterialized: true,
    isUserLocked: base.isUserLocked === true,
    isAutoManaged: base.isAutoManaged !== false,
  });
  backlog.bulkState.mapSuggestionToPlanningFields = (suggestion, base = {}) => ({
    technicianId: Number(suggestion.technicianId || 0),
    technicianName: suggestion.technicianName || base.technicianName || "",
    startAt: suggestion.startAt || base.startAt || "",
    endAt: suggestion.endAt || base.endAt || "",
    isUserLocked: false,
    isAutoManaged: true,
  });
  backlog.bulkState.collectMergedBulkSnapshot = (panel, idx) => {
    const modal = window.schedule?.tasks?.modal;
    if (!panel || !modal) return backlog.bulkState.ensurePlanningMetadata(state._bulkScheduledData[idx] || {});
    const base = backlog.bulkState.ensurePlanningMetadata(state._bulkScheduledData[idx] || {});
    const item = state._bulkQueue[idx] || null;
    const formData = modal.collectFormData(panel) || {};
    const objectSelect = panel.querySelector("#taskDetailObject");
    const addressSelect = panel.querySelector("#taskDetailLocation");
    const assigneeSelect = panel.querySelector("#taskDetailAssignee");
    return backlog.bulkState.composeSnapshot({
      base,
      data: backlog.bulkState.mapFormDataToSnapshotFields({
        base,
        item,
        formData,
        selectedObjectValue: objectSelect?.value || "",
        selectedObjectText: objectSelect?.selectedOptions?.[0]?.textContent?.trim() || "",
        selectedAddressValue: addressSelect?.value || "",
        selectedAddressText: addressSelect?.selectedOptions?.[0]?.textContent?.trim() || "",
        selectedAssigneeText: assigneeSelect?.selectedOptions?.[0]?.textContent?.trim() || "",
      }),
    });
  };
  backlog.bulkState.isBulkSnapshotSchedulable = (snapshot, item) => {
    const start = state.parseDatetimeComponents(snapshot?.startAt);
    const end = state.parseDatetimeComponents(snapshot?.endAt);
    return !!(snapshot && snapshot.technicianId && start?.date && end && end.minutes > start.minutes && (snapshot.title || item?.title || "").trim());
  };
  backlog.bulkState.buildMaterializedTaskCreatePayload = (idx) => {
    const item = state._bulkQueue[idx];
    const snap = state._bulkScheduledData[idx] || {};
    const start = state.parseDatetimeComponents(snap.startAt);
    const end = state.parseDatetimeComponents(snap.endAt);
    if (!item || !start || !end || !snap.technicianId) return null;
    return {
      technicianId: snap.technicianId,
      targetDate: start.date,
      startMinutes: start.minutes,
      endMinutes: end.minutes,
      title: snap.title || item.title || "",
      priority: snap.priorityVal ?? item.priority ?? 0,
      status: 0,
      skillLevel: snap.skillLevel || item.skillLevel || "",
      objectId: snap.objectId ? Number(snap.objectId) : null,
      objectName: snap.objectName || item.objectName || "",
      objectAddress: snap.objectAddress || item.locationText || "",
      instructions: snap.instructions || item.instructions || "",
      technicianComment: snap.technicianComment || "",
      sourceBacklogItemId: item.id ? parseInt(item.id, 10) : null,
    };
  };
  backlog.bulkState.buildMaterializedTaskUpdatePayload = (idx) => {
    const item = state._bulkQueue[idx];
    const snap = state._bulkScheduledData[idx] || {};
    return {
      title: snap.title || item?.title || "",
      location: snap.objectAddress || item?.locationText || "",
      startAt: snap.startAt || "",
      endAt: snap.endAt || "",
      priority: snap.priorityVal ?? item?.priority ?? 0,
      technicianId: snap.technicianId || 0,
      skillLevel: snap.skillLevel || item?.skillLevel || "",
      objectName: snap.objectName || item?.objectName || "",
      objectAddress: snap.objectAddress || item?.locationText || "",
      instructions: snap.instructions || item?.instructions || "",
      technicianComment: snap.technicianComment || "",
    };
  };
  backlog.bulkState.getCurrentBulkItem = () => state._bulkQueue[state._bulkQueueIdx] ?? null;
  backlog.bulkState.getBulkPlanMode = () => {
    const panel = state.getPanelEl();
    return panel?.querySelector("#taskPlanMode")?.value || state._bulkPlanMode || "rankinis";
  };
  backlog.bulkState.applyBulkPlanModeToUi = () => {
    const panel = state.getPanelEl();
    if (!panel) return;
    const mode = state._bulkPlanMode || "rankinis";
    const hidden = panel.querySelector("#taskPlanMode");
    if (hidden) hidden.value = mode;
    panel.querySelectorAll("input[name='taskPlanModeRadio']").forEach((radio) => { radio.checked = radio.value === mode; });
  };
  backlog.bulkState.collectBulkSimulatedAssignments = (excludeIdx = -1) => {
    return state._bulkScheduledData
      .map((snapshot, idx) => ({ snapshot, idx }))
      .filter(({ idx, snapshot }) =>
        idx !== excludeIdx &&
        snapshot &&
        snapshot.technicianId &&
        snapshot.startAt &&
        snapshot.endAt &&
        snapshot.isMaterialized !== true &&
        !Number(snapshot.draftTaskId || 0))
      .map(({ snapshot }) => ({
        technicianId: Number(snapshot.technicianId),
        startAt: snapshot.startAt,
        endAt: snapshot.endAt,
        addressId: Number(snapshot.addressId || 0),
        serviceObjectId: snapshot.objectId ? Number(snapshot.objectId) : null,
      }));
  };
  backlog.bulkState.buildAutoAssignmentPayload = (idx) => {
    const item = state._bulkQueue[idx];
    const snapshot = state._bulkScheduledData[idx] || {};
    if (!item) return null;
    const start = state.parseDatetimeComponents(snapshot.startAt) || (item.dueDate ? { date: item.dueDate, minutes: 8 * 60 } : null);
    const end = state.parseDatetimeComponents(snapshot.endAt) || (start ? { date: start.date, minutes: start.minutes + 30 } : null);
    if (!start || !end) return null;
    return {
      technicianId: Number(snapshot.technicianId || 0),
      targetDate: start.date,
      startMinutes: start.minutes,
      endMinutes: end.minutes,
      title: snapshot.title || item.title || "",
      priority: snapshot.priorityVal ?? item.priority ?? 0,
      status: 0,
      skillLevel: snapshot.skillLevel || item.skillLevel || "",
      objectId: snapshot.objectId ? Number(snapshot.objectId) : null,
      objectName: snapshot.objectName || item.objectName || "",
      objectAddress: snapshot.objectAddress || item.locationText || "",
      instructions: snapshot.instructions || item.instructions || "",
      technicianComment: snapshot.technicianComment || "",
      timezoneOffsetMinutes: window.schedule?.grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset(),
      sourceBacklogItemId: item.id ? parseInt(item.id, 10) : null,
      simulatedAssignments: backlog.bulkState.collectBulkSimulatedAssignments(idx),
    };
  };

  // ============================================================================
  // Persistence Helpers
  // ============================================================================

  backlog.bulkSync = backlog.bulkSync || {};
  backlog.bulkState.getBulkTargetDateForEntry = (item, snapshot, fallbackDate = state.getActiveScheduleDate()) => {
    const start = state.parseDatetimeComponents(snapshot?.startAt);
    if (start?.date) return start.date;
    const end = state.parseDatetimeComponents(snapshot?.endAt);
    if (end?.date) return end.date;
    return /^\d{4}-\d{2}-\d{2}$/.test(fallbackDate || "") ? fallbackDate : state.getActiveScheduleDate();
  };
  backlog.bulkState.getBulkDraftRangeForQueue = (
    queue = state._bulkQueue,
    snapshots = state._bulkScheduledData,
    fallbackDate = state.getActiveScheduleDate(),
  ) => {
    const dates = (queue || [])
      .map((item, idx) => backlog.bulkState.getBulkTargetDateForEntry(item, snapshots?.[idx] || {}, fallbackDate))
      .filter((date) => /^\d{4}-\d{2}-\d{2}$/.test(date || ""));
    if (!dates.length) return null;
    dates.sort();
    return {
      startDate: dates[0],
      endDate: dates[dates.length - 1],
    };
  };
  backlog.bulkSync.ensureDraftCoverageForQueue = async (
    queue = state._bulkQueue,
    snapshots = state._bulkScheduledData,
    { force = false } = {},
  ) => {
    const range = backlog.bulkState.getBulkDraftRangeForQueue(queue, snapshots);
    if (!range) return { changed: false, data: state.getBulkDraftContext() };
    const alreadyCovered = state.isBulkDraftRangeCovered(range.startDate, range.endDate);
    if (!force && alreadyCovered) {
      return { changed: false, data: state.getBulkDraftContext() };
    }

    const data = await backlog.scheduleBridge.ensureBacklogDraftSandbox(range);
    return { changed: true, data };
  };
  backlog.bulkSync.getBulkEarliestCreatablePlacementForDate = (targetDate) => {
    const grid = window.schedule?.grid;
    const config = grid?.getConfig?.() || {
      slotSizeMinutes: 30,
      dayStartMinutes: 8 * 60,
      dayEndMinutes: 20 * 60,
    };
    const normalizedTargetDate = /^\d{4}-\d{2}-\d{2}$/.test(targetDate || "") ? targetDate : state.getActiveScheduleDate();
    const slotSizeMinutes = Number(config.slotSizeMinutes || 30);
    const dayStartMinutes = Number(config.dayStartMinutes || 8 * 60);
    const dayEndMinutes = Number(config.dayEndMinutes || 20 * 60);
    const now = new Date();
    const pad = (value) => String(value).padStart(2, "0");
    const today = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
    if (normalizedTargetDate !== today) {
      return {
        targetDate: normalizedTargetDate,
        startMinutes: dayStartMinutes,
        dayEndMinutes,
      };
    }

    const nowMinutes = now.getHours() * 60 + now.getMinutes();
    const roundedNow = Math.ceil(nowMinutes / slotSizeMinutes) * slotSizeMinutes;
    const earliestStartMinutes = Math.max(dayStartMinutes, roundedNow);
    if (earliestStartMinutes <= dayEndMinutes) {
      return {
        targetDate: today,
        startMinutes: earliestStartMinutes,
        dayEndMinutes,
      };
    }

    const tomorrow = new Date(now);
    tomorrow.setDate(tomorrow.getDate() + 1);
    return {
      targetDate: `${tomorrow.getFullYear()}-${pad(tomorrow.getMonth() + 1)}-${pad(tomorrow.getDate())}`,
      startMinutes: dayStartMinutes,
      dayEndMinutes,
    };
  };
  backlog.bulkSync.isBulkSnapshotPlacementStale = (snapshot) => {
    const start = state.parseDatetimeComponents(snapshot?.startAt);
    const end = state.parseDatetimeComponents(snapshot?.endAt);
    if (!start?.date || !end?.date) return false;
    const earliestPlacement = backlog.bulkSync.getBulkEarliestCreatablePlacementForDate(start.date);
    if (start.date < earliestPlacement.targetDate) return true;
    if (start.date > earliestPlacement.targetDate) return false;
    return start.minutes < earliestPlacement.startMinutes;
  };
  backlog.bulkSync.normalizeBulkSnapshotPlacementForMaterialization = async (idx) => {
    const item = state._bulkQueue[idx];
    const snapshot = backlog.bulkState.getItemState(idx);
    if (!item || !backlog.bulkSync.isBulkSnapshotPlacementStale(snapshot)) return true;

    if (snapshot.isUserLocked === true || snapshot.isAutoManaged === false) {
      state._bulkLastSyncError = "Suplanuotas laikas jau praejo. Pasirinkite nauja laika.";
      return false;
    }

    const suggestion = await backlog.planningSession.fetchAutoSuggestionForIndex(idx);
    if (!suggestion) {
      state._bulkLastSyncError = "Nepavyko perskaiciuoti automatines uzduoties laiko.";
      return false;
    }

    const changed = backlog.planningSession.applyAutomaticSuggestionToSnapshot(idx, suggestion);
    if (!changed) {
      state._bulkLastSyncError = "Nepavyko atnaujinti automatines uzduoties laiko.";
      return false;
    }

    if (state._bulkQueueIdx === idx && backlog.bulkSync.shouldRestoreBulkPanel()) {
      await backlog.bulkSync.restoreBulkPanelFromSnapshot(idx);
    }

    return true;
  };
  backlog.bulkSync.refreshDraftScheduleTable = async () => {
    const query = window.schedule?.state?.getQuery?.();
    if (!query) return false;
    const stack = new Error().stack ?? "";
    const callers = stack.split("\n").slice(2, 7).map((l) => l.trim()).join(" | ");
    window.scheduleLogger?.log("info", "bulk.refreshDraftScheduleTable", {
      idx: state._bulkQueueIdx,
      callers,
    });
    query.set("planScope", "draft");
    return await window.schedule?.table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
  };
  backlog.bulkSync.deleteDraftTaskForSnapshot = async (snapshot, { silent = true } = {}) => {
    const taskApi = window.schedule?.tasks?.api;
    const taskId = Number(snapshot?.draftTaskId || 0);
    if (!taskId) return true;
    if (!taskApi?.deleteTask) {
      state._bulkLastSyncError = "Nepavyko pasiekti uzduociu API.";
      return false;
    }

    const result = await taskApi.deleteTask(taskId, Number(snapshot?.draftTaskVersion || 0), { autoCompact: true });
    if (result?.success || result?.errorType === "not_found") return true;

    state._bulkLastSyncError = result?.error || "Nepavyko istrinti juodrascio uzduoties.";
    if (!silent) {
      window.schedule?.ui?.toast?.showWarning?.(state._bulkLastSyncError);
    }
    return false;
  };
  backlog.bulkSync.reconcileDeselectedDraftTasks = async (nextQueue, { silent = true } = {}) => {
    const previousQueue = Array.isArray(state._bulkQueue) ? state._bulkQueue : [];
    const previousSnapshots = Array.isArray(state._bulkScheduledData) ? state._bulkScheduledData : [];
    if (!previousQueue.length || !previousSnapshots.length) return true;

    const nextIds = new Set(
      (nextQueue || [])
        .map((item) => String(item?.id ?? ""))
        .filter(Boolean)
    );

    const staleSnapshots = previousQueue
      .map((item, idx) => ({
        itemId: String(item?.id ?? ""),
        snapshot: previousSnapshots[idx] || null,
      }))
      .filter(({ itemId, snapshot }) =>
        itemId &&
        !nextIds.has(itemId) &&
        Number(snapshot?.draftTaskId || 0) > 0)
      .map(({ snapshot }) => snapshot);

    if (!staleSnapshots.length) return true;

    for (const snapshot of staleSnapshots) {
      const deleted = await backlog.bulkSync.deleteDraftTaskForSnapshot(snapshot, { silent });
      if (!deleted) return false;
    }

    await backlog.bulkSync.refreshDraftScheduleTable();
    return true;
  };
  backlog.bulkSync.materializeOrUpdateBulkItem = async (idx, { refreshSchedule = true, silent = true } = {}) => {
    const item = state._bulkQueue[idx];
    const snap = state._bulkScheduledData[idx] || {};
    const taskApi = window.schedule?.tasks?.api;
    state._bulkLastSyncError = null;
    if (!item || !taskApi?.createDraftTask || !taskApi?.updateTask) {
      state._bulkLastSyncError = "Nepavyko pasiekti uzduočių API.";
      return false;
    }
    if (!backlog.bulkState.isBulkSnapshotSchedulable(snap, item)) {
      state._bulkLastSyncError = "Nepakanka planavimo duomenu.";
      return false;
    }
    const normalized = await backlog.bulkSync.normalizeBulkSnapshotPlacementForMaterialization(idx);
    if (!normalized) {
      if (!silent && state._bulkLastSyncError) window.schedule?.ui?.toast?.showWarning?.(state._bulkLastSyncError);
      return false;
    }
    try {
      await backlog.bulkSync.ensureDraftCoverageForQueue(state._bulkQueue, state._bulkScheduledData);
    } catch (error) {
      state._bulkLastSyncError = error?.message || "Nepavyko paruosti juodrascio plano lango.";
      if (!silent) window.schedule?.ui?.toast?.showWarning?.(state._bulkLastSyncError);
      return false;
    }
    if (!snap.draftTaskId) {
      const createPayload = backlog.bulkState.buildMaterializedTaskCreatePayload(idx);
      if (!createPayload) {
        state._bulkLastSyncError = "Nepavyko suformuoti uzduoties duomenu.";
        return false;
      }
      const createResult = await taskApi.createDraftTask(createPayload);
      if (!createResult?.success || !createResult.id) {
        state._bulkLastSyncError = createResult?.error || "Nepavyko sukurti juodrascio uzduoties.";
        if (!silent) window.schedule?.ui?.toast?.showWarning?.(createResult?.error || "Nepavyko sukurti juodrascio uzduoties.");
        return false;
      }
      state._bulkScheduledData[idx] = backlog.bulkState.composeSnapshot({
        base: snap,
        data: {
          draftTaskId: Number(createResult.id),
          draftTaskVersion: Number(createResult.version || 0),
          isMaterialized: true,
        },
      });
      backlog.bulkState.markPlanningSaved(idx, backlog.bulkState.getSnapshotPlanningRevision(idx));
      await backlog.bulkSync.fetchAndSyncBulkTask(Number(createResult.id));
      if (refreshSchedule) await backlog.bulkSync.refreshDraftScheduleTable();
      if (state._bulkQueueIdx === idx) await backlog.bulkSync.restoreBulkPanelFromSnapshot(idx);
      return true;
    }
    const updateResult = await taskApi.updateTask(Number(snap.draftTaskId), backlog.bulkState.buildMaterializedTaskUpdatePayload(idx), Number(snap.draftTaskVersion || 0));
    if (!updateResult?.success) {
      state._bulkLastSyncError = updateResult?.error || "Nepavyko atnaujinti juodrascio uzduoties.";
      if (!silent) window.schedule?.ui?.toast?.showWarning?.(updateResult?.error || "Nepavyko atnaujinti juodrascio uzduoties.");
      return false;
    }
    state._bulkScheduledData[idx] = backlog.bulkState.composeSnapshot({
      base: state._bulkScheduledData[idx] || {},
      data: {
        draftTaskVersion: Number(updateResult.version || snap.draftTaskVersion || 0),
        isMaterialized: true,
      },
    });
    backlog.bulkState.markPlanningSaved(idx, backlog.bulkState.getSnapshotPlanningRevision(idx));
    await backlog.bulkSync.fetchAndSyncBulkTask(Number(snap.draftTaskId));
    if (refreshSchedule) await backlog.bulkSync.refreshDraftScheduleTable();
    if (state._bulkQueueIdx === idx) await backlog.bulkSync.restoreBulkPanelFromSnapshot(idx);
    return true;
  };
  backlog.bulkSync.taskToBulkSnapshot = (taskData, base = {}) =>
    backlog.bulkState.composeSnapshot({
      base,
      data: backlog.bulkState.mapTaskDataToSnapshotFields(taskData, base),
    });
  backlog.bulkSync.findBulkIndexByTaskId = (taskId) =>
    state._bulkScheduledData.findIndex((snapshot) => Number(snapshot?.draftTaskId || 0) === Number(taskId || 0));
  backlog.bulkSync.findBulkIndexBySourceBacklogItemId = (sourceBacklogItemId) =>
    state._bulkScheduledData.findIndex((snapshot) => Number(snapshot?.sourceBacklogItemId || 0) === Number(sourceBacklogItemId || 0));
  backlog.bulkSync.findBulkIndexForTask = (taskData) => {
    if (!taskData) return -1;
    const byTaskId = backlog.bulkSync.findBulkIndexByTaskId(taskData.id);
    if (byTaskId >= 0) return byTaskId;
    return backlog.bulkSync.findBulkIndexBySourceBacklogItemId(taskData.sourceBacklogItemId);
  };
  backlog.bulkSync.syncBulkSnapshotFromTask = (taskData, idx = backlog.bulkSync.findBulkIndexForTask(taskData)) => {
    if (!taskData || idx < 0) return false;
    const current = backlog.bulkState.ensurePlanningMetadata(state._bulkScheduledData[idx] || {});
    state._bulkScheduledData[idx] = backlog.bulkSync.taskToBulkSnapshot(taskData, current);
    return true;
  };
  backlog.bulkSync.lockBulkSnapshotFromTask = (taskData, idx = backlog.bulkSync.findBulkIndexForTask(taskData)) => {
    if (!taskData || idx < 0) return -1;
    const current = backlog.bulkState.ensurePlanningMetadata(state._bulkScheduledData[idx] || {});
    state._bulkScheduledData[idx] = backlog.bulkState.composeSnapshot({
      base: backlog.bulkSync.taskToBulkSnapshot(taskData, current),
      data: {
        isUserLocked: true,
        isAutoManaged: false,
      },
    });
    return idx;
  };
  backlog.bulkSync.shouldRestoreBulkPanel = () =>
    (window.schedule?.panel?.getActiveVariant?.() || "").startsWith("backlog-bulk");
  backlog.bulkSync.fetchAndSyncBulkTask = async (taskId) => {
    if (!taskId || !window.schedule?.tasks?.api?.fetchTask) return null;
    try {
      const taskData = await window.schedule.tasks.api.fetchTask(taskId);
      backlog.bulkSync.syncBulkSnapshotFromTask(taskData);
      return taskData;
    } catch {
      return null;
    }
  };
  backlog.bulkSync.syncBulkItemsByTaskIds = async (taskIds = []) => {
    const uniqueIds = [...new Set((taskIds || []).map((id) => Number(id)).filter(Boolean))];
    if (!uniqueIds.length) return [];
    const tasks = (await Promise.all(uniqueIds.map((taskId) => backlog.bulkSync.fetchAndSyncBulkTask(taskId)))).filter(Boolean);
    if (state._bulkQueueIdx >= 0 && backlog.bulkSync.shouldRestoreBulkPanel()) {
      await backlog.bulkSync.restoreBulkPanelFromSnapshot(state._bulkQueueIdx);
    }
    return tasks;
  };
  backlog.bulkSync.syncAllMaterializedBulkItems = async () => {
    const ids = state._bulkScheduledData.map((snapshot) => Number(snapshot?.draftTaskId || 0)).filter(Boolean);
    await backlog.bulkSync.syncBulkItemsByTaskIds(ids);
  };
  backlog.bulkSync.restoreBulkPanelFromSnapshot = async (idx) => {
    const panel = state.getPanelEl();
    const snapshot = backlog.bulkState.ensurePlanningMetadata(state._bulkScheduledData[idx]);
    if (!panel || !snapshot) return;
    const modal = window.schedule?.tasks?.modal;
    state._restoringSnapshot = true;
    try {
      const set = (selector, value) => {
        const el = panel.querySelector(selector);
        if (el) el.value = value ?? "";
      };
      set("#taskDetailTitle", snapshot.title || "");
      set("#taskDetailPriority", snapshot.priorityRaw || (snapshot.priorityVal != null ? String(snapshot.priorityVal) : ""));
      set("#taskDetailSkill", snapshot.skillLevel || "");
      set("#taskDetailStart", snapshot.startAt || "");
      set("#taskDetailEnd", snapshot.endAt || "");
      set("#taskDetailInstructions", snapshot.instructions || "");
      set("#taskDetailTechnicianComment", snapshot.technicianComment || "");
      if (modal?.syncInstructionFields) modal.syncInstructionFields(panel, snapshot.instructions || "");

      if (modal) {
        await modal.populateObjectDropdown(panel, snapshot.objectId || null, snapshot.objectName || null, snapshot.objectAddress || null);
        await modal.populateAddressDropdown(panel, snapshot.objectAddress || null);
        modal.ensureObjectAddressBinding(panel);
      }

      const assigneeEl = panel.querySelector("#taskDetailAssignee");
      if (assigneeEl) {
        const techValue = String(snapshot.technicianId || "");
        if (techValue) {
          const existingOption = Array.from(assigneeEl.options || []).some((option) => option.value === techValue);
          if (!existingOption) assigneeEl.add(new Option(techValue, techValue, true, true));
          if (window.jQuery && window.jQuery.fn.select2) window.jQuery(assigneeEl).val(techValue).trigger("change.select2");
          else assigneeEl.value = techValue;
        } else if (window.jQuery && window.jQuery.fn.select2) {
          window.jQuery(assigneeEl).val("").trigger("change.select2");
        } else {
          assigneeEl.value = "";
        }
      }
    } finally {
      state._restoringSnapshot = false;
    }
  };
  backlog.bulkSync.queueBulkPreviewRefresh = (delay = 320) => {
    if (!state._isBulkMode || state._restoringSnapshot) return;
    if (state._bulkPreviewRefreshTimer) clearTimeout(state._bulkPreviewRefreshTimer);
    state._bulkPreviewRefreshTimer = setTimeout(async () => {
      state._bulkPreviewRefreshTimer = null;
      if (!state._isBulkMode || state._restoringSnapshot) return;
      backlog.bulkState.persistCurrentBulkSnapshot();
      await backlog.bulkSync.flushCurrentBulkLiveSave();
    }, delay);
  };
  backlog.bulkSync.flushPendingAutoPersist = async (reason = "") => {
    const pending = state._bulkPendingPersistRequest;
    if (!pending || !state._isBulkMode) return false;
    if (state._bulkQueueIdx !== pending.idx) return false;
    if (backlog.bulkState.hasPlanningWorkPending(pending.idx)) return false;
    if (backlog.bulkState.getSnapshotAppliedRevision(pending.idx) < pending.revision) return false;
    state._bulkPendingPersistRequest = null;
    return await backlog.bulkSync.flushCurrentBulkLiveSave({ ...(pending.options || {}), reason });
  };
  backlog.bulkSync.flushCurrentBulkLiveSave = async (options = {}) => {
    if (!state._isBulkMode || state._restoringSnapshot || state._bulkQueueIdx < 0) return false;
    const opts = { ...state.getDefaultBulkSaveOptions(), ...options };
    const idx = state._bulkQueueIdx;
    backlog.bulkState.persistCurrentBulkSnapshot();
    if (
      backlog.bulkState.getBulkPlanMode() === "automatinis" &&
      backlog.bulkState.hasPlanningWorkPending(idx)
    ) {
      state._bulkPendingPersistRequest = {
        idx,
        revision: backlog.bulkState.getSnapshotPlanningRevision(idx),
        options: opts,
      };
      if (state._isBulkMode) state.applyDraftBulkFooterState({ busy: state.isBulkOperationBusy() });
      return true;
    }
    if (state._bulkSavePromise) {
      state._bulkSaveQueued = true;
      state.mergeBulkSaveOptions(opts);
      state.applyDraftBulkFooterState({ busy: true });
      await state._bulkSavePromise;
      return true;
    }
    state.mergeBulkSaveOptions(opts);
    state.applyDraftBulkFooterState({ busy: true });
    const runSaveLoop = async () => {
      let result = false;
      do {
        state._bulkSaveQueued = false;
        const currentOptions = state._bulkQueuedSaveOptions || state.getDefaultBulkSaveOptions();
        state._bulkQueuedSaveOptions = null;
        const loopIdx = state._bulkQueueIdx;
        backlog.bulkState.persistCurrentBulkSnapshot();
        result = await backlog.bulkSync.materializeOrUpdateBulkItem(loopIdx, currentOptions);
      } while (state._bulkSaveQueued && state._isBulkMode);
      return result;
    };
    state._bulkSavePromise = runSaveLoop();
    try { return await state._bulkSavePromise; }
    finally {
      state._bulkSavePromise = null;
      if (state._isBulkMode) {
        state.applyDraftBulkFooterState({ busy: state.isBulkOperationBusy() });
        await backlog.bulkSync.flushPendingAutoPersist("save-finished");
      }
    }
  };

  backlog.bulkSync.clearPendingBulkTimers = () => {
    if (state._bulkPreviewRefreshTimer) {
      clearTimeout(state._bulkPreviewRefreshTimer);
      state._bulkPreviewRefreshTimer = null;
    }
    if (state._bulkAutoPlanTimer) {
      clearTimeout(state._bulkAutoPlanTimer);
      state._bulkAutoPlanTimer = null;
    }
    if (state._bulkAutoPersistTimer) {
      clearTimeout(state._bulkAutoPersistTimer);
      state._bulkAutoPersistTimer = null;
    }
  };

  backlog.bulkSync.queueAutomaticBulkPersist = async () => {
    if (!state._isBulkMode || state._bulkQueueIdx < 0) return false;
    backlog.bulkState.persistCurrentBulkSnapshot();
    state._bulkPendingPersistRequest = {
      idx: state._bulkQueueIdx,
      revision: backlog.bulkState.getSnapshotPlanningRevision(state._bulkQueueIdx),
      options: { refreshSchedule: true, silent: true },
    };
    if (backlog.bulkState.hasPlanningWorkPending(state._bulkQueueIdx)) {
      state.applyDraftBulkFooterState({ busy: state.isBulkOperationBusy() });
      return true;
    }
    return await backlog.bulkSync.flushPendingAutoPersist("immediate");
  };

  backlog.bulkSync.commitCurrentBulkItemForNavigation = async ({ refreshSchedule = true } = {}) => {
    const panel = state.getPanelEl();
    if (!panel || state._bulkQueueIdx < 0) return false;
    window.scheduleLogger?.log("info", "bulk.commitCurrentForNavigation.start", { idx: state._bulkQueueIdx, refreshSchedule });

    backlog.bulkSync.clearPendingBulkTimers();

    const currentSnapshot = backlog.bulkState.getCurrentItemState();
    const needsPlanningCommit =
      backlog.bulkState.getBulkPlanMode() === "automatinis" &&
      !state._autoPlanningInProgress &&
      !backlog.bulkState.isPlanningRevisionApplied(state._bulkQueueIdx);
    const shouldReplanCurrentItem =
      backlog.bulkState.getBulkPlanMode() === "automatinis" &&
      !state._autoPlanningInProgress &&
      !Number(currentSnapshot.draftTaskId || 0) &&
      currentSnapshot.isMaterialized !== true;

    if (state._bulkAutoPlanPromise && state._bulkAutoPlanInFlight?.idx === state._bulkQueueIdx) {
      await state._bulkAutoPlanPromise;
    } else if (shouldReplanCurrentItem || needsPlanningCommit) {
      await backlog.planningSession.runAutomaticPlanningForCurrentItem({ usePrefetch: false, persist: false });
    }

    state._bulkScheduledData[state._bulkQueueIdx] = backlog.bulkState.collectMergedBulkSnapshot(panel, state._bulkQueueIdx);
    return await backlog.bulkSync.flushCurrentBulkLiveSave({ refreshSchedule, silent: true });
  };

  // ============================================================================
  // Planning Decision Helpers
  // ============================================================================

  backlog.planningSession = backlog.planningSession || {};
  backlog.planningSession.prefetchBulkAutoAssignments = async () => {
    if (!state._bulkQueue.length) return;
    const payload = state._bulkQueue
      .map((_, idx) => backlog.bulkState.buildAutoAssignmentPayload(idx))
      .filter(Boolean);
    if (!payload.length) return;
    const resp = await fetch("/api/tasks/suggest-auto-assignment/bulk", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest" },
      body: JSON.stringify(payload),
    }).catch(() => null);
    const data = await resp?.json?.().catch(() => null);
    if (!resp?.ok || !Array.isArray(data)) return;
    state._bulkPrecomputedSuggestions = data;
  };
  backlog.planningSession.fetchAutoSuggestionForIndex = async (idx) => {
    const payload = backlog.bulkState.buildAutoAssignmentPayload(idx);
    if (!payload) return null;
    const resp = await fetch("/api/tasks/suggest-auto-assignment/bulk", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest" },
      body: JSON.stringify([payload]),
    }).catch(() => null);
    const data = await resp?.json?.().catch(() => null);
    if (!resp?.ok || !Array.isArray(data)) return null;
    return data[0] || null;
  };
  backlog.planningSession.applyAutomaticSuggestionToSnapshot = (idx, suggestion) => {
    if (!suggestion) return false;
    const current = backlog.bulkState.getItemState(idx);
    if (current.isUserLocked === true || current.isAutoManaged === false) return false;
    state._bulkScheduledData[idx] = backlog.bulkState.composeSnapshot({
      base: current,
      data: backlog.bulkState.mapSuggestionToPlanningFields(suggestion, current),
    });
    backlog.bulkState.markBulkItemAutoPlanned(idx);
    return true;
  };
  backlog.planningSession.applyAutomaticSuggestion = async (idx, suggestion, { persist = true, refreshSchedule = false, revision = 0, requestId = 0 } = {}) => {
    if (!suggestion || !state._isBulkMode || state._bulkQueueIdx !== idx) return false;
    const currentSnapshot = backlog.bulkState.getItemState(idx);
    if (Number(currentSnapshot._planningRevision || 0) !== Number(revision || 0)) return false;
    if (!state._bulkAutoPlanInFlight || state._bulkAutoPlanInFlight.requestId !== requestId) return false;
    const panel = state.getPanelEl();
    if (!panel) return false;
    state._autoPlanningInProgress = true;
    try {
      const assigneeEl = panel.querySelector("#taskDetailAssignee");
      const setField = (selector, value, eventName = "input") => {
        const el = panel.querySelector(selector);
        if (!el) return;
        el.value = value ?? "";
        el.dispatchEvent(new Event(eventName, { bubbles: true }));
      };
      if (assigneeEl) {
        const techValue = String(suggestion.technicianId ?? "");
        if (techValue) {
          const existingOption = Array.from(assigneeEl.options || []).some((option) => option.value === techValue);
          if (!existingOption) assigneeEl.add(new Option(suggestion.technicianName || techValue, techValue, true, true));
          if (window.jQuery && window.jQuery.fn.select2) window.jQuery(assigneeEl).val(techValue).trigger("change");
          else {
            assigneeEl.value = techValue;
            assigneeEl.dispatchEvent(new Event("change", { bubbles: true }));
          }
        }
      }
      setField("#taskDetailStart", suggestion.startAt || "", "input");
      setField("#taskDetailEnd", suggestion.endAt || "", "input");
      backlog.bulkState.persistCurrentBulkSnapshot();
      backlog.planningSession.applyAutomaticSuggestionToSnapshot(idx, suggestion);
      backlog.bulkState.markPlanningApplied(idx, revision);
    } finally {
      state._autoPlanningInProgress = false;
    }
    try {
      const ensureResult = await backlog.bulkSync.ensureDraftCoverageForQueue(
        state._bulkQueue,
        state._bulkScheduledData,
      );
      // Only refresh here when not persisting — if persist=true, materializeOrUpdateBulkItem
      // will refresh after placing the task at the correct auto-planned position, avoiding
      // a premature render at the stale DB position before the task is moved.
      if (ensureResult.changed && refreshSchedule && !persist) {
        await backlog.bulkSync.refreshDraftScheduleTable();
      }
    } catch (error) {
      state._bulkLastSyncError = error?.message || "Nepavyko atnaujinti juodrascio plano lango.";
      return false;
    }
    if (persist) {
      const saved = await backlog.bulkSync.materializeOrUpdateBulkItem(idx, { refreshSchedule, silent: false });
      if (!saved) return false;
    } else if (refreshSchedule) {
      await backlog.bulkSync.queueAutomaticBulkPersist();
    }
    return true;
  };
  backlog.planningSession.runAutomaticPlanningForCurrentItem = async ({ usePrefetch = true, persist = true, refreshSchedule = false } = {}) => {
    if (!state._isBulkMode || state._bulkQueueIdx < 0) return false;
    window.scheduleLogger?.log("info", "bulk.runAutoPlan.start", { idx: state._bulkQueueIdx, persist, refreshSchedule });
    const idx = state._bulkQueueIdx;
    backlog.bulkState.persistCurrentBulkSnapshot();
    const revision = backlog.bulkState.getSnapshotPlanningRevision(idx);
    const requestId = ++state._bulkAutoPlanRequestSeq;
    state._bulkAutoPlanInFlight = { idx, revision, requestId };
    state.applyDraftBulkFooterState({ busy: state.isBulkOperationBusy() });
    const planPromise = (async () => {
      let suggestion = null;
      try {
        if (usePrefetch && Array.isArray(state._bulkPrecomputedSuggestions)) suggestion = state._bulkPrecomputedSuggestions[idx] || null;
        if (!suggestion) suggestion = await backlog.planningSession.fetchAutoSuggestionForIndex(idx);
        if (!suggestion) return false;
        return await backlog.planningSession.applyAutomaticSuggestion(idx, suggestion, { persist, refreshSchedule, revision, requestId });
      } finally {
        if (state._bulkAutoPlanInFlight?.requestId === requestId) {
          state._bulkAutoPlanInFlight = null;
        }
        if (state._isBulkMode) {
          state.applyDraftBulkFooterState({ busy: state.isBulkOperationBusy() });
          await backlog.bulkSync.flushPendingAutoPersist("plan-finished");
        }
      }
    })();
    state._bulkAutoPlanPromise = planPromise;
    try {
      return await planPromise;
    } finally {
      if (state._bulkAutoPlanPromise === planPromise) {
        state._bulkAutoPlanPromise = null;
      }
    }
  };
  backlog.planningSession.queueAutomaticPlanningRefresh = (delay = 250) => {
    if (!state._isBulkMode || state._restoringSnapshot || state._autoPlanningInProgress) return;
    if (state._bulkAutoPlanTimer) clearTimeout(state._bulkAutoPlanTimer);
    state._bulkAutoPlanTimer = setTimeout(async () => {
      state._bulkAutoPlanTimer = null;
      if (!state._isBulkMode || state._restoringSnapshot || state._autoPlanningInProgress) return;
      await backlog.planningSession.runAutomaticPlanningForCurrentItem({ usePrefetch: false, persist: true, refreshSchedule: true });
    }, delay);
  };
  backlog.planningSession.handlePlanModeChanged = async (mode) => {
    backlog.bulkSync.clearPendingBulkTimers();
    backlog.bulkState.persistCurrentBulkSnapshot();
    state._bulkPlanMode = mode === "automatinis" ? "automatinis" : "rankinis";
    backlog.bulkState.applyBulkPlanModeToUi();
    if (!state._isBulkMode) return;
    if (state._bulkPlanMode === "automatinis") {
      const idx = state._bulkQueueIdx;
      const shouldReplan =
        !backlog.bulkState.getItemState(idx)._autoPlanned ||
        backlog.bulkState.hasPlanningInputsChangedSinceAutoPlan(idx);

      if (!shouldReplan) {
        const snapshot = backlog.bulkState.getItemState(idx);
        backlog.bulkState.patchItemState(idx, {
          _dirty: false,
          _autoPlanLocked: true,
        });
        if (!Number(snapshot.draftTaskId || 0)) {
          await backlog.bulkSync.flushCurrentBulkLiveSave({ refreshSchedule: true, silent: true });
        }
        backlog.bulkFlow.updateBulkNavButtons();
        return;
      }

      backlog.bulkState.markBulkItemDirty(idx, true);
      await backlog.planningSession.runAutomaticPlanningForCurrentItem({ usePrefetch: true, persist: true, refreshSchedule: true });
    } else {
      backlog.bulkState.markBulkItemDirty(state._bulkQueueIdx, false);
      await backlog.bulkSync.flushCurrentBulkLiveSave({ refreshSchedule: true, silent: true });
    }
  };
  backlog.planningSession.recomputeDownstreamAutoManagedItems = async (startIdx, { refreshSchedule = true } = {}) => {
    if (!state._isBulkMode) return true;
    let anyChanged = false;
    let hasErrors = false;
    for (let idx = Math.max(Number(startIdx) + 1, 0); idx < state._bulkQueue.length; idx++) {
      const snapshot = state._bulkScheduledData[idx] || {};
      if (snapshot.isUserLocked === true || snapshot.isAutoManaged === false) continue;
      const suggestion = await backlog.planningSession.fetchAutoSuggestionForIndex(idx);
      if (!suggestion) {
        hasErrors = true;
        backlog.planningSession.setConfirmItemStatus(idx, "error", "Nepavyko perskaiciuoti automatines uzduoties.");
        continue;
      }
      backlog.planningSession.applyAutomaticSuggestionToSnapshot(idx, suggestion);
      // Only push to DB for items already materialized (visited). Unvisited items keep
      // the updated snapshot in memory and are materialized when the user navigates to
      // them, avoiding a premature render with the wrong technician in the schedule.
      if (Number(snapshot.draftTaskId || 0)) {
        const ok = await backlog.bulkSync.materializeOrUpdateBulkItem(idx, { refreshSchedule: false, silent: true });
        if (!ok) {
          hasErrors = true;
          backlog.planningSession.setConfirmItemStatus(idx, "error", state._bulkLastSyncError || "Nepavyko issaugoti perskaiciuotos uzduoties.");
          continue;
        }
      }
      anyChanged = true;
      if (state._bulkConfirmPhase) backlog.planningSession.setConfirmItemStatus(idx, "ok", "Gerai");
    }
    if (anyChanged && refreshSchedule) {
      await backlog.bulkSync.refreshDraftScheduleTable();
      await backlog.bulkSync.syncAllMaterializedBulkItems();
    }
    if (state._bulkQueueIdx >= 0 && backlog.bulkSync.shouldRestoreBulkPanel()) {
      await backlog.bulkSync.restoreBulkPanelFromSnapshot(state._bulkQueueIdx);
    }
    return !hasErrors;
  };
  backlog.planningSession.recomputeAutoManagedItemsAfterLockedEdits = async ({ refreshSchedule = false } = {}) => {
    const lockedIndexes = state._bulkScheduledData
      .map((snapshot, idx) => (snapshot?.isUserLocked === true ? idx : -1))
      .filter((idx) => idx >= 0);
    if (!lockedIndexes.length) return true;
    return await backlog.planningSession.recomputeDownstreamAutoManagedItems(Math.min(...lockedIndexes), { refreshSchedule });
  };
  backlog.planningSession.setConfirmItemStatus = (idx, status, tooltip) => {
    state._bulkConfirmStatuses[idx] = { status, tooltip: tooltip || "" };
    if (state._bulkConfirmPhase) backlog.planningSession.renderConfirmSummary();
  };
  backlog.planningSession.hasConfirmErrors = () =>
    state._bulkConfirmStatuses.some((item) => item?.status === "error");
  backlog.planningSession.buildBulkValidationPayload = () => {
    const timezoneOffsetMinutes = window.schedule?.grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    return state._bulkQueue.map((item, idx) => {
      const snapshot = backlog.bulkState.getItemState(idx);
      const createPayload = backlog.bulkState.buildMaterializedTaskCreatePayload(idx);
      const start = state.parseDatetimeComponents(snapshot?.startAt);
      const end = state.parseDatetimeComponents(snapshot?.endAt);
      const existingDraftTaskId = Number(snapshot?.draftTaskId || 0) || null;
      return {
        technicianId: Number(snapshot?.technicianId || createPayload?.technicianId || 0),
        targetDate: start?.date || createPayload?.targetDate || item?.dueDate || state.getActiveScheduleDate(),
        startMinutes: start?.minutes ?? createPayload?.startMinutes ?? 0,
        endMinutes: end?.minutes ?? createPayload?.endMinutes ?? 0,
        title: snapshot?.title || item?.title || "",
        priority: snapshot?.priorityVal ?? item?.priority ?? 0,
        status: 0,
        timezoneOffsetMinutes,
        skillLevel: snapshot?.skillLevel || item?.skillLevel || "",
        objectName: snapshot?.objectName || item?.objectName || "",
        objectAddress: snapshot?.objectAddress || item?.locationText || "",
        instructions: snapshot?.instructions || item?.instructions || "",
        sourceBacklogItemId: item?.id ? Number(item.id) : null,
        ignoreTaskId: existingDraftTaskId,
      };
    });
  };
  backlog.planningSession.validateBulkQueueForConfirm = async () => {
    await backlog.planningSession.recomputeAutoManagedItemsAfterLockedEdits({ refreshSchedule: false });
    // Materialize any items that were never visited (skipped during downstream recompute).
    // All items must be in the draft plan before apply-to-active runs.
    for (let idx = 0; idx < state._bulkQueue.length; idx++) {
      const snap = backlog.bulkState.getItemState(idx);
      if (!Number(snap.draftTaskId || 0) && backlog.bulkState.isBulkSnapshotSchedulable(snap, state._bulkQueue[idx])) {
        await backlog.bulkSync.materializeOrUpdateBulkItem(idx, { refreshSchedule: false, silent: true });
      }
    }
    for (let idx = 0; idx < state._bulkQueue.length; idx++) {
      backlog.planningSession.setConfirmItemStatus(idx, "pending", "Apdorojama");
    }

    const payload = backlog.planningSession.buildBulkValidationPayload();
    const resp = await fetch("/api/tasks/bulk/validate", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest" },
      body: JSON.stringify({
        planMode: backlog.bulkState.getBulkPlanMode(),
        planScope: "draft",
        items: payload,
      }),
    }).catch(() => null);

    const data = await resp?.json?.().catch(() => null);
    if (!resp?.ok) {
      const message = data?.message || "Nepavyko patikrinti uzduociu paketo.";
      for (let idx = 0; idx < state._bulkQueue.length; idx++) {
        backlog.planningSession.setConfirmItemStatus(idx, "error", message);
      }
      backlog.bulkFlow.updateBulkNavButtons();
      return false;
    }

    const errorsByIndex = new Map((Array.isArray(data?.errors) ? data.errors : []).map((error) => [Number(error.index), error.message || "Nepavyko patikrinti uzduoties."]));
    for (let idx = 0; idx < state._bulkQueue.length; idx++) {
      const errorMessage = errorsByIndex.get(idx);
      backlog.planningSession.setConfirmItemStatus(idx, errorMessage ? "error" : "ok", errorMessage || "Gerai");
    }

    backlog.bulkFlow.updateBulkNavButtons();
    return !backlog.planningSession.hasConfirmErrors();
  };
  backlog.planningSession.renderConfirmSummary = () => {
    const listEl = document.getElementById("taskBulkConfirmList");
    if (!listEl) return;
    listEl.innerHTML = "";

    const escapeHtml = state.escapeHtml || ((value) => String(value ?? ""));
    const priorityLabel = (priority) => state.PRIORITY_LABELS?.[priority] || "Nenustatytas";
    const formatDateTime = (value) => {
      if (!value) return "";
      const normalized = String(value).replace("T", " ");
      const parsed = new Date(value);
      if (Number.isNaN(parsed.getTime())) return normalized;
      return parsed.toLocaleString("lt-LT", {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
      }).replace(",", "");
    };

    state._bulkQueue.forEach((item, idx) => {
      const snapshot = state._bulkScheduledData[idx] || {};
      const confirmStatus = state._bulkConfirmStatuses[idx] || { status: "ok", tooltip: "Gerai" };
      const isCollapsed = snapshot._confirmCollapsed !== false;
      const title = snapshot.title || item?.title || "Be pavadinimo";
      const start = snapshot.startAt || "";
      const end = snapshot.endAt || "";
      const techLabel = snapshot.technicianName || (snapshot.technicianId ? `Darbuotojas #${snapshot.technicianId}` : "Nepriskirta");
      const objectName = snapshot.objectName || item?.objectName || "";
      const addressOnly = snapshot.objectAddress || item?.locationText || "Be adreso";
      const address = objectName ? `${objectName} · ${addressOnly}` : addressOnly;
      const priority = priorityLabel(snapshot.priorityVal ?? item?.priority);
      const iconClass = confirmStatus.status === "error"
        ? "bi-x-circle-fill bulk-status-icon--err"
        : confirmStatus.status === "pending"
          ? "bi-hourglass-split bulk-status-icon--pending"
          : "bi-check-circle-fill bulk-status-icon--ok";
      const chevronClass = isCollapsed ? "bi-chevron-right" : "bi-chevron-down";

      const row = document.createElement("div");
      row.className = "backlog-card backlog-card--confirm mb-2";
      row.dataset.confirmItemIdx = String(idx);
      row.innerHTML = `
        <div class="backlog-card__row">
          <div class="backlog-card__title">${escapeHtml(title)}</div>
          <div class="backlog-card__badges">
            <span class="badge ${state.PRIORITY_CLASSES?.[snapshot.priorityVal ?? item?.priority] || "bg-secondary"}">${escapeHtml(priority)}</span>
            <button type="button" class="backlog-card__toggle" data-confirm-toggle="${idx}" aria-expanded="${isCollapsed ? "false" : "true"}" aria-label="Rodyti detales">
              <i class="bi ${chevronClass}"></i>
            </button>
            <span class="bulk-status-icon ${confirmStatus.status === "error" ? "bulk-status-icon--err" : confirmStatus.status === "pending" ? "bulk-status-icon--pending" : "bulk-status-icon--ok"}" data-tooltip="${escapeHtml(confirmStatus.tooltip || "Gerai")}" aria-label="${escapeHtml(confirmStatus.tooltip || "Gerai")}">
              <i class="bi ${iconClass}"></i>
            </span>
          </div>
        </div>
        <div class="backlog-card__details"${isCollapsed ? " hidden" : ""}>
          <div class="backlog-card__detail-block backlog-card__detail--technician">
            <span class="backlog-card__detail-label">Priskirtas</span>
            <div class="backlog-card__skill">
              <span class="backlog-card__technician-value">${escapeHtml(techLabel)}</span>
            </div>
          </div>
          <div class="backlog-card__detail-block backlog-card__detail--time">
            <span class="backlog-card__detail-label">Pradžia</span>
            <div class="backlog-card__skill">
              <span class="backlog-card__time-value">${escapeHtml(formatDateTime(start))}</span>
            </div>
          </div>
          <div class="backlog-card__detail-block backlog-card__detail--time">
            <span class="backlog-card__detail-label">Pabaiga</span>
            <div class="backlog-card__skill">
              <span class="backlog-card__time-value">${escapeHtml(formatDateTime(end))}</span>
            </div>
          </div>
          <div class="backlog-card__detail-block backlog-card__detail--object">
            <span class="backlog-card__detail-label">Objektas</span>
            <div class="backlog-card__location">
              <i class="bi bi-geo-alt"></i>
              <span class="backlog-card__object-value">${escapeHtml(address)}</span>
            </div>
          </div>
        </div>
      `;
      listEl.appendChild(row);
    });

    if (!listEl.children.length) {
      const empty = document.createElement("div");
      empty.className = "text-muted small";
      empty.textContent = "Nera ka patvirtinti.";
      listEl.appendChild(empty);
    }
  };
  backlog.planningSession.bindPanelListeners = () => {
    const panel = state.getPanelEl();
    if (!panel || panel.dataset.backlogPlanningSessionBound === "1") return;
    panel.dataset.backlogPlanningSessionBound = "1";

    panel.addEventListener("change", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement)) return;
      if (target.matches("input[name='taskPlanModeRadio']")) {
        void backlog.planningSession.handlePlanModeChanged(target.value);
        return;
      }
      if (!state._isBulkMode || state._restoringSnapshot || state._autoPlanningInProgress) return;
      if (target.matches("#taskDetailPriority, #taskDetailSkill, #taskDetailAssignee, #taskDetailObject, #taskDetailLocation")) {
        if (backlog.bulkState.getBulkPlanMode() === "automatinis") {
          backlog.bulkState.invalidateCurrentItemForAutoPlan();
          backlog.planningSession.queueAutomaticPlanningRefresh(220);
        }
        else backlog.bulkSync.queueBulkPreviewRefresh(220);
      }
    });

    panel.addEventListener("input", (event) => {
      const target = event.target;
      if (!(target instanceof HTMLElement) || !state._isBulkMode || state._restoringSnapshot || state._autoPlanningInProgress) return;
      if (target.matches("#taskDetailTitle, #taskDetailInstructions, #taskDetailTechnicianComment")) {
        backlog.bulkSync.queueBulkPreviewRefresh(500);
        return;
      }
      if (target.matches("#taskDetailStart, #taskDetailEnd")) {
        if (backlog.bulkState.getBulkPlanMode() === "automatinis") {
          backlog.bulkState.invalidateCurrentItemForAutoPlan();
          backlog.planningSession.queueAutomaticPlanningRefresh(220);
        }
        else backlog.bulkSync.queueBulkPreviewRefresh(220);
      }
    });

    if (window.jQuery && window.jQuery.fn.select2) {
      window.jQuery(panel).on("select2:select select2:clear", "#taskDetailObject, #taskDetailLocation, #taskDetailAssignee", () => {
        if (!state._isBulkMode || state._restoringSnapshot || state._autoPlanningInProgress) return;
        if (backlog.bulkState.getBulkPlanMode() === "automatinis") {
          backlog.bulkState.invalidateCurrentItemForAutoPlan();
          backlog.planningSession.queueAutomaticPlanningRefresh(220);
        }
        else backlog.bulkSync.queueBulkPreviewRefresh(220);
      });
    }

    document.getElementById("taskBulkConfirmList")?.addEventListener("click", (event) => {
      const toggleBtn = event.target.closest("[data-confirm-toggle]");
      if (!toggleBtn) return;
      const idx = Number(toggleBtn.dataset.confirmToggle);
      const snapshot = state._bulkScheduledData[idx];
      if (!snapshot) return;
      snapshot._confirmCollapsed = !snapshot._confirmCollapsed;
      backlog.planningSession.renderConfirmSummary();
    });

    document.getElementById("taskBulkConfirmList")?.addEventListener("click", async (event) => {
      const card = event.target.closest("[data-confirm-item-idx]");
      if (!card || event.target.closest("[data-confirm-toggle]")) return;
      const idx = Number(card.dataset.confirmItemIdx);
      if (!Number.isFinite(idx) || idx < 0 || idx >= state._bulkQueue.length) return;
      backlog.bulkFlow.hideBulkConfirmView();
      state._bulkQueueIdx = idx;
      await backlog.bulkFlow.activateBulkQueueItem(idx, { skipAutoPlanning: true });
    });
  };
  // ============================================================================
  // Panel Hydration Helpers
  // ============================================================================

  backlog.bulkUi = backlog.bulkUi || {};
  backlog.bulkUi.setFieldValue = (panel, selector, value) => {
    const el = panel?.querySelector?.(selector);
    if (el) el.value = value ?? "";
  };
  backlog.bulkUi.clearAssigneeSelection = (panel) => {
    const assigneeEl = panel?.querySelector?.("#taskDetailAssignee");
    if (!assigneeEl) return;
    if (window.jQuery) window.jQuery(assigneeEl).val("").trigger("change");
    else assigneeEl.value = "";
  };
  backlog.bulkUi.resetFreshItemPanel = async (panel, item, { neutralizePlanningFields = false } = {}) => {
    if (!panel) return;
    const set = (selector, value) => backlog.bulkUi.setFieldValue(panel, selector, value);
    const defaultPlacement = state.getEarliestCreatableRangeForScheduleDate?.() || null;
    if (neutralizePlanningFields) {
      set("#taskDetailAssignee", "");
      set("#taskDetailStart", "");
      set("#taskDetailEnd", "");
    }
    set("#taskDetailTitle", "");
    set("#taskDetailPriority", "");
    set("#taskDetailSkill", "");
    set("#taskDetailInstructions", "");
    set("#taskDetailTechnicianComment", "");
    set("#taskDetailStart", defaultPlacement?.start ?? "");
    set("#taskDetailEnd", defaultPlacement?.end ?? "");
    backlog.bulkUi.clearAssigneeSelection(panel);

    const modal = window.schedule?.tasks?.modal;
    if (modal) {
      await Promise.all([
        modal.populateObjectDropdown(panel, null, null, null),
        modal.populateAddressDropdown(panel, null),
      ]);
      modal.ensureObjectAddressBinding(panel);
    }

    await backlog.form.applyPrefill(item);
  };
  backlog.bulkUi.updateProgress = (idx, item) => {
    const stepEl = document.getElementById("taskBulkProgressStep");
    const titleEl = document.getElementById("taskBulkProgressTitle");
    if (stepEl) stepEl.textContent = `${idx + 1} / ${state._bulkQueue.length}`;
    if (titleEl) titleEl.textContent = item?.title || "Be pavadinimo";
  };

  // ============================================================================
  // Navigation / Controller Helpers
  // ============================================================================

  backlog.bulkFlow = backlog.bulkFlow || {};
  backlog.bulkFlow.shouldNeutralizePlanningFieldsOnNext = (idx) =>
    backlog.bulkState.getBulkPlanMode() === "automatinis" &&
    !backlog.bulkState.hasVisitedBulkItem(idx);
  backlog.bulkFlow.activateTargetItem = async (idx, options = {}) =>
    await backlog.bulkFlow.activateBulkQueueItemAndSettle(idx, options);
  backlog.bulkFlow.commitCurrentBeforeTraversal = async (options = {}) =>
    await backlog.bulkSync.commitCurrentBulkItemForNavigation(options);

  backlog.planningSession.handleBulkSchedule = backlog.bulkFlow;
  backlog.bulkFlow.buildQueueFromSelection = () => {
    const selectedIds = state.getSelectedIdsInOrder().map((id) => String(id));
    if (!selectedIds.length && state._isBulkMode && state._bulkQueue.length) {
      return state._bulkQueue.slice();
    }

    const existingById = new Map(
      (state._bulkQueue || [])
        .filter((item) => item?.id != null)
        .map((item) => [String(item.id), item])
    );
    const loadedById = new Map(
      (state._items || [])
        .filter((item) => item?.id != null)
        .map((item) => [String(item.id), item])
    );

    const nextQueue = [];
    selectedIds.forEach((id) => {
      const item = loadedById.get(id) || existingById.get(id);
      if (item) nextQueue.push(item);
    });

    return nextQueue;
  };
  backlog.bulkFlow.mergeQueueSelectionIntoDraftState = (nextQueue) => {
    const previousQueue = Array.isArray(state._bulkQueue) ? state._bulkQueue : [];
    const previousSnapshots = Array.isArray(state._bulkScheduledData) ? state._bulkScheduledData : [];
    const previousIndexById = new Map(
      previousQueue
        .map((item, idx) => [String(item?.id ?? ""), idx])
        .filter(([id]) => !!id)
    );

    const nextSnapshots = nextQueue.map((item) => {
      const idx = previousIndexById.get(String(item?.id ?? ""));
      if (typeof idx === "number" && idx >= 0) {
        return backlog.bulkState.ensurePlanningMetadata(previousSnapshots[idx] || {});
      }
      return backlog.bulkState.ensurePlanningMetadata({
        sourceBacklogItemId: item?.id ? Number(item.id) : null,
        isUserLocked: false,
        isAutoManaged: true,
      });
    });

    let nextIdx = 0;
    if (state._isBulkMode && previousQueue.length) {
      const currentItem = previousQueue[state._bulkQueueIdx];
      const mappedIdx = nextQueue.findIndex((item) => String(item?.id ?? "") === String(currentItem?.id ?? ""));
      nextIdx = mappedIdx >= 0 ? mappedIdx : Math.min(state._bulkQueueIdx, Math.max(nextQueue.length - 1, 0));
    }

    state._bulkQueue = nextQueue;
    state._bulkScheduledData = nextSnapshots;
    state._bulkQueueIdx = nextIdx;
  };
  backlog.bulkFlow.handleBulkSchedule = async () => {
    const nextQueue = backlog.bulkFlow.buildQueueFromSelection();
    if (!nextQueue.length) return;
    // Determine BEFORE merging state whether we're resuming an existing draft.
    // Reopened bulk sessions should preserve the current draft session context and
    // only re-ensure when the queue expands outside the already ensured range.
    const reopeningExistingDraft = state._isBulkMode && state._bulkQueue.length > 0;
    if (reopeningExistingDraft) {
      backlog.bulkState.persistCurrentBulkSnapshot();
      const reconciled = await backlog.bulkSync.reconcileDeselectedDraftTasks(nextQueue, { silent: false });
      if (!reconciled) return;
    }
    backlog.bulkFlow.mergeQueueSelectionIntoDraftState(nextQueue);
    try {
      const ensureResult = await backlog.bulkSync.ensureDraftCoverageForQueue(
        state._bulkQueue,
        state._bulkScheduledData,
        { force: !reopeningExistingDraft },
      );
      if (!reopeningExistingDraft || !backlog.scheduleBridge.isBacklogDraftScheduleActive()) {
        await backlog.scheduleBridge.setBacklogDraftScheduleView(true);
      } else if (ensureResult.changed) {
        await backlog.bulkSync.refreshDraftScheduleTable();
      }
    } catch (error) {
      window.schedule?.ui?.toast?.showError?.(error?.message || "Nepavyko paruosti juodrascio aplinkos.");
      return;
    }
    state._isBulkMode = true;
    if (!reopeningExistingDraft) state._bulkPlanMode = "rankinis";
    backlog.bulkFlow.hideBulkConfirmView();
    const initialPlacement = state.getEarliestCreatableRangeForScheduleDate?.() || {};
    await window.schedule?.tasks?.openTaskPanel?.({
      targetDate: initialPlacement.targetDate,
      startMinutes: initialPlacement.startMinutes,
      endMinutes: initialPlacement.endMinutes,
    });
    const panel = state.getPanelEl();
    if (!panel) return;
    panel.dataset.bulkMode = "true";
    window.schedule?.panel?.activateVariant?.("backlog-bulk-edit");
    backlog.bulkState.applyBulkPlanModeToUi();
    state.applyDraftBulkFooterState({ busy: false });
    void backlog.planningSession.prefetchBulkAutoAssignments();
    await backlog.bulkFlow.activateBulkQueueItem(state._bulkQueueIdx, { skipAutoPlanning: reopeningExistingDraft });
  };
  backlog.bulkFlow.startBulkQueueItem = async (idx, { neutralizePlanningFields = false } = {}) => {
    const item = state._bulkQueue[idx];
    if (!item) return;
    window.scheduleLogger?.log("info", "bulk.startBulkQueueItem", {
      idx,
      neutralizePlanningFields,
      hasDraftTaskId: !!state._bulkScheduledData[idx]?.draftTaskId,
      visited: !!state._bulkScheduledData[idx]?._visited,
      autoPlanned: !!state._bulkScheduledData[idx]?._autoPlanned,
      autoPlanLocked: !!state._bulkScheduledData[idx]?._autoPlanLocked,
    });
    backlog.bulkState.markBulkItemVisited(idx);
    state._schedulingFromBacklogId = item.id;
    state._schedulingFromBacklogItemType = item.itemType;
    const panel = state.getPanelEl();
    if (!panel) return;
    window.schedule?.panel?.activateVariant?.("backlog-bulk-edit");
    backlog.bulkState.applyBulkPlanModeToUi();
    const existingSnapshot = state._bulkScheduledData[idx];
    if (existingSnapshot?.draftTaskId) {
      await backlog.bulkSync.fetchAndSyncBulkTask(existingSnapshot.draftTaskId);
    }
    if (!neutralizePlanningFields && (state._bulkScheduledData[idx]?.draftTaskId || state._bulkScheduledData[idx]?.title || state._bulkScheduledData[idx]?.technicianId)) {
      await backlog.bulkSync.restoreBulkPanelFromSnapshot(idx);
    } else {
      await backlog.bulkUi.resetFreshItemPanel(panel, item, { neutralizePlanningFields });
      state._bulkScheduledData[idx] = backlog.bulkState.ensurePlanningMetadata(backlog.bulkState.collectMergedBulkSnapshot(panel, idx));
    }
    backlog.bulkUi.updateProgress(idx, item);
  };
  backlog.bulkFlow.activateBulkQueueItem = async (idx, { skipAutoPlanning = false } = {}) => {
    await backlog.bulkFlow.startBulkQueueItem(idx);
    if (backlog.bulkState.shouldAutoPlanItemOnActivation(idx, { skipAutoPlanning })) {
      await backlog.planningSession.runAutomaticPlanningForCurrentItem({ usePrefetch: true, persist: true, refreshSchedule: true });
    }
    backlog.bulkFlow.updateBulkNavButtons();
  };
  backlog.bulkFlow.activateBulkQueueItemAndSettle = async (idx, { skipAutoPlanning = false, neutralizePlanningFields = false } = {}) => {
    await backlog.bulkFlow.startBulkQueueItem(idx, { neutralizePlanningFields });
    if (backlog.bulkState.shouldAutoPlanItemOnActivation(idx, { skipAutoPlanning })) {
      await backlog.planningSession.runAutomaticPlanningForCurrentItem({ usePrefetch: true, persist: true, refreshSchedule: true });
      backlog.bulkSync.clearPendingBulkTimers();
      await backlog.bulkSync.flushPendingAutoPersist("navigation-settle");
      if (state._bulkAutoPlanPromise && state._bulkAutoPlanInFlight?.idx === idx) {
        await state._bulkAutoPlanPromise;
      }
    }
    backlog.bulkFlow.updateBulkNavButtons();
  };
  backlog.bulkFlow.updateBulkNavButtons = () => state.applyDraftBulkFooterState({ busy: state.isBulkOperationBusy() });
  backlog.bulkFlow.onBulkNext = async () => {
    if (state._bulkNavInFlight) return;
    state._bulkNavInFlight = true;
    state._bulkTransitionInFlight = true;
    state.applyDraftBulkFooterState({ busy: true });
    try {
      if (state._bulkConfirmPhase) {
        await backlog.bulkFlow.confirmAndCreateAll();
        return;
      }
      const nextIdx = state._bulkQueueIdx + 1;
      const nextWillAutoplan =
        nextIdx < state._bulkQueue.length &&
        backlog.bulkState.shouldAutoPlanItemOnActivation(nextIdx, { skipAutoPlanning: false });
      window.scheduleLogger?.log("info", "bulk.onBulkNext", {
        currentIdx: state._bulkQueueIdx,
        nextIdx,
        nextWillAutoplan,
        nextSnapshot: nextIdx < state._bulkQueue.length ? {
          draftTaskId: state._bulkScheduledData[nextIdx]?.draftTaskId,
          visited: state._bulkScheduledData[nextIdx]?._visited,
          autoPlanned: state._bulkScheduledData[nextIdx]?._autoPlanned,
          autoPlanLocked: state._bulkScheduledData[nextIdx]?._autoPlanLocked,
        } : null,
      });
      await backlog.bulkFlow.commitCurrentBeforeTraversal({ refreshSchedule: !nextWillAutoplan });
      if (state._bulkQueueIdx < state._bulkQueue.length - 1) {
        state._bulkQueueIdx++;
        await backlog.bulkFlow.activateTargetItem(state._bulkQueueIdx, {
          neutralizePlanningFields: backlog.bulkFlow.shouldNeutralizePlanningFieldsOnNext(state._bulkQueueIdx),
        });
      } else {
        await backlog.bulkFlow.showBulkConfirmView();
      }
    } finally {
      state._bulkNavInFlight = false;
      state._bulkTransitionInFlight = false;
      if (state._isBulkMode) backlog.bulkFlow.updateBulkNavButtons();
    }
  };
  backlog.bulkFlow.onBulkBack = async () => {
    if (state._bulkNavInFlight) return;
    state._bulkNavInFlight = true;
    state._bulkTransitionInFlight = true;
    state.applyDraftBulkFooterState({ busy: true });
    try {
      await backlog.bulkFlow.commitCurrentBeforeTraversal();
      if (state._bulkConfirmPhase) {
        backlog.bulkFlow.hideBulkConfirmView();
        state._bulkQueueIdx = state._bulkQueue.length - 1;
        await backlog.bulkFlow.activateTargetItem(state._bulkQueueIdx, { skipAutoPlanning: true });
        return;
      }
      if (state._bulkQueueIdx === 0) {
        await backlog.scheduleBridge.exitBacklogDraftMode();
        window.schedule?.panel?.hide?.();
        return;
      }
      if (state._bulkQueueIdx > 0) {
        state._bulkQueueIdx--;
        await backlog.bulkFlow.activateTargetItem(state._bulkQueueIdx, { skipAutoPlanning: true });
      }
    } finally {
      state._bulkNavInFlight = false;
      state._bulkTransitionInFlight = false;
      if (state._isBulkMode) backlog.bulkFlow.updateBulkNavButtons();
    }
  };
  backlog.bulkFlow.showBulkConfirmView = async () => {
    state._bulkConfirmPhase = true;
    state._bulkConfirmStatuses = state._bulkQueue.map(() => ({ status: "pending", tooltip: "Apdorojama" }));
    state._bulkScheduledData.forEach((snapshot) => {
      if (snapshot && typeof snapshot._confirmCollapsed === "undefined") snapshot._confirmCollapsed = true;
    });
    backlog.planningSession.renderConfirmSummary();
    window.schedule?.panel?.activateVariant?.("backlog-bulk-confirm");
    await backlog.planningSession.validateBulkQueueForConfirm();
    backlog.bulkFlow.updateBulkNavButtons();
  };
  backlog.bulkFlow.hideBulkConfirmView = () => {
    state._bulkConfirmPhase = false;
    window.schedule?.panel?.activateVariant?.("backlog-bulk-edit");
  };
  backlog.bulkFlow.confirmAndCreateAll = async () => {
    const nextBtn = document.getElementById("taskBulkNext");
    const backBtn = document.getElementById("taskBulkBack");
    const closeAfterPublish = async () => {
      backlog.bulkFlow.hideBulkConfirmView();
      state.clearSchedulingContext();
      state.resetBulkState();
      backlog.list.exitSelectionMode();
      backlog.list.renderList();
      if (backlog.scheduleBridge.isBacklogDraftScheduleActive()) {
        await backlog.scheduleBridge.setBacklogDraftScheduleView(false);
      }
      window.schedule?.panel?.hide?.();
    };
    if (nextBtn) nextBtn.disabled = true;
    if (backBtn) backBtn.disabled = true;
    if (backlog.planningSession.hasConfirmErrors()) {
      window.schedule?.ui?.toast?.showWarning?.("Pirmiausia pataisykite raudonai pazymetas uzduotis.");
      backlog.bulkFlow.updateBulkNavButtons();
      return;
    }
    backlog.bulkState.persistCurrentBulkSnapshot();
    const applyResp = await fetch("/api/plans/backlog-draft/apply-to-active", {
      method: "POST",
      credentials: "same-origin",
      headers: { "X-Requested-With": "XMLHttpRequest" },
    }).catch(() => null);
    if (!applyResp?.ok) {
      const data = await applyResp?.json?.().catch(() => null);
      window.schedule?.ui?.toast?.showError?.(data?.message || "Nepavyko pritaikyti juodrascio aktyviam planui.");
      backlog.bulkFlow.updateBulkNavButtons();
      return;
    }
    for (const item of state._bulkQueue) {
      if (item.itemType === state.ITEM_TYPE.Temporary) {
        try { await backlog.api.apiRemoveItem(item.id); } catch { }
      }
    }
    backlog.list.updateBadge();
    await closeAfterPublish();
  };
})();
