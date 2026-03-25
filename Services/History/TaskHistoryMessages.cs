using System;
using ServiceTaskPlanningSystem.Services.PlanManagement;

namespace ServiceTaskPlanningSystem.Services.History
{
    /// <summary>
    /// Centralized mapper from operation result enums to human-readable summary strings and outcome codes.
    /// Every call site in PlanService uses ForXxx() – no summary string is built inline in the business layer.
    /// </summary>
    public static class TaskHistoryMessages
    {
        private static string Hhmm(DateTime utc, int tzOffsetMinutes) =>
            utc.AddMinutes(tzOffsetMinutes).ToString("HH:mm");

        public static (string summary, string outcome) ForCreate(TaskCreateResult r, string title) => r.Error switch
        {
            TaskCreateError.None => ($"Sukurta užduotis: {title}.", "success"),
            TaskCreateError.Overlap => ($"Nepavyko sukurti užduoties – laiko persidengimas ({title}).", "failure"),
            TaskCreateError.TravelInfeasible => ("Nepavyko sukurti užduoties – kelionės laiko apribojimas.", "failure"),
            TaskCreateError.InvalidWindow => ("Nepavyko sukurti užduoties – neteisingas laiko langas.", "failure"),
            TaskCreateError.NotEditable => ("Nepavyko sukurti užduoties – planas neredaguojamas.", "failure"),
            TaskCreateError.TechnicianNotFound => ("Nepavyko sukurti užduoties – technikas nerastas.", "failure"),
            _ => (r.Message ?? "Kūrimas nepavyko.", "failure")
        };

        public static (string summary, string outcome) ForUpdate(TaskUpdateResult r, string title) => r.Error switch
        {
            TaskUpdateError.None => ($"Atnaujinta užduotis: {title}.", "success"),
            TaskUpdateError.NotEditable => ("Nepavyko atnaujinti užduoties – planas neredaguojamas.", "failure"),
            TaskUpdateError.TravelInfeasible => ("Nepavyko atnaujinti užduoties – kelionės apribojimas.", "failure"),
            TaskUpdateError.InvalidWindow => ("Nepavyko atnaujinti užduoties – neteisingas laiko langas.", "failure"),
            TaskUpdateError.TechnicianNotFound => ("Nepavyko atnaujinti užduoties – technikas nerastas.", "failure"),
            TaskUpdateError.NotFound => ("Nepavyko atnaujinti užduoties – užduotis nerasta.", "warning"),
            _ => (r.Message ?? "Užduoties atnaujinimas nepavyko.", "failure")
        };

        public static (string summary, string outcome) ForMove(
            TaskMoveResult r, string title,
            DateTime? oldStart = null, DateTime? newStart = null,
            int tz = 0, bool technicianChanged = false,
            string? newTechnicianName = null) => r.Error switch
        {
            TaskMoveError.None => BuildMoveSuccess(title, oldStart, newStart, tz, technicianChanged, newTechnicianName),
            TaskMoveError.Overlap => ($"Nepavyko perkelti užduoties – laiko persidengimas ({title}).", "failure"),
            TaskMoveError.TravelInfeasible => ("Nepavyko perkelti užduoties – kelionės apribojimas.", "failure"),
            TaskMoveError.Stale => ("Nepavyko perkelti užduoties – versijų konfliktas.", "warning"),
            TaskMoveError.NotEditable => ("Nepavyko perkelti užduoties – planas neredaguojamas.", "failure"),
            TaskMoveError.TechnicianNotFound => ("Nepavyko perkelti užduoties – technikas nerastas.", "failure"),
            TaskMoveError.InvalidWindow => ("Nepavyko perkelti užduoties – neteisingas laiko langas.", "failure"),
            TaskMoveError.NotFound => ("Nepavyko perkelti užduoties – užduotis nerasta.", "warning"),
            _ => (r.Message ?? "Perkėlimas nepavyko.", "failure")
        };

        private static (string, string) BuildMoveSuccess(
            string title, DateTime? oldStart, DateTime? newStart, int tz,
            bool techChanged, string? newTechnicianName)
        {
            var time = oldStart.HasValue && newStart.HasValue
                ? $" ({Hhmm(oldStart.Value, tz)} → {Hhmm(newStart.Value, tz)})"
                : string.Empty;

            var tech = techChanged
                ? $", technikui {(string.IsNullOrWhiteSpace(newTechnicianName) ? "nenurodytas" : newTechnicianName)}"
                : string.Empty;

            return ($"Perkelta užduotis: {title}{time}{tech}.", "success");
        }

        public static (string summary, string outcome) ForReschedule(
            TaskTimeUpdateResult r, string title,
            DateTime? oldStart = null, DateTime? oldEnd = null,
            DateTime? newStart = null, DateTime? newEnd = null,
            int tz = 0) => r.Error switch
        {
            TaskTimeUpdateError.None => BuildRescheduleSuccess(title, oldStart, oldEnd, newStart, newEnd, tz),
            TaskTimeUpdateError.Overlap => ("Nepavyko keisti laiko – persidengimas.", "failure"),
            TaskTimeUpdateError.TravelInfeasible => ("Nepavyko keisti laiko – kelionės apribojimas.", "failure"),
            TaskTimeUpdateError.Stale => ("Nepavyko keisti laiko – versijų konfliktas.", "warning"),
            TaskTimeUpdateError.NotEditable => ("Nepavyko keisti laiko – planas neredaguojamas.", "failure"),
            TaskTimeUpdateError.InvalidWindow => ("Nepavyko keisti laiko – neteisingas laiko langas.", "failure"),
            TaskTimeUpdateError.NotFound => ("Nepavyko keisti laiko – užduotis nerasta.", "warning"),
            _ => (r.Message ?? "Laiko keitimas nepavyko.", "failure")
        };

        private static (string, string) BuildRescheduleSuccess(
            string title, DateTime? oldStart, DateTime? oldEnd,
            DateTime? newStart, DateTime? newEnd, int tz)
        {
            var time = oldStart.HasValue && newStart.HasValue
                ? $" ({Hhmm(oldStart.Value, tz)} - {Hhmm(oldEnd!.Value, tz)} → {Hhmm(newStart.Value, tz)} - {Hhmm(newEnd!.Value, tz)})"
                : string.Empty;

            return ($"Laikas pakeistas užduočiai: {title}{time}.", "success");
        }

        public static (string summary, string outcome) ForSwap(TaskSwapResult r, string titleA, string titleB) => r.Error switch
        {
            TaskSwapError.None => ($"Sukeista: {titleA} ↔ {titleB}.", "success"),
            TaskSwapError.Overlap => ("Nepavyko sukeisti – laiko persidengimas.", "failure"),
            TaskSwapError.TravelInfeasible => ("Nepavyko sukeisti – kelionės apribojimas.", "failure"),
            TaskSwapError.Stale => ("Nepavyko sukeisti – versijų konfliktas.", "warning"),
            TaskSwapError.InvalidSwap => (r.Message ?? "Nepavyko sukeisti – užduotys nesuderinamos.", "failure"),
            TaskSwapError.NotFound => ("Nepavyko sukeisti – pirma užduotis nerasta.", "warning"),
            TaskSwapError.TargetNotFound => ("Nepavyko sukeisti – antra užduotis nerasta.", "warning"),
            TaskSwapError.NotEditable => ("Nepavyko sukeisti – planas neredaguojamas.", "failure"),
            _ => (r.Message ?? "Sukeitimas nepavyko.", "failure")
        };

        public static (string summary, string outcome) ForDelete(TaskDeleteResult r, string title) => r.Error switch
        {
            TaskDeleteError.None => ($"Ištrinta užduotis: {title}.", "success"),
            TaskDeleteError.NotEditable => ("Nepavyko ištrinti – planas neredaguojamas.", "failure"),
            TaskDeleteError.Stale => ("Nepavyko ištrinti – versijų konfliktas.", "warning"),
            TaskDeleteError.NotFound => ("Nepavyko ištrinti – užduotis nerasta.", "warning"),
            _ => (r.Message ?? "Trinimas nepavyko.", "failure")
        };

        public static (string summary, string outcome) ForTechnicianUpdate(TechnicianUpdateResult r, string newStatus) => r.Error switch
        {
            TechnicianUpdateError.None => ($"Technikas atnaujino statusą: {newStatus}.", "success"),
            TechnicianUpdateError.NotEditable => ("Technikas negalėjo atnaujinti – planas neredaguojamas.", "failure"),
            TechnicianUpdateError.NotOwner => ("Negalima atnaujinti – ne jūsų užduotis.", "failure"),
            TechnicianUpdateError.InvalidStatus => ("Neteisingas statusas.", "failure"),
            TechnicianUpdateError.Stale => ("Techniko atnaujinimas nepavyko – versijų konfliktas.", "warning"),
            TechnicianUpdateError.TaskNotFound => ("Techniko atnaujinimas nepavyko – užduotis nerasta.", "warning"),
            _ => (r.Message ?? "Techniko atnaujinimas nepavyko.", "failure")
        };

        public static (string summary, string outcome) ForBacklog(string action, string title) => action switch
        {
            "backlog_create" => ($"Užduoties šablonas pridėtas: {title}.", "success"),
            "backlog_update" => ($"Užduoties šablonas atnaujintas: {title}.", "success"),
            "backlog_delete" => ($"Užduoties šablonas pašalintas: {title}.", "success"),
            _ => ($"Eilės veiksmas: {title}.", "success")
        };
    }
}