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

      // Add placeholder option
      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "Pasirinkite darbuotoją...";
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
    } catch (err) {
      console.error("Error loading technicians:", err);
    }
  };

  const populateTaskModal = (modalEl, data, editable) => {
    const setVal = (selector, value) => {
      const el = modalEl.querySelector(selector);
      if (el) el.value = value || "";
    };

    setVal("#taskDetailTitle", data.title);
    setVal("#taskDetailLocation", data.location);
    setVal("#taskDetailStart", data.startAt ? data.startAt.substring(0, 16) : "");
    setVal("#taskDetailEnd", data.endAt ? data.endAt.substring(0, 16) : "");
    setVal("#taskDetailPriority", data.priority);
    setVal("#taskDetailStatus", data.status);
    setVal("#taskDetailTechnicianComment", data.technicianComment);

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

    if (isTechnician) {
      // Technician: First disable ALL inputs
      const allInputs = modalEl.querySelectorAll("input, select, textarea");
      allInputs.forEach((el) => {
        el.setAttribute("disabled", "disabled");
        el.classList.add("bg-light");
      });

      // Enable technician fields ONLY if it's their task and editable
      const canEdit = isOwnTask && editable;
      const statusField = modalEl.querySelector("#taskDetailStatus");
      const commentField = modalEl.querySelector("#taskDetailTechnicianComment");

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

    } else {
      // Manager: Enable ALL fields if editable
      const inputs = modalEl.querySelectorAll("input, select, textarea");
      inputs.forEach((el) => {
        if (editable) {
          el.removeAttribute("disabled");
          el.classList.remove("bg-light");
        } else {
          el.setAttribute("disabled", "disabled");
        }
      });
    }
  };

  const saveTask = async (modalEl) => {
    const mode = modalEl.dataset.mode || "update";
    const taskId = modalEl.dataset.taskId;

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
    const priority = modalEl.querySelector("#taskDetailPriority")?.value || "";
    const status = modalEl.querySelector("#taskDetailStatus")?.value || "";

    // Additional fields
    const shift = modalEl.querySelector("#taskDetailShift")?.value || null;
    const taskType = modalEl.querySelector("#taskDetailType")?.value || null;
    const skillLevel = modalEl.querySelector("#taskDetailSkill")?.value || null;
    const objectName = modalEl.querySelector("#taskDetailObject")?.value || null;
    const objectType = modalEl.querySelector("#taskDetailObjectType")?.value || null;
    const instructions = modalEl.querySelector("#taskDetailInstructions")?.value || null;
    const safetyRequirements = modalEl.querySelector("#taskDetailSafety")?.value || null;

    // Validate required fields
    if (!title.trim()) {
      alert("Pavadinimas yra privalomas.");
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
      const technicianId = parseInt(modalEl.dataset.technicianId);
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
          priority: parseInt(priority) || 1,
          status: parseInt(status) || 0,
          shift,
          taskType,
          skillLevel,
          objectName,
          objectType,
          instructions,
          safetyRequirements,
        };

        result = await window.schedule?.selection?.submitCreateTask?.(formData);

        if (result?.success) {
          // Close modal
          const bsModal = window.bootstrap?.Offcanvas?.getInstance?.(modalEl);
          bsModal?.hide?.();
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
          priority: parseInt(priority) || 1,
          status: parseInt(status) || 0,
          version: parseInt(version) || 0,
          timezoneOffsetMinutes: timezoneOffset,
          technicianId: technicianId || 0,
          shift,
          taskType,
          skillLevel,
          objectName,
          objectType,
          instructions,
            safetyRequirements,
            technicianComment: modalEl.querySelector("#taskDetailTechnicianComment")?.value || null,
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
        } else if (!resp.ok) {
          throw new Error("Nepavyko issaugoti uzduoties");
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

    document.addEventListener("click", async (evt) => {
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
      populateTaskModal(detailPanel, data, data.isEditable);
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

    if (detailPanel?.classList?.contains("is-collapsed")) {
      detailPanel.classList.remove("is-collapsed");
      detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
    }

    if (taskId) {
      // === EDIT MODE ===
      try {
        const data = await fetchTask(taskId);
        populateTaskModal(detailPanel, data, data.isEditable);
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
        if (el) el.value = value || "";
      };

      setVal("#taskDetailTitle", "");
      setVal("#taskDetailLocation", "");
      setVal("#taskDetailStart", "");
      setVal("#taskDetailEnd", "");
      setVal("#taskDetailPriority", "1");
      setVal("#taskDetailStatus", "0");
      setVal("#taskDetailShift", "");
      setVal("#taskDetailType", "");
      setVal("#taskDetailSkill", "");
      setVal("#taskDetailObject", "");
      setVal("#taskDetailObjectType", "");
      setVal("#taskDetailInstructions", "");
      setVal("#taskDetailSafety", "");

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

  schedule.tasks = { initOnce, refreshOpenTask, setActiveCardByTaskId, openTaskPanel };
})();
