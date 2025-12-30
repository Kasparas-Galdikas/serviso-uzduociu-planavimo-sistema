(function () {
    const applyBtn = document.getElementById('applyFiltersBtn');
    const modalEl = document.getElementById('filterConsoleModal');
    const viewButtons = modalEl ? modalEl.querySelectorAll('[data-view-mode]') : null;

    let selectedView = 'day';

    if (viewButtons && viewButtons.length) {
        const activeBtn = Array.from(viewButtons).find(b => b.classList.contains('active') || b.classList.contains('btn-primary'));
        selectedView = activeBtn?.getAttribute('data-view-mode') || selectedView;
    }

    console.log('[filter-console] init', { hasApply: !!applyBtn, hasModal: !!modalEl, viewButtons: viewButtons?.length ?? 0, selectedView });

    if (!applyBtn || !modalEl || !window.bootstrap) return;

    applyBtn.addEventListener('click', () => {
        const modal = bootstrap.Modal.getInstance(modalEl);
        modal?.hide();

        const url = new URL(window.location.href);
        url.searchParams.set('view', selectedView || 'day');
        console.log('[filter-console] apply filters', { selectedView, url: url.toString() });
        window.location.href = url.toString();
    });

    if (viewButtons) {
        viewButtons.forEach(btn => {
            btn.addEventListener('click', () => {
                selectedView = btn.getAttribute('data-view-mode') || selectedView;

                // Update styles immediately for feedback
                viewButtons.forEach(b => {
                    const isActive = b === btn;
                    b.classList.toggle('btn-primary', isActive);
                    b.classList.toggle('active', isActive);
                    b.classList.toggle('btn-outline-secondary', !isActive);
                });

                console.log('[filter-console] switch view (no reload until apply)', selectedView);
            });
        });
    }
})();
