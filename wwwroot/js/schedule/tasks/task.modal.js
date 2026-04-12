/**
 * task.modal.js
 *
 * Task modal/form population, field restrictions, validation display,
 * and dropdown management (technicians, objects, addresses).
 *
 * Dependencies: schedule.grid, schedule.ui.select2, schedule.tasks.api
 * Dependents: schedule.tasks (main orchestrator)
 */
(() => {
  "use strict";

  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  window.schedule.tasks = window.schedule.tasks || {};

  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const panel = schedule.panel;

  // Import from shared modules
  const grid = schedule.grid;
  const select2 = schedule.ui?.select2;
  const taskApi = schedule.tasks?.api;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

  const isBulkDraftLiveEditMode = (modalEl) =>
    getActiveVariant(modalEl) === "backlog-bulk-edit" &&
    modalEl?.dataset?.bulkMode === "true" &&
    window.schedule?.backlog?.state?._isBulkMode === true;

  const getActiveVariant = (modalEl) =>
    modalEl?.dataset?.panelVariant || panel?.getActiveVariant?.() || "task-edit";

  // ========== Modal Field Cache (Performance: reduce 40+ queries to 1 lookup) ==========

  /**
   * Cache for modal element and field references.
   * Populated on modal open, cleared on modal close.
   * Reduces querySelector calls from 40+ per operation to single cache population.
   */
  const modalFields = {
    modal: null,
    title: null,
    location: null,
    start: null,
    end: null,
    priority: null,
    status: null,
    assignee: null,
    object: null,
    skill: null,
    instructions: null,
    instructionsReadonly: null,
    technicianComment: null,
    label: null,
    code: null,
    saveBtn: null,
    deleteBtn: null,
    readonlyNote: null,
  };

  /**
   * Populate modal field cache from DOM.
   * Called once when modal is opened to avoid repeated queries.
   */
  const populateFieldCache = (modalEl) => {
    if (modalFields.modal === modalEl) return; // Already cached for this modal
    modalFields.modal = modalEl;
    modalFields.title = modalEl.querySelector("#taskDetailTitle");
    modalFields.location = modalEl.querySelector("#taskDetailLocation");
    modalFields.start = modalEl.querySelector("#taskDetailStart");
    modalFields.end = modalEl.querySelector("#taskDetailEnd");
    modalFields.priority = modalEl.querySelector("#taskDetailPriority");
    modalFields.status = modalEl.querySelector("#taskDetailStatus");
    modalFields.assignee = modalEl.querySelector("#taskDetailAssignee");
    modalFields.object = modalEl.querySelector("#taskDetailObject");
    modalFields.skill = modalEl.querySelector("#taskDetailSkill");
    modalFields.instructions = modalEl.querySelector("#taskDetailInstructions");
    modalFields.instructionsReadonly = modalEl.querySelector("#taskDetailInstructionsReadonly");
    modalFields.technicianComment = modalEl.querySelector("#taskDetailTechnicianComment");
    modalFields.label = modalEl.querySelector("#taskDetailLabel");
    modalFields.code = modalEl.querySelector("#taskDetailCode");
    modalFields.saveBtn = modalEl.querySelector("#taskDetailSave");
    modalFields.deleteBtn = modalEl.querySelector("#taskDetailDelete");
    modalFields.readonlyNote = modalEl.querySelector("#taskDetailReadonlyNote");
  };

  /**
   * Clear modal field cache (called on modal close).
   */
  const clearFieldCache = () => {
    Object.keys(modalFields).forEach(key => modalFields[key] = null);
  };

  // ========== Select2 Helpers (with fallbacks) ==========

  const initSelect2 = (selectEl, placeholder) => {
    if (select2?.init) {
      select2.init(selectEl, { placeholder });
      return;
    }
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.select2("destroy");
    }
    const dropdownParent = selectEl.closest(".offcanvas") || document.body;
    $select.select2({
      theme: "bootstrap-5",
      placeholder: placeholder || "Pasirinkite...",
      allowClear: false,
      width: "100%",
      dropdownParent: $(dropdownParent),
      language: {
        noResults: () => "Nerasta rezultatų",
        searching: () => "Ieškoma...",
        inputTooShort: (args) => `įveskite bent ${args.minimum} simbolius`,
      }
    });
  };

  const destroySelect2 = (selectEl) => {
    if (select2?.destroy) {
      select2.destroy(selectEl);
      return;
    }
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.select2("destroy");
    }
  };

  const setSelect2Value = (selectEl, value) => {
    if (select2?.setValue) {
      select2.setValue(selectEl, value);
      return;
    }
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.val(value).trigger("change.select2");
    }
  };

  const setSelect2Disabled = (selectEl, disabled) => {
    if (select2?.setDisabled) {
      select2.setDisabled(selectEl, disabled);
      return;
    }
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.prop("disabled", disabled);
    }
  };

  // ========== Data Loading ==========

  const loadObjects = async () => {
    if (taskApi?.fetchObjects) return taskApi.fetchObjects();
    const resp = await fetch("/api/objects", { headers: { "X-Requested-With": "XMLHttpRequest" } });
    if (!resp.ok) {
      console.error("Failed to load objects");
      return [];
    }
    return await resp.json();
  };

  const loadTechnicians = async () => {
    if (taskApi?.fetchTechnicians) return taskApi.fetchTechnicians();
    const resp = await fetch("/api/technicians", { headers: { "X-Requested-With": "XMLHttpRequest" } });
    if (!resp.ok) {
      console.error("Failed to load technicians");
      return [];
    }
    return await resp.json();
  };

  const formatTravelDuration = (minutes) => {
    if (minutes == null || Number.isNaN(minutes) || minutes < 0) return "-";
    if (minutes < 60) return `${minutes} min`;
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;
    return mins > 0 ? `${hours} h ${mins} min` : `${hours} h`;
  };

  const escapeHtml = (value) => String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");

  const parseLocalDateTime = (raw) => {
    if (!raw || !raw.includes("T")) return null;
    const [datePart, timePart] = raw.split("T");
    if (!datePart || !timePart) return null;
    const [hStr, mStr] = timePart.split(":");
    const h = parseInt(hStr, 10);
    const m = parseInt(mStr, 10);
    if (Number.isNaN(h) || Number.isNaN(m)) return null;
    return { date: datePart, minutes: h * 60 + m };
  };


  const persistCreatePlacementDataset = (modalEl, placement) => {
    if (!modalEl || !placement) return;
    modalEl.dataset.technicianId = String(placement.technicianId);
    modalEl.dataset.targetDate = placement.targetDate;
    modalEl.dataset.startMinutes = String(placement.startMinutes);
    modalEl.dataset.endMinutes = String(placement.endMinutes);
  };

  const collectCreatePlacement = (modalEl) => {
    if (!modalEl) return { ok: false, error: "Nepavyko nuskaityti uzduoties laiko." };

    const startRaw = modalEl.querySelector("#taskDetailStart")?.value || "";
    const endRaw = modalEl.querySelector("#taskDetailEnd")?.value || "";
    const start = parseLocalDateTime(startRaw);
    const end = parseLocalDateTime(endRaw);
    const selectedTech = parseInt(modalEl.querySelector("#taskDetailAssignee")?.value || "0", 10);
    const fallbackTech = parseInt(modalEl.dataset.technicianId || "0", 10);
    const technicianId = Number.isFinite(selectedTech) && selectedTech > 0 ? selectedTech : fallbackTech;
    const slotSize = grid?.getConfig?.()?.slotSizeMinutes || 30;

    if (!start || !end) {
      return { ok: false, error: "Nurodykite uzduoties pradzios ir pabaigos laika." };
    }
    if (start.date !== end.date) {
      return { ok: false, error: "Uzduotis turi tilpti i viena diena." };
    }
    if (end.minutes <= start.minutes) {
      return { ok: false, error: "Pabaigos laikas turi buti veliau nei pradzios laikas." };
    }
    if (start.minutes % slotSize !== 0 || end.minutes % slotSize !== 0) {
      return { ok: false, error: "Laikas turi atitikti grafiko zingsni." };
    }
    if (!Number.isFinite(technicianId) || technicianId <= 0) {
      return { ok: false, error: "Priskirtas darbuotojas yra privalomas." };
    }

    return {
      ok: true,
      technicianId,
      targetDate: start.date,
      startMinutes: start.minutes,
      endMinutes: end.minutes,
    };
  };

  const bindCreatePlacementSync = (modalEl) => {
    if (!modalEl || modalEl.dataset.createPlacementSyncBound === "1") return;
    modalEl.dataset.createPlacementSyncBound = "1";

    const syncFromForm = () => {
      if (modalEl.dataset.mode !== "create") return;
      const placement = collectCreatePlacement(modalEl);
      if (!placement.ok) return;

      persistCreatePlacementDataset(modalEl, placement);
      schedule.selection?.syncCreateRangeFromPanel?.({
        technicianId: placement.technicianId,
        targetDate: placement.targetDate,
        startMinutes: placement.startMinutes,
        endMinutes: placement.endMinutes,
      });
    };

    const bind = (selector, eventName = "input") => {
      const el = modalEl.querySelector(selector);
      if (!el) return;
      el.addEventListener(eventName, syncFromForm);
      if (typeof $ !== "undefined" && $.fn.select2 && el.tagName === "SELECT") {
        $(el).on("select2:select select2:clear", syncFromForm);
      }
    };

    bind("#taskDetailStart", "input");
    bind("#taskDetailEnd", "input");
    bind("#taskDetailAssignee", "change");

    modalEl._syncCreatePlacementFromForm = syncFromForm;
  };
  const collectAssigneePreviewContext = (modalEl) => {
    const startRaw = modalEl.querySelector("#taskDetailStart")?.value || "";
    const endRaw = modalEl.querySelector("#taskDetailEnd")?.value || "";
    const address = (modalEl.querySelector("#taskDetailLocation")?.value || "").trim();
    const start = parseLocalDateTime(startRaw);
    const end = parseLocalDateTime(endRaw);

    if (!start || !end || !address || start.date !== end.date || end.minutes <= start.minutes) {
      return null;
    }

    return {
      taskId: (() => {
        const id = parseInt(modalEl.dataset.taskId || "", 10);
        return Number.isFinite(id) && id > 0 ? id : null;
      })(),
      targetDate: start.date,
      startMinutes: start.minutes,
      endMinutes: end.minutes,
      objectAddress: address,
    };
  };

  /**
   * Calls /api/tasks/suggest-start and updates #taskDetailStart / #taskDetailEnd via setFn.
   *
   * chainContext = { afterEndMinutes, targetDate, previousObjectId }  → bulk chain mode
   * chainContext = null                                                → standalone mode (appendAfterLastTask=true)
   *
   * setFn(selector, value) must dispatch an "input" event so downstream listeners cascade.
   */
  const scheduleSuggestSlot = async (modalEl, techId, chainContext, setFn) => {
    if (!techId) return;
    const tz = window.schedule?.grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    const currentObjectId = parseInt(modalEl.querySelector("#taskDetailObject")?.value || "0", 10) || 0;

    let url;
    if (chainContext) {
      url = `/api/tasks/suggest-start`
          + `?technicianId=${techId}`
          + `&targetDate=${chainContext.targetDate}`
          + `&afterEndMinutes=${chainContext.afterEndMinutes}`;
      if (chainContext.previousObjectId) url += `&previousServiceObjectId=${chainContext.previousObjectId}`;
      if (currentObjectId)               url += `&newServiceObjectId=${currentObjectId}`;
    } else {
      const startRaw   = modalEl.querySelector("#taskDetailStart")?.value || "";
      const parsed     = parseLocalDateTime(startRaw);
      // Fall back to today in the user's local timezone when start field is empty
      // (e.g. calendar-icon flow opens the form with no pre-filled times).
      const todayLocal = new Date(Date.now() + tz * 60000).toISOString().slice(0, 10);
      const targetDate = parsed?.date ?? todayLocal;
      url = `/api/tasks/suggest-start`
          + `?technicianId=${techId}`
          + `&targetDate=${targetDate}`
          + `&appendAfterLastTask=true`;
      if (currentObjectId) url += `&newServiceObjectId=${currentObjectId}`;
    }

    try {
      const resp = await fetch(url, { credentials: "same-origin", headers: { "X-Timezone-Offset": String(tz) } });
      if (resp.ok) {
        const data = await resp.json();
        if (data.startAt) setFn("#taskDetailStart", data.startAt);
        if (data.endAt)   setFn("#taskDetailEnd",   data.endAt);
      }
    } catch (err) {
      console.error("[suggest-slot] fetch error", err);
    }
  };

  const initAssigneeSelect2 = (selectEl) => {
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) {
      initSelect2(selectEl, "Pasirinkite darbuotoją...");
      return;
    }

    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.select2("destroy");
    }

    const dropdownParent = selectEl.closest(".offcanvas") || document.body;
    $select.select2({
      theme: "bootstrap-5",
      placeholder: "Pasirinkite darbuotoją...",
      allowClear: false,
      width: "100%",
      dropdownParent: $(dropdownParent),
      escapeMarkup: (markup) => markup,
      templateResult: (data) => {
        if (!data?.id || !data.element) return data?.text || "";
        const minutes = data.element.dataset.travelMinutes;
        const selectable = data.element.dataset.selectable !== "false";
        const travelText = !selectable
          ? "X"
          : formatTravelDuration(minutes ? parseInt(minutes, 10) : null);
        const name = escapeHtml(data.element.dataset.baseLabel || data.text);
        return `
          <div class="tech-option-row">
            <span class="tech-option-name">${name}</span>
            <span class="tech-option-travel"><i class="bi bi-car-front-fill" aria-hidden="true"></i> ${escapeHtml(travelText)}</span>
          </div>`;
      },
      templateSelection: (data) => {
        if (!data?.id || !data.element) return data?.text || "";
        const selectable = data.element.dataset.selectable !== "false";
        if (!selectable) {
          const nameX = escapeHtml(data.element.dataset.baseLabel || data.text);
          return `${nameX} <span class="tech-option-travel"><i class="bi bi-car-front-fill" aria-hidden="true"></i> X</span>`;
        }
        const minutes = data.element.dataset.travelMinutes;
        if (!minutes) return escapeHtml(data.element.dataset.baseLabel || data.text);
        const travelText = formatTravelDuration(parseInt(minutes, 10));
        const name = escapeHtml(data.element.dataset.baseLabel || data.text);
        return `${name} <span class="tech-option-travel"><i class="bi bi-car-front-fill" aria-hidden="true"></i> ${escapeHtml(travelText)}</span>`;
      },
      language: {
        noResults: () => "Nerasta rezultatų",
        searching: () => "Ieškoma...",
      },
    });
  };

  // ========== Dropdown Population ==========

  const populateObjectDropdown = async (modalEl, selectedObjectId, fallbackObjectName, fallbackAddress) => {
    const select = modalEl.querySelector("#taskDetailObject");
    if (!select) return;

    destroySelect2(select);

    const objects = (await loadObjects())
      .filter((obj) => (obj?.name || "").trim().toLowerCase() !== "- pasirinkite objekta -");
    select.innerHTML = "";

    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "";
    select.appendChild(placeholder);

    objects.forEach((obj) => {
      const opt = document.createElement("option");
      opt.value = obj.id;
      opt.textContent = obj.name;
      opt.dataset.address = obj.address || "";
      if (selectedObjectId && String(selectedObjectId) === String(obj.id)) {
        opt.selected = true;
      }
      select.appendChild(opt);
    });

    if (selectedObjectId && !select.value) {
      const custom = document.createElement("option");
      custom.value = selectedObjectId;
      custom.textContent = fallbackObjectName || "";
      custom.dataset.address = fallbackAddress || "";
      custom.selected = true;
      select.appendChild(custom);
    } else if (!selectedObjectId && fallbackObjectName) {
      const custom = document.createElement("option");
      custom.value = "";
      custom.textContent = fallbackObjectName;
      custom.dataset.address = fallbackAddress || "";
      custom.selected = true;
      select.appendChild(custom);
    }

    initSelect2(select, "Pasirinkite objektą...");
  };

  const populateAddressDropdown = async (modalEl, selectedAddress) => {
    const select = modalEl.querySelector("#taskDetailLocation");
    if (!select) return;

    destroySelect2(select);

    const objects = await loadObjects();
    select.innerHTML = "";

    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "";
    select.appendChild(placeholder);

    const addressToObject = new Map();
    objects.forEach((obj) => {
      if (obj.address && obj.address.trim()) {
        const addr = obj.address.trim();
        if (!addressToObject.has(addr)) {
          addressToObject.set(addr, obj.id);
        }
      }
    });

    addressToObject.forEach((objectId, addr) => {
      const opt = document.createElement("option");
      opt.value = addr;
      opt.textContent = addr;
      opt.dataset.objectId = objectId;
      if (selectedAddress && addr === selectedAddress) {
        opt.selected = true;
      }
      select.appendChild(opt);
    });

    if (selectedAddress && !addressToObject.has(selectedAddress)) {
      const custom = document.createElement("option");
      custom.value = selectedAddress;
      custom.textContent = selectedAddress;
      custom.selected = true;
      select.appendChild(custom);
    }

    initSelect2(select, "Pasirinkite adresą...");
  };

  const populateTechnicianDropdown = async (modalEl, selectedTechnicianId, travelPreviewByTech = null) => {
    const dropdown = modalEl.querySelector("#taskDetailAssignee");
    if (!dropdown) return;

    destroySelect2(dropdown);

    try {
      const technicians = await loadTechnicians();
      const sortedTechnicians = [...technicians].sort((a, b) => {
        const pa = travelPreviewByTech instanceof Map ? travelPreviewByTech.get(a.id) : null;
        const pb = travelPreviewByTech instanceof Map ? travelPreviewByTech.get(b.id) : null;
        const sa = pa == null ? true : pa.isSelectable !== false;
        const sb = pb == null ? true : pb.isSelectable !== false;

        if (sa !== sb) return sa ? -1 : 1;

        const ma = pa && Number.isFinite(pa.travelMinutes) ? pa.travelMinutes : Number.POSITIVE_INFINITY;
        const mb = pb && Number.isFinite(pb.travelMinutes) ? pb.travelMinutes : Number.POSITIVE_INFINITY;
        if (ma !== mb) return ma - mb;

        const na = (a?.name || "").toString();
        const nb = (b?.name || "").toString();
        return na.localeCompare(nb, "lt");
      });

      dropdown.innerHTML = "";

      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "";
      dropdown.appendChild(placeholder);

      sortedTechnicians.forEach((tech) => {
        const option = document.createElement("option");
        option.value = tech.id;
        const baseLabel = tech.region ? `${tech.name} (${tech.region})` : tech.name;
        option.textContent = baseLabel;
        option.dataset.baseLabel = baseLabel;
        const preview = travelPreviewByTech instanceof Map ? travelPreviewByTech.get(tech.id) : null;
        const minutes = preview && Number.isFinite(preview.travelMinutes) ? preview.travelMinutes : null;
        const selectable = preview == null ? true : preview.isSelectable !== false;
        option.dataset.travelMinutes = Number.isFinite(minutes) ? String(minutes) : "";
        // X badge shown via data-selectable; options are never disabled — suggest-start
        // moves the time to a valid slot after the user picks an infeasible technician.
        option.dataset.selectable = selectable ? "true" : "false";
        if (tech.id === selectedTechnicianId) {
          option.selected = true;
        }
        dropdown.appendChild(option);
      });

      initAssigneeSelect2(dropdown);
    } catch (err) {
      console.error("Error loading technicians:", err);
    }
  };

  const scheduleAssigneeTravelPreview = (modalEl) => {
    const dropdown = modalEl.querySelector("#taskDetailAssignee");
    if (!dropdown) return;
    if (dropdown.dataset.travelPreviewBound === "1") {
      if (typeof dropdown._refreshAssigneeTravelPreview === "function") {
        dropdown._refreshAssigneeTravelPreview();
      }
      return;
    }
    dropdown.dataset.travelPreviewBound = "1";

    let debounceId = null;
    let requestSeq = 0;

    const refresh = async () => {
      const selectedTechnicianId = parseInt(dropdown.value || modalEl.dataset.technicianId || "0", 10);
      const context = collectAssigneePreviewContext(modalEl);

      if (!context || !taskApi?.fetchAssigneeTravelPreview) {
        await populateTechnicianDropdown(modalEl, selectedTechnicianId, null);
        return;
      }

      const seq = ++requestSeq;
      const preview = await taskApi.fetchAssigneeTravelPreview(context);
      if (seq !== requestSeq) return;

      const travelMap = new Map();
      if (preview?.success && Array.isArray(preview.items)) {
        preview.items.forEach((item) => {
          if (item && Number.isFinite(item.technicianId)) {
            travelMap.set(item.technicianId, {
              travelMinutes: Number.isFinite(item.travelMinutes) ? item.travelMinutes : null,
              isSelectable: item.isSelectable !== false,
            });
          }
        });
      }

      await populateTechnicianDropdown(modalEl, selectedTechnicianId, travelMap);
    };

    const scheduleRefresh = () => {
      if (debounceId) clearTimeout(debounceId);
      debounceId = setTimeout(refresh, 350);
    };
    dropdown._refreshAssigneeTravelPreview = scheduleRefresh;

    const bind = (selector, eventName = "change") => {
      const el = modalEl.querySelector(selector);
      if (!el) return;
      el.addEventListener(eventName, scheduleRefresh);
      if (typeof $ !== "undefined" && $.fn.select2 && el.tagName === "SELECT") {
        $(el).on("select2:select select2:clear", scheduleRefresh);
      }
    };

    bind("#taskDetailObject");
    bind("#taskDetailLocation");
    bind("#taskDetailStart", "input");
    bind("#taskDetailEnd", "input");

    // Tech selection in CREATE mode → suggest best free slot, then travel preview cascades.
    // Excluded in edit mode (times must stay when reassigning) and bulk mode (bulk has its
    // own chain-aware handler in schedule.backlog.js that calls scheduleSuggestSlot directly).
    const onTechSelect = async (rawVal) => {
      const techId = parseInt(rawVal || dropdown.value || "0", 10) || 0;
      if (!techId) return;
      if (modalEl.dataset.taskId) return;                      // edit mode — keep times
      if (window.schedule?.backlog?._isBulkMode) return;       // bulk mode handles chaining
      const setFn = (id, val) => {
        const el = modalEl.querySelector(id);
        if (!el) return;
        el.value = val ?? "";
        el.dispatchEvent(new Event("input", { bubbles: true })); // cascades travel preview
      };
      await scheduleSuggestSlot(modalEl, techId, null, setFn);
    };
    dropdown.addEventListener("change", (e) => onTechSelect(e.target.value));
    if (typeof $ !== "undefined" && $.fn.select2) {
      $(dropdown).on("select2:select select2:clear", (e) => onTechSelect(e.params?.data?.id ?? ""));
    }

    refresh();
  };

  // ========== Object-Address Binding ==========

  const ensureObjectAddressBinding = (modalEl) => {
    const objectSelect = modalEl.querySelector("#taskDetailObject");
    const addressSelect = modalEl.querySelector("#taskDetailLocation");

    if (!objectSelect || !addressSelect) return;
    if (objectSelect.dataset.objectAddressBinding === "1") return;
    objectSelect.dataset.objectAddressBinding = "1";

    let isUpdating = false;

    const updateAddressFromObject = () => {
      if (isUpdating) return;
      isUpdating = true;
      const selected = objectSelect.selectedOptions[0];
      const address = selected?.dataset?.address || "";
      setSelect2Value(addressSelect, address);
      isUpdating = false;
    };

    const updateObjectFromAddress = () => {
      if (isUpdating) return;
      isUpdating = true;
      const selected = addressSelect.selectedOptions[0];
      const objectId = selected?.dataset?.objectId || "";
      if (objectId) {
        setSelect2Value(objectSelect, objectId);
      }
      isUpdating = false;
    };

    objectSelect.addEventListener("change", updateAddressFromObject);
    if (typeof $ !== "undefined" && $.fn.select2) {
      $(objectSelect).on("select2:select select2:clear", updateAddressFromObject);
    }

    addressSelect.addEventListener("change", updateObjectFromAddress);
    if (typeof $ !== "undefined" && $.fn.select2) {
      $(addressSelect).on("select2:select select2:clear", updateObjectFromAddress);
    }
  };

  // ========== Form Population ==========

  const syncInstructionFields = (modalEl, instructionsText) => {
    const instructions = instructionsText || "";
    const instructionsInput = modalEl.querySelector("#taskDetailInstructions");
    if (instructionsInput) instructionsInput.value = instructions;

    const instructionsReadonly = modalEl.querySelector("#taskDetailInstructionsReadonly");
    if (instructionsReadonly) instructionsReadonly.textContent = instructions || "-";
  };

  const populateTaskModal = async (modalEl, data, editable, { onSave }) => {
    // Populate field cache for performance (reduces 40+ queries to 1 lookup)
    populateFieldCache(modalEl);

    const setVal = (selector, value) => {
      const el = modalEl.querySelector(selector);
      if (!el) return;
      el.value = value ?? "";
    };

    setVal("#taskDetailTitle", data.title);
    setVal("#taskDetailStart", data.startAt ? data.startAt.substring(0, 16) : "");
    setVal("#taskDetailEnd", data.endAt ? data.endAt.substring(0, 16) : "");
    setVal("#taskDetailPriority", data.priority);
    setVal("#taskDetailStatus", data.status);
    setVal("#taskDetailTechnicianComment", data.technicianComment);
    setVal("#taskDetailSkill", data.skillLevel);

    await populateObjectDropdown(modalEl, data.objectId, data.objectName, data.objectAddress);
    const selectedAddress = data.objectAddress || data.location;
    await populateAddressDropdown(modalEl, selectedAddress);
    ensureObjectAddressBinding(modalEl);

    syncInstructionFields(modalEl, data.instructions);
    await populateTechnicianDropdown(modalEl, data.technicianId);
    scheduleAssigneeTravelPreview(modalEl);

    modalEl.dataset.mode = "update";
    modalEl.dataset.taskId = data.id;
    modalEl.dataset.editable = editable ? "true" : "false";
    modalEl.dataset.technicianId = data.technicianId;
    if (data.version != null) modalEl.dataset.version = String(data.version);
    delete modalEl.dataset.targetDate;
    delete modalEl.dataset.startMinutes;
    delete modalEl.dataset.endMinutes;

    modalEl.querySelector("#taskDetailLabel").textContent = "Užduoties informacija";
    modalEl.querySelector("#taskDetailCode").textContent = `Kodas: #${data.id}`;

    panel?.activateVariant?.("task-edit");
    applyFieldRestrictions(modalEl, data.technicianId, editable);

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    if (saveBtn) {
      if (isBulkDraftLiveEditMode(modalEl)) {
        saveBtn.classList.add("d-none");
        saveBtn.onclick = null;
      } else {
        saveBtn.classList.toggle("d-none", !editable);
        saveBtn.textContent = "Išsaugoti";
        saveBtn.onclick = onSave;
      }
    }
  };

  const prepareCreateMode = async (modalEl, {
    technicianId,
    targetDate,
    startMinutes,
    endMinutes,
    allowEmptySchedule = false,
    onSave,
  }) => {
    // Populate field cache for performance (reduces 40+ queries to 1 lookup)
    populateFieldCache(modalEl);

    modalEl.dataset.mode = "create";
    modalEl.dataset.taskId = "";
    modalEl.dataset.editable = "true";
    modalEl.dataset.technicianId = technicianId;
    modalEl.dataset.targetDate = targetDate;
    modalEl.dataset.startMinutes = startMinutes;
    modalEl.dataset.endMinutes = endMinutes;

    const setVal = (selector, value) => {
      const el = modalEl.querySelector(selector);
      if (!el) return;
      el.value = value ?? "";
    };

    setVal("#taskDetailTitle", "");
    setVal("#taskDetailStart", "");
    setVal("#taskDetailEnd", "");
    setVal("#taskDetailPriority", "");
    setVal("#taskDetailStatus", "");
    setVal("#taskDetailSkill", "");
    syncInstructionFields(modalEl, "");
    await populateObjectDropdown(modalEl, null, null, null);
    await populateAddressDropdown(modalEl, null);
    await populateTechnicianDropdown(modalEl, technicianId);
    scheduleAssigneeTravelPreview(modalEl);
    ensureObjectAddressBinding(modalEl);

    modalEl.querySelector("#taskDetailLabel").textContent = "Nauja užduotis";
    modalEl.querySelector("#taskDetailCode").textContent = "";

    // Resolve times: use the selected slot when present.
    // For backlog-origin planning we keep scheduling blank so the user maps it manually.
    // Other create entry points may still fall back to the rounded local time.
    let resolvedDate = targetDate;
    let resolvedStart = startMinutes;
    let resolvedEnd = endMinutes;

    if (!allowEmptySchedule && (!resolvedDate || resolvedStart == null || resolvedEnd == null)) {
      const placement = window.schedule?.backlog?.state?.getEarliestCreatableRangeForScheduleDate?.();
      if (placement) {
        resolvedDate = resolvedDate ?? placement.targetDate;
        resolvedStart = resolvedStart ?? placement.startMinutes;
        resolvedEnd = resolvedEnd ?? placement.endMinutes;
      } else {
        const nowLocal = new Date();
        const slotSize = grid.getConfig?.()?.slotSizeMinutes ?? 30;
        const nowMins = nowLocal.getHours() * 60 + nowLocal.getMinutes();
        const snappedStart = Math.ceil(nowMins / slotSize) * slotSize;
        resolvedDate   = resolvedDate   ?? nowLocal.toISOString().slice(0, 10);
        resolvedStart  = resolvedStart  ?? snappedStart;
        resolvedEnd    = resolvedEnd    ?? (snappedStart + slotSize);
      }
    }

    if (resolvedDate && resolvedStart != null && resolvedEnd != null) {
      const startTime = grid.minutesToTime(resolvedStart);
      const endTime   = grid.minutesToTime(resolvedEnd);
      setVal("#taskDetailStart", `${resolvedDate}T${startTime}`);
      setVal("#taskDetailEnd",   `${resolvedDate}T${endTime}`);
    }

    const startInput = modalEl.querySelector("#taskDetailStart");
    const endInput = modalEl.querySelector("#taskDetailEnd");
    if (startInput) startInput.setAttribute("step", "1800");
    if (endInput) endInput.setAttribute("step", "1800");

    const inputs = modalEl.querySelectorAll("input, select, textarea");
    inputs.forEach((el) => el.removeAttribute("disabled"));

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    if (saveBtn) {
      if (isBulkDraftLiveEditMode(modalEl)) {
        saveBtn.classList.add("d-none");
        saveBtn.onclick = null;
      } else {
        saveBtn.classList.remove("d-none");
        saveBtn.textContent = "Sukurti";
        saveBtn.onclick = onSave;
      }
    }

    panel?.activateVariant?.("task-create");
    applyFieldRestrictions(modalEl, technicianId, true);
    bindCreatePlacementSync(modalEl);
    modalEl._syncCreatePlacementFromForm?.();
  };

  // ========== Field Restrictions ==========

  const applyFieldRestrictions = (modalEl, taskTechnicianId, editable) => {
    const viewModel = window.scheduleViewModel || {};
    const viewModelManagerFlag =
      typeof viewModel.isManager === "string"
        ? viewModel.isManager === "true"
        : !!viewModel.isManager;
    const dataAttrManagerFlag = tables?.dataset?.isManager === "true";
    const isManager = viewModelManagerFlag || dataAttrManagerFlag;
    const currentUserId = viewModel.currentUserId;

    const isTechnician = !isManager;
    const paramsSection = modalEl.querySelector("#taskSecParams")?.closest("section");
    const instructionsInput = modalEl.querySelector("#taskDetailInstructions");
    const instructionsReadonly = modalEl.querySelector("#taskDetailInstructionsReadonly");
    const statusField = modalEl.querySelector("#taskDetailStatus");
    const commentField = modalEl.querySelector("#taskDetailTechnicianComment");
    const deleteBtn = modalEl.querySelector("#taskDetailDelete");
    const isOwnTask =
      currentUserId != null &&
      taskTechnicianId != null &&
      String(taskTechnicianId) === String(currentUserId);

    const readonlyNote = modalEl.querySelector("#taskDetailReadonlyNote");
    if (readonlyNote) {
      if (!editable) {
        readonlyNote.classList.remove("d-none");
        readonlyNote.textContent = "Užduoties redaguoti nebegalima (plano / laiko apribojimas).";
      } else {
        readonlyNote.classList.add("d-none");
      }
    }

    const assigneeSelect = modalEl.querySelector("#taskDetailAssignee");
    const objectSelect = modalEl.querySelector("#taskDetailObject");
    const addressSelect = modalEl.querySelector("#taskDetailLocation");

    if (isTechnician) {
      if (paramsSection) paramsSection.classList.add("d-none");

      if (instructionsInput) {
        instructionsInput.classList.add("d-none");
        instructionsInput.setAttribute("disabled", "disabled");
        instructionsInput.setAttribute("readonly", "readonly");
      }
      if (instructionsReadonly) instructionsReadonly.classList.remove("d-none");

      const allInputs = modalEl.querySelectorAll("input, select, textarea");
      allInputs.forEach((el) => {
        el.setAttribute("disabled", "disabled");
        el.classList.add("bg-light");
      });

      setSelect2Disabled(assigneeSelect, true);
      setSelect2Disabled(objectSelect, true);
      setSelect2Disabled(addressSelect, true);

      const canEdit = isOwnTask && editable;
      if (canEdit) {
        if (statusField) {
          statusField.removeAttribute("disabled");
          statusField.classList.remove("bg-light");
        }
        if (commentField) {
          commentField.removeAttribute("disabled");
          commentField.classList.remove("bg-light");
        }
      }

      const saveBtn = modalEl.querySelector("#taskDetailSave");
      if (saveBtn) {
        if (!isOwnTask) {
          saveBtn.setAttribute("disabled", "disabled");
          saveBtn.title = "Galite redaguoti tik savo Užduotis";
        } else if (!editable) {
          saveBtn.setAttribute("disabled", "disabled");
          saveBtn.title = "Užduotis nebegali būti redaguojama";
        } else {
          saveBtn.removeAttribute("disabled");
          saveBtn.title = "";
        }
      }

      if (deleteBtn) {
        deleteBtn.classList.add("d-none");
      }

    } else {
      if (paramsSection) paramsSection.classList.remove("d-none");
      if (instructionsInput) {
        instructionsInput.classList.remove("d-none");
        instructionsInput.removeAttribute("readonly");
      }
      if (instructionsReadonly) instructionsReadonly.classList.add("d-none");

      const inputs = modalEl.querySelectorAll("input, select, textarea");
      inputs.forEach((el) => {
        if (el === statusField || el === commentField) return;
        if (editable) {
          el.removeAttribute("disabled");
          el.classList.remove("bg-light");
        } else {
          el.setAttribute("disabled", "disabled");
          el.classList.add("bg-light");
        }
      });

      setSelect2Disabled(assigneeSelect, !editable);
      setSelect2Disabled(objectSelect, !editable);
      setSelect2Disabled(addressSelect, !editable);

      if (statusField) {
        statusField.setAttribute("disabled", "disabled");
        statusField.classList.add("bg-light");
      }
      if (commentField) {
        commentField.setAttribute("disabled", "disabled");
        commentField.classList.add("bg-light");
      }

      if (deleteBtn) {
        const isCreateMode = modalEl.dataset.mode === "create";
        deleteBtn.classList.toggle("d-none", !editable || isCreateMode);
      }
    }
  };

  // ========== Validation ==========

  const clearFieldErrors = (modalEl) => {
    modalEl.querySelectorAll("[data-field-error]").forEach((el) => el.remove());
    modalEl.querySelectorAll(".is-invalid").forEach((el) => el.classList.remove("is-invalid"));
  };

  const setFieldError = (modalEl, selector, message) => {
    const field = modalEl.querySelector(selector);
    if (!field) return;
    field.classList.add("is-invalid");
    const helper = document.createElement("div");
    helper.className = "form-text text-danger";
    helper.setAttribute("data-field-error", selector);
    helper.textContent = message;
    const parent = field.closest(".col-md-6, .col-12, .col");
    if (parent) {
      parent.appendChild(helper);
    } else {
      field.insertAdjacentElement("afterend", helper);
    }
  };

  const displayValidationErrors = (modalEl, validationErrors, fieldMapping) => {
    let hasFieldError = false;
    for (const [field, messages] of Object.entries(validationErrors)) {
      const selector = fieldMapping[field];
      const message = Array.isArray(messages) ? messages[0] : messages;
      if (selector && message) {
        setFieldError(modalEl, selector, message);
        hasFieldError = true;
      }
    }

    if (!hasFieldError) {
      const firstError = Object.values(validationErrors).flat()[0];
      alert(firstError || "Patikrinkite įvestus duomenis.");
    }
  };

  // ========== Form Data Collection ==========

  const collectFormData = (modalEl) => {
    const title = modalEl.querySelector("#taskDetailTitle")?.value || "";
    const location = modalEl.querySelector("#taskDetailLocation")?.value || "";
    const startAt = modalEl.querySelector("#taskDetailStart")?.value || "";
    const endAt = modalEl.querySelector("#taskDetailEnd")?.value || "";
    const priorityRaw = modalEl.querySelector("#taskDetailPriority")?.value || "";
    const status = modalEl.querySelector("#taskDetailStatus")?.value || "";
    const selectedTechVal = modalEl.querySelector("#taskDetailAssignee")?.value || "";

    const skillLevel = modalEl.querySelector("#taskDetailSkill")?.value || null;
    const selectedObjectOption = modalEl.querySelector("#taskDetailObject option:checked");
    const selectedObjectValue = selectedObjectOption?.value || "";
    const hasSelectedObject = selectedObjectValue !== "";
    const objectName = hasSelectedObject ? selectedObjectOption.textContent?.trim() || null : null;
    const instructions = modalEl.querySelector("#taskDetailInstructions")?.value || null;
    const objectAddress = hasSelectedObject
      ? (selectedObjectOption?.dataset?.address || location || null)
      : null;
    const technicianId = selectedTechVal
      ? parseInt(selectedTechVal, 10)
      : parseInt(modalEl.dataset.technicianId || "0", 10);
    const priorityVal = parseInt(priorityRaw, 10);
    const technicianComment = modalEl.querySelector("#taskDetailTechnicianComment")?.value || null;

    return {
      title,
      location,
      startAt,
      endAt,
      priorityRaw,
      priorityVal,
      status,
      technicianId,
      skillLevel,
      objectId: hasSelectedObject ? selectedObjectValue : null,
      objectName,
      objectAddress,
      instructions,
      technicianComment,
      hasSelectedObject,
    };
  };

  const validateTaskFormData = (modalEl, formData, isManager, { skipScheduling = false } = {}) => {
    clearFieldErrors(modalEl);
    const hasSuspiciousInput = (val) => /<|>|script/i.test(val || "");

    if (!formData.title.trim()) {
      setFieldError(modalEl, "#taskDetailTitle", "Pavadinimas yra privalomas.");
    } else if (hasSuspiciousInput(formData.title)) {
      setFieldError(modalEl, "#taskDetailTitle", "Netinkami simboliai.");
    }
    if (hasSuspiciousInput(formData.location)) {
      setFieldError(modalEl, "#taskDetailLocation", "Netinkami simboliai.");
    }
    if (hasSuspiciousInput(formData.instructions || "")) {
      setFieldError(modalEl, "#taskDetailInstructions", "Netinkami simboliai.");
    }
    if (isManager) {
      if (!skipScheduling && (!formData.technicianId || Number.isNaN(formData.technicianId))) {
        setFieldError(modalEl, "#taskDetailAssignee", "Priskirtas darbuotojas yra privalomas.");
      }
      if (!formData.skillLevel || !String(formData.skillLevel).trim()) {
        setFieldError(modalEl, "#taskDetailSkill", "Reikalingas kvalifikacijos lygis yra privalomas.");
      }
      if (formData.priorityRaw === "" || Number.isNaN(formData.priorityVal)) {
        setFieldError(modalEl, "#taskDetailPriority", "Prioritetas yra privalomas.");
      }
      if (!formData.hasSelectedObject) {
        setFieldError(modalEl, "#taskDetailObject", "Objektas yra privalomas.");
      }
      if (!formData.location.trim()) {
        setFieldError(modalEl, "#taskDetailLocation", "Adresas / lokacija yra privaloma.");
      }
    }

    return modalEl.querySelectorAll("[data-field-error]").length === 0;
  };

  const validateBacklogFormData = (modalEl, formData) =>
    validateTaskFormData(modalEl, formData, true, { skipScheduling: true });

  const validateBulkFormData = (modalEl, formData) =>
    validateTaskFormData(modalEl, formData, true, { skipScheduling: false });

  const validateFormData = (modalEl, formData, isManager, options = {}) => {
    const variant = getActiveVariant(modalEl);
    if (variant === "backlog-create") return validateBacklogFormData(modalEl, formData);
    if (variant === "backlog-bulk-edit") return validateBulkFormData(modalEl, formData);
    return validateTaskFormData(modalEl, formData, isManager, options);
  };

  // ========== Field Mappings ==========

  const CREATE_FIELD_MAPPING = {
    Title: "#taskDetailTitle",
    Location: "#taskDetailLocation",
    TechnicianId: "#taskDetailAssignee",
    TargetDate: "#taskDetailStart",
    StartMinutes: "#taskDetailStart",
    EndMinutes: "#taskDetailEnd",
    Priority: "#taskDetailPriority",
    Status: "#taskDetailStatus",
    SkillLevel: "#taskDetailSkill",
    ObjectName: "#taskDetailObject",
    ObjectAddress: "#taskDetailLocation",
    Instructions: "#taskDetailInstructions"
  };

  const UPDATE_FIELD_MAPPING = {
    Title: "#taskDetailTitle",
    Location: "#taskDetailLocation",
    TechnicianId: "#taskDetailAssignee",
    StartAt: "#taskDetailStart",
    EndAt: "#taskDetailEnd",
    Priority: "#taskDetailPriority",
    Status: "#taskDetailStatus",
    SkillLevel: "#taskDetailSkill",
    ObjectName: "#taskDetailObject",
    ObjectAddress: "#taskDetailLocation",
    Instructions: "#taskDetailInstructions",
    TechnicianComment: "#taskDetailTechnicianComment",
    Version: null
  };

  // ========== Panel Time Update (used by task.resize after saving new times) ==========

  /**
   * Update only the start/end time fields in an open panel for a specific task.
   * Uses the modalFields cache. Also syncs the version on the panel dataset.
   * @param {string|number} taskId
   * @param {string} startAt  - ISO datetime string
   * @param {string} endAt    - ISO datetime string
   * @param {string|number} [newVersion]
   */
  const updatePanelTimes = (taskId, startAt, endAt, newVersion) => {
    const detailPanel = document.getElementById("taskDetailModal");
    if (!detailPanel?.classList?.contains("show")) return;
    if (String(detailPanel.dataset.taskId || "") !== String(taskId || "")) return;
    populateFieldCache(detailPanel);
    if (modalFields.start && startAt) modalFields.start.value = String(startAt).substring(0, 16);
    if (modalFields.end && endAt) modalFields.end.value = String(endAt).substring(0, 16);
    if (newVersion != null) detailPanel.dataset.version = String(newVersion);
  };

  // ========== Exports ==========

  schedule.tasks.modal = {
    populateTaskModal,
    prepareCreateMode,
    applyFieldRestrictions,
    ensureObjectAddressBinding,
    clearFieldErrors,
    setFieldError,
    displayValidationErrors,
    collectFormData,
    validateFormData,
    validateTaskFormData,
    validateBacklogFormData,
    validateBulkFormData,
    syncInstructionFields,
    CREATE_FIELD_MAPPING,
    UPDATE_FIELD_MAPPING,
    populateFieldCache,
    clearFieldCache,
    getField: (key) => modalFields[key],
    updatePanelTimes,
    collectCreatePlacement,
    populateObjectDropdown,
    populateAddressDropdown,
    scheduleSuggestSlot,
  };
})();
