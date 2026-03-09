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

        public async Task<SchedulePageViewModel> GetScheduleAsync(string view, string? date, string? location, string? workload, string? status, string? informationType, long? technicianId, PlanEntity? activePlan, PlanEntity? draftPlan, UserContext user, int timezoneOffsetMinutes)
        {
            var selectedDate = DateResolver.ParseOrToday(date);
            var viewMode = NormalizeView(view);
            var infoType = NormalizeInformationType(informationType);
            if (string.Equals(viewMode, "week", StringComparison.OrdinalIgnoreCase))
            {
                infoType = "apkrova";
                status = null;
            }
            var weekStart = _timeService.GetWeekStart(selectedDate);
            var weekEnd = _timeService.GetWeekEnd(selectedDate);
            var weekStartUtc = DateTime.SpecifyKind(weekStart, DateTimeKind.Utc);
            var weekEndUtcExclusive = DateTime.SpecifyKind(weekEnd.AddDays(1), DateTimeKind.Utc);
            var boundary = await _planningCalendar.GetBoundaryAsync();
            var includeCanceled = string.Equals(infoType, "darbo-statusas", StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation("svc.schedule.request view={ViewMode} date={Date} week={WeekStart}-{WeekEnd} location={Location} workload={Workload} technicianId={TechnicianId}", viewMode, selectedDate, weekStart, weekEnd, location, workload, technicianId);

            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.WorkplaceAddress)
                .Include(t => t.User)
                .Select(t => new Technician
                {
                    Id = (int)t.Id,
                    Name = t.User != null
                        ? (!string.IsNullOrWhiteSpace(t.User.Name) ? t.User.Name : t.User.Email)
                        : string.Empty,
                    Location = t.WorkplaceAddress.City,
                    WeeklyHours = 0,
                    WeeklyLimit = (int)t.WeeklyLimit,
                    DailyHours = 0,
                    DailyLimit = (int)t.DailyLimit
                })
                .ToListAsync();

            var taskBaseQuery = _db.Tasks
                .AsNoTracking()
                .Where(t => t.StartAt < weekEndUtcExclusive && t.EndAt > weekStartUtc);

            if (!includeCanceled)
            {
                taskBaseQuery = taskBaseQuery.Where(t => t.Status != ScheduleTaskStatus.Canceled);
            }

            var taskEntities = await taskBaseQuery
                .Include(t => t.Plan)
                .Include(t => t.Evaluation)
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .ToListAsync();

            var taskVersions = await taskBaseQuery
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

            var travelMetricsByTaskId = await BuildTravelMetricsAsync(taskEntities, CancellationToken.None);
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
                var task = MapTask(t, technicians, planStatus, version, timezoneOffsetMinutes, travelMetricsByTaskId);
                task.Source = source;
                task.IsEditable = await _taskPermissionService.CanEditTaskAsync(t, user, timezoneOffsetMinutes);
                task.CanDelete = await _taskPermissionService.CanDeleteTaskAsync(t, user, timezoneOffsetMinutes);
                task.IsTimeExpired = !_timeService.IsTaskStillEditable(t.EndAt);
                tasks.Add(task);
            }

            tasks = tasks
                .OrderBy(t => t.Start)
                .ThenBy(t => t.Source == TaskPlanSource.Active ? 0 : t.Source == TaskPlanSource.Draft ? 1 : 2)
                .ToList();

            if (string.Equals(viewMode, "day", StringComparison.OrdinalIgnoreCase))
            {
                var travelBlocks = BuildTravelDisplayBlocks(tasks);
                if (travelBlocks.Count > 0)
                {
                    tasks.AddRange(travelBlocks);
                    tasks = tasks
                        .OrderBy(t => t.Start)
                        .ThenBy(t => t.IsTravelBlock ? 0 : 1)
                        .ThenBy(t => t.Source == TaskPlanSource.Active ? 0 : t.Source == TaskPlanSource.Draft ? 1 : 2)
                        .ToList();
                }
            }

            var availableLocations = technicians
                .Select(t => t.Location)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l)
                .ToList();
            var nonTravelTasks = tasks.Where(t => !t.IsTravelBlock).ToList();
            var availableWorkloadLevels = BuildAvailableWorkloadLevels(nonTravelTasks);

            var summaryTasks = includeCanceled
                ? nonTravelTasks.Where(t => !IsCanceledStatus(t.Status)).ToList()
                : nonTravelTasks;

            var issues = BuildIssues(summaryTasks, technicians);
            var summary = BuildSummary(summaryTasks, technicians);
            _logger.LogInformation("svc.schedule.summary tasks={Tasks} issues={Issues} locations={Locations} workload={Workload}", tasks.Count, issues.Count, availableLocations.Count, availableWorkloadLevels.Count);

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
            if (string.Equals(infoType, "darbo-statusas", StringComparison.OrdinalIgnoreCase))
            {
                (technicians, tasks) = ApplyStatusFilter(status, technicians, tasks);
            }
            else
            {
                (technicians, tasks) = ApplyWorkloadFilter(workload, viewMode, selectedDate, technicians, tasks);
            }
            (technicians, tasks) = ApplyTechnicianFilter(technicianId, technicians, tasks);

            var planStatusText = draftPlan?.Status.ToString() ?? activePlan?.Status.ToString();

            return new SchedulePageViewModel
            {
                WeekStart = weekStart,
                SelectedDate = selectedDate,
                IsManager = user.IsManager,
                ViewMode = viewMode,
                InformationType = infoType,
                SelectedTechnicianId = technicianId,
                Technicians = technicians,
                Tasks = tasks,
                Issues = issues,
                AvailableLocations = availableLocations,
                AvailableStatuses = availableWorkloadLevels,
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

        private TaskItem MapTask(
            Data.TaskEntity task,
            List<Technician> technicians,
            PlanStatus planStatus,
            uint version,
            int timezoneOffsetMinutes,
            IReadOnlyDictionary<long, (decimal? DistanceKm, int? DurationMinutes)> travelMetricsByTaskId)
        {
            var eval = task.Evaluation;
            var tech = technicians.FirstOrDefault(t => t.Id == task.TechnicianId);
            var status = eval?.Status ?? LoadState.Normal;
            var objectAddress = task.ServiceObject?.Address?.FullAddress;
            travelMetricsByTaskId.TryGetValue(task.Id, out var travelMetrics);

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
                // Prefer the object's address when available; fallback to legacy location field.
                Location = objectAddress ?? string.Empty,
                Start = startLocal,
                End = endLocal,
                Priority = task.Priority.ToString().ToLowerInvariant(),
                Status = task.Status.ToString().ToLowerInvariant(),
                IsConflict = status == LoadState.Conflict,
                IsLoad = status == LoadState.Load,
                IsNewPlan = false,
                OverloadHours = eval?.OverloadHours ?? 0,
                LoadStatus = status.ToString().ToLowerInvariant(),
                TravelDurationMinutes = travelMetrics.DurationMinutes.HasValue ? (decimal)travelMetrics.DurationMinutes.Value : null,
                TravelDistanceKm = travelMetrics.DistanceKm
            };
        }

        private static string NormalizeView(string view) =>
            string.Equals(view, "week", StringComparison.OrdinalIgnoreCase) ? "week" : "day";

        private static string NormalizeInformationType(string? informationType) =>
            string.Equals(informationType, "darbo-statusas", StringComparison.OrdinalIgnoreCase)
                ? "darbo-statusas"
                : "apkrova";

        private static string NormalizeStatusFilter(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return string.Empty;
            }

            var normalized = status.Trim()
                .ToLowerInvariant()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty);

            return normalized switch
            {
                "atlikta" => "completed",
                "problema" => "conflict",
                _ => normalized
            };
        }

        private static bool IsCanceledStatus(string? status) =>
            string.Equals(NormalizeStatusFilter(status), "canceled", StringComparison.OrdinalIgnoreCase);

        private static (List<Technician> technicians, List<TaskItem> tasks) ApplyLocationFilter(string? location, List<Technician> technicians, List<TaskItem> tasks)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return (technicians, tasks);
            }

            // Use technician city (Location) as the source of truth for location filtering.
            var techById = technicians.ToDictionary(t => t.Id);
            var filteredTasks = tasks
                .Where(t => techById.TryGetValue(t.TechnicianId, out var tech) &&
                            string.Equals(tech.Location, location, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var filteredTechnicians = technicians
                .Where(t => string.Equals(t.Location, location, StringComparison.OrdinalIgnoreCase) || filteredTasks.Any(x => x.TechnicianId == t.Id))
                .ToList();

            return (filteredTechnicians, filteredTasks);
        }

        private static (List<Technician> technicians, List<TaskItem> tasks) ApplyWorkloadFilter(string? workload, string viewMode, DateTime selectedDate, List<Technician> technicians, List<TaskItem> tasks)
        {
            if (string.IsNullOrWhiteSpace(workload))
            {
                return (technicians, tasks);
            }

            var workloadKey = workload.ToLowerInvariant();
            if (workloadKey == "visos" || workloadKey == "all")
            {
                return (technicians, tasks);
            }

            var isDayView = string.Equals(viewMode, "day", StringComparison.OrdinalIgnoreCase);

            // For weekly view: consider whole week window. For daily view: consider only the selected day.
            var tasksForMatching = isDayView
                ? tasks.Where(t => !t.IsTravelBlock && t.Start.Date == selectedDate.Date).ToList()
                : tasks.Where(t => !t.IsTravelBlock).ToList();

            // Show technicians who have at least one task in the selected workload bucket (keep their tasks intact).
            var techIdsByWorkload = tasksForMatching
                .GroupBy(t => t.TechnicianId)
                .Select(g => new
                {
                    TechnicianId = g.Key,
                    HasAnyConflict = g.Any(t => t.IsConflict),
                    HasAnyLoad = g.Any(t => t.IsLoad && !t.IsConflict),
                    HasAnyNormal = g.Any(t => !t.IsLoad && !t.IsConflict)
                })
                .ToList();

            var matchingTechIds = workloadKey switch
            {
                "normali" => techIdsByWorkload
                    .Where(x => x.HasAnyNormal)
                    .Select(x => x.TechnicianId)
                    .ToHashSet(),
                "apkrova" => techIdsByWorkload
                    .Where(x => x.HasAnyLoad)
                    .Select(x => x.TechnicianId)
                    .ToHashSet(),
                "perkrova" => techIdsByWorkload
                    .Where(x => x.HasAnyConflict)
                    .Select(x => x.TechnicianId)
                    .ToHashSet(),
                _ => techIdsByWorkload.Select(x => x.TechnicianId).ToHashSet()
            };

            var filteredTechnicians = technicians
                .Where(t => matchingTechIds.Contains(t.Id))
                .ToList();

            var filteredTasks = tasks
                .Where(t => matchingTechIds.Contains(t.TechnicianId))
                .Where(t => !isDayView || t.Start.Date == selectedDate.Date)
                .ToList();

            return (filteredTechnicians, filteredTasks);
        }

        private static (List<Technician> technicians, List<TaskItem> tasks) ApplyStatusFilter(string? status, List<Technician> technicians, List<TaskItem> tasks)
        {
            var normalized = NormalizeStatusFilter(status);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized == "visi" ||
                normalized == "all")
            {
                return (technicians, tasks);
            }

            var filteredTasks = tasks
                .Where(t => NormalizeStatusFilter(t.Status) == normalized)
                .ToList();

            var techIds = filteredTasks
                .Select(t => t.TechnicianId)
                .ToHashSet();

            var filteredTechnicians = technicians
                .Where(t => techIds.Contains(t.Id))
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

        private static List<string> BuildAvailableWorkloadLevels(IEnumerable<TaskItem> tasks)
        {
            var workloadMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "normal", "Normali" },
                { "load", "Apkrova" },
                { "conflict", "Perkrova" }
            };

            var workloads = tasks
                .Select(t => t.LoadStatus)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(s => workloadMap.TryGetValue(s, out var label) ? label : s)
                .OrderBy(s => s)
                .ToList();

            // Ensure all standard workload levels are available even if not present in current data.
            foreach (var label in workloadMap.Values)
            {
                if (!workloads.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    workloads.Add(label);
                }
            }

            workloads.Insert(0, "Visos");
            return workloads;
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
            }

            return issues;
        }

        private static ScheduleSummary BuildSummary(IEnumerable<TaskItem> tasks, IEnumerable<Technician> technicians)
        {
            var techLookup = technicians.ToDictionary(t => t.Id);

            return new ScheduleSummary
            {
                TotalTasks = tasks.Count(),
                ConflictTasks = tasks.Count(t => t.IsConflict),
                OverloadHours = tasks.Sum(t => t.OverloadHours)
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

        private async Task<Dictionary<long, (decimal? DistanceKm, int? DurationMinutes)>> BuildTravelMetricsAsync(
            List<Data.TaskEntity> tasks,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, (decimal? DistanceKm, int? DurationMinutes)>();
            if (tasks.Count == 0)
            {
                return result;
            }

            var routes = await _db.TravelRoutes
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var routeLookup = routes.ToDictionary(r => (r.FromAddressId, r.ToAddressId), r => r);

            var workplaceLookup = await _db.Technicians
                .AsNoTracking()
                .Select(t => new { t.Id, t.WorkplaceAddressId })
                .ToDictionaryAsync(t => t.Id, t => t.WorkplaceAddressId, cancellationToken);

            var tasksByTech = tasks
                .Where(t => t.ServiceObject != null)
                .GroupBy(t => t.TechnicianId)
                .ToList();

            foreach (var techGroup in tasksByTech)
            {
                if (!workplaceLookup.TryGetValue(techGroup.Key, out var workplaceAddressId))
                {
                    continue;
                }

                foreach (var dayGroup in techGroup.OrderBy(t => t.StartAt).GroupBy(t => t.StartAt.Date))
                {
                    Data.TaskEntity? prev = null;
                    foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                    {
                        var fromAddressId = prev != null
                            ? prev.ServiceObject!.AddressId
                            : workplaceAddressId;
                        var toAddressId = task.ServiceObject!.AddressId;

                        if (routeLookup.TryGetValue((fromAddressId, toAddressId), out var route))
                        {
                            result[task.Id] = (route.DistanceKm, route.DurationMinutes);
                        }
                        else
                        {
                            result[task.Id] = (null, null);
                        }

                        prev = task;
                    }
                }
            }

            return result;
        }

        private static List<TaskItem> BuildTravelDisplayBlocks(List<TaskItem> tasks)
        {
            var blocks = new List<TaskItem>();
            if (tasks.Count == 0)
            {
                return blocks;
            }

            var grouped = tasks
                .Where(t => !t.IsTravelBlock)
                .GroupBy(t => new { t.TechnicianId, Day = t.Start.Date });

            var syntheticId = -1;
            foreach (var group in grouped)
            {
                TaskItem? previous = null;
                foreach (var task in group.OrderBy(t => t.Start))
                {
                    var requiredTravelMinutes = task.TravelDurationMinutes.HasValue
                        ? (int)Math.Ceiling(task.TravelDurationMinutes.Value)
                        : 0;
                    if (requiredTravelMinutes <= 0)
                    {
                        previous = task;
                        continue;
                    }

                    var taskStartMinutes = (int)task.Start.TimeOfDay.TotalMinutes;
                    var earliestAllowedStart = previous != null
                        ? (int)previous.End.TimeOfDay.TotalMinutes
                        : ScheduleGrid.DayStartMinutes;

                    var availableGap = taskStartMinutes - earliestAllowedStart;
                    if (availableGap <= 0)
                    {
                        previous = task;
                        continue;
                    }

                    var displayMinutes = Math.Min(requiredTravelMinutes, availableGap);
                    var rawStartMinutes = taskStartMinutes - displayMinutes;
                    var snappedStart = FloorToSlot(rawStartMinutes);
                    var snappedEnd = CeilingToSlot(taskStartMinutes);
                    if (snappedStart < earliestAllowedStart)
                    {
                        snappedStart = earliestAllowedStart;
                    }

                    if (snappedEnd <= snappedStart)
                    {
                        previous = task;
                        continue;
                    }

                    blocks.Add(new TaskItem
                    {
                        Id = syntheticId--,
                        PlanId = task.PlanId,
                        TechnicianId = task.TechnicianId,
                        Version = 0,
                        Title = "Kelione",
                        Location = string.Empty,
                        Start = task.Start.Date.AddMinutes(snappedStart),
                        End = task.Start.Date.AddMinutes(snappedEnd),
                        PlanStatus = task.PlanStatus,
                        Priority = string.Empty,
                        Status = "travel",
                        IsConflict = false,
                        IsLoad = false,
                        IsNewPlan = false,
                        Source = task.Source,
                        IsEditable = false,
                        CanDelete = false,
                        IsTimeExpired = false,
                        OverloadHours = 0,
                        LoadStatus = string.Empty,
                        // Keep evaluated duration visible in UI; block width may still be clamped by free gap.
                        TravelDurationMinutes = requiredTravelMinutes,
                        TravelDistanceKm = task.TravelDistanceKm,
                        IsTravelBlock = true,
                        TravelForTaskId = task.Id
                    });

                    previous = task;
                }
            }

            return blocks;
        }

        private static int FloorToSlot(int minutes)
        {
            var slot = ScheduleGrid.SlotSizeMinutes;
            return (minutes / slot) * slot;
        }

        private static int CeilingToSlot(int minutes)
        {
            var slot = ScheduleGrid.SlotSizeMinutes;
            return ((minutes + slot - 1) / slot) * slot;
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
}

