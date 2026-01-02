using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    /// <summary>
    /// Central place for mock schedule data and basic filtering logic.
    /// Swap this implementation with a data-backed provider later.
    /// </summary>
    public class ScheduleProvider : IScheduleProvider
    {
        public SchedulePageViewModel GetSchedule(string view, string? date, string? location, string? status, bool preview = false, string? plannedStart = null, int plannedWeeks = 1)
        {
            var selectedDate = DateResolver.ParseOrToday(date);
            var viewMode = NormalizeView(view);

            var actualWeekStart = WeekCalculator.GetWeekStart(DateTime.Today);
            var actualWeekEnd = WeekCalculator.GetWeekEnd(DateTime.Today);
            var baseTasks = MockScheduleData.SeedTasksRange(actualWeekStart, 5, false);
            var lastPlannedEnd = baseTasks.Any() ? baseTasks.Max(t => t.End.Date) : actualWeekEnd;

            var planningWindow = PlanningWindowCalculator.Calculate(plannedStart, plannedWeeks, lastPlannedEnd);
            var isActualWeek = WeekCalculator.IsInRange(selectedDate, actualWeekStart, actualWeekEnd);
            var isPlannedInterval = PlanningWindowCalculator.IsWithin(selectedDate, planningWindow);

            var technicians = MockScheduleData.SeedTechnicians();
            var tasks = new List<TaskItem>();
            if (preview)
            {
                tasks.AddRange(baseTasks);
                var plannedTasks = MockScheduleData.SeedTasksRange(planningWindow.Start, planningWindow.TotalDays, true)
                    .Where(t => t.Start.Date >= planningWindow.Start && t.Start.Date <= planningWindow.End)
                    .ToList();
                tasks.AddRange(plannedTasks);
            }
            else if (isActualWeek)
            {
                tasks.AddRange(baseTasks);
            }

            var showIssues = (preview && (isActualWeek || isPlannedInterval)) || (!preview && isActualWeek);
            var decisions = MockScheduleData.SeedDecisions();
            var issues = showIssues ? MockScheduleData.SeedIssues(selectedDate, decisions) : new List<IssueItem>();

            var availableLocations = technicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
            var availableStatuses = new List<string> { "Visi", "Normali apkrova", "Apkrova", "Perkrova" };

            (technicians, tasks) = ApplyLocationFilter(location, technicians, tasks);
            (technicians, tasks) = ApplyStatusFilter(status, technicians, tasks);

            return new SchedulePageViewModel
            {
                WeekStart = WeekCalculator.GetWeekStart(selectedDate),
                SelectedDate = selectedDate,
                IsManager = true,
                ViewMode = viewMode,
                Technicians = technicians,
                Tasks = tasks,
                DecisionOptions = decisions,
                Issues = issues,
                AvailableLocations = availableLocations,
                AvailableStatuses = availableStatuses,
                IsPreviewMode = preview,
                PlannedPeriodStart = planningWindow.Start,
                PlannedPeriodEnd = planningWindow.End,
                PlannedWeeks = planningWindow.Weeks
            };
        }

        private static string NormalizeView(string view) =>
            string.Equals(view, "week", StringComparison.OrdinalIgnoreCase) ? "week" : "day";

        private static (List<Technician> technicians, List<TaskItem> tasks) ApplyLocationFilter(string? location, List<Technician> technicians, List<TaskItem> tasks)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return (technicians, tasks);
            }

            var filteredTasks = tasks
                .Where(t => string.Equals(t.Location, location, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var filteredTechnicians = technicians
                .Where(t => string.Equals(t.Location, location, StringComparison.OrdinalIgnoreCase) || filteredTasks.Any(x => x.TechnicianId == t.Id))
                .ToList();

            return (filteredTechnicians, filteredTasks);
        }

        private static (List<Technician> technicians, List<TaskItem> tasks) ApplyStatusFilter(string? status, List<Technician> technicians, List<TaskItem> tasks)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return (technicians, tasks);
            }

            var statusKey = status.ToLowerInvariant();
            var filteredTasks = statusKey switch
            {
                "normali apkrova" => tasks.Where(t => !t.IsLoad && !t.IsConflict).ToList(),
                "apkrova" => tasks.Where(t => t.IsLoad && !t.IsConflict).ToList(),
                "perkrova" => tasks.Where(t => t.IsConflict).ToList(),
                _ => tasks
            };

            var filteredTechnicians = technicians
                .Where(t => filteredTasks.Any(x => x.TechnicianId == t.Id))
                .ToList();

            return (filteredTechnicians, filteredTasks);
        }
    }

    internal static class DateResolver
    {
        private static readonly string[] AcceptedFormats = { "yyyy-MM-dd" };

        public static DateTime ParseOrToday(string? value) =>
            TryParse(value, out var parsed) ? parsed : DateTime.Today;

        public static DateTime? ParseOrNull(string? value) =>
            TryParse(value, out var parsed) ? parsed : null;

        private static bool TryParse(string? value, out DateTime parsed)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(value, AcceptedFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedDate))
            {
                parsed = parsedDate.Date;
                return true;
            }

            parsed = default;
            return false;
        }
    }

    internal static class WeekCalculator
    {
        public static DateTime GetWeekStart(DateTime date)
        {
            var localDate = date.Date;
            var offset = ((int)localDate.DayOfWeek + 6) % 7; // Monday = 0
            return localDate.AddDays(-offset);
        }

        public static DateTime GetWeekEnd(DateTime date) => GetWeekStart(date).AddDays(6);

        public static bool IsInRange(DateTime date, DateTime start, DateTime end) =>
            date.Date >= start.Date && date.Date <= end.Date;
    }

    internal readonly struct PlanningWindow
    {
        public PlanningWindow(DateTime start, DateTime end, int totalDays, int weeks)
        {
            Start = start.Date;
            End = end.Date;
            TotalDays = totalDays;
            Weeks = weeks;
        }

        public DateTime Start { get; }
        public DateTime End { get; }
        public int TotalDays { get; }
        public int Weeks { get; }
    }

    internal static class PlanningWindowCalculator
    {
        public static PlanningWindow Calculate(string? plannedStart, int plannedWeeks, DateTime lastPlannedEnd)
        {
            var requestedStart = DateResolver.ParseOrNull(plannedStart);
            var earliestStart = lastPlannedEnd.Date.AddDays(1);
            var effectiveStart = requestedStart.HasValue && requestedStart.Value >= earliestStart
                ? requestedStart.Value
                : earliestStart;

            var weeks = Math.Max(1, plannedWeeks);
            var daysRemainingInStartWeek = (WeekCalculator.GetWeekEnd(effectiveStart) - effectiveStart).Days + 1;
            var totalDays = daysRemainingInStartWeek + weeks * 7;
            var end = effectiveStart.AddDays(totalDays - 1);

            return new PlanningWindow(effectiveStart, end, totalDays, weeks);
        }

        public static bool IsWithin(DateTime date, PlanningWindow window) =>
            WeekCalculator.IsInRange(date, window.Start, window.End);
    }

    internal static class MockScheduleData
    {
        private static readonly Dictionary<int, string> Locations = new()
        {
            { 1, "Vilnius" }, { 2, "Kaunas" }, { 3, "Siauliai" }, { 4, "Klaipeda" }, { 5, "Panevezys" },
            { 6, "Vilnius" }, { 7, "Kaunas" }, { 8, "Vilnius" }, { 9, "Alytus" }, { 10, "Klaipeda" },
            { 11, "Vilnius" }, { 12, "Kaunas" }, { 13, "Siauliai" }, { 14, "Vilnius" }, { 15, "Kaunas" }
        };

        public static List<Technician> SeedTechnicians() => new()
        {
            new() { Id = 1, Name = "Austeja J.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h, savaites 40h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 2, Name = "Dovydas K.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h, savaites 45h", WeeklyHours = 22, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 3, Name = "Gabija P.", Location = "Siauliai", RulesSummary = "Dienos limitas 8h, savaites 42h", WeeklyHours = 15, WeeklyLimit = 42, DailyHours = 5, DailyLimit = 8, TravelSwitches = 3 },
            new() { Id = 4, Name = "Lukas V.", Location = "Klaipeda", RulesSummary = "Dienos limitas 8h", WeeklyHours = 19, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 5, Name = "Monika S.", Location = "Panevezys", RulesSummary = "Dienos limitas 8h", WeeklyHours = 12, WeeklyLimit = 40, DailyHours = 4, DailyLimit = 8, TravelSwitches = 0 },
            new() { Id = 6, Name = "Rokas M.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 21, WeeklyLimit = 35, DailyHours = 6, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 7, Name = "Greta B.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 27, WeeklyLimit = 45, DailyHours = 7, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 8, Name = "Mantas P.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 10, WeeklyLimit = 40, DailyHours = 3, DailyLimit = 8, TravelSwitches = 0 },
            new() { Id = 9, Name = "Ieva Z.", Location = "Alytus", RulesSummary = "Dienos limitas 8h", WeeklyHours = 8, WeeklyLimit = 40, DailyHours = 4, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 10, Name = "Paulius T.", Location = "Klaipeda", RulesSummary = "Dienos limitas 8h", WeeklyHours = 24, WeeklyLimit = 40, DailyHours = 7, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 11, Name = "Jurate R.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 16, WeeklyLimit = 40, DailyHours = 5, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 12, Name = "Tomas G.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 20, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 3 },
            new() { Id = 13, Name = "Elze K.", Location = "Siauliai", RulesSummary = "Dienos limitas 8h", WeeklyHours = 18, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 },
            new() { Id = 14, Name = "Vaidotas L.", Location = "Vilnius", RulesSummary = "Dienos limitas 8h", WeeklyHours = 30, WeeklyLimit = 40, DailyHours = 7, DailyLimit = 8, TravelSwitches = 2 },
            new() { Id = 15, Name = "Karolina N.", Location = "Kaunas", RulesSummary = "Dienos limitas 8h", WeeklyHours = 25, WeeklyLimit = 40, DailyHours = 6, DailyLimit = 8, TravelSwitches = 1 }
        };

        public static List<TaskItem> SeedTasksRange(DateTime startDate, int totalDays, bool markNewPlan)
        {
            var tasks = new List<TaskItem>();
            var id = 1;
            var durationOptions = new[] { 1.5, 2, 2.5, 3 };

            for (var dayOffset = 0; dayOffset < totalDays; dayOffset++)
            {
                var currentDate = startDate.Date.AddDays(dayOffset);
                var weekStart = WeekCalculator.GetWeekStart(currentDate);
                var weekShift = (GetStableSeed(weekStart) % 3) * 0.5;

                foreach (var techId in Locations.Keys)
                {
                    var start = 8.0 + weekShift;
                    var tasksToday = new List<TaskItem>();
                    var taskCount = 2 + ((techId + dayOffset + (int)(weekShift * 10)) % 3);

                    for (var i = 0; i < taskCount; i++)
                    {
                        var duration = durationOptions[(techId + dayOffset + i) % durationOptions.Length];
                        var end = start + duration;
                        if (end > 18) break;

                        var startDateTime = currentDate.AddHours(start);
                        var endDateTime = currentDate.AddHours(end);
                        tasksToday.Add(new TaskItem
                        {
                            Id = id++,
                            TechnicianId = techId,
                            Title = i == 0 ? "Ryto vizitas" : i == taskCount - 1 ? "Dienos pabaigos darbai" : "Darbai objektuose",
                            Location = Locations.TryGetValue(techId, out var loc) ? loc : "Vilnius",
                            Start = startDateTime,
                            End = endDateTime,
                            Priority = "vidutinis",
                            Status = "planuojama",
                            IsLoad = false,
                            IsConflict = false,
                            IsTravelSwitch = false,
                            IsNewPlan = markNewPlan,
                            Risk = "vidutinis"
                        });

                        start = end + 0.5;
                    }

                    if ((techId % 2 == 0 && currentDate.DayOfWeek == DayOfWeek.Friday) ||
                        (techId % 3 == 0 && currentDate.DayOfWeek == DayOfWeek.Tuesday))
                    {
                        var lastTask = tasksToday.LastOrDefault();
                        if (lastTask != null)
                        {
                            lastTask.End = lastTask.End.AddHours(1.5);
                        }
                    }

                    var totalHours = tasksToday.Sum(t => (t.End - t.Start).TotalHours);
                    var isConflict = totalHours > 8;
                    var isLoad = !isConflict && totalHours >= 6.5;

                    foreach (var t in tasksToday)
                    {
                        t.IsConflict = isConflict;
                        t.IsLoad = isLoad;
                        t.Priority = isConflict ? "aukstas" : isLoad ? "vidutinis" : "vidutinis";
                        t.Risk = isConflict ? "aukstas" : isLoad ? "vidutinis" : "zemas";
                    }

                    tasks.AddRange(tasksToday);
                }
            }

            return tasks;
        }

        public static List<DecisionOption> SeedDecisions() => new()
        {
            new() { Title = "Perkelti Klaipedos uzduoti", Description = "Atideti Klaipedos perjungima i ryto langa.", RiskLevel = "vidutinis", LoadImpact = "-2h Vilnius", TravelImpact = "+1 kelione", IsRecommended = true },
            new() { Title = "Paskirstyti apkrova", Description = "Dali Kauno darbu perleisti Gabijai.", RiskLevel = "zemas", LoadImpact = "-1h Kaunas, +1h Siauliai", TravelImpact = "0", IsRecommended = false },
            new() { Title = "Palikti kaip yra", Description = "Jokiu pakeitimu - stebeti konfliktus.", RiskLevel = "aukstas", LoadImpact = "0", TravelImpact = "0", IsRecommended = false }
        };

        public static List<IssueItem> SeedIssues(DateTime selectedDate, IEnumerable<DecisionOption> decisions)
        {
            var decisionList = decisions.ToList();
            var dayName = selectedDate.ToString("dddd", CultureInfo.InvariantCulture);

            return new List<IssueItem>
            {
                new()
                {
                    Id = "task-3",
                    Title = $"[!] Austeja J. {dayName} 16:00-18:00",
                    Detail = "Klaipedos perjungimas persidengia su Vilniaus darbu",
                    Decisions = decisionList
                },
                new()
                {
                    Id = "task-5",
                    Title = $"[!] Dovydas K. {dayName} 12:00-15:00",
                    Detail = "Kaunas virsija dienos limita +1h",
                    Decisions = decisionList
                },
                new()
                {
                    Id = "task-8",
                    Title = $"[!] Gabija P. {dayName} 14:00-17:00",
                    Detail = "Papildoma kelione - perjungimas Klaipedoje",
                    Decisions = decisionList
                }
            };
        }

        private static int GetStableSeed(DateTime date)
        {
            var weekOfYear = ISOWeek.GetWeekOfYear(date);
            return unchecked((date.Year * 100) + weekOfYear);
        }
    }
}
