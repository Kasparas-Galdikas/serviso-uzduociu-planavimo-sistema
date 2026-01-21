(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const panel = schedule.panel;
  const selection = schedule.selection;

  const logger = window.scheduleLogger;
  const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);
  const formatRange = (startMinutes, endMinutes) => {
    const fmt = (m) => `${String(Math.floor(m / 60)).padStart(2, "0")}:${String(m % 60).padStart(2, "0")}`;
    return `${fmt(startMinutes)}–${fmt(endMinutes)}`;
  };

  // Select2 initialization helper
  const initSelect2 = (selectEl, placeholder) => {
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);

    // Destroy existing instance if any
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.select2("destroy");
    }

    // Get the parent offcanvas for dropdown positioning
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
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.select2("destroy");
    }
  };

  const setSelect2Value = (selectEl, value) => {
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.val(value).trigger("change.select2");
    }
  };

  const setSelect2Disabled = (selectEl, disabled) => {
    if (!selectEl || typeof $ === "undefined" || !$.fn.select2) return;
    const $select = $(selectEl);
    if ($select.hasClass("select2-hidden-accessible")) {
      $select.prop("disabled", disabled);
    }
  };

  let objectsPromise = null;
  const loadObjects = async () => {
    if (objectsPromise) return objectsPromise;
    objectsPromise = (async () => {
      const resp = await fetch("/api/objects", { headers: { "X-Requested-With": "XMLHttpRequest" } });
      if (!resp.ok) {
        console.error("Failed to load objects");
        return [];
      }
      return await resp.json();
    })();
    return objectsPromise;
  };

  const populateObjectDropdown = async (modalEl, selectedObjectId, fallbackObjectName, fallbackAddress) => {
    const select = modalEl.querySelector("#taskDetailObject");
    if (!select) return;

    // Destroy existing Select2 before repopulating
    destroySelect2(select);

    const objects = (await loadObjects())
      // Drop any accidentally created placeholder objects (legacy data)
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

    // If no match but we have a name/address, add it as a temporary option
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

    // Initialize Select2
    initSelect2(select, "Pasirinkite objektą...");
  };

  const populateAddressDropdown = async (modalEl, selectedAddress) => {
    const select = modalEl.querySelector("#taskDetailLocation");
    if (!select) return;

    // Destroy existing Select2 before repopulating
    destroySelect2(select);

    const objects = await loadObjects();
    select.innerHTML = "";

    // Add placeholder
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "";
    select.appendChild(placeholder);

    // Build address-to-object mapping (first object wins for each address)
    const addressToObject = new Map();
    objects.forEach((obj) => {
      if (obj.address && obj.address.trim()) {
        const addr = obj.address.trim();
        if (!addressToObject.has(addr)) {
          addressToObject.set(addr, obj.id);
        }
      }
    });

    // Add address options with object ID stored
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

    // If selected address not in list, add it
    if (selectedAddress && !addressToObject.has(selectedAddress)) {
      const custom = document.createElement("option");
      custom.value = selectedAddress;
      custom.textContent = selectedAddress;
      custom.selected = true;
      select.appendChild(custom);
    }

    // Initialize Select2
    initSelect2(select, "Pasirinkite adresą...");
  };

  const clearActiveCards = () => {
    document.querySelectorAll(".task-card.is-active").forEach((el) => {
      el.classList.remove("is-active");
    });
  };

  const setActiveCard = (card) => {
    if (!card) return;
    clearActiveCards();
    card.classList.add("is-active");
  };

  const setActiveCardByTaskId = (taskId) => {
    if (!taskId) return;
    const card = document.querySelector(`.task-card[data-task-id="${CSS.escape(String(taskId))}"]`);
    if (card) setActiveCard(card);
  };

  const ensureObjectAddressBinding = (modalEl) => {
    const objectSelect = modalEl.querySelector("#taskDetailObject");
    const addressSelect = modalEl.querySelector("#taskDetailLocation");

    // Skip if already bound or elements missing
    if (!objectSelect || !addressSelect) return;
    if (objectSelect.dataset.objectAddressBinding === "1") return;
    objectSelect.dataset.objectAddressBinding = "1";

    // Flag to prevent infinite loops
    let isUpdating = false;

    // Object → Address: when object changes, update address
    const updateAddressFromObject = () => {
      if (isUpdating) return;
      isUpdating = true;
      const selected = objectSelect.selectedOptions[0];
      const address = selected?.dataset?.address || "";
      setSelect2Value(addressSelect, address);
      isUpdating = false;
    };

    // Address → Object: when address changes, update object
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

    // Bind object → address
    objectSelect.addEventListener("change", updateAddressFromObject);
    if (typeof $ !== "undefined" && $.fn.select2) {
      $(objectSelect).on("select2:select select2:clear", updateAddressFromObject);
    }

    // Bind address → object
    addressSelect.addEventListener("change", updateObjectFromAddress);
    if (typeof $ !== "undefined" && $.fn.select2) {
      $(addressSelect).on("select2:select select2:clear", updateObjectFromAddress);
    }
  };

  const fetchTask = async (taskId) => {
    // Include timezone offset in request headers so server can convert UTC times to local
    const timezoneOffset = -new Date().getTimezoneOffset();

    const resp =
      (await logger?.fetchWithTrace?.(`/api/tasks/${taskId}`, {
        headers: { "X-Timezone-Offset": String(timezoneOffset) }
      }, { event: "ui.task.fetch", taskId })) ||
      (await fetch(`/api/tasks/${taskId}`, {
        headers: {
          "X-Requested-With": "XMLHttpRequest",
          "X-Timezone-Offset": String(timezoneOffset)
        }
      }));

    if (!resp.ok) {
      throw new Error("Nepavyko gauti uzduoties duomenu");
    }

    return await resp.json();
  };

  const populateTechnicianDropdown = async (modalEl, selectedTechnicianId) => {
    const dropdown = modalEl.querySelector("#taskDetailAssignee");
    if (!dropdown) return;

    // Destroy existing Select2 before repopulating
    destroySelect2(dropdown);

    try {
      const resp = await fetch("/api/technicians", {
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });

      if (!resp.ok) {
        console.error("Failed to load technicians");
        return;
      }

      const technicians = await resp.json();

      // Clear existing options
      dropdown.innerHTML = "";

      // Add placeholder option (empty for Select2)
      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "";
      dropdown.appendChild(placeholder);

      // Add technician options
      technicians.forEach((tech) => {
        const option = document.createElement("option");
        option.value = tech.id;
        option.textContent = tech.region ? `${tech.name} (${tech.region})` : tech.name;
        if (tech.id === selectedTechnicianId) {
          option.selected = true;
        }
        dropdown.appendChild(option);
      });

      // Initialize Select2
      initSelect2(dropdown, "Pasirinkite darbuotoją...");
    } catch (err) {
      console.error("Error loading technicians:", err);
    }
  };

  const syncInstructionFields = (modalEl, instructionsText) => {
    const instructions = instructionsText || "";
    const instructionsInput = modalEl.querySelector("#taskDetailInstructions");
    if (instructionsInput) instructionsInput.value = instructions;

    const instructionsReadonly = modalEl.querySelector("#taskDetailInstructionsReadonly");
    if (instructionsReadonly) instructionsReadonly.textContent = instructions || "-";
  };

  const populateTaskModal = async (modalEl, data, editable) => {
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

    // Populate dropdowns
    await populateObjectDropdown(modalEl, data.objectId, data.objectName, data.objectAddress);
    const selectedAddress = data.objectAddress || data.location;
    await populateAddressDropdown(modalEl, selectedAddress);
    ensureObjectAddressBinding(modalEl);

    syncInstructionFields(modalEl, data.instructions);

    // Populate technician dropdown for managers
    populateTechnicianDropdown(modalEl, data.technicianId);

    // Set to UPDATE mode
    modalEl.dataset.mode = "update";
    modalEl.dataset.taskId = data.id;
    modalEl.dataset.editable = editable ? "true" : "false";
    modalEl.dataset.technicianId = data.technicianId;
    // Store version from response for optimistic concurrency control
    if (data.version != null) modalEl.dataset.version = String(data.version);
    // Clear create-mode data
    delete modalEl.dataset.targetDate;
    delete modalEl.dataset.startMinutes;
    delete modalEl.dataset.endMinutes;

    // Update modal title
    modalEl.querySelector("#taskDetailLabel").textContent = "Užduoties informacija";
    modalEl.querySelector("#taskDetailCode").textContent = `Kodas: #${data.id}`;

    // Apply role-based field restrictions
    applyFieldRestrictions(modalEl, data.technicianId, editable);

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    if (saveBtn) {
      saveBtn.classList.toggle("d-none", !editable);
      saveBtn.textContent = "Išsaugoti";
      saveBtn.onclick = async () => {
        await saveTask(modalEl);
      };
    }
  };

  const applyFieldRestrictions = (modalEl, taskTechnicianId, editable) => {
    const tables = document.getElementById("scheduleTables");
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

    // Toggle readonly notice for clarity (time/plan restrictions)
    const readonlyNote = modalEl.querySelector("#taskDetailReadonlyNote");
    if (readonlyNote) {
      if (!editable) {
        readonlyNote.classList.remove("d-none");
        readonlyNote.textContent = "Užduoties redaguoti nebegalima (plano / laiko apribojimas).";
      } else {
        readonlyNote.classList.add("d-none");
      }
    }

    // Debug: Log to browser console
    console.log("Field restrictions:", {
      isManager,
      isTechnician,
      currentUserId,
      taskTechnicianId,
      isOwnTask,
      editable
    });

    // Select2 dropdowns need special handling
    const assigneeSelect = modalEl.querySelector("#taskDetailAssignee");
    const objectSelect = modalEl.querySelector("#taskDetailObject");
    const addressSelect = modalEl.querySelector("#taskDetailLocation");

    if (isTechnician) {
      // Hide parameters from technicians
      if (paramsSection) paramsSection.classList.add("d-none");

      // Show read-only instructions, hide textarea
      if (instructionsInput) {
        instructionsInput.classList.add("d-none");
        instructionsInput.setAttribute("disabled", "disabled");
        instructionsInput.setAttribute("readonly", "readonly");
      }
      if (instructionsReadonly) instructionsReadonly.classList.remove("d-none");

      // Technician: First disable ALL inputs
      const allInputs = modalEl.querySelectorAll("input, select, textarea");
      allInputs.forEach((el) => {
        el.setAttribute("disabled", "disabled");
        el.classList.add("bg-light");
      });

      // Disable Select2 dropdowns
      setSelect2Disabled(assigneeSelect, true);
      setSelect2Disabled(objectSelect, true);
      setSelect2Disabled(addressSelect, true);

      // Enable technician fields ONLY if it's their task and editable
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

      // Update save button state
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
      // Manager view: show parameters and editable instructions
      if (paramsSection) paramsSection.classList.remove("d-none");
      if (instructionsInput) {
        instructionsInput.classList.remove("d-none");
        instructionsInput.removeAttribute("readonly");
      }
      if (instructionsReadonly) instructionsReadonly.classList.add("d-none");

      // Manager: Enable ALL fields if editable
      const inputs = modalEl.querySelectorAll("input, select, textarea");
      inputs.forEach((el) => {
        // Status / comment should always be read-only for manager
        if (el === statusField || el === commentField) return;
        if (editable) {
          el.removeAttribute("disabled");
          el.classList.remove("bg-light");
        } else {
          el.setAttribute("disabled", "disabled");
          el.classList.add("bg-light");
        }
      });

      // Handle Select2 dropdowns for manager
      setSelect2Disabled(assigneeSelect, !editable);
      setSelect2Disabled(objectSelect, !editable);
      setSelect2Disabled(addressSelect, !editable);

      // Keep technician-specific fields read-only
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

  const saveTask = async (modalEl) => {
    const mode = modalEl.dataset.mode || "update";
    const taskId = modalEl.dataset.taskId;
    const tables = document.getElementById("scheduleTables");
    const viewModel = window.scheduleViewModel || {};
    const viewModelManagerFlag =
      typeof viewModel.isManager === "string"
        ? viewModel.isManager === "true"
        : !!viewModel.isManager;
    const isManager = viewModelManagerFlag || tables?.dataset?.isManager === "true";

    // Validate mode
    if (mode === "update" && !taskId) {
      alert("Nepavyko nustatyti uzduoties ID.");
      return;
    }

    // Collect form data
    const title = modalEl.querySelector("#taskDetailTitle")?.value || "";
    const location = modalEl.querySelector("#taskDetailLocation")?.value || "";
    const startAt = modalEl.querySelector("#taskDetailStart")?.value || "";
    const endAt = modalEl.querySelector("#taskDetailEnd")?.value || "";
    const priorityRaw = modalEl.querySelector("#taskDetailPriority")?.value || "";
    const status = modalEl.querySelector("#taskDetailStatus")?.value || "";
    const selectedTechVal = modalEl.querySelector("#taskDetailAssignee")?.value || "";

    // Additional fields
    const skillLevel = modalEl.querySelector("#taskDetailSkill")?.value || null;
    const selectedObjectOption = modalEl.querySelector("#taskDetailObject option:checked");
    const selectedObjectValue = selectedObjectOption?.value || "";
    const hasSelectedObject = selectedObjectValue !== "";
    const objectName = hasSelectedObject ? selectedObjectOption.textContent?.trim() || null : null;
    const instructions = modalEl.querySelector("#taskDetailInstructions")?.value || null;
    // Only send an object address when an object is actually selected; otherwise we end up creating new objects from the placeholder.
    const objectAddress = hasSelectedObject
      ? (selectedObjectOption?.dataset?.address || location || null)
      : null;
    const technicianId = selectedTechVal
      ? parseInt(selectedTechVal, 10)
      : parseInt(modalEl.dataset.technicianId || "0", 10);
    const priorityVal = parseInt(priorityRaw, 10);

    // Validate required fields
    clearFieldErrors(modalEl);
    const hasSuspiciousInput = (val) => /<|>|script/i.test(val || "");
    if (!title.trim()) {
      setFieldError(modalEl, "#taskDetailTitle", "Pavadinimas yra privalomas.");
    } else if (hasSuspiciousInput(title)) {
      setFieldError(modalEl, "#taskDetailTitle", "Netinkami simboliai.");
    }
    if (hasSuspiciousInput(location)) {
      setFieldError(modalEl, "#taskDetailLocation", "Netinkami simboliai.");
    }
    if (hasSuspiciousInput(instructions || "")) {
      setFieldError(modalEl, "#taskDetailInstructions", "Netinkami simboliai.");
    }
    if (isManager) {
      if (!technicianId || Number.isNaN(technicianId)) {
        setFieldError(modalEl, "#taskDetailAssignee", "Priskirtas darbuotojas yra privalomas.");
      }
      if (!skillLevel || !String(skillLevel).trim()) {
        setFieldError(modalEl, "#taskDetailSkill", "Reikalingas kvalifikacijos lygis yra privalomas.");
      }
      if (priorityRaw === "" || Number.isNaN(priorityVal)) {
        setFieldError(modalEl, "#taskDetailPriority", "Prioritetas yra privalomas.");
      }
      if (!hasSelectedObject) {
        setFieldError(modalEl, "#taskDetailObject", "Objektas yra privalomas.");
      }
      if (!location.trim()) {
        setFieldError(modalEl, "#taskDetailLocation", "Adresas / lokacija yra privaloma.");
      }
    }
    if (modalEl.querySelectorAll("[data-field-error]").length > 0) {
      return;
    }

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    const setBusy = (state) => {
      if (saveBtn) {
        saveBtn.disabled = state;
        saveBtn.setAttribute("aria-busy", state ? "true" : "false");
      }
    };

    setBusy(true);
    try {
      let result;

    if (mode === "create") {
      // CREATE MODE - call submitCreateTask from selection module
        const targetDate = modalEl.dataset.targetDate;
        const startMinutes = parseInt(modalEl.dataset.startMinutes);
        const endMinutes = parseInt(modalEl.dataset.endMinutes);

        const formData = {
          technicianId,
          targetDate,
          startMinutes,
          endMinutes,
          title,
          location,
          priority: Number.isNaN(priorityVal) ? 1 : priorityVal,
          status: parseInt(status) || 0,
          skillLevel,
          objectName,
          objectAddress,
          instructions
        };

        result = await window.schedule?.selection?.submitCreateTask?.(formData);

        if (result?.success) {
          // Close modal
          const bsModal = window.bootstrap?.Offcanvas?.getInstance?.(modalEl);
          bsModal?.hide?.();
        } else if (result?.validationErrors) {
          // Display validation errors from DTO on form fields
          const fieldMapping = {
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

          let hasFieldError = false;
          for (const [field, messages] of Object.entries(result.validationErrors)) {
            const selector = fieldMapping[field];
            const message = Array.isArray(messages) ? messages[0] : messages;
            if (selector && message) {
              setFieldError(modalEl, selector, message);
              hasFieldError = true;
            }
          }

          if (!hasFieldError) {
            const firstError = Object.values(result.validationErrors).flat()[0];
            alert(firstError || "Patikrinkite įvestus duomenis.");
          }
        } else if (result?.error) {
          alert(result.error);
        }
    } else {
      // UPDATE MODE - Route to correct endpoint based on role
      const tables = document.getElementById("scheduleTables");
      const isManager = tables?.dataset?.isManager === "true";
      const version = modalEl.dataset.version || 0;
      const timezoneOffset = -new Date().getTimezoneOffset();
      const selectedTechVal = modalEl.querySelector("#taskDetailAssignee")?.value;
      const technicianId = selectedTechVal
        ? parseInt(selectedTechVal, 10)
        : parseInt(modalEl.dataset.technicianId || "0", 10);

        let endpoint, requestBody;

      if (isManager) {
        // Manager: Use full update endpoint with all fields
        endpoint = `/api/tasks/${taskId}`;
        requestBody = JSON.stringify({
          title,
          location,
          startAt,
          endAt,
          priority: Number.isNaN(priorityVal) ? 1 : priorityVal,
          status: parseInt(status) || 0,
          version: parseInt(version) || 0,
          timezoneOffsetMinutes: timezoneOffset,
          technicianId: technicianId || 0,
          skillLevel,
          objectName,
          objectAddress,
          instructions,
          technicianComment: modalEl.querySelector("#taskDetailTechnicianComment")?.value || null
        });
        } else {
          // Technician: Use limited update endpoint with only Status and Comment
          endpoint = `/api/tasks/${taskId}/technician-update`;
          requestBody = JSON.stringify({
            status: parseInt(status) || 0,
            comment: modalEl.querySelector("#taskDetailTechnicianComment")?.value || "",
            version: parseInt(version) || 0,
            timezoneOffsetMinutes: timezoneOffset,
          });
        }

        const resp =
          (await logger?.fetchWithTrace(
            endpoint,
            {
              method: "PATCH",
              headers: { "Content-Type": "application/json" },
              body: requestBody,
            },
            { event: "ui.task.save", taskId, role: isManager ? "manager" : "technician" },
          )) ||
          (await fetch(endpoint, {
            method: "PATCH",
            headers: {
              "X-Requested-With": "XMLHttpRequest",
              "Content-Type": "application/json",
            },
            body: requestBody,
          }));

        if (resp.status === 409 || resp.status === 403) {
          const errorData = await resp.json().catch(() => ({}));
          alert(errorData.message || "Planas pasikeite. Uzduoties redaguoti negalima.");
        } else if (resp.status === 400 || resp.status === 422) {
          // Handle validation errors from DTO or custom UnprocessableEntity
          const errorData = await resp.json().catch(() => ({}));

          // Check if it's a ValidationProblem response (has 'errors' object)
          if (errorData.errors && typeof errorData.errors === "object") {
            // Map DTO field names to form field selectors
            const fieldMapping = {
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
              Version: null // No specific field for version errors
            };

            let hasFieldError = false;
            for (const [field, messages] of Object.entries(errorData.errors)) {
              const selector = fieldMapping[field];
              const message = Array.isArray(messages) ? messages[0] : messages;
              if (selector && message) {
                setFieldError(modalEl, selector, message);
                hasFieldError = true;
              }
            }

            // If no specific field errors were mapped, show generic alert
            if (!hasFieldError) {
              const firstError = Object.values(errorData.errors).flat()[0];
              alert(firstError || "Patikrinkite įvestus duomenis.");
            }
          } else if (errorData.message) {
            // Custom error message from UnprocessableEntity
            alert(errorData.message);
          } else {
            alert("Patikrinkite įvestus duomenis.");
          }
        } else if (!resp.ok) {
          alert("Įvyko klaida išsaugant užduotį.");
        } else {
          // Response is TaskCreatedOrUpdatedResponseDto with id and version
          const responseData = await resp.json().catch(() => null);
          if (responseData?.version) {
            modalEl.dataset.version = responseData.version;
          }

          const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
          if (!query.has("view")) query.set("view", "day");
          await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
          panel?.hide?.();
        }

        logEvent("ui.task.saved", { taskId });
      }
    } catch (err) {
      logEvent("ui.task.save.error", { taskId, error: err?.message }, "error");
      alert("Nepavyko issaugoti uzduoties.");
    } finally {
      setBusy(false);
    }
  };

  const initOnce = () => {
    if (document.documentElement.dataset.scheduleTaskClickInit === "1") return;
    document.documentElement.dataset.scheduleTaskClickInit = "1";

    wireDeleteModal();
    wireDetailDeleteButton();


    document.addEventListener("click", async (evt) => {
      if (evt.target?.closest?.(".task-card-action")) return;
      if (evt.target?.closest?.(".task-drag-handle")) return;
      const card = evt.target?.closest?.(".task-card");
      if (!card) return;

      selection?.clearSelection?.("task.open");

      const taskId = card.getAttribute("data-task-id");
      if (!taskId) return;

      if (!state?.assertDayView?.("task.open")) {
        panel?.closeForNonDailyContext?.("week");
        logEvent("ui.task.open.skipped", { taskId, view: state?.getView?.() || "unknown" });
        return;
      }

      try {
        setActiveCard(card);

        await schedule.tasks.openTaskPanel({
          taskId
        });

      } catch (err) {
        clearActiveCards();
        logEvent("ui.task.fetch.error", { taskId, error: err?.message }, "error");
        alert("Nepavyko atidaryti uzduoties duomenu.");
      }

    });
  };

  const refreshOpenTask = async (taskId) => {
    const detailPanel = document.getElementById("taskDetailModal");
    if (!detailPanel) return false;
    if (!detailPanel.classList.contains("show")) return false;
    if (String(detailPanel.dataset.taskId || "") !== String(taskId || "")) return false;

    try {
      const data = await fetchTask(taskId);
      await populateTaskModal(detailPanel, data, data.isEditable);
      setActiveCardByTaskId(taskId);
      logEvent("ui.task.refresh.ok", { taskId });
      return true;
    } catch (err) {
      logEvent("ui.task.refresh.error", { taskId, error: err?.message }, "error");
      return false;
    }
  };

  const openTaskPanel = async ({ taskId = null, technicianId, targetDate, startMinutes, endMinutes }) => {
    const detailPanel = document.getElementById("taskDetailModal");
    if (!detailPanel) return;
    ensureObjectAddressBinding(detailPanel);

    if (detailPanel?.classList?.contains("is-collapsed")) {
      detailPanel.classList.remove("is-collapsed");
      detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
    }

    if (taskId) {
      // === EDIT MODE ===
      try {
        const data = await fetchTask(taskId);
        await populateTaskModal(detailPanel, data, data.isEditable);
        setActiveCardByTaskId(taskId);
        logEvent("ui.task.panel.open", { mode: "edit", taskId });
        panel?.show?.();
      } catch (err) {
        logEvent("ui.task.fetch.error", { taskId, error: err?.message }, "error");
        alert("Nepavyko atidaryti užduoties.");
      }
    } else {
      // === CREATE MODE ===
      detailPanel.dataset.mode = "create";
      detailPanel.dataset.taskId = "";
      detailPanel.dataset.editable = "true";
      detailPanel.dataset.technicianId = technicianId;
      detailPanel.dataset.targetDate = targetDate;
      detailPanel.dataset.startMinutes = startMinutes;
      detailPanel.dataset.endMinutes = endMinutes;

      const setVal = (selector, value) => {
        const el = detailPanel.querySelector(selector);
        if (el) el.value = value ?? "";
      };

      setVal("#taskDetailTitle", "");
      setVal("#taskDetailStart", "");
      setVal("#taskDetailEnd", "");
      setVal("#taskDetailPriority", "");
      setVal("#taskDetailStatus", "");
      setVal("#taskDetailSkill", "");
      syncInstructionFields(detailPanel, "");
      await populateObjectDropdown(detailPanel, null, null, null);
      await populateAddressDropdown(detailPanel, null);
      await populateTechnicianDropdown(detailPanel, technicianId);
      ensureObjectAddressBinding(detailPanel);

      detailPanel.querySelector("#taskDetailLabel").textContent = "Nauja užduotis";
      detailPanel.querySelector("#taskDetailCode").textContent = "";

      // Format minutes into HH:MM
      const minutesToTime = (m) => {
        const h = String(Math.floor(m / 60)).padStart(2, "0");
        const min = String(m % 60).padStart(2, "0");
        return `${h}:${min}`;
      };

      if (targetDate && startMinutes != null && endMinutes != null) {
        const dateStr = targetDate; // Already formatted YYYY-MM-DD
        const startTime = minutesToTime(startMinutes);
        const endTime = minutesToTime(endMinutes);

        setVal("#taskDetailStart", `${dateStr}T${startTime}`);
        setVal("#taskDetailEnd", `${dateStr}T${endTime}`);
      }

      const inputs = detailPanel.querySelectorAll("input, select, textarea");
      inputs.forEach((el) => el.removeAttribute("disabled"));

      const saveBtn = detailPanel.querySelector("#taskDetailSave");
      if (saveBtn) {
        saveBtn.classList.remove("d-none");
        saveBtn.textContent = "Sukurti";
        saveBtn.onclick = async () => {
          await saveTask(detailPanel);
        };
      }

      applyFieldRestrictions(detailPanel, technicianId, true);

      panel?.show?.();
      logEvent("ui.task.panel.open", {
        mode: "create",
        technicianId,
        targetDate,
        startMinutes,
        endMinutes,
      });
    }
  };

  const getDeleteModal = () => document.getElementById("taskDeleteModal");

  const formatMinutes = (minutes) => {
    if (!Number.isFinite(minutes)) return "";
    const h = String(Math.floor(minutes / 60)).padStart(2, "0");
    const m = String(minutes % 60).padStart(2, "0");
    return `${h}:${m}`;
  };

  const setDeleteModalDetails = ({ taskId, sourceEl }) => {
    const modalEl = getDeleteModal();
    if (!modalEl) return;

    const setRowText = (rowKey, spanSelector, value) => {
      const row = modalEl.querySelector(`[data-task-delete-row="${rowKey}"]`);
      const span = modalEl.querySelector(spanSelector);
      const text = value ? String(value).trim() : "";
      if (span) span.textContent = text;
      if (row) row.classList.toggle("d-none", !text);
    };

    setRowText("code", "[data-task-delete-code]", taskId ? `#${taskId}` : "");

    let title = "";
    let location = "";
    let timeRange = "";

    if (sourceEl?.classList?.contains?.("task-card")) {
      title = sourceEl.querySelector(".task-title")?.textContent || "";
      location = sourceEl.querySelector(".task-location")?.textContent || "";
      const startMinutes = parseInt(sourceEl.getAttribute("data-start-minutes") || "", 10);
      const durationMinutes = parseInt(sourceEl.getAttribute("data-duration-minutes") || "", 10);
      if (Number.isFinite(startMinutes) && Number.isFinite(durationMinutes)) {
        const endMinutes = startMinutes + durationMinutes;
        timeRange = `${formatMinutes(startMinutes)} - ${formatMinutes(endMinutes)}`;
      }
    } else if (sourceEl?.id === "taskDetailModal") {
      title = sourceEl.querySelector("#taskDetailTitle")?.value || "";
      const locationEl = sourceEl.querySelector("#taskDetailLocation");
      location = locationEl?.value || "";
      const startAt = sourceEl.querySelector("#taskDetailStart")?.value || "";
      const endAt = sourceEl.querySelector("#taskDetailEnd")?.value || "";
      const startTime = startAt.length >= 16 ? startAt.substring(11, 16) : "";
      const endTime = endAt.length >= 16 ? endAt.substring(11, 16) : "";
      if (startTime && endTime) {
        timeRange = `${startTime} - ${endTime}`;
      }
    }

    setRowText("title", "[data-task-delete-title]", title);
    setRowText("time", "[data-task-delete-time]", timeRange);
    setRowText("location", "[data-task-delete-location]", location);
  };

  const openDeleteModal = ({ taskId, version, sourceEl }) => {
    if (!taskId) return;
    const modalEl = getDeleteModal();
    if (!modalEl) return;
    modalEl.dataset.taskId = String(taskId);
    if (version != null && !Number.isNaN(version)) {
      modalEl.dataset.version = String(version);
    }
    setDeleteModalDetails({ taskId, sourceEl });
    const modal = window.bootstrap?.Modal?.getOrCreateInstance?.(modalEl);
    modal?.show?.();
  };

  const updateTaskVersion = (taskId, nextVersion) => {
    if (nextVersion == null) return;
    const detailPanel = document.getElementById("taskDetailModal");
    if (detailPanel && String(detailPanel.dataset.taskId || "") == String(taskId)) {
      detailPanel.dataset.version = String(nextVersion);
    }
    const card = document.querySelector(`.task-card[data-task-id="${CSS.escape(String(taskId))}"]`);
    if (card) card.setAttribute("data-task-version", String(nextVersion));
  };

  const deleteTaskAndRefresh = async ({ taskId, version }) => {
    const timezoneOffset = -new Date().getTimezoneOffset();
    const resp = await fetch(`/api/tasks/${taskId}`, {
      method: "DELETE",
      headers: {
        "Content-Type": "application/json",
        "X-Requested-With": "XMLHttpRequest"
      },
      body: JSON.stringify({
        version: Number.isFinite(version) ? version : 0,
        timezoneOffsetMinutes: timezoneOffset
      })
    });

    if (resp.status === 409) {
      const body = await resp.json().catch(() => ({}));
      updateTaskVersion(taskId, body?.version);
      alert(body.message || "Uzduotis pasikeite (stale). Atnaujinkite lentele.");
      return false;
    }

    if (resp.status === 403) {
      const body = await resp.json().catch(() => ({}));
      alert(body.message || "Neturite teisiu istrinti sios uzduoties.");
      return false;
    }

    if (resp.status === 404) {
      const body = await resp.json().catch(() => ({}));
      alert(body.message || "Uzduotis nerasta.");
      return false;
    }

    if (!resp.ok) {
      const body = await resp.json().catch(() => ({}));
      alert(body.message || "Nepavyko istrinti uzduoties.");
      return false;
    }

    await resp.json().catch(() => null);

    const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
    await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
    panel?.hide?.();
    clearActiveCards();
    return true;
  };

  const wireDeleteModal = () => {
    const modalEl = getDeleteModal();
    if (!modalEl || modalEl.dataset.bound === "1") return;
    modalEl.dataset.bound = "1";
    const confirmBtn = modalEl.querySelector("#taskDeleteConfirm");
    if (!confirmBtn) return;

    confirmBtn.addEventListener("click", async () => {
      const taskId = modalEl.dataset.taskId;
      const version = parseInt(modalEl.dataset.version || "0", 10);
      if (!taskId) {
        alert("Nepavyko nustatyti uzduoties.");
        return;
      }

      confirmBtn.disabled = true;
      confirmBtn.setAttribute("aria-busy", "true");

      try {
        logEvent("ui.task.delete.requested", { taskId, version });
        const ok = await deleteTaskAndRefresh({ taskId, version });
        if (ok) {
          logEvent("ui.task.delete.succeeded", { taskId });
          const modal = window.bootstrap?.Modal?.getInstance?.(modalEl);
          modal?.hide?.();
        } else {
          logEvent("ui.task.delete.rejected", { taskId });
        }
      } catch (err) {
        logEvent("ui.task.delete.error", { taskId, error: err?.message }, "error");
        alert("Nepavyko istrinti uzduoties.");
      } finally {
        confirmBtn.disabled = false;
        confirmBtn.removeAttribute("aria-busy");
      }
    });
  };

  const wireDetailDeleteButton = () => {
    const detailPanel = document.getElementById("taskDetailModal");
    if (!detailPanel) return;
    const deleteBtn = detailPanel.querySelector("#taskDetailDelete");
    if (!deleteBtn || deleteBtn.dataset.bound === "1") return;
    deleteBtn.dataset.bound = "1";
    deleteBtn.addEventListener("click", (evt) => {
      evt.preventDefault();
      const taskId = detailPanel.dataset.taskId;
      const version = parseInt(detailPanel.dataset.version || "0", 10);
      openDeleteModal({ taskId, version, sourceEl: detailPanel });
    });
  };

  document.addEventListener("click", (evt) => {
    const action = evt.target?.closest?.(".task-card-action");
    if (!action) return;
    evt.stopPropagation();
    evt.preventDefault();

    const card = action.closest(".task-card");
    if (!card || card.dataset.editable !== "true") return;

    const taskId = card.getAttribute("data-task-id");
    const version = parseInt(card.getAttribute("data-task-version") || "0", 10);
    const date = card.getAttribute("data-date");
    const startMinutes = parseInt(card.getAttribute("data-start-minutes") || "0", 10);
    const durationMinutes = parseInt(card.getAttribute("data-duration-minutes") || "0", 10);
    if (!taskId) return;

    if (action.classList.contains("task-card-action-delete")) {
      openDeleteModal({ taskId, version, sourceEl: card });
      return;
    }

    if (action.classList.contains("task-card-action-edit")) {
      startInlineResize(card, {
        taskId,
        version,
        date,
        startMinutes,
        durationMinutes
      });
    }
  });

  const startInlineResize = (card, { taskId, version, date, startMinutes, durationMinutes }) => {
    const tables = document.getElementById("scheduleTables");
    if (!tables) return;
    const slotSize = parseInt(tables.dataset.slotSizeMinutes || "30", 10);
    const dayStart = parseInt(tables.dataset.dayStartMinutes || "480", 10);
    const dayEnd = parseInt(tables.dataset.dayEndMinutes || "1200", 10);

    // Calculate minimum start time based on current local time
    const now = new Date();
    const taskDate = new Date(date + "T00:00:00");
    const isToday = now.toDateString() === taskDate.toDateString();
    const currentTimeMinutes = now.getHours() * 60 + now.getMinutes();
    // Round up to next slot boundary
    const minStartMinutes = isToday ? Math.ceil(currentTimeMinutes / slotSize) * slotSize : dayStart;

    let currentStart = startMinutes;
    let currentEnd = startMinutes + durationMinutes;

    // Use table-responsive container for positioning (same as selection bar)
    const container = tables.querySelector(".table-responsive") || tables.closest(".table-responsive");
    if (!container) return;

    // Find the starting TD cell and calculate span based on duration
    const startTd = card.closest("td");
    if (!startTd) return;
    const row = startTd.parentElement;
    if (!row) return;

    // Get all TDs in the row to find the range
    const allTds = Array.from(row.querySelectorAll("td"));
    const startTdIndex = allTds.indexOf(startTd);
    if (startTdIndex < 0) return;

    // Calculate how many slots the task spans
    const durationSlots = Math.max(1, Math.round(durationMinutes / slotSize));

    // Find the end TD by walking through cells considering colspan
    const getEndTdForSlots = (fromTd, slots) => {
      let remaining = slots;
      let td = fromTd;
      while (td && remaining > 0) {
        const span = parseInt(td.getAttribute("colspan") || "1", 10);
        remaining -= span;
        if (remaining <= 0) return td;
        td = td.nextElementSibling;
      }
      return fromTd; // fallback to start if not found
    };

    let endTd = getEndTdForSlots(startTd, durationSlots);

    // Get initial TD boundaries for overlay positioning
    const startTdRect = startTd.getBoundingClientRect();
    const endTdRect = endTd.getBoundingClientRect();

    const overlay = document.createElement("div");
    overlay.className = "slot-selection-bar task-resize-bar";
    overlay.innerHTML = `
      <button type="button" class="slot-selection-hit" data-slot-hit aria-label="Keisti laiką"></button>
      <button type="button" class="slot-selection-handle" data-slot-handle="left" aria-label="Anksčiau">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟨</span>
      </button>
      <button type="button" class="slot-selection-handle" data-slot-handle="right" aria-label="Vėliau">
        <span class="slot-selection-handle-pill" aria-hidden="true">⟩</span>
      </button>
    `;
    overlay.style.position = "absolute";

    // Position overlay using TD boundaries (same math as selection bar)
    const containerRect = container.getBoundingClientRect();
    // Nudge by 1px so the bar aligns with table borders (matches selection bar logic)
    const overlayLeft = startTdRect.left - containerRect.left + container.scrollLeft + 1;
    const overlayTop = startTdRect.top - containerRect.top + container.scrollTop;
    const overlayWidth = Math.max(1, endTdRect.right - startTdRect.left - 2);
    const overlayHeight = Math.max(1, startTdRect.height);

    overlay.style.left = `${overlayLeft}px`;
    overlay.style.top = `${overlayTop}px`;
    overlay.style.width = `${overlayWidth}px`;
    overlay.style.height = `${overlayHeight}px`;
    overlay.style.zIndex = "8";

    // Apply card styling to overlay
    const cardClasses = card.className;
    const taskCardClassMatch = cardClasses.match(/task-(normal|load|conflict|travel)/);
    if (taskCardClassMatch) {
      overlay.classList.add(taskCardClassMatch[1] === "normal" ? "task-normal" :
                           taskCardClassMatch[1] === "load" ? "task-load" :
                           taskCardClassMatch[1] === "conflict" ? "task-conflict" : "task-travel");
    }

    const teardown = () => {
      overlay.remove();
      document.removeEventListener("pointerdown", onOutsidePointerDown, true);
    };

    const onOutsidePointerDown = (evt) => {
      if (overlay.contains(evt.target) || card.contains(evt.target)) return;
      teardown();
    };
    document.addEventListener("pointerdown", onOutsidePointerDown, true);

    const applyTimeUpdate = async (newStart, newEnd) => {
      const timezoneOffset = -new Date().getTimezoneOffset();
      const resp = await fetch(`/api/tasks/${taskId}/time`, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
          "X-Requested-With": "XMLHttpRequest"
        },
        body: JSON.stringify({
          targetDate: date,
          targetSlotStartMinutes: newStart,
          targetSlotEndMinutes: newEnd,
          version,
          timezoneOffsetMinutes: timezoneOffset
        })
      });

      if (resp.status === 409) {
        const body = await resp.json().catch(() => ({}));
        alert(body.message || "Užduotis pasikeitė. Atnaujinkite lentelę.");
        teardown();
        return false;
      } else if (resp.status === 403) {
        const body = await resp.json().catch(() => ({}));
        alert(body.message || "Neturite teisių keisti laiko.");
        teardown();
        return false;
      } else if (!resp.ok) {
        const body = await resp.json().catch(() => ({}));
        alert(body.message || "Nepavyko pakeisti laiko.");
        return false;
      }

      const data = await resp.json().catch(() => null);
      if (data?.version) {
        version = data.version;
        card.setAttribute("data-task-version", String(version));
      }

      const detailPanel = document.getElementById("taskDetailModal");
      if (detailPanel?.classList?.contains("show") && String(detailPanel.dataset.taskId || "") === String(taskId)) {
        const startInput = detailPanel.querySelector("#taskDetailStart");
        const endInput = detailPanel.querySelector("#taskDetailEnd");
        if (startInput && data?.startAt) startInput.value = String(data.startAt).substring(0, 16);
        if (endInput && data?.endAt) endInput.value = String(data.endAt).substring(0, 16);
      }

      const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
      await table?.fetchAndRender?.(query, { updateUrl: false, preserveScroll: true });
      return true;
    };

    // Helper to get TD index in row
    const getTdIndexInRow = (td) => {
      if (!td) return null;
      const r = td.parentElement;
      if (!r) return null;
      const tds = Array.from(r.querySelectorAll("td"));
      const idx = tds.indexOf(td);
      return idx >= 0 ? idx : null;
    };

    // Track current start/end TD indices for resize
    let currentStartTdIndex = getTdIndexInRow(startTd);
    let currentEndTdIndex = getTdIndexInRow(endTd);

    const updateOverlayFromTds = (newStartTd, newEndTd) => {
      // Update time display on card
      const taskTimeEl = card.querySelector(".task-time");
      if (taskTimeEl) {
        const dragHandle = taskTimeEl.querySelector(".task-drag-handle");
        const handleHtml = dragHandle ? dragHandle.outerHTML : "";
        taskTimeEl.innerHTML = `${handleHtml}${formatRange(currentStart, currentEnd)}`;
      }

      // Recalculate overlay position from actual TD boundaries
      const newStartRect = newStartTd.getBoundingClientRect();
      const newEndRect = newEndTd.getBoundingClientRect();
      const newContainerRect = container.getBoundingClientRect();

      const newLeft = newStartRect.left - newContainerRect.left + container.scrollLeft + 1;
      const newTop = newStartRect.top - newContainerRect.top + container.scrollTop;
      const newWidth = Math.max(1, newEndRect.right - newStartRect.left - 2);
      const newHeight = Math.max(1, newStartRect.height);

      overlay.style.left = `${newLeft}px`;
      overlay.style.top = `${newTop}px`;
      overlay.style.width = `${newWidth}px`;
      overlay.style.height = `${newHeight}px`;
      overlay.style.transform = "none";
    };

    const attachDrag = (handle, dir) => {
      if (!handle) return;
      handle.style.cursor = "ew-resize";
      handle.addEventListener("pointerdown", (e) => {
        if (e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        handle.setPointerCapture(e.pointerId);
        overlay.classList.add("is-dragging");
        overlay.style.pointerEvents = "none";

        const baseStart = currentStart;
        const baseEnd = currentEnd;
        const baseStartTdIndex = currentStartTdIndex;
        const baseEndTdIndex = currentEndTdIndex;

        const move = (ev) => {
          // Find which TD the mouse is currently over
          const el = document.elementFromPoint(ev.clientX, ev.clientY);
          const hoveredTd = el?.closest?.("td");
          if (!hoveredTd) return;

          // Must be in the same row
          const hoveredRow = hoveredTd.parentElement;
          if (!hoveredRow || hoveredRow !== row) return;

          const hoveredIdx = getTdIndexInRow(hoveredTd);
          if (hoveredIdx == null) return;

          // Check if we're hovering over the task's own TD (for shrinking)
          const isOverTaskTd = hoveredTd === startTd;

          let newStartMinutes = baseStart;
          let newEndMinutes = baseEnd;

          if (isOverTaskTd) {
            // Shrinking: calculate slot position within the task TD based on mouse X
            const tdRect = startTd.getBoundingClientRect();
            const tdWidth = tdRect.width;
            const mouseXInTd = ev.clientX - tdRect.left;
            const totalTaskSlots = Math.round((baseEnd - baseStart) / slotSize);
            const slotWidthInTd = tdWidth / totalTaskSlots;

            if (dir === "left") {
              // Shrink from left: move start time forward
              const slotsFromLeft = Math.round(mouseXInTd / slotWidthInTd);
              const clampedSlots = Math.max(0, Math.min(slotsFromLeft, totalTaskSlots - 1));
              newStartMinutes = baseStart + clampedSlots * slotSize;
            } else {
              // Shrink from right: move end time backward
              const slotsFromLeft = Math.round(mouseXInTd / slotWidthInTd);
              const clampedSlots = Math.max(1, Math.min(slotsFromLeft, totalTaskSlots));
              newEndMinutes = baseStart + clampedSlots * slotSize;
            }
          } else {
            // Expanding: use the hovered empty slot TD
            const slotEl = hoveredTd.querySelector(".schedule-slot");
            if (!slotEl?.dataset?.slotStartMinutes) return;

            const hoveredSlotStart = parseInt(slotEl.dataset.slotStartMinutes, 10);

            if (dir === "left") {
              // Expand left: can only go to TDs before the task
              if (hoveredIdx >= baseStartTdIndex) return;
              newStartMinutes = hoveredSlotStart;
            } else {
              // Expand right: can only go to TDs after the task
              if (hoveredIdx <= baseEndTdIndex) return;
              newEndMinutes = hoveredSlotStart + slotSize;
            }
          }

          // Apply time constraints
          newStartMinutes = Math.max(dayStart, Math.max(minStartMinutes, newStartMinutes));
          newEndMinutes = Math.min(dayEnd, newEndMinutes);
          if (newEndMinutes - newStartMinutes < slotSize) return;

          // Update state
          currentStart = newStartMinutes;
          currentEnd = newEndMinutes;

          // Recalculate overlay position
          // For shrinking, we stay within the same TD but adjust visual width
          // For expanding, we need to find the new boundary TDs
          const newDurationSlots = Math.round((currentEnd - currentStart) / slotSize);
          const offsetFromOriginalStart = (currentStart - startMinutes) / slotSize;

          // Calculate new overlay dimensions based on original TD and slot calculations
          const origStartRect = startTd.getBoundingClientRect();
          const origSlotWidth = origStartRect.width / Math.round((baseEnd - baseStart) / slotSize);

          // For expanding, we need actual TD boundaries
          if (!isOverTaskTd) {
            // Find actual start/end TDs for the new range
            let actualStartTd = startTd;
            let actualEndTd = startTd;

            if (dir === "left" && hoveredIdx < baseStartTdIndex) {
              actualStartTd = hoveredTd;
              currentStartTdIndex = hoveredIdx;
            } else if (dir === "right" && hoveredIdx > baseEndTdIndex) {
              actualEndTd = hoveredTd;
              currentEndTdIndex = hoveredIdx;
            }

            updateOverlayFromTds(actualStartTd, actualEndTd);
          } else {
            // Shrinking within same TD - use pixel-based positioning
            const containerRect = container.getBoundingClientRect();
            const newLeft = origStartRect.left - containerRect.left + container.scrollLeft + 1 + (offsetFromOriginalStart * origSlotWidth);
            const newWidth = Math.max(origSlotWidth, newDurationSlots * origSlotWidth - 2);

            overlay.style.left = `${newLeft}px`;
            overlay.style.width = `${newWidth}px`;
            overlay.style.transform = "none";

            // Update time display
            const taskTimeEl = card.querySelector(".task-time");
            if (taskTimeEl) {
              const dragHandle = taskTimeEl.querySelector(".task-drag-handle");
              const handleHtml = dragHandle ? dragHandle.outerHTML : "";
              taskTimeEl.innerHTML = `${handleHtml}${formatRange(currentStart, currentEnd)}`;
            }
          }
        };

        const up = async (ev) => {
          handle.releasePointerCapture(ev.pointerId);
          handle.removeEventListener("pointermove", move);
          handle.removeEventListener("pointerup", up);
          handle.removeEventListener("pointercancel", up);
          overlay.classList.remove("is-dragging");
          overlay.style.pointerEvents = "";

          const changed = currentStart !== baseStart || currentEnd !== baseEnd;
          if (changed) {
            await applyTimeUpdate(currentStart, currentEnd);
          }
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", up);
        handle.addEventListener("pointercancel", up);
      });
    };

    attachDrag(overlay.querySelector('[data-slot-handle="left"]'), "left");
    attachDrag(overlay.querySelector('[data-slot-handle="right"]'), "right");
    overlay.querySelector("[data-slot-hit]")?.addEventListener("click", (e) => {
      e.stopPropagation();
      teardown();
    });

    // Append to container (same as selection bar) instead of parent cell
    container.appendChild(overlay);
  };

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
    // Append error to parent container so it appears below the field (including Select2 widgets)
    const parent = field.closest(".col-md-6, .col-12, .col");
    if (parent) {
      parent.appendChild(helper);
    } else {
      field.insertAdjacentElement("afterend", helper);
    }
  };

  schedule.tasks = { initOnce, refreshOpenTask, setActiveCardByTaskId, openTaskPanel };
})();