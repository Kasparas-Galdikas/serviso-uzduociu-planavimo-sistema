using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServiceTaskPreview
    {
        private const double BatchBalancePenalty = 20.0;
        private const double TravelPenaltyPerHour = 15.0;

        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly SchedulePlacementValidator _placementValidator;
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly ILogger<PlanServiceTaskPreview> _logger;

        public PlanServiceTaskPreview(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            ITaskPermissionService taskPermissionService,
            ITravelTimeResolver travelResolver,
            ITaskTravelFeasibilityService travelFeasibilityService,
            IScheduleRepairService scheduleRepairService,
            SchedulePlacementValidator placementValidator,
            IRouteEvaluationService routeEvaluationService,
            ILogger<PlanServiceTaskPreview> logger)
        {
            _db = db;
            _timeService = timeService;
            _taskPermissionService = taskPermissionService;
            _travelResolver = travelResolver;
            _travelFeasibilityService = travelFeasibilityService;
            _scheduleRepairService = scheduleRepairService;
            _placementValidator = placementValidator;
            _routeEvaluationService = routeEvaluationService;
            _logger = logger;
        }

        public async Task<AutoAssignmentSuggestionDto?> SuggestAutoAssignmentAsync(
            DateTime targetDate,
            int durationMinutes,
            string objectName,
            TaskPriority priority,
            int timezoneOffsetMinutes,
            List<SimulatedTaskAssignmentDto>? simulatedAssignments,
            Dictionary<long, int>? batchAssignmentCounts,
            CancellationToken cancellationToken = default)
        {
            if (durationMinutes <= 0 || string.IsNullOrWhiteSpace(objectName))
            {
                return null;
            }

            var targetPlan = await _db.Plans
                .AsNoTracking()
                .OrderBy(p => p.Status == PlanStatus.Draft ? 0 : 1)
                .FirstOrDefaultAsync(
                    p => p.Status == PlanStatus.Draft || p.Status == PlanStatus.Active,
                    cancellationToken);

            if (targetPlan == null)
            {
                return null;
            }

            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.User)
                .OrderBy(t => t.Id)
                .ToListAsync(cancellationToken);

            if (technicians.Count == 0)
            {
                return null;
            }

            var serviceObject = await _db.ServiceObjects
                .AsNoTracking()
                .FirstOrDefaultAsync(so => so.Name == objectName, cancellationToken);

            if (serviceObject == null)
            {
                return null;
            }

            var planTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                .Include(t => t.Technician)
                    .ThenInclude(t => t.User)
                .Where(t => t.PlanId == targetPlan.Id &&
                            t.StartAt >= targetDate.Date &&
                            t.StartAt < targetDate.Date.AddDays(1))
                .ToListAsync(cancellationToken);

            var simulatedTasks = planTasks.ToList();
            AppendSimulatedAssignments(simulatedTasks, simulatedAssignments);

            var addressIds = simulatedTasks
                .Where(t => t.ServiceObject != null)
                .Select(t => t.ServiceObject.AddressId)
                .Concat(technicians.Select(t => t.WorkplaceAddressId))
                .Append(serviceObject.AddressId)
                .Distinct()
                .ToList();

            var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);
            var weights = LoadScoreWeights.CreateDefault();
            var dayStart = targetDate.Date;
            var dayEnd = dayStart.AddDays(1);
            var weekStart = _timeService.GetWeekStart(dayStart);
            var weekEnd = weekStart.AddDays(7);
            var nextTempTaskId = NextTemporaryTaskId(simulatedTasks);
            AutoAssignmentSuggestionDto? best = null;
            var bestScore = double.MaxValue;

            foreach (var technician in technicians)
            {
                var technicianDayTasks = simulatedTasks
                    .Where(t => t.TechnicianId == technician.Id &&
                                t.StartAt >= dayStart &&
                                t.StartAt < dayEnd &&
                                WorkloadCalculator.CountsTowardsWorkload(t.Status))
                    .OrderBy(t => t.StartAt)
                    .ToList();

                var candidateStarts = EnumerateCandidateStartMinutes(
                    technician,
                    technicianDayTasks,
                    dayStart,
                    timezoneOffsetMinutes,
                    durationMinutes,
                    serviceObject,
                    routeLookup);

                foreach (var candidateStartMinutes in candidateStarts)
                {
                    var candidateStartAt = _timeService.CreateUtcFromDateAndMinutes(
                        dayStart,
                        candidateStartMinutes,
                        timezoneOffsetMinutes);
                    var candidateEndAt = candidateStartAt.AddMinutes(durationMinutes);
                    var candidateTaskId = nextTempTaskId--;
                    var candidateTasks = simulatedTasks.ToList();
                    candidateTasks.Add(new TaskEntity
                    {
                        Id = candidateTaskId,
                        PlanId = targetPlan.Id,
                        TechnicianId = technician.Id,
                        ServiceObjectId = serviceObject.Id,
                        ServiceObject = serviceObject,
                        Technician = technician,
                        Title = string.Empty,
                        StartAt = candidateStartAt,
                        EndAt = candidateEndAt,
                        Priority = priority,
                        Status = ScheduleTaskStatus.InProgress,
                        CreatedAt = DateTime.UtcNow
                    });

                    var dayTasks = candidateTasks
                        .Where(t => t.TechnicianId == technician.Id &&
                                    t.StartAt >= dayStart &&
                                    t.StartAt < dayEnd)
                        .OrderBy(t => t.StartAt)
                        .ToList();

                    var weekTasks = candidateTasks
                        .Where(t => t.TechnicianId == technician.Id &&
                                    t.StartAt >= weekStart &&
                                    t.StartAt < weekEnd)
                        .OrderBy(t => t.StartAt)
                        .ToList();

                    var travelMinutesByTaskId = BuildTravelMinutesByTaskIdFromLookup(
                        technician,
                        weekTasks,
                        routeLookup,
                        strictRouteCoverage: false) ?? new Dictionary<long, int>();

                    var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
                    var score = LoadScoreCalculator.ComputeLoadScore(snapshot.Context, weights);
                    score += (batchAssignmentCounts?.GetValueOrDefault(technician.Id) ?? 0) * BatchBalancePenalty;
                    score += snapshot.Context.TravelTimeHours * TravelPenaltyPerHour;

                    _logger.LogDebug(
                        "svc.plan.auto.best-tech.score planId={PlanId} technicianId={TechnicianId} score={Score} startMinutes={StartMinutes} dayTaskCount={DayTaskCount} weekTaskCount={WeekTaskCount}",
                        targetPlan.Id,
                        technician.Id,
                        score,
                        candidateStartMinutes,
                        dayTasks.Count,
                        weekTasks.Count);

                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    best = new AutoAssignmentSuggestionDto
                    {
                        TechnicianId = technician.Id,
                        TechnicianName = technician.User?.Name ?? $"Technician #{technician.Id}",
                        StartMinutes = candidateStartMinutes,
                        EndMinutes = candidateStartMinutes + durationMinutes
                    };
                }
            }

            return best;
        }

        public async Task<DragPreviewResult> PreviewDragAsync(
            long taskId,
            long targetTechnicianId,
            DateTime targetDate,
            int targetSlotStartMinutes,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            var targetTask = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .Include(t => t.Technician)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (targetTask == null)
            {
                return new DragPreviewResult(DragPreviewError.TaskNotFound);
            }

            var isEditable = await _taskPermissionService.CanEditTaskAsync(targetTask, user, timezoneOffsetMinutes);
            if (!isEditable)
            {
                return new DragPreviewResult(DragPreviewError.NotEditable);
            }

            if (targetTask.PlanId == null)
            {
                return new DragPreviewResult(DragPreviewError.NoPlan, "Užduotis neturi plano.");
            }

            var hoverTech = await _db.Technicians
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == targetTechnicianId, cancellationToken);

            if (hoverTech == null)
            {
                return new DragPreviewResult(DragPreviewError.TechnicianNotFound, "Technikas nerastas.");
            }

            var duration = targetTask.EndAt - targetTask.StartAt;
            var durationMinutesRaw = duration.TotalMinutes;
            if (durationMinutesRaw <= 0 || Math.Abs(durationMinutesRaw - Math.Round(durationMinutesRaw)) > 0.0001)
            {
                return new DragPreviewResult(DragPreviewError.NoPlan, "Netinkama uzduoties trukme.");
            }

            var durationMinutes = (int)Math.Round(durationMinutesRaw);
            var targetStartAt = _timeService.CreateUtcFromDateAndMinutes(targetDate.Date, targetSlotStartMinutes, timezoneOffsetMinutes);
            var targetEndAt = targetStartAt.AddMinutes(durationMinutes);
            var dayStart = targetStartAt.Date;
            var weekStart = _timeService.GetWeekStart(dayStart);
            var weekEndExclusive = weekStart.AddDays(7);

            var scopeTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Technician)
                .Include(t => t.ServiceObject)
                .Where(t => t.PlanId == targetTask.PlanId &&
                            t.StartAt < weekEndExclusive &&
                            t.EndAt > weekStart)
                .ToListAsync(cancellationToken);

            var technicians = await _db.Technicians
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var simulation = PlanSimulationContext.Create(scopeTasks, technicians, targetTask, _timeService);

            var modifiedSim = simulation.WithModifiedTasks(tasks =>
            {
                var modified = tasks.ToList();
                var idx = modified.FindIndex(t => t.Id == targetTask.Id);
                if (idx >= 0)
                {
                    modified[idx] = new TaskEntity
                    {
                        Id = targetTask.Id,
                        PlanId = targetTask.PlanId,
                        TechnicianId = targetTechnicianId,
                        Title = targetTask.Title,
                        StartAt = targetStartAt,
                        EndAt = targetEndAt,
                        Priority = targetTask.Priority,
                        Status = targetTask.Status,
                        CreatedAt = targetTask.CreatedAt,
                        Plan = targetTask.Plan,
                        Technician = targetTask.Technician,
                        Evaluation = targetTask.Evaluation,
                        ServiceObject = targetTask.ServiceObject,
                        ServiceObjectId = targetTask.ServiceObjectId,
                        Instructions = targetTask.Instructions,
                        SkillLevel = targetTask.SkillLevel,
                        TechnicianComment = targetTask.TechnicianComment
                    };
                }
                return modified;
            });

            var visibleTechs = technicians
                .Where(t => scopeTasks.Any(task => task.TechnicianId == t.Id) || t.Id == targetTechnicianId)
                .ToList();

            var addressIds = scopeTasks
                .Select(t => t.ServiceObject!.AddressId)
                .Append(hoverTech.WorkplaceAddressId)
                .Distinct()
                .ToList();

            await _routeEvaluationService.EnsureAllPairwiseRoutesAsync(addressIds, cancellationToken);
            var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);

            var response = new Models.DTOs.Responses.DragPreviewResponseDto
            {
                DraggedTaskId = targetTask.Id
            };

            var targetPreviewValidation = SchedulePlacementValidator.TimingTravelValidationResult.Success;
            {
                var localTargetStartAt = _timeService.ConvertUtcToLocal(targetStartAt, timezoneOffsetMinutes);
                var localTargetStartMinutes = (int)localTargetStartAt.TimeOfDay.TotalMinutes;

                targetPreviewValidation = await _placementValidator.ValidateTimingAndTravelAsync(
                    taskDateLocal: localTargetStartAt.Date,
                    startMinutesLocal: localTargetStartMinutes,
                    timezoneOffsetMinutes: timezoneOffsetMinutes,
                    technicianId: targetTechnicianId,
                    startAtUtc: targetStartAt,
                    endAtUtc: targetEndAt,
                    serviceAddressId: targetTask.ServiceObject.AddressId,
                    excludeTaskId: targetTask.Id,
                    cancellationToken: cancellationToken);
            }

            foreach (var tech in visibleTechs)
            {
                var loadSnapshot = BuildLoadSnapshotForSimulation(
                    tech.Id,
                    dayStart,
                    modifiedSim.Tasks.ToList(),
                    modifiedSim.Technicians.ToList());

                var fallbackTravelHours = ComputeTravelHoursForSimulation(
                    tech.Id,
                    dayStart,
                    scopeTasks,
                    technicians,
                    routeLookup,
                    strictRouteCoverage: false);

                var projectedTravelHours = ComputeTravelHoursForSimulation(
                    tech.Id,
                    dayStart,
                    modifiedSim.Tasks.ToList(),
                    modifiedSim.Technicians.ToList(),
                    routeLookup,
                    strictRouteCoverage: true);

                var travelHoursToShow = projectedTravelHours ?? fallbackTravelHours ?? loadSnapshot.TravelTimeHours;
                var requiredInboundTravelMinutes = ComputeRequiredInboundTravelMinutesForTask(
                    tech.Id,
                    dayStart,
                    targetTask.Id,
                    modifiedSim.Tasks.ToList(),
                    modifiedSim.Technicians.ToList(),
                    routeLookup);

                response.PreviewStates.Add(new Models.DTOs.Responses.DragPreviewStateDto
                {
                    TechnicianId = tech.Id,
                    LoadState = loadSnapshot.LoadState,
                    HasConflict = loadSnapshot.HasConflict,
                    IsDailyLimitExceeded = loadSnapshot.IsDailyLimitExceeded,
                    IsWeeklyLimitExceeded = loadSnapshot.IsWeeklyLimitExceeded,
                    DailyHours = Math.Round(loadSnapshot.DailyHours, 2),
                    WeeklyHours = Math.Round(loadSnapshot.WeeklyHours, 2),
                    DailyLimit = loadSnapshot.DailyLimit,
                    WeeklyLimit = loadSnapshot.WeeklyLimit,
                    DailyUtilization = Math.Round(loadSnapshot.DailyUtilization, 2),
                    WeeklyUtilization = Math.Round(loadSnapshot.WeeklyUtilization, 2),
                    TravelTimeHours = Math.Round(travelHoursToShow, 2),
                    RequiredInboundTravelMinutes = requiredInboundTravelMinutes,
                    IsPlacementAllowed = tech.Id != targetTechnicianId || targetPreviewValidation.IsValid,
                    RejectionCode = tech.Id == targetTechnicianId && !targetPreviewValidation.IsValid
                        ? (targetPreviewValidation.IsInvalidWindow ? "InvalidWindow" : "TravelInfeasible")
                        : null,
                    RejectionMessage = tech.Id == targetTechnicianId && !targetPreviewValidation.IsValid
                        ? targetPreviewValidation.Message
                        : null
                });
            }

            _logger.LogInformation("svc.plan.preview.drag task={TaskId} plan={PlanId} tech={Tech} date={Day} states={Count}",
                targetTask.Id, targetTask.PlanId, targetTechnicianId, dayStart, response.PreviewStates.Count);

            return new DragPreviewResult(DragPreviewError.None, Response: response);
        }

        public async Task<SwapSmartPreviewResult> PreviewSwapSmartAsync(
            long sourceTaskId,
            long targetTaskId,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            if (sourceTaskId == targetTaskId)
                return SwapSmartPreviewResult.Hard("Negalima sukeisti uzduoties su pacia savimi.");

            var source = await _db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == sourceTaskId, cancellationToken);
            if (source == null)
                return SwapSmartPreviewResult.Hard($"Task {sourceTaskId} was not found.");

            var target = await _db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == targetTaskId, cancellationToken);
            if (target == null)
                return SwapSmartPreviewResult.Hard($"Task {targetTaskId} was not found.");

            if (source.TechnicianId != target.TechnicianId)
                return SwapSmartPreviewResult.Hard("Sukeitimas galimas tik to paties techniko uzduotims.");
            if (source.StartAt.Date != target.StartAt.Date)
                return SwapSmartPreviewResult.Hard("Sukeitimas galimas tik tame paciame dienos grafike.");
            if (source.PlanId != target.PlanId)
                return SwapSmartPreviewResult.Hard("Sukeitimas galimas tik to paties plano viduje.");
            if (source.Status == ScheduleTaskStatus.Canceled || target.Status == ScheduleTaskStatus.Canceled)
                return SwapSmartPreviewResult.Hard("Atsauktu uzduociu sukeisti negalima.");

            // Check if a strict slot-swap would fit within the work window.
            var sourceDuration = source.EndAt - source.StartAt;
            var targetDuration = target.EndAt - target.StartAt;
            if (sourceDuration <= TimeSpan.Zero || targetDuration <= TimeSpan.Zero)
                return SwapSmartPreviewResult.Hard("Netinkama uzduoties trukme.");

            var sourceNewEndLocal = target.StartAt.AddMinutes(timezoneOffsetMinutes) + sourceDuration;
            var targetNewEndLocal = source.StartAt.AddMinutes(timezoneOffsetMinutes) + targetDuration;
            var sourceNewStartLocal = target.StartAt.AddMinutes(timezoneOffsetMinutes);
            var targetNewStartLocal = source.StartAt.AddMinutes(timezoneOffsetMinutes);

            var window = SchedulePlacementValidator.GetWorkingWindow(source.TechnicianId, sourceNewStartLocal.Date);
            var strictFits =
                sourceNewEndLocal.Date == sourceNewStartLocal.Date &&   // no midnight overflow
                targetNewEndLocal.Date == targetNewStartLocal.Date &&
                (int)sourceNewStartLocal.TimeOfDay.TotalMinutes >= window.StartMinutes &&
                (int)sourceNewEndLocal.TimeOfDay.TotalMinutes <= window.EndMinutes &&
                (int)targetNewStartLocal.TimeOfDay.TotalMinutes >= window.StartMinutes &&
                (int)targetNewEndLocal.TimeOfDay.TotalMinutes <= window.EndMinutes;

            if (strictFits)
            {
                // Strict swap would pass the window check; it may still fail on travel
                // or overlap, but report tentatively feasible without doing a full check.
                return SwapSmartPreviewResult.Ok(smartRequired: false);
            }

            // Strict swap fails on window; try whether the smart planner can recover.
            var plan = await _placementValidator.BuildSwapRepairPlanAsync(sourceTaskId, targetTaskId, timezoneOffsetMinutes, cancellationToken);

            if (plan.Success && plan.Adjustments.Count > 0)
                return SwapSmartPreviewResult.Ok(smartRequired: true);

            return SwapSmartPreviewResult.Fail(plan.Message ?? "Nepakanka laisvo laiko.");
        }

        internal LoadSnapshotDto BuildLoadSnapshotForSimulation(
            long technicianId,
            DateTime dayStartUtc,
            List<TaskEntity> tasks,
            List<TechnicianEntity> technicians)
        {
            var technician = technicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
            {
                return new LoadSnapshotDto();
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var weekStart = _timeService.GetWeekStart(dayStartUtc);
            var weekEnd = weekStart.AddDays(7);

            var dayTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= dayStartUtc &&
                            t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var weekTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= weekStart &&
                            t.StartAt < weekEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks);
            return PlanServiceTaskQuery.MapToLoadSnapshotDto(technician, snapshot);
        }

        internal static Dictionary<long, int>? BuildTravelMinutesByTaskIdFromLookup(
            TechnicianEntity technician,
            List<TaskEntity> weekTasks,
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            bool strictRouteCoverage)
        {
            var result = new Dictionary<long, int>();

            var tasksByDay = weekTasks
                .OrderBy(t => t.StartAt)
                .GroupBy(t => t.StartAt.Date)
                .ToList();

            foreach (var dayGroup in tasksByDay)
            {
                TaskEntity? prev = null;
                foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                {
                    var fromAddressId = prev?.ServiceObject?.AddressId ?? technician.WorkplaceAddressId;
                    var toAddressId = task.ServiceObject?.AddressId ?? 0;

                    if (fromAddressId <= 0 || toAddressId <= 0)
                    {
                        if (strictRouteCoverage)
                        {
                            return null;
                        }

                        result[task.Id] = 0;
                        prev = task;
                        continue;
                    }

                    if (routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes))
                    {
                        result[task.Id] = minutes;
                    }
                    else
                    {
                        if (strictRouteCoverage)
                        {
                            return null;
                        }

                        result[task.Id] = 0;
                    }

                    prev = task;
                }
            }

            return result;
        }

        internal double? ComputeTravelHoursForSimulation(
            long technicianId,
            DateTime dayStartUtc,
            List<TaskEntity> tasks,
            List<TechnicianEntity> technicians,
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            bool strictRouteCoverage)
        {
            var technician = technicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
            {
                return null;
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var weekStart = _timeService.GetWeekStart(dayStartUtc);
            var weekEnd = weekStart.AddDays(7);

            var dayTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= dayStartUtc &&
                            t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var weekTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= weekStart &&
                            t.StartAt < weekEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var travelMinutesByTaskId = BuildTravelMinutesByTaskIdFromLookup(
                technician,
                weekTasks,
                routeLookup,
                strictRouteCoverage);

            if (travelMinutesByTaskId == null)
            {
                return null;
            }

            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
            return snapshot.Context.TravelTimeHours;
        }

        internal static int? ComputeRequiredInboundTravelMinutesForTask(
            long technicianId,
            DateTime dayStartUtc,
            long taskId,
            List<TaskEntity> tasks,
            List<TechnicianEntity> technicians,
            IReadOnlyDictionary<(long From, long To), int> routeLookup)
        {
            var technician = technicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
            {
                return null;
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var dayTasks = tasks
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= dayStartUtc &&
                            t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var idx = dayTasks.FindIndex(t => t.Id == taskId);
            if (idx < 0)
            {
                return null;
            }

            var task = dayTasks[idx];
            var toAddressId = task.ServiceObject?.AddressId ?? 0;
            if (toAddressId <= 0)
            {
                return null;
            }

            var fromAddressId = idx > 0
                ? (dayTasks[idx - 1].ServiceObject?.AddressId ?? 0)
                : technician.WorkplaceAddressId;
            if (fromAddressId <= 0)
            {
                return null;
            }

            return routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes)
                ? minutes
                : null;
        }

        private static long NextTemporaryTaskId(IEnumerable<TaskEntity> tasks)
        {
            var minExistingId = tasks.Select(t => t.Id).DefaultIfEmpty(0).Min();
            return Math.Min(-1, minExistingId - 1);
        }

        private IEnumerable<int> EnumerateCandidateStartMinutes(
            TechnicianEntity technician,
            List<TaskEntity> dayTasks,
            DateTime targetDate,
            int timezoneOffsetMinutes,
            int durationMinutes,
            ServiceObjectEntity serviceObject,
            IReadOnlyDictionary<(long From, long To), int> routeLookup)
        {
            var slotSize = ScheduleGrid.SlotSizeMinutes;
            var window = SchedulePlacementValidator.GetWorkingWindow(technician.Id, targetDate);
            var earliestAllowed = _timeService.GetEarliestCreatableStartMinutes(
                targetDate,
                timezoneOffsetMinutes,
                slotSize,
                ScheduleGrid.DayStartMinutes);
            var candidates = new HashSet<int>();
            var ordered = dayTasks
                .Where(t => WorkloadCalculator.CountsTowardsWorkload(t.Status))
                .OrderBy(t => t.StartAt)
                .ToList();

            if (ordered.Count == 0)
            {
                if (FitsWithinWindow(earliestAllowed, durationMinutes, window.EndMinutes))
                {
                    candidates.Add(earliestAllowed);
                }

                return candidates.OrderBy(x => x);
            }

            var first = ordered[0];
            if (IsSlotFeasible(
                    candidateStartMinutes: earliestAllowed,
                    durationMinutes: durationMinutes,
                    previousTask: null,
                    nextTask: first,
                    technician: technician,
                    serviceObject: serviceObject,
                    routeLookup: routeLookup,
                    timezoneOffsetMinutes: timezoneOffsetMinutes,
                    windowEndMinutes: window.EndMinutes))
            {
                candidates.Add(earliestAllowed);
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var previous = ordered[i];
                var next = i + 1 < ordered.Count ? ordered[i + 1] : null;
                var previousEndMinutes = LocalMinutes(previous.EndAt, timezoneOffsetMinutes);
                var previousTravelMinutes = GetTravelMinutes(
                    routeLookup,
                    previous.ServiceObject?.AddressId ?? 0,
                    serviceObject.AddressId);
                var candidateStartMinutes = _timeService.CeilingToSlot(
                    previousEndMinutes + previousTravelMinutes,
                    slotSize);

                if (candidateStartMinutes < earliestAllowed)
                {
                    candidateStartMinutes = earliestAllowed;
                }

                if (IsSlotFeasible(
                        candidateStartMinutes,
                        durationMinutes,
                        previous,
                        next,
                        technician,
                        serviceObject,
                        routeLookup,
                        timezoneOffsetMinutes,
                        window.EndMinutes))
                {
                    candidates.Add(candidateStartMinutes);
                }
            }

            return candidates.OrderBy(x => x);
        }

        private bool IsSlotFeasible(
            int candidateStartMinutes,
            int durationMinutes,
            TaskEntity? previousTask,
            TaskEntity? nextTask,
            TechnicianEntity technician,
            ServiceObjectEntity serviceObject,
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            int timezoneOffsetMinutes,
            int windowEndMinutes)
        {
            if (!FitsWithinWindow(candidateStartMinutes, durationMinutes, windowEndMinutes))
            {
                return false;
            }

            var inboundFromAddressId = previousTask?.ServiceObject?.AddressId ?? technician.WorkplaceAddressId;
            var requiredInboundTravel = GetTravelMinutes(
                routeLookup,
                inboundFromAddressId,
                serviceObject.AddressId);

            if (previousTask != null)
            {
                var previousEndMinutes = LocalMinutes(previousTask.EndAt, timezoneOffsetMinutes);
                if (candidateStartMinutes < previousEndMinutes + requiredInboundTravel)
                {
                    return false;
                }
            }
            else if (candidateStartMinutes < ScheduleGrid.DayStartMinutes + requiredInboundTravel)
            {
                return false;
            }

            if (nextTask != null)
            {
                var nextStartMinutes = LocalMinutes(nextTask.StartAt, timezoneOffsetMinutes);
                var requiredOutboundTravel = GetTravelMinutes(
                    routeLookup,
                    serviceObject.AddressId,
                    nextTask.ServiceObject?.AddressId ?? 0);

                if (candidateStartMinutes + durationMinutes + requiredOutboundTravel > nextStartMinutes)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool FitsWithinWindow(int candidateStartMinutes, int durationMinutes, int windowEndMinutes) =>
            candidateStartMinutes >= ScheduleGrid.DayStartMinutes &&
            candidateStartMinutes + durationMinutes <= windowEndMinutes;

        private int LocalMinutes(DateTime utcDateTime, int timezoneOffsetMinutes) =>
            (int)_timeService.ConvertUtcToLocal(utcDateTime, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;

        private static int GetTravelMinutes(
            IReadOnlyDictionary<(long From, long To), int> routeLookup,
            long fromAddressId,
            long toAddressId)
        {
            if (fromAddressId <= 0 || toAddressId <= 0 || fromAddressId == toAddressId)
            {
                return 0;
            }

            return routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes)
                ? minutes
                : 0;
        }

        private static void AppendSimulatedAssignments(
            ICollection<TaskEntity> target,
            IEnumerable<SimulatedTaskAssignmentDto>? simulatedAssignments)
        {
            if (simulatedAssignments == null)
            {
                return;
            }

            var nextTaskId = NextTemporaryTaskId(target);
            long nextServiceObjectId = -1;

            foreach (var assignment in simulatedAssignments)
            {
                if (assignment.TechnicianId <= 0 ||
                    assignment.AddressId <= 0 ||
                    !TryParseSimulatedDateTime(assignment.StartAt, out var startAt) ||
                    !TryParseSimulatedDateTime(assignment.EndAt, out var endAt) ||
                    endAt <= startAt)
                {
                    continue;
                }

                var serviceObjectId = assignment.ServiceObjectId ?? nextServiceObjectId--;
                target.Add(new TaskEntity
                {
                    Id = nextTaskId--,
                    TechnicianId = assignment.TechnicianId,
                    ServiceObjectId = serviceObjectId,
                    ServiceObject = new ServiceObjectEntity
                    {
                        Id = serviceObjectId,
                        AddressId = assignment.AddressId,
                        Name = string.Empty
                    },
                    Title = string.Empty,
                    StartAt = startAt,
                    EndAt = endAt,
                    Priority = TaskPriority.Medium,
                    Status = ScheduleTaskStatus.InProgress,
                    CreatedAt = DateTime.UtcNow,
                    SourceBacklogItemId = assignment.SourceBacklogItemId
                });
            }
        }

        private static bool TryParseSimulatedDateTime(string value, out DateTime parsed)
        {
            var formats = new[]
            {
                "yyyy-MM-dd'T'HH:mm",
                "yyyy-MM-dd'T'HH:mm:ss",
                "yyyy-MM-dd'T'HH:mm:ss.FFF",
                "O"
            };

            return DateTime.TryParseExact(
                       value,
                       formats,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind,
                       out parsed)
                   || DateTime.TryParse(
                       value,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeLocal,
                       out parsed);
        }
    }
}
