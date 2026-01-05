(()=> {
    const modal = document.getElementById("filterConsoleModal");
    if (!modal) return;

    const logger = window.scheduleLogger;
    if (logger) logger.initTrace();
    const logEvent = (event, data = {}, level = "info") => logger?.log(level, event, data);

    const locationInput = modal.querySelector("#filterLocation");
    const statusInput = modal.querySelector("#filterStatus");
    const dayDateInput = modal.querySelector("#filterDayDate");
    const weekDateInput = modal.querySelector("#filterWeekDate");
    const applyBtn = modal.querySelector("#applyFiltersBtn");
    const tables = document.getElementById("scheduleTables");
    const viewModeButtons = modal.querySelectorAll("[data-view-mode]");
    const viewTargets = modal.querySelectorAll("[data-view-target]");
    const dataSet = modal.dataset;
    let currentView = (dataSet.initialView || "").toLowerCase() === "week" ? "week" : "day";
    const plannedStart = dataSet.plannedPeriodStart;
    const plannedEnd = dataSet.plannedPeriodEnd;

    const parseDate = (val) => val ? new Date(`${val}T00:00:00`) : null;
    const formatDate = (date) => date ? date.toISOString().slice(0, 10) : "";
    const addDays = (date, days) => {
        const next = new Date(date);
        next.setDate(next.getDate() + days);
        return next;
    };

    const plannedEndDate = parseDate(plannedEnd);
    const nextFreeDate = plannedEndDate ? addDays(plannedEndDate, 1) : null;

    const clampToNextFree = (input) => {
        if (!input || !nextFreeDate) return;
        const minStr = formatDate(nextFreeDate);
        input.min = minStr;
        input.setAttribute("title", `Galima rinktis nuo ${minStr} (nepersidengiant su aktyviu planu)`);
        if (input.value) {
            const current = parseDate(input.value);
            if (current && current < nextFreeDate) {
                input.value = minStr;
            }
        }
    };

    const switchView = (next) => {
        const normalized = (next || "").toLowerCase() === "week" ? "week" : "day";
        currentView = normalized;
        viewTargets.forEach(el => {
            el.classList.toggle("d-none", (el.dataset.viewTarget || "") !== normalized);
        });
        viewModeButtons.forEach(btn => {
            const isActive = (btn.dataset.viewMode || "") === normalized;
            btn.classList.toggle("btn-primary", isActive);
            btn.classList.toggle("btn-outline-secondary", !isActive);
            btn.classList.toggle("active", isActive);
        });
    };

    const notifyTableUpdated = () => {
        document.dispatchEvent(new CustomEvent("schedule:table:updated"));
        logEvent("ui.table.updated");
    };

    const bindTaskClick = () => {
        document.addEventListener("click", async (evt) => {
            const card = evt.target?.closest?.(".task-card");
            if (!card) return;

            const taskId = card.getAttribute("data-task-id");
            const isEditableAttr = card.getAttribute("data-editable") || "false";
            const planSource = card.getAttribute("data-plan-source") || "";
            const isEditable = isEditableAttr.toLowerCase() === "true";

            if (!taskId) return;

            try {
                const resp = await (logger?.fetchWithTrace(`/tasks/${taskId}`, {}, { event: "ui.task.fetch", taskId }) || fetch(`/tasks/${taskId}`, { headers: { "X-Requested-With": "XMLHttpRequest" } }));
                if (!resp.ok) {
                    await logFetchError(resp, `/tasks/${taskId}`);
                    throw new Error("Nepavyko gauti uzduoties duomenu");
                }
                const data = await resp.json();
                const detailModal = document.getElementById("taskDetailModal");
                if (!detailModal) return;

                populateTaskModal(detailModal, data, isEditable && data.isEditable);
                const modal = new bootstrap.Modal(detailModal);
                modal.show();
                logEvent("ui.task.opened", { taskId, editable: isEditable && data.isEditable });
            } catch (err) {
                logEvent("ui.task.fetch.error", { taskId, error: err?.message }, "error");
                alert("Nepavyko atidaryti uzduoties duomenu.");
            }
        });
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

        const inputs = modalEl.querySelectorAll("input, select, textarea");
        inputs.forEach(el => {
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
            const resp = await (logger?.fetchWithTrace(`/tasks/${taskId}`, {
                method: "PATCH",
                headers: {
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({
                    title,
                    location,
                    startAt,
                    endAt,
                    priority,
                    status
                })
            }, { event: "ui.task.save", taskId }) || fetch(`/tasks/${taskId}`, {
                method: "PATCH",
                headers: {
                    "X-Requested-With": "XMLHttpRequest",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({
                    title,
                    location,
                    startAt,
                    endAt,
                    priority,
                    status
                })
            }));
            if (resp.status === 409 || resp.status === 403) {
                alert("Planas pasikeite. Uzduoties redaguoti negalima.");
            } else if (!resp.ok) {
                throw new Error("Nepavyko issaugoti uzduoties");
            } else {
                // refresh table
                const query = new URLSearchParams(window.location.search || "");
                if (!query.has("view")) query.set("view", "day");
                await fetchTable(query, { skipHistory: true });
                const bsModal = bootstrap.Modal.getInstance(modalEl);
                bsModal?.hide();
            }
                logEvent("ui.task.saved", { taskId });
            } catch (err) {
                logEvent("ui.task.save.error", { taskId, error: err?.message }, "error");
                alert("Nepavyko issaugoti uzduoties.");
            } finally {
                setBusy(false);
            }
        };

    const logFetchError = async (resp, url) => {
        try {
            const text = await resp.text();
            logEvent("ui.fetch.error", { url, status: resp.status, statusText: resp.statusText, body: text }, "error");
        } catch (err) {
            logEvent("ui.fetch.error", { url, status: resp.status, statusText: resp.statusText }, "error");
        }
    };

    const fetchTable = async (query, { button, skipHistory } = {}) => {
        if (!tables) return;
        button?.classList.add("disabled");
        button?.setAttribute("aria-busy", "true");

        const url = `/Schedule/Table?${query.toString()}`;
        try {
            const response = await (logger?.fetchWithTrace(url, {}, { event: "ui.table.fetch", view: query.get("view"), date: query.get("date"), location: query.get("location"), status: query.get("status") }) || fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } }));
            if (!response.ok) {
                await logFetchError(response, url);
                return;
            }
            const html = await response.text();
            tables.innerHTML = html;
            logEvent("ui.table.fetch.ok", { url, status: response.status, length: html.length });

            const nextUrl = new URL(window.location.href);
            nextUrl.pathname = "/Schedule";
            nextUrl.search = query.toString();
            if (!skipHistory) {
                window.history.replaceState({}, "", nextUrl.toString());
            }
            notifyTableUpdated();
        } catch (err) {
            logEvent("ui.table.fetch.error", { url, error: err?.message }, "error");
        } finally {
            button?.classList.remove("disabled");
            button?.removeAttribute("aria-busy");
        }
    };

    const applyFilters = async () => {
        if (!tables) return;
        const query = new URLSearchParams();
        query.set("view", currentView || "day");
        const dateVal = currentView === "week" ? (weekDateInput?.value || "") : (dayDateInput?.value || "");
        if (dateVal) query.set("date", dateVal);

        const locVal = locationInput?.value || "";
        if (locVal) query.set("location", locVal);
        const statusVal = statusInput?.value || "";
        if (statusVal) query.set("status", statusVal);
        logEvent("ui.filter.apply", {
            view: currentView,
            date: dateVal || "none",
            location: locVal || "all",
            status: statusVal || "all"
        });

        applyBtn?.setAttribute("disabled", "disabled");
        try {
            await fetchTable(query);
        } catch (err) {
            logEvent("ui.filter.apply.error", { error: err?.message }, "error");
        } finally {
            applyBtn?.removeAttribute("disabled");
        }
    };

    // Prefill console inputs
    (() => {
        if (dayDateInput && dataSet.selectedDate) dayDateInput.value = dataSet.selectedDate;
        if (weekDateInput && dataSet.weekStart) weekDateInput.value = dataSet.weekStart;
        if (locationInput && dataSet.selectedLocation) locationInput.value = dataSet.selectedLocation;
        if (statusInput && dataSet.selectedStatus) statusInput.value = dataSet.selectedStatus;
    })();

    switchView(currentView);
    clampToNextFree(dayDateInput);
    clampToNextFree(weekDateInput);
    viewModeButtons.forEach(btn => {
        btn.addEventListener("click", () => switchView(btn.dataset.viewMode));
    });
    if (applyBtn) {
        applyBtn.addEventListener("click", (e) => {
            e.preventDefault();
            applyFilters();
        });
    }

    // Edit mode toggles
    const editToggle = modal.querySelector("#consoleEditMode");
    const editActions = modal.querySelectorAll("[data-edit-action]");
    if (editToggle && editActions.length) {
        const syncEdit = () => {
            const enabled = editToggle.checked;
            editActions.forEach(btn => {
                btn.disabled = !enabled;
                btn.classList.toggle("disabled", !enabled);
            });
        };
        syncEdit();
        editToggle.addEventListener("change", syncEdit);
        modal.addEventListener("shown.bs.modal", syncEdit);
    }

    // AJAX navigation + popstate
    if (tables) {
        tables.addEventListener("click", async (evt) => {
            const navBtn = evt.target?.closest?.("[data-nav]");
            if (!navBtn) return;
            evt.preventDefault();

            const query = new URLSearchParams();
            const navView = (navBtn.dataset.view || currentView || "day").toLowerCase() === "week" ? "week" : "day";
            query.set("view", navView);

            const targetDate = navBtn.dataset.targetDate || "";
            if (targetDate) query.set("date", targetDate);

            const loc = navBtn.dataset.location || "";
            if (loc) query.set("location", loc);
            const status = navBtn.dataset.status || "";
            if (status) query.set("status", status);

            logEvent("ui.nav.click", {
                view: navView,
                date: targetDate || "none",
                location: loc || "all",
                status: status || "all"
            });

            window.history.pushState({}, "", `/Schedule?${query.toString()}`);
            await fetchTable(query, { button: navBtn });
        });

        window.addEventListener("popstate", () => {
            const query = new URLSearchParams(window.location.search || "");
            if (!query.has("view")) {
                query.set("view", "day");
            }
            fetchTable(query, { skipHistory: true });
        });
    }

    bindTaskClick();
})();

