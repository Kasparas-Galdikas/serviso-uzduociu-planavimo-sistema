(()=> {
    const modal = document.getElementById("filterConsoleModal");
    if (!modal) return;

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
                const resp = await fetch(`/tasks/${taskId}`, {
                    headers: { "X-Requested-With": "XMLHttpRequest" }
                });
                if (!resp.ok) {
                    throw new Error("Nepavyko gauti uzduoties duomenu");
                }
                const data = await resp.json();
                const detailModal = document.getElementById("taskDetailModal");
                if (!detailModal) return;

                populateTaskModal(detailModal, data, isEditable && data.isEditable);
                const modal = new bootstrap.Modal(detailModal);
                modal.show();
            } catch (err) {
                console.error(err);
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
            const resp = await fetch(`/tasks/${taskId}`, {
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
            });
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
        } catch (err) {
            console.error(err);
            alert("Nepavyko issaugoti uzduoties.");
        } finally {
            setBusy(false);
        }
    };

    const fetchTable = async (query, { button, skipHistory } = {}) => {
        if (!tables) return;
        button?.classList.add("disabled");
        button?.setAttribute("aria-busy", "true");

        const url = `/Schedule/Table?${query.toString()}`;
        try {
            const response = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
            const html = await response.text();
            tables.innerHTML = html;

            const nextUrl = new URL(window.location.href);
            nextUrl.pathname = "/Schedule";
            nextUrl.search = query.toString();
            if (!skipHistory) {
                window.history.replaceState({}, "", nextUrl.toString());
            }
            notifyTableUpdated();
        } catch (err) {
            console.error("Nepavyko atnaujinti navigacijos per Ajax", err);
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

        applyBtn?.setAttribute("disabled", "disabled");
        try {
            await fetchTable(query);
        } catch (err) {
            console.error("Nepavyko atnaujinti filtro rezultatu", err);
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

