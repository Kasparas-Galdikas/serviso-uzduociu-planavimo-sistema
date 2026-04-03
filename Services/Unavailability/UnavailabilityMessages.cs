namespace ServiceTaskPlanningSystem.Services.Unavailability
{
    /// <summary>
    /// Single source of truth for all user-facing error messages produced by
    /// the unavailability feature — controller validation, service-layer errors,
    /// and infrastructure failures all read from here.
    /// </summary>
    internal static class UnavailabilityMessages
    {
        // ── Input parsing ────────────────────────────────────────────────────
        public const string InvalidStartFormat =
            "Neteisinga pradzios data. Naudokite yyyy-MM-ddTHH:mm formata.";

        public const string InvalidEndFormat =
            "Neteisinga pabaigos data. Naudokite yyyy-MM-ddTHH:mm formata.";

        // ── Basic period rules ───────────────────────────────────────────────
        public const string EndNotAfterStart =
            "Pabaigos data ir laikas turi buti velesni nei pradzia.";

        public const string SlotMisaligned =
            "Laikas turi atitikti grafiko 30 minuciu zingsni.";

        public const string PeriodInPast =
            "Negalima zymeti neaktyvumo praeityje.";

        private const string OutsideWorkingWindowTemplate =
            "Neaktyvumo periodas turi tilpti i darbo laiko langa ({0}-{1}).";

        /// <param name="windowStart">Formatted start e.g. "08:00"</param>
        /// <param name="windowEnd">Formatted end e.g. "20:00"</param>
        public static string OutsideWorkingWindow(string windowStart, string windowEnd) =>
            string.Format(OutsideWorkingWindowTemplate, windowStart, windowEnd);

        // ── Authorization ────────────────────────────────────────────────────
        public const string Unauthorized = "Nera teisiu.";

        // ── Entity lookup ────────────────────────────────────────────────────
        public const string PeriodNotFound = "Neaktyvumo periodas nerastas.";

        private const string TechnicianNotFoundTemplate = "Technikas {0} nerastas.";

        public static string TechnicianNotFound(long technicianId) =>
            string.Format(TechnicianNotFoundTemplate, technicianId);

        // ── Concurrency / stale data ─────────────────────────────────────────
        public const string StaleTasksChanged =
            "Paveiktos uzduotys pasikeite. Atnaujinkite lentele ir bandykite dar karta.";

        public const string StaleDataChanged =
            "Duomenys pasikeite. Atnaujinkite lentele ir bandykite dar karta.";

        // ── Restore failures ─────────────────────────────────────────────────
        public const string RestoreItemFailed =
            "Nepavyko atkurti bent vienos uzduoties i pradine laika.";

        private const string RestoreItemMissingDataTemplate =
            "Nepakanka duomenu atkurti uzduoti '{0}' is eiles.";

        private const string RestoreItemPlacementFailedTemplate =
            "Nepavyko atkurti uzduoties '{0}': {1}";

        public static string RestoreItemMissingData(string title) =>
            string.Format(RestoreItemMissingDataTemplate, title);

        public static string RestoreItemPlacementFailed(string title, string? reason) =>
            string.Format(RestoreItemPlacementFailedTemplate, title, reason);

        // ── Infrastructure ───────────────────────────────────────────────────
        public const string MigrationRequired =
            "Nepavyko pasalinti periodo. Perleiskite programa, kad butu pritaikyta naujausia migracija.";
    }
}
