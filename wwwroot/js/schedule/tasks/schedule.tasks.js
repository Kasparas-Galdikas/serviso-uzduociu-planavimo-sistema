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
      (await logger?.fetchWithTrace?.(`/tasks/${taskId}`, {
        headers: { "X-Timezone-Offset": String(timezoneOffset) }
      }, { event: "ui.task.fetch", taskId })) ||
      (await fetch(`/tasks/${taskId}`, {
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

    // Set to UPDATE mode
    modalEl.dataset.mode = "update";
    modalEl.dataset.taskId = data.id;
    modalEl.dataset.editable = editable ? "true" : "false";
    // Store version from response for optimistic concurrency control
    if (data.version != null) modalEl.dataset.version = String(data.version);
    // Clear create-mode data
    delete modalEl.dataset.technicianId;
    delete modalEl.dataset.targetDate;
    delete modalEl.dataset.startMinutes;
    delete modalEl.dataset.endMinutes;

    // Update modal title
    modalEl.querySelector("#taskDetailLabel").textContent = "Užduoties informacija";
    modalEl.querySelector("#taskDetailCode").textContent = `Kodas: #${data.id}`;

    const inputs = modalEl.querySelectorAll("input, select, textarea");
    inputs.forEach((el) => {
      if (editable) {
        el.removeAttribute("disabled");
      } else {
        el.setAttribute("disabled", "disabled");
      }
    });

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    if (saveBtn) {
      saveBtn.classList.toggle("d-none", !editable);
      saveBtn.textContent = "Išsaugoti";
      saveBtn.onclick = async () => {
        await saveTask(modalEl);
      };
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
        // UPDATE MODE - existing PATCH logic
        const version = modalEl.dataset.version || 0;

        // Capture browser timezone offset (in minutes)
        const timezoneOffset = -new Date().getTimezoneOffset();

        // Build request using UpdateTaskRequestDto schema
        const requestBody = JSON.stringify({
          title,
          location,
          startAt,
          endAt,
          priority: parseInt(priority) || 1,
          status: parseInt(status) || 0,
          version: parseInt(version) || 0,
          timezoneOffsetMinutes: timezoneOffset,
          // Additional fields
          shift,
          taskType,
          skillLevel,
          objectName,
          objectType,
          instructions,
          safetyRequirements,
        });

        const resp =
          (await logger?.fetchWithTrace(
            `/tasks/${taskId}`,
            {
              method: "PATCH",
              headers: { "Content-Type": "application/json" },
              body: requestBody,
            },
            { event: "ui.task.save", taskId },
          )) ||
          (await fetch(`/tasks/${taskId}`, {
            method: "PATCH",
            headers: {
              "X-Requested-With": "XMLHttpRequest",
              "Content-Type": "application/json",
            },
            body: requestBody,
          }));

        if (resp.status === 409 || resp.status === 403) {
          alert("Planas pasikeite. Uzduoties redaguoti negalima.");
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
