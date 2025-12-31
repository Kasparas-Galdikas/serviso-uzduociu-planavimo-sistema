(() => {
    const modal = document.getElementById('filterConsoleModal');
    if (!modal) return;

    const locationSelect = modal.querySelector('#filterLocation');
    const statusSelect = modal.querySelector('#filterStatus');
    const dayInput = modal.querySelector('#filterDayDate');
    const weekInput = modal.querySelector('#filterWeekDate');
    const applyBtn = modal.querySelector('#applyFiltersBtn');
    const scheduleContainer = document.getElementById('scheduleTables');
    const viewButtons = modal.querySelectorAll('[data-view-mode]');
    const viewSections = modal.querySelectorAll('[data-view-target]');
    const dataset = modal.dataset;

    let currentView = (dataset.initialView || '').toLowerCase() === 'week' ? 'week' : 'day';

    const setView = (mode) => {
        const normalized = (mode || '').toLowerCase() === 'week' ? 'week' : 'day';
        currentView = normalized;
        viewSections.forEach(el => {
            el.classList.toggle('d-none', (el.dataset.viewTarget || '') !== normalized);
        });
        viewButtons.forEach(btn => {
            const isActive = (btn.dataset.viewMode || '') === normalized;
            btn.classList.toggle('btn-primary', isActive);
            btn.classList.toggle('btn-outline-secondary', !isActive);
            btn.classList.toggle('active', isActive);
        });
    };

    const applyFilters = async () => {
        if (!scheduleContainer) return;
        const params = new URLSearchParams();
        params.set('view', currentView || 'day');

        const dateVal = currentView === 'week' ? (weekInput?.value || '') : (dayInput?.value || '');
        if (dateVal) {
            params.set('date', dateVal);
        }

        const loc = locationSelect?.value || '';
        if (loc) {
            params.set('location', loc);
        }

        const status = statusSelect?.value || '';
        if (status) {
            params.set('status', status);
        }

        const partialUrl = `/Schedule/Table?${params.toString()}`;
        applyBtn?.setAttribute('disabled', 'disabled');

        try {
            const response = await fetch(partialUrl, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            const html = await response.text();
            scheduleContainer.innerHTML = html;

            const newUrl = new URL(window.location.href);
            newUrl.search = params.toString();
            window.history.replaceState({}, '', newUrl.toString());

            const modalInstance = bootstrap.Modal.getInstance(modal);
            modalInstance?.hide();
        } catch (err) {
            console.error('Nepavyko atnaujinti filtro rezultatu', err);
        } finally {
            applyBtn?.removeAttribute('disabled');
        }
    };

    const syncInputsFromDataset = () => {
        if (dayInput && dataset.selectedDate) {
            dayInput.value = dataset.selectedDate;
        }
        if (weekInput && dataset.weekStart) {
            weekInput.value = dataset.weekStart;
        }
        if (locationSelect && dataset.selectedLocation) {
            locationSelect.value = dataset.selectedLocation;
        }
        if (statusSelect && dataset.selectedStatus) {
            statusSelect.value = dataset.selectedStatus;
        }
    };

    syncInputsFromDataset();
    setView(currentView);

    viewButtons.forEach(btn => {
        btn.addEventListener('click', () => setView(btn.dataset.viewMode));
    });

    if (applyBtn) {
        applyBtn.addEventListener('click', (e) => {
            e.preventDefault();
            applyFilters();
        });
    }

    const editToggle = modal.querySelector('#consoleEditMode');
    const editButtons = modal.querySelectorAll('[data-edit-action]');
    if (editToggle && editButtons.length) {
        const syncEditState = () => {
            const isOn = editToggle.checked;
            editButtons.forEach(btn => {
                btn.disabled = !isOn;
                btn.classList.toggle('disabled', !isOn);
            });
        };
        syncEditState();
        editToggle.addEventListener('change', syncEditState);
        modal.addEventListener('shown.bs.modal', syncEditState);
    }
})();
