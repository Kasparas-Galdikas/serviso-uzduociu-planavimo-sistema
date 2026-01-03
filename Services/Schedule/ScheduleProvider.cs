using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data.Seed;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    public class ScheduleProvider : IScheduleProvider
    {
        private readonly ScheduleDbContext _db;

        public ScheduleProvider(ScheduleDbContext db)
        {
            _db = db;
        }

        public SchedulePageViewModel GetSchedule(string view, string? date, string? location, string? status, bool preview = false, string? plannedStart = null, int plannedWeeks = 1)
        {
            var selectedDate = DateResolver.ParseOrToday(date);
            var viewMode = NormalizeView(view);
            var weekStart = WeekCalculator.GetWeekStart(selectedDate);
            var weekEnd = WeekCalculator.GetWeekEnd(selectedDate);
            var actualWeekStart = WeekCalculator.GetWeekStart(DateTime.Today);
            var actualWeekEnd = WeekCalculator.GetWeekEnd(DateTime.Today);
            var displayStart = viewMode == "week" ? weekStart : selectedDate.Date;
            var displayEnd = viewMode == "week" ? weekEnd : selectedDate.Date;

            var activePlanId = _db.Plans.AsNoTracking()
                .Where(p => p.Status == PlanStatus.Active)
                .Select(p => (long?)p.Id)
                .FirstOrDefault();

            var technicians = _db.Technicians
                .AsNoTracking()
                .Include(t => t.Region)
                .Select(t => new Technician
                {
                    Id = (int)t.Id,
                    Name = t.Name,
                    Location = t.Region != null ? t.Region.Name : string.Empty,
                    RulesSummary = t.RulesSummary ?? string.Empty,
                    WeeklyHours = 0,
                    WeeklyLimit = (int)t.WeeklyLimit,
                    DailyHours = 0,
                    DailyLimit = (int)t.DailyLimit,
                    TravelSwitches = t.TravelSwitchLimit
                })
                .ToList();

            var activeTasks = new List<TaskItem>();
            DateTime? activePlanLastEnd = null;
            if (activePlanId.HasValue)
            {
                var weekStartUtc = DateTime.SpecifyKind(weekStart, DateTimeKind.Utc);
                var weekEndUtcExclusive = DateTime.SpecifyKind(weekEnd.AddDays(1), DateTimeKind.Utc);

                var tasksQuery = _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.PlanId == activePlanId.Value &&
                                t.StartAt >= weekStartUtc &&
                                t.StartAt < weekEndUtcExclusive)
                    .Include(t => t.Evaluation);

                activeTasks = tasksQuery
                    .AsEnumerable()
                    .Select(t => MapTask(t))
                    .ToList();

                activePlanLastEnd = _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.PlanId == activePlanId.Value)
                    .Select(t => (DateTime?)t.EndAt)
                    .Max();
            }

            var tasks = new List<TaskItem>();
            tasks.AddRange(activeTasks);

            var lastPlannedEnd = activePlanLastEnd?.Date ?? weekEnd.Date;
            var planningWindow = PlanningWindowCalculator.Calculate(plannedStart, plannedWeeks, lastPlannedEnd);

            if (preview)
            {
                var plannedTasks = GeneratePlannedTasks(planningWindow.Start, planningWindow.TotalDays, technicians);
                tasks.AddRange(plannedTasks);
            }

            var availableLocations = technicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
            var availableStatuses = new List<string> { "Visi", "Normali apkrova", "Apkrova", "Perkrova" };

            var decisions = SeedDecisions();
            var showIssues = (preview && (WeekCalculator.IsInRange(selectedDate, actualWeekStart, actualWeekEnd) || PlanningWindowCalculator.IsWithin(selectedDate, planningWindow)))
                            || (!preview && WeekCalculator.IsInRange(selectedDate, actualWeekStart, actualWeekEnd));
            var issues = showIssues ? SeedIssues(selectedDate, decisions) : new List<IssueItem>();

            (technicians, tasks) = ApplyLocationFilter(location, technicians, tasks);
            (technicians, tasks) = ApplyStatusFilter(status, technicians, tasks);

            return new SchedulePageViewModel
            {
                WeekStart = weekStart,
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

        private IEnumerable<TaskItem> GeneratePlannedTasks(
        DateTime start,
         int totalDays,
        List<Technician> technicians)
        {
            var planned = MockScheduleData.SeedTasksRange(start, totalDays, true);

            var mockTechs = MockScheduleData.SeedTechnicians()
                .ToDictionary(t => t.Id);

            var technicianByName = technicians
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var t in planned)
            {
                Technician? tech = null;

                if (mockTechs.TryGetValue(t.TechnicianId, out var mockTech) &&
                    technicianByName.TryGetValue(mockTech.Name, out var matched))
                {
                    tech = matched;
                }

                tech ??= technicians.FirstOrDefault();

                yield return new TaskItem
                {
                    Id = t.Id,
                    TechnicianId = tech?.Id ?? t.TechnicianId,
                    Title = t.Title,
                    Location = t.Location,
                    Start = DateTime.SpecifyKind(t.Start, DateTimeKind.Utc),
                    End = DateTime.SpecifyKind(t.End, DateTimeKind.Utc),
                    Priority = t.Priority,
                    Status = t.Status,
                    IsConflict = t.IsConflict,
                    IsLoad = t.IsLoad,
                    IsTravelSwitch = t.IsTravelSwitch,
                    IsNewPlan = true,
                    Risk = t.Risk
                };
            }
        }


        private TaskItem MapTask(Data.TaskEntity task)
        {
            var eval = task.Evaluation;
            return new TaskItem
            {
                Id = (int)task.Id,
                TechnicianId = (int)task.TechnicianId,
                Title = task.Title,
                Location = task.Location,
                Start = DateTime.SpecifyKind(task.StartAt, DateTimeKind.Utc),
                End = DateTime.SpecifyKind(task.EndAt, DateTimeKind.Utc),
                Priority = task.Priority.ToString().ToLowerInvariant(),
                Status = task.Status.ToString().ToLowerInvariant(),
                IsConflict = eval?.IsConflict ?? false,
                IsLoad = eval?.IsOverloaded ?? false,
                IsTravelSwitch = eval?.TravelRequired ?? false,
                IsNewPlan = false,
                Risk = eval?.RiskLevel?.ToString().ToLowerInvariant()
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

        private static List<DecisionOption> SeedDecisions() => new()
        {
            new() { Title = "Perkelti Klaipedos uzduoti", Description = "Atideti Klaipedos perjungima i ryto langa.", RiskLevel = "vidutinis", LoadImpact = "-2h Vilnius", TravelImpact = "+1 kelione", IsRecommended = true },
            new() { Title = "Paskirstyti apkrova", Description = "Dali Kauno darbu perleisti Gabijai.", RiskLevel = "zemas", LoadImpact = "-1h Kaunas, +1h Siauliai", TravelImpact = "0", IsRecommended = false },
            new() { Title = "Palikti kaip yra", Description = "Jokiu pakeitimu - stebeti konfliktus.", RiskLevel = "aukstas", LoadImpact = "0", TravelImpact = "0", IsRecommended = false }
        };

        private static List<IssueItem> SeedIssues(DateTime selectedDate, IEnumerable<DecisionOption> decisions)
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
}
