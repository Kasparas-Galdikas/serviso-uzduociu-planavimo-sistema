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
        inputTooShort: (args) => `Įveskite bent ${args.minimum} simbolius`,
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

  const populateTechnicianDropdown = async (modalEl, selectedTechnicianId) => {
    const dropdown = modalEl.querySelector("#taskDetailAssignee");
    if (!dropdown) return;

    destroySelect2(dropdown);

    try {
      const technicians = await loadTechnicians();

      dropdown.innerHTML = "";

      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "";
      dropdown.appendChild(placeholder);

      technicians.forEach((tech) => {
        const option = document.createElement("option");
        option.value = tech.id;
        option.textContent = tech.region ? `${tech.name} (${tech.region})` : tech.name;
        if (tech.id === selectedTechnicianId) {
          option.selected = true;
        }
        dropdown.appendChild(option);
      });

      initSelect2(dropdown, "Pasirinkite darbuotoją...");
    } catch (err) {
      console.error("Error loading technicians:", err);
    }
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
      if (el) el.value = value ?? "";
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
    populateTechnicianDropdown(modalEl, data.technicianId);

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

    applyFieldRestrictions(modalEl, data.technicianId, editable);

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    if (saveBtn) {
      saveBtn.classList.toggle("d-none", !editable);
      saveBtn.textContent = "Išsaugoti";
      saveBtn.onclick = onSave;
    }
  };

  const prepareCreateMode = async (modalEl, { technicianId, targetDate, startMinutes, endMinutes, onSave }) => {
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
      if (el) el.value = value ?? "";
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
    ensureObjectAddressBinding(modalEl);

    modalEl.querySelector("#taskDetailLabel").textContent = "Nauja užduotis";
    modalEl.querySelector("#taskDetailCode").textContent = "";

    const minutesToTime = (m) => {
      const h = String(Math.floor(m / 60)).padStart(2, "0");
      const min = String(m % 60).padStart(2, "0");
      return `${h}:${min}`;
    };

    if (targetDate && startMinutes != null && endMinutes != null) {
      const dateStr = targetDate;
      const startTime = minutesToTime(startMinutes);
      const endTime = minutesToTime(endMinutes);

      setVal("#taskDetailStart", `${dateStr}T${startTime}`);
      setVal("#taskDetailEnd", `${dateStr}T${endTime}`);
    }

    const inputs = modalEl.querySelectorAll("input, select, textarea");
    inputs.forEach((el) => el.removeAttribute("disabled"));

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    if (saveBtn) {
      saveBtn.classList.remove("d-none");
      saveBtn.textContent = "Sukurti";
      saveBtn.onclick = onSave;
    }

    applyFieldRestrictions(modalEl, technicianId, true);
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
          saveBtn.title = "Galite redaguoti tik savo užduotis";
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
      objectName,
      objectAddress,
      instructions,
      technicianComment,
      hasSelectedObject,
    };
  };

  const validateFormData = (modalEl, formData, isManager) => {
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
      if (!formData.technicianId || Number.isNaN(formData.technicianId)) {
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
    syncInstructionFields,
    CREATE_FIELD_MAPPING,
    UPDATE_FIELD_MAPPING,
    populateFieldCache,
    clearFieldCache,
    getField: (key) => modalFields[key],
  };
})();