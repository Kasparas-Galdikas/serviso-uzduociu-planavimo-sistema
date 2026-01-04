using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    public class ScheduleProvider : IScheduleProvider
    {
        private readonly ScheduleDbContext _db;

        public ScheduleProvider(ScheduleDbContext db)
        {
            _db = db;
        }

        public SchedulePageViewModel GetSchedule(string view, string? date, string? location, string? status, PlanEntity? activePlan, PlanEntity? draftPlan)
        {
            var selectedDate = DateResolver.ParseOrToday(date);
            var viewMode = NormalizeView(view);
            var weekStart = WeekCalculator.GetWeekStart(selectedDate);
            var weekEnd = WeekCalculator.GetWeekEnd(selectedDate);
            var actualWeekStart = WeekCalculator.GetWeekStart(DateTime.Today);
            var actualWeekEnd = WeekCalculator.GetWeekEnd(DateTime.Today);

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

            var tasks = new List<TaskItem>();
            var weekStartUtc = DateTime.SpecifyKind(weekStart, DateTimeKind.Utc);
            var weekEndUtcExclusive = DateTime.SpecifyKind(weekEnd.AddDays(1), DateTimeKind.Utc);
            var draftRangeOverlapsWeek = draftPlan != null &&
                                         draftPlan.PeriodStart.Date <= weekEnd.Date &&
                                         draftPlan.PeriodEnd.Date >= weekStart.Date;

            if (activePlan != null)
            {
                var activeTasks = _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.PlanId == activePlan.Id &&
                                t.StartAt >= weekStartUtc &&
                                t.StartAt < weekEndUtcExclusive)
                    .Include(t => t.Evaluation)
                    .AsEnumerable()
                    .Select(t => TagTask(MapTask(t), TaskPlanSource.Active, false))
                    .ToList();
                tasks.AddRange(activeTasks);
            }

            if (draftPlan != null && draftRangeOverlapsWeek)
            {
                var draftTasks = _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.PlanId == draftPlan.Id &&
                                t.StartAt >= weekStartUtc &&
                                t.StartAt < weekEndUtcExclusive)
                    .Include(t => t.Evaluation)
                    .AsEnumerable()
                    .Select(t => TagTask(MapTask(t), TaskPlanSource.Draft, true))
                    .ToList();
                tasks.AddRange(draftTasks);
            }

            tasks = tasks
                .OrderBy(t => t.Start)
                .ThenBy(t => t.Source == TaskPlanSource.Active ? 0 : 1)
                .ToList();

            var availableLocations = technicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
            var availableStatuses = new List<string> { "Visi", "Normali apkrova", "Apkrova", "Perkrova" };

            var decisions = SeedDecisions();
            var showIssues = WeekCalculator.IsInRange(selectedDate, actualWeekStart, actualWeekEnd);
            var issues = showIssues ? SeedIssues(selectedDate, decisions) : new List<IssueItem>();

            (technicians, tasks) = ApplyLocationFilter(location, technicians, tasks);
            (technicians, tasks) = ApplyStatusFilter(status, technicians, tasks);

            var planStatusText = draftPlan?.Status.ToString() ?? activePlan?.Status.ToString();

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
                IsPreviewMode = draftPlan != null,
                HasDraft = draftPlan != null,
                IsPlanningMode = draftPlan != null,
                CurrentPlanId = (draftPlan ?? activePlan)?.Id,
                CurrentPlanStatus = planStatusText,
                PlannedPeriodStart = draftPlan?.PeriodStart ?? activePlan?.PeriodStart ?? default,
                PlannedPeriodEnd = draftPlan?.PeriodEnd ?? activePlan?.PeriodEnd ?? default,
                PlannedWeeks = draftPlan?.Weeks ?? activePlan?.Weeks ?? 1
            };
        }

        private TaskItem MapTask(Data.TaskEntity task)
        {
            var eval = task.Evaluation;
            return new TaskItem
            {
                Id = (int)task.Id,
                PlanId = task.PlanId ?? 0,
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

        private static TaskItem TagTask(TaskItem task, TaskPlanSource source, bool isEditable)
        {
            task.Source = source;
            task.IsEditable = isEditable;
            return task;
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
            if (statusKey == "visi")
            {
                return (technicians, tasks);
            }
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
}
