(() => {
  const tables = document.getElementById("scheduleTables");
  if (!tables) return;

  window.schedule = window.schedule || {};
  const schedule = window.schedule;
  const state = schedule.state;
  const table = schedule.table;
  const panel = schedule.panel;

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
    const resp =
      (await logger?.fetchWithTrace?.(`/tasks/${taskId}`, {}, { event: "ui.task.fetch", taskId })) ||
      (await fetch(`/tasks/${taskId}`, { headers: { "X-Requested-With": "XMLHttpRequest" } }));

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

    modalEl.dataset.taskId = data.id;
    modalEl.dataset.editable = editable ? "true" : "false";
    if (data.version != null) modalEl.dataset.taskVersion = String(data.version);

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
      saveBtn.onclick = async () => {
        await saveTask(modalEl);
      };
    }
  };

  const saveTask = async (modalEl) => {
    const taskId = modalEl.dataset.taskId;
    if (!taskId) return;

    const title = modalEl.querySelector("#taskDetailTitle")?.value || "";
    const location = modalEl.querySelector("#taskDetailLocation")?.value || "";
    const startAt = modalEl.querySelector("#taskDetailStart")?.value || "";
    const endAt = modalEl.querySelector("#taskDetailEnd")?.value || "";
    const priority = modalEl.querySelector("#taskDetailPriority")?.value || "";
    const status = modalEl.querySelector("#taskDetailStatus")?.value || "";

    const saveBtn = modalEl.querySelector("#taskDetailSave");
    const setBusy = (state) => {
      if (saveBtn) {
        saveBtn.disabled = state;
        saveBtn.setAttribute("aria-busy", state ? "true" : "false");
      }
    };

    setBusy(true);
    try {
      const resp =
        (await logger?.fetchWithTrace(
          `/tasks/${taskId}`,
          {
            method: "PATCH",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title, location, startAt, endAt, priority, status }),
          },
          { event: "ui.task.save", taskId },
        )) ||
        (await fetch(`/tasks/${taskId}`, {
          method: "PATCH",
          headers: {
            "X-Requested-With": "XMLHttpRequest",
            "Content-Type": "application/json",
          },
          body: JSON.stringify({ title, location, startAt, endAt, priority, status }),
        }));

      if (resp.status === 409 || resp.status === 403) {
        alert("Planas pasikeite. Uzduoties redaguoti negalima.");
      } else if (!resp.ok) {
        throw new Error("Nepavyko issaugoti uzduoties");
      } else {
        const query = state?.getQuery?.() || new URLSearchParams(window.location.search || "");
        if (!query.has("view")) query.set("view", "day");
        await table?.fetchAndRender?.(query, { updateUrl: false });
        panel?.hide?.();
      }

      logEvent("ui.task.saved", { taskId });
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

      const taskId = card.getAttribute("data-task-id");
      if (!taskId) return;

      if (!state?.assertDayView?.("task.open")) {
        panel?.closeForNonDailyContext?.("week");
        logEvent("ui.task.open.skipped", { taskId, view: state?.getView?.() || "unknown" });
        return;
      }

      try {
        setActiveCard(card);

        const detailPanel = document.getElementById("taskDetailModal");
        if (detailPanel?.classList?.contains("is-collapsed")) {
          detailPanel.classList.remove("is-collapsed");
          detailPanel.dispatchEvent(new CustomEvent("schedule:panel:collapse-changed"));
        }

        const data = await fetchTask(taskId);
        if (!detailPanel) return;

        populateTaskModal(detailPanel, data, data.isEditable);
        panel?.show?.();
        logEvent("ui.task.opened", { taskId, editable: data.isEditable });
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

  schedule.tasks = { initOnce, refreshOpenTask, setActiveCardByTaskId };
})();
