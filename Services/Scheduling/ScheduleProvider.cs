using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.Scheduling
{
    public class ScheduleProvider : IScheduleProvider
    {
        private readonly ScheduleDbContext _db;
        private readonly ILogger<ScheduleProvider> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly Services.Authorization.ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;

        public ScheduleProvider(ScheduleDbContext db, IServiceScopeFactory scopeFactory, IPlanningCalendar planningCalendar, Services.Authorization.ITaskPermissionService taskPermissionService, IScheduleTimeService timeService, ILogger<ScheduleProvider> logger)
        {
            _db = db;
            _scopeFactory = scopeFactory;
            _planningCalendar = planningCalendar;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _logger = logger;
        }

        public async Task<SchedulePageViewModel> GetScheduleAsync(string view, string? date, string? location, string? status, long? technicianId, PlanEntity? activePlan, PlanEntity? draftPlan, UserContext user, int timezoneOffsetMinutes)
        {
            var selectedDate = DateResolver.ParseOrToday(date);
            var viewMode = NormalizeView(view);
            var weekStart = WeekCalculator.GetWeekStart(selectedDate);
            var weekEnd = WeekCalculator.GetWeekEnd(selectedDate);
            var weekStartUtc = DateTime.SpecifyKind(weekStart, DateTimeKind.Utc);
            var weekEndUtcExclusive = DateTime.SpecifyKind(weekEnd.AddDays(1), DateTimeKind.Utc);
            var boundary = await _planningCalendar.GetBoundaryAsync();

            _logger.LogInformation("svc.schedule.request view={ViewMode} date={Date} week={WeekStart}-{WeekEnd} location={Location} status={Status} technicianId={TechnicianId}", viewMode, selectedDate, weekStart, weekEnd, location, status, technicianId);

            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.Region)
                .Include(t => t.User)
                .Select(t => new Technician
                {
                    Id = (int)t.Id,
                    Name = t.User != null
                        ? (!string.IsNullOrWhiteSpace(t.User.Name) ? t.User.Name : t.User.Email)
                        : string.Empty,
                    Location = t.Region != null ? t.Region.Name : string.Empty,
                    RulesSummary = t.RulesSummary ?? string.Empty,
                    WeeklyHours = 0,
                    WeeklyLimit = (int)t.WeeklyLimit,
                    DailyHours = 0,
                    DailyLimit = (int)t.DailyLimit,
                    TravelSwitches = t.TravelSwitchLimit
                })
                .ToListAsync();

            var taskEntities = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .Include(t => t.Evaluation)
                .Where(t => t.StartAt < weekEndUtcExclusive && t.EndAt > weekStartUtc)
                .ToListAsync();

            var taskVersions = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.StartAt < weekEndUtcExclusive && t.EndAt > weekStartUtc)
                .Select(t => new { t.Id, Version = EF.Property<uint>(t, "xmin") })
                .ToDictionaryAsync(x => x.Id, x => x.Version);

            var plansInView = taskEntities
                .Select(t => t.Plan)
                .Where(p => p != null)
                .Distinct()
                .ToList();

            // Trigger repair for any plan missing evaluations in this window.
            var planIdsMissingEval = taskEntities
                .Where(t => t.PlanId != null && t.Evaluation == null)
                .Select(t => t.PlanId!.Value)
                .Distinct()
                .ToList();

            // Debug logging: which plans contribute tasks for this view.
            var plansById = taskEntities
                .Where(t => t.Plan != null)
                .GroupBy(t => t.PlanId!.Value)
                .Select(g => new
                {
                    PlanId = g.Key,
                    Status = g.First().Plan!.Status,
                    PeriodStart = g.First().Plan!.PeriodStart,
                    PeriodEnd = g.First().Plan!.PeriodEnd,
                    TaskCount = g.Count()
                })
                .ToList();
            _logger.LogInformation("svc.schedule.plans view={ViewMode} count={Count}", viewMode, plansById.Count);

            foreach (var pid in planIdsMissingEval)
            {
                TriggerEvaluationRepair(pid);
            }

            var draftExists = plansInView.Any(p => p!.Status == PlanStatus.Draft);

            var tasks = new List<TaskItem>();
            foreach (var t in taskEntities)
            {
                var planStatus = t.Plan?.Status ?? PlanStatus.Active;
                var source = planStatus switch
                {
                    PlanStatus.Draft => TaskPlanSource.Draft,
                    PlanStatus.Superseded => TaskPlanSource.Superseded,
                    _ => TaskPlanSource.Active
                };
                taskVersions.TryGetValue(t.Id, out var version);
                var task = MapTask(t, technicians, planStatus, version, timezoneOffsetMinutes);
                task.Source = source;
                task.IsEditable = await _taskPermissionService.CanEditTaskAsync(t, user, timezoneOffsetMinutes);
                tasks.Add(task);
            }

            tasks = tasks
                .OrderBy(t => t.Start)
                .ThenBy(t => t.Source == TaskPlanSource.Active ? 0 : t.Source == TaskPlanSource.Draft ? 1 : 2)
                .ToList();

            var availableLocations = technicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
            var availableStatuses = BuildAvailableStatuses(tasks);

            var issues = BuildIssues(tasks, technicians);
            var summary = BuildSummary(tasks, technicians);
            _logger.LogInformation("svc.schedule.summary tasks={Tasks} issues={Issues} locations={Locations} statuses={Statuses}", tasks.Count, issues.Count, availableLocations.Count, availableStatuses.Count);

            var lastPlanPeriod = await _db.Plans
                .AsNoTracking()
                .OrderByDescending(p => p.PeriodEnd)
                .Select(p => new { p.PeriodStart, p.PeriodEnd })
                .FirstOrDefaultAsync();

            var plannedPeriodEnd = draftPlan?.PeriodEnd
                ?? activePlan?.PeriodEnd
                ?? boundary.LastOccupiedDate
                ?? lastPlanPeriod?.PeriodEnd
                ?? default;
            var firstFreeDate = boundary.FirstFreeDate;

            (technicians, tasks) = ApplyLocationFilter(location, technicians, tasks);
            (technicians, tasks) = ApplyStatusFilter(status, technicians, tasks);
            (technicians, tasks) = ApplyTechnicianFilter(technicianId, technicians, tasks);

            var planStatusText = draftPlan?.Status.ToString() ?? activePlan?.Status.ToString();

            return new SchedulePageViewModel
            {
                WeekStart = weekStart,
                SelectedDate = selectedDate,
                IsManager = user.IsManager,
                ViewMode = viewMode,
                SelectedTechnicianId = technicianId,
                Technicians = technicians,
                Tasks = tasks,
                Issues = issues,
                AvailableLocations = availableLocations,
                AvailableStatuses = availableStatuses,
                Summary = summary,
                IsPreviewMode = draftPlan != null,
                HasDraft = draftPlan != null,
                IsPlanningMode = draftPlan != null,
                CurrentPlanId = (draftPlan ?? activePlan)?.Id,
                CurrentPlanStatus = planStatusText,
                PlannedPeriodStart = draftPlan?.PeriodStart ?? activePlan?.PeriodStart ?? lastPlanPeriod?.PeriodStart ?? default,
                PlannedPeriodEnd = plannedPeriodEnd,
                PlannedWeeks = draftPlan?.Weeks ?? activePlan?.Weeks ?? 1,
                FirstFreeDate = firstFreeDate
            };
        }

        private TaskItem MapTask(Data.TaskEntity task, List<Technician> technicians, PlanStatus planStatus, uint version, int timezoneOffsetMinutes)
        {
            var eval = task.Evaluation;
            var tech = technicians.FirstOrDefault(t => t.Id == task.TechnicianId);
            var status = eval?.Status ?? LoadState.Normal;

            // Convert UTC times to local time for display
            var startLocal = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var endLocal = _timeService.ConvertUtcToLocal(task.EndAt, timezoneOffsetMinutes);

            return new TaskItem
            {
                Id = (int)task.Id,
                PlanId = task.PlanId ?? 0,
                PlanStatus = planStatus,
                TechnicianId = (int)task.TechnicianId,
                Version = version,
                Title = task.Title,
                Location = task.Location,
                Start = startLocal,
                End = endLocal,
                Priority = task.Priority.ToString().ToLowerInvariant(),
                Status = task.Status.ToString().ToLowerInvariant(),
                IsConflict = status == LoadState.Conflict,
                IsLoad = status == LoadState.Load,
                IsTravelSwitch = eval?.TravelRequired == true,
                IsNewPlan = false,
                Risk = eval?.RiskLevel?.ToString().ToLowerInvariant(),
                OverloadHours = eval?.OverloadHours ?? 0,
                SwitchCount = eval?.SwitchCount ?? 0,
                LoadStatus = status.ToString().ToLowerInvariant()
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

        private static (List<Technician> technicians, List<TaskItem> tasks) ApplyTechnicianFilter(long? technicianId, List<Technician> technicians, List<TaskItem> tasks)
        {
            if (!technicianId.HasValue)
            {
                return (technicians, tasks);
            }

            var filteredTechnicians = technicians
                .Where(t => t.Id == technicianId.Value)
                .ToList();

            var filteredTasks = tasks
                .Where(t => t.TechnicianId == technicianId.Value)
                .ToList();

            return (filteredTechnicians, filteredTasks);
        }

        private static List<string> BuildAvailableStatuses(IEnumerable<TaskItem> tasks)
        {
            var statusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "normal", "Normali apkrova" },
                { "load", "Apkrova" },
                { "conflict", "Perkrova" }
            };

            var statuses = tasks
                .Select(t => t.LoadStatus)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(s => statusMap.TryGetValue(s, out var label) ? label : s)
                .OrderBy(s => s)
                .ToList();

            // Ensure all standard statuses are available even if not present in current data.
            foreach (var label in statusMap.Values)
            {
                if (!statuses.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    statuses.Add(label);
                }
            }

            statuses.Insert(0, "Visi");
            return statuses;
        }

        private static List<IssueItem> BuildIssues(IEnumerable<TaskItem> tasks, IEnumerable<Technician> technicians)
        {
            var techLookup = technicians.ToDictionary(t => t.Id);
            var issues = new List<IssueItem>();

            foreach (var task in tasks)
            {
                var techName = techLookup.TryGetValue(task.TechnicianId, out var tech) ? tech.Name : $"Technikas #{task.TechnicianId}";
                if (task.IsConflict)
                {
                    issues.Add(new IssueItem
                    {
                        Id = $"task-{task.Id}",
                        Title = $"[!] {techName} {task.Start:HH:mm}-{task.End:HH:mm}",
                        Detail = "Persidengiantys darbai"
                    });
                    continue;
                }

                if (task.OverloadHours > 0)
                {
                    issues.Add(new IssueItem
                    {
                        Id = $"task-{task.Id}",
                        Title = $"[~] {techName} {task.Start:HH:mm}-{task.End:HH:mm}",
                        Detail = $"Apkrova virsija limita +{task.OverloadHours:0.##}h"
                    });
                }
                else if (task.IsTravelSwitch)
                {
                    issues.Add(new IssueItem
                    {
                        Id = $"task-{task.Id}",
                        Title = $"[~] {techName} {task.Start:HH:mm}-{task.End:HH:mm}",
                        Detail = "Virsyti kelioniu perjungimai"
                    });
                }
            }

            return issues;
        }

        private static ScheduleSummary BuildSummary(IEnumerable<TaskItem> tasks, IEnumerable<Technician> technicians)
        {
            var techLookup = technicians.ToDictionary(t => t.Id);
            var switchExceedances = tasks.Count(t => t.IsTravelSwitch);

            return new ScheduleSummary
            {
                TotalTasks = tasks.Count(),
                ConflictTasks = tasks.Count(t => t.IsConflict),
                OverloadHours = tasks.Sum(t => t.OverloadHours),
                SwitchExceedances = switchExceedances
            };
        }

        private void TriggerEvaluationRepair(long planId)
        {
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var evaluator = scope.ServiceProvider.GetRequiredService<IEvaluationEngine>();
                try
                {
                    await evaluator.RecalculatePlanAsync(planId);
                    _logger.LogInformation("Triggered evaluation repair for plan {PlanId} due to missing evaluations.", planId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to repair evaluations for plan {PlanId} on read.", planId);
                }
            });
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
