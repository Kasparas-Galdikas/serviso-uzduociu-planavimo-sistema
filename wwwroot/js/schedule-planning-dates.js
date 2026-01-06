window.schedulePlanningDates = (function () {
    const iso = (d) => d.toISOString().slice(0, 10);
    const addDays = (date, days) => {
        const d = new Date(date);
        d.setDate(d.getDate() + days);
        return d;
    };
    const resolveFirstFree = ({ scheduleContainer, startInput }) => {
        const todayIso = new Date().toISOString().slice(0, 10);
        const firstFreeAttr = scheduleContainer?.getAttribute('data-first-free');
        const activeEndIso = scheduleContainer?.getAttribute('data-active-end');
        let firstFree = firstFreeAttr || startInput?.min || startInput?.value || todayIso;
        if (!firstFree && activeEndIso) {
            const parsed = new Date(activeEndIso + 'T00:00:00');
            if (!isNaN(parsed)) {
                firstFree = iso(addDays(parsed, 1));
            }
        }
        if (firstFree < todayIso) firstFree = todayIso;
        return firstFree;
    };

    const syncStartEndMins = ({ scheduleContainer, startInput, endInput }) => {
        if (!startInput) return;
        const firstFree = resolveFirstFree({ scheduleContainer, startInput });
        startInput.min = firstFree;
        if (!startInput.value || startInput.value < firstFree) {
            startInput.value = firstFree;
        }
        if (endInput) {
            const startVal = startInput.value || firstFree;
            const minEnd = startVal < firstFree ? firstFree : startVal;
            endInput.min = minEnd;
            if (!endInput.value || endInput.value < minEnd) {
                endInput.value = minEnd;
            }
        }
        return firstFree;
    };

    return {
        resolveFirstFree,
        syncStartEndMins
    };
})();
