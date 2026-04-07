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
using ServiceTaskPlanningSystem.Services.PlanManagement.Matching;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServiceTaskPreview
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly SchedulePlacementValidator _placementValidator;
        private readonly RoutePreviewLookupBuilder _routePreviewLookupBuilder;
        private readonly TechnicianUnavailabilityImpactResolver _unavailabilityImpactResolver;
        private readonly ILogger<PlanServiceTaskPreview> _logger;

        public PlanServiceTaskPreview(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            ITaskPermissionService taskPermissionService,
            ITravelTimeResolver travelResolver,
            SchedulePlacementValidator placementValidator,
            RoutePreviewLookupBuilder routePreviewLookupBuilder,
            TechnicianUnavailabilityImpactResolver unavailabilityImpactResolver,
            ILogger<PlanServiceTaskPreview> logger)
        {
            _db = db;
            _timeService = timeService;
            _taskPermissionService = taskPermissionService;
            _travelResolver = travelResolver;
            _placementValidator = placementValidator;
            _routePreviewLookupBuilder = routePreviewLookupBuilder;
            _unavailabilityImpactResolver = unavailabilityImpactResolver;
            _logger = logger;
        }

        public async Task<UnavailabilityPreviewResult> PreviewDisplacementAsync(
            long technicianId,
            DateTime periodStart,
            DateTime periodEnd,
            UserContext actorUser,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            if (!actorUser.IsManager)
            {
                return new UnavailabilityPreviewResult(new List<AffectedTaskSummary>());
            }

            var affectedTasks = await _unavailabilityImpactResolver.GetAffectedTasksAsync(
                technicianId,
                periodStart,
                periodEnd,
                timezoneOffsetMinutes,
                cancellationToken);

            var summaries = new List<AffectedTaskSummary>(affectedTasks.Count);
            foreach (var item in affectedTasks.OrderBy(task => task.Task.StartAt))
            {
                var repairPlan = await _placementValidator.TryBuildUnavailabilityRepairPlanAsync(
                    technicianId,
                    item.Task.StartAt,
                    item.Task.EndAt,
                    item.EffectiveStartAt,
                    periodEnd,
                    item.Task.ServiceObject.AddressId,
                    timezoneOffsetMinutes,
                    item.Task.Id,
                    cancellationToken,
                    blockingPeriodStartUtc: periodStart);

                summaries.Add(new AffectedTaskSummary(
                    TaskId: item.Task.Id,
                    Title: item.Task.Title,
                    StartAt: item.EffectiveStartAt,
                    EndAt: item.Task.EndAt,
                    Priority: item.Task.Priority,
                    ObjectName: item.Task.ServiceObject.Name,
                    ObjectAddress: item.Task.ServiceObject.Address.FullAddress,
                    Resolution: repairPlan.Success ? UnavailabilityTaskResolution.Repair : UnavailabilityTaskResolution.Backlog));
            }

            return new UnavailabilityPreviewResult(summaries);
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

            var routeLookup = await _routePreviewLookupBuilder.BuildAsync(addressIds, cancellationToken);

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

            var travelMinutesByTaskId = CandidateScoreCalculator.BuildTravelMinutesByTaskIdFromLookup(
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

        private int LocalMinutes(DateTime utcDateTime, int timezoneOffsetMinutes) =>
            (int)_timeService.ConvertUtcToLocal(utcDateTime, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
    }
}
