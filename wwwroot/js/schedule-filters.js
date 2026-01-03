(()=> {
    const modal = document.getElementById("filterConsoleModal");
    if (!modal) return;

    const locationInput = modal.querySelector("#filterLocation");
    const statusInput = modal.querySelector("#filterStatus");
    const dayDateInput = modal.querySelector("#filterDayDate");
    const weekDateInput = modal.querySelector("#filterWeekDate");
    const applyBtn = modal.querySelector("#applyFiltersBtn");
    const tables = document.getElementById("scheduleTables");
    const isPreview = (tables?.dataset.preview || "").toLowerCase() === "true";
    const plannedStart = tables?.dataset.plannedStart || "";
    const plannedWeeks = tables?.dataset.plannedWeeks || "";
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

        if (isPreview) {
            query.set("preview", "true");
            if (plannedStart) query.set("plannedStart", plannedStart);
            if (plannedWeeks) query.set("plannedWeeks", plannedWeeks);
        }

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

            const previewFlag = (navBtn.dataset.preview || "").toLowerCase() === "true";
            const pStart = navBtn.dataset.plannedStart || "";
            const pWeeks = navBtn.dataset.plannedWeeks || "";
            if (previewFlag) {
                query.set("preview", "true");
                if (pStart) query.set("plannedStart", pStart);
                if (pWeeks) query.set("plannedWeeks", pWeeks);
            }

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
})();

