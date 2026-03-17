using System;
using ServiceTaskPlanningSystem.Services.PlanManagement;

namespace ServiceTaskPlanningSystem.Services.History
{
    /// <summary>
    /// Centralized mapper from operation result enums to human-readable summary strings and outcome codes.
    /// Every call site in PlanService uses ForXxx() — no summary string is built inline in the business layer.
    /// </summary>
    public static class TaskHistoryMessages
    {
        private static string Hhmm(DateTime utc, int tzOffsetMinutes) =>
            utc.AddMinutes(tzOffsetMinutes).ToString("HH:mm");

        // ── CREATE ──────────────────────────────────────────────────────────────
        public static (string summary, string outcome) ForCreate(TaskCreateResult r, string title) => r.Error switch
        {
            TaskCreateError.None             => ($"Sukurta: {title}.", "success"),
            TaskCreateError.Overlap          => ($"Nepavyko sukurti – laiko persidengimas ({title}).", "failure"),
            TaskCreateError.TravelInfeasible => ("Nepavyko sukurti – kelionės laiko apribojimas.", "failure"),
            TaskCreateError.InvalidWindow    => ("Nepavyko sukurti – neteisingas laiko langas.", "failure"),
            TaskCreateError.NotEditable      => ("Nepavyko sukurti – planas neredaguojamas.", "failure"),
            TaskCreateError.TechnicianNotFound => ("Nepavyko sukurti – technikas nerastas.", "failure"),
            _                                => (r.Message ?? "Kūrimas nepavyko.", "failure")
        };

        // ── UPDATE ──────────────────────────────────────────────────────────────
        public static (string summary, string outcome) ForUpdate(TaskUpdateResult r, string title) => r.Error switch
        {
            TaskUpdateError.None             => ($"Atnaujinta: {title}.", "success"),
            TaskUpdateError.NotEditable      => ("Nepavyko atnaujinti – planas neredaguojamas.", "failure"),
            TaskUpdateError.TravelInfeasible => ("Nepavyko atnaujinti – kelionės apribojimas.", "failure"),
            TaskUpdateError.InvalidWindow    => ("Nepavyko atnaujinti – neteisingas laiko langas.", "failure"),
            TaskUpdateError.TechnicianNotFound => ("Nepavyko atnaujinti – technikas nerastas.", "failure"),
            TaskUpdateError.NotFound         => ("Nepavyko atnaujinti – užduotis nerasta.", "warning"),
            _                                => (r.Message ?? "Atnaujinimas nepavyko.", "failure")
        };

        // ── MOVE ────────────────────────────────────────────────────────────────
        public static (string summary, string outcome) ForMove(
            TaskMoveResult r, string title,
            DateTime? oldStart = null, DateTime? newStart = null,
            int tz = 0, bool technicianChanged = false) => r.Error switch
        {
            TaskMoveError.None               => BuildMoveSuccess(title, oldStart, newStart, tz, technicianChanged),
            TaskMoveError.Overlap            => ($"Nepavyko perkelti – laiko persidengimas ({title}).", "failure"),
            TaskMoveError.TravelInfeasible   => ("Nepavyko perkelti – kelionės apribojimas.", "failure"),
            TaskMoveError.Stale              => ("Nepavyko perkelti – versijų konfliktas.", "warning"),
            TaskMoveError.NotEditable        => ("Nepavyko perkelti – planas neredaguojamas.", "failure"),
            TaskMoveError.TechnicianNotFound => ("Nepavyko perkelti – technikas nerastas.", "failure"),
            TaskMoveError.InvalidWindow      => ("Nepavyko perkelti – neteisingas laiko langas.", "failure"),
            TaskMoveError.NotFound           => ("Nepavyko perkelti – užduotis nerasta.", "warning"),
            _                                => (r.Message ?? "Perkėlimas nepavyko.", "failure")
        };

        private static (string, string) BuildMoveSuccess(
            string title, DateTime? oldStart, DateTime? newStart, int tz, bool techChanged)
        {
            var time = oldStart.HasValue && newStart.HasValue
                ? $" ({Hhmm(oldStart.Value, tz)} → {Hhmm(newStart.Value, tz)})"
                : "";
            var tech = techChanged ? ", kitas technikas" : "";
            return ($"Perkelta: {title}{time}{tech}.", "success");
        }

        // ── RESCHEDULE (time resize) ─────────────────────────────────────────────
        public static (string summary, string outcome) ForReschedule(
            TaskTimeUpdateResult r, string title,
            DateTime? oldStart = null, DateTime? oldEnd = null,
            DateTime? newStart = null, DateTime? newEnd = null,
            int tz = 0) => r.Error switch
        {
            TaskTimeUpdateError.None             => BuildRescheduleSuccess(title, oldStart, oldEnd, newStart, newEnd, tz),
            TaskTimeUpdateError.Overlap          => ("Nepavyko keisti laiko – persidengimas.", "failure"),
            TaskTimeUpdateError.TravelInfeasible => ("Nepavyko keisti laiko – kelionės apribojimas.", "failure"),
            TaskTimeUpdateError.Stale            => ("Nepavyko keisti laiko – versijų konfliktas.", "warning"),
            TaskTimeUpdateError.NotEditable      => ("Nepavyko keisti laiko – planas neredaguojamas.", "failure"),
            TaskTimeUpdateError.InvalidWindow    => ("Nepavyko keisti laiko – neteisingas laiko langas.", "failure"),
            TaskTimeUpdateError.NotFound         => ("Nepavyko keisti laiko – užduotis nerasta.", "warning"),
            _                                    => (r.Message ?? "Laiko keitimas nepavyko.", "failure")
        };

        private static (string, string) BuildRescheduleSuccess(
            string title, DateTime? oldStart, DateTime? oldEnd,
            DateTime? newStart, DateTime? newEnd, int tz)
        {
            var time = oldStart.HasValue && newStart.HasValue
                ? $" ({Hhmm(oldStart.Value, tz)}–{Hhmm(oldEnd!.Value, tz)} → {Hhmm(newStart.Value, tz)}–{Hhmm(newEnd!.Value, tz)})"
                : "";
            return ($"Laikas pakeistas: {title}{time}.", "success");
        }

        // ── SWAP ────────────────────────────────────────────────────────────────
        public static (string summary, string outcome) ForSwap(TaskSwapResult r, string titleA, string titleB) => r.Error switch
        {
            TaskSwapError.None               => ($"Sukeista: {titleA} ↔ {titleB}.", "success"),
            TaskSwapError.Overlap            => ("Nepavyko sukeisti – laiko persidengimas.", "failure"),
            TaskSwapError.TravelInfeasible   => ("Nepavyko sukeisti – kelionės apribojimas.", "failure"),
            TaskSwapError.Stale              => ("Nepavyko sukeisti – versijų konfliktas.", "warning"),
            TaskSwapError.InvalidSwap        => (r.Message ?? "Nepavyko sukeisti – užduotys nesuderinamos.", "failure"),
            TaskSwapError.NotFound           => ("Nepavyko sukeisti – pirma užduotis nerasta.", "warning"),
            TaskSwapError.TargetNotFound     => ("Nepavyko sukeisti – antra užduotis nerasta.", "warning"),
            TaskSwapError.NotEditable        => ("Nepavyko sukeisti – planas neredaguojamas.", "failure"),
            _                                => (r.Message ?? "Sukeitimas nepavyko.", "failure")
        };

        // ── DELETE ──────────────────────────────────────────────────────────────
        public static (string summary, string outcome) ForDelete(TaskDeleteResult r, string title) => r.Error switch
        {
            TaskDeleteError.None        => ($"Ištrinta: {title}.", "success"),
            TaskDeleteError.NotEditable => ("Nepavyko ištrinti – planas neredaguojamas.", "failure"),
            TaskDeleteError.Stale       => ("Nepavyko ištrinti – versijų konfliktas.", "warning"),
            TaskDeleteError.NotFound    => ("Nepavyko ištrinti – užduotis nerasta.", "warning"),
            _                           => (r.Message ?? "Trynimas nepavyko.", "failure")
        };

        // ── TECHNICIAN UPDATE ───────────────────────────────────────────────────
        public static (string summary, string outcome) ForTechnicianUpdate(TechnicianUpdateResult r, string newStatus) => r.Error switch
        {
            TechnicianUpdateError.None        => ($"Technikas atnaujino statusą: {newStatus}.", "success"),
            TechnicianUpdateError.NotEditable => ("Technikas negalėjo atnaujinti – planas neredaguojamas.", "failure"),
            TechnicianUpdateError.NotOwner    => ("Negalima atnaujinti – ne jūsų užduotis.", "failure"),
            TechnicianUpdateError.InvalidStatus => ("Negalimas statusas.", "failure"),
            TechnicianUpdateError.Stale       => ("Techniko atnaujinimas nepavyko – versijų konfliktas.", "warning"),
            TechnicianUpdateError.TaskNotFound => ("Techniko atnaujinimas nepavyko – užduotis nerasta.", "warning"),
            _                                 => (r.Message ?? "Techniko atnaujinimas nepavyko.", "failure")
        };
    }
}
