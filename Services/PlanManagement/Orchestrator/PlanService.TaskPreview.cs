using System;
using System.Collections.Generic;
using System.Globalization;
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
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
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
        private readonly PlanServiceAssignmentMatching _assignmentMatching;
        private readonly ILogger<PlanServiceTaskPreview> _logger;

        public PlanServiceTaskPreview(
            ScheduleDbContext db,
            IScheduleTimeService timeService,
            ITaskPermissionService taskPermissionService,
            ITravelTimeResolver travelResolver,
            SchedulePlacementValidator placementValidator,
            RoutePreviewLookupBuilder routePreviewLookupBuilder,
            TechnicianUnavailabilityImpactResolver unavailabilityImpactResolver,
            PlanServiceAssignmentMatching assignmentMatching,
            ILogger<PlanServiceTaskPreview> logger)
        {
            _db = db;
            _timeService = timeService;
            _taskPermissionService = taskPermissionService;
            _travelResolver = travelResolver;
            _placementValidator = placementValidator;
            _routePreviewLookupBuilder = routePreviewLookupBuilder;
            _unavailabilityImpactResolver = unavailabilityImpactResolver;
            _assignmentMatching = assignmentMatching;
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
                    cancellationToken: cancellationToken,
                    planId: targetTask.PlanId);
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

        public async Task<BulkSchedulePreviewResponseDto> PreviewBulkScheduleAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            UserContext user,
            CancellationToken cancellationToken = default)
        {
            var response = new BulkSchedulePreviewResponseDto();
            if (items == null || items.Count == 0)
            {
                return response;
            }

            var effectiveItems = items.Select(CloneCreateTaskRequest).ToList();
            if ("automatinis".Equals(planMode, StringComparison.OrdinalIgnoreCase) &&
                effectiveItems.Any(NeedsAutomaticResolution))
            {
                var suggestions = await _assignmentMatching.SuggestBulkAutoAssignmentsAsync(effectiveItems, cancellationToken);
                for (var i = 0; i < effectiveItems.Count; i++)
                {
                    var suggestion = suggestions[i];
                    if (!NeedsAutomaticResolution(effectiveItems[i]) || suggestion == null)
                    {
                        continue;
                    }

                    effectiveItems[i].TechnicianId = suggestion.TechnicianId;
                    effectiveItems[i].TargetDate = suggestion.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    effectiveItems[i].StartMinutes = suggestion.StartMinutes;
                    effectiveItems[i].EndMinutes = suggestion.EndMinutes;
                }
            }

            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.User)
                .ToListAsync(cancellationToken);

            var validDates = effectiveItems
                .Select(item => TryParseDate(item.TargetDate, out var parsed) ? parsed.Date : (DateTime?)null)
                .Where(date => date.HasValue)
                .Select(date => date!.Value)
                .ToList();

            var rangeStart = validDates.Count > 0
                ? validDates.Min()
                : DateTime.UtcNow.Date;
            var rangeEnd = validDates.Count > 0
                ? validDates.Max()
                : rangeStart;

            var loadRangeStart = _timeService.GetWeekStart(rangeStart);
            var loadRangeEndExclusive = _timeService.GetWeekEnd(rangeEnd).AddDays(1);

            var persistedTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Technician)
                    .ThenInclude(t => t.User)
                .Include(t => t.ServiceObject)
                .Where(t => t.StartAt >= loadRangeStart && t.StartAt < loadRangeEndExclusive)
                .ToListAsync(cancellationToken);

            var objectNames = effectiveItems
                .Select(item => item.ObjectName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var objectAddresses = effectiveItems
                .Select(item => item.ObjectAddress)
                .Where(address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var serviceObjects = await _db.ServiceObjects
                .AsNoTracking()
                .Include(o => o.Address)
                .Where(o => objectNames.Contains(o.Name) || objectAddresses.Contains(o.Address.FullAddress))
                .ToListAsync(cancellationToken);

            var serviceObjectByName = serviceObjects
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var serviceObjectByAddress = serviceObjects
                .GroupBy(o => o.Address.FullAddress, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var addressIds = persistedTasks
                .Where(t => t.ServiceObject != null)
                .Select(t => t.ServiceObject!.AddressId)
                .Concat(technicians.Select(t => t.WorkplaceAddressId))
                .Concat(serviceObjects.Select(o => o.AddressId))
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var routeLookup = await _routePreviewLookupBuilder.BuildAsync(addressIds, cancellationToken);
            var simulatedPreviewTasks = new List<TaskEntity>();

            for (var index = 0; index < effectiveItems.Count; index++)
            {
                var item = effectiveItems[index];
                var technician = technicians.FirstOrDefault(t => t.Id == item.TechnicianId);
                var serviceObject = ResolveServiceObject(item, serviceObjectByName, serviceObjectByAddress);

                var previewItem = new BulkSchedulePreviewItemDto
                {
                    Index = index,
                    SourceBacklogItemId = item.SourceBacklogItemId,
                    TechnicianId = item.TechnicianId,
                    TechnicianName = technician?.User?.Name ?? string.Empty,
                    TargetDate = item.TargetDate ?? string.Empty,
                    StartMinutes = item.StartMinutes,
                    EndMinutes = item.EndMinutes,
                    Title = string.IsNullOrWhiteSpace(item.Title) ? "Be pavadinimo" : item.Title.Trim(),
                    Priority = (int)item.Priority,
                    ObjectName = item.ObjectName ?? string.Empty,
                    ObjectAddress = item.ObjectAddress ?? string.Empty,
                    LoadState = "Conflict",
                    IsPlacementAllowed = false,
                };

                response.Items.Add(previewItem);

                if (technician == null)
                {
                    previewItem.RejectionCode = "TechnicianNotFound";
                    previewItem.RejectionMessage = "Technikas nerastas.";
                    continue;
                }

                if (serviceObject == null || serviceObject.AddressId <= 0)
                {
                    previewItem.RejectionCode = "ObjectNotFound";
                    previewItem.RejectionMessage = "Nepavyko nustatyti objekto vietos perziurai.";
                    continue;
                }

                if (!TryParseDate(item.TargetDate, out var targetDate))
                {
                    previewItem.RejectionCode = "InvalidDate";
                    previewItem.RejectionMessage = "Netinkamas datos formatas.";
                    continue;
                }

                if (item.EndMinutes <= item.StartMinutes)
                {
                    previewItem.RejectionCode = "InvalidTime";
                    previewItem.RejectionMessage = "Pabaigos laikas turi buti veliau uz pradzios laika.";
                    continue;
                }

                var startAtUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate.Date, item.StartMinutes, item.TimezoneOffsetMinutes);
                var endAtUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate.Date, item.EndMinutes, item.TimezoneOffsetMinutes);
                var dayStartUtc = startAtUtc.Date;
                var runningTasks = persistedTasks.Concat(simulatedPreviewTasks).ToList();
                var simulatedStops = BuildSimulatedStops(simulatedPreviewTasks, technician.Id, dayStartUtc);

                var placement = await _placementValidator.ValidateTimingAndTravelAsync(
                    taskDateLocal: targetDate.Date,
                    startMinutesLocal: item.StartMinutes,
                    timezoneOffsetMinutes: item.TimezoneOffsetMinutes,
                    technicianId: technician.Id,
                    startAtUtc: startAtUtc,
                    endAtUtc: endAtUtc,
                    serviceAddressId: serviceObject.AddressId,
                    excludeTaskId: null,
                    cancellationToken: cancellationToken,
                    simulatedStops: simulatedStops,
                    checkOverlap: true);

                var previewTask = new TaskEntity
                {
                    Id = -1000000 - index,
                    PlanId = 0,
                    TechnicianId = technician.Id,
                    Technician = technician,
                    ServiceObjectId = serviceObject.Id,
                    ServiceObject = serviceObject,
                    Title = previewItem.Title,
                    StartAt = startAtUtc,
                    EndAt = endAtUtc,
                    Priority = item.Priority,
                    Status = ScheduleTaskStatus.InProgress,
                    CreatedAt = DateTime.UtcNow,
                    SourceBacklogItemId = item.SourceBacklogItemId
                };

                runningTasks.Add(previewTask);

                var loadSnapshot = BuildLoadSnapshotForSimulation(
                    technician.Id,
                    dayStartUtc,
                    runningTasks,
                    technicians);

                var fallbackTravelHours = ComputeTravelHoursForSimulation(
                    technician.Id,
                    dayStartUtc,
                    runningTasks,
                    technicians,
                    routeLookup,
                    strictRouteCoverage: false);

                var projectedTravelHours = ComputeTravelHoursForSimulation(
                    technician.Id,
                    dayStartUtc,
                    runningTasks,
                    technicians,
                    routeLookup,
                    strictRouteCoverage: true);

                previewItem.LoadState = loadSnapshot.LoadState;
                previewItem.HasConflict = loadSnapshot.HasConflict;
                previewItem.IsDailyLimitExceeded = loadSnapshot.IsDailyLimitExceeded;
                previewItem.IsWeeklyLimitExceeded = loadSnapshot.IsWeeklyLimitExceeded;
                previewItem.DailyHours = Math.Round(loadSnapshot.DailyHours, 2);
                previewItem.WeeklyHours = Math.Round(loadSnapshot.WeeklyHours, 2);
                previewItem.DailyLimit = loadSnapshot.DailyLimit;
                previewItem.WeeklyLimit = loadSnapshot.WeeklyLimit;
                previewItem.DailyUtilization = Math.Round(loadSnapshot.DailyUtilization, 2);
                previewItem.WeeklyUtilization = Math.Round(loadSnapshot.WeeklyUtilization, 2);
                previewItem.TravelTimeHours = Math.Round(projectedTravelHours ?? fallbackTravelHours ?? loadSnapshot.TravelTimeHours, 2);
                previewItem.RequiredInboundTravelMinutes = ComputeRequiredInboundTravelMinutesForTask(
                    technician.Id,
                    dayStartUtc,
                    previewTask.Id,
                    runningTasks,
                    technicians,
                    routeLookup);
                previewItem.IsPlacementAllowed = placement.IsValid;
                previewItem.RejectionCode = placement.IsValid
                    ? null
                    : (placement.IsInvalidWindow ? "InvalidWindow" : "TravelInfeasible");
                previewItem.RejectionMessage = placement.IsValid ? null : placement.Message;

                simulatedPreviewTasks.Add(previewTask);
            }

            return response;
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

        private static bool NeedsAutomaticResolution(CreateTaskRequestDto item) =>
            item.TechnicianId <= 0 ||
            string.IsNullOrWhiteSpace(item.TargetDate) ||
            item.EndMinutes <= item.StartMinutes;

        private static CreateTaskRequestDto CloneCreateTaskRequest(CreateTaskRequestDto item) =>
            new()
            {
                TechnicianId = item.TechnicianId,
                TargetDate = item.TargetDate,
                StartMinutes = item.StartMinutes,
                EndMinutes = item.EndMinutes,
                Title = item.Title,
                Priority = item.Priority,
                Status = item.Status,
                TimezoneOffsetMinutes = item.TimezoneOffsetMinutes,
                SkillLevel = item.SkillLevel,
                ObjectName = item.ObjectName,
                ObjectAddress = item.ObjectAddress,
                Instructions = item.Instructions,
                SourceBacklogItemId = item.SourceBacklogItemId,
                SimulatedAssignments = item.SimulatedAssignments == null
                    ? null
                    : new List<SimulatedTaskAssignmentDto>(item.SimulatedAssignments.Select(assignment => new SimulatedTaskAssignmentDto
                    {
                        TechnicianId = assignment.TechnicianId,
                        StartAt = assignment.StartAt,
                        EndAt = assignment.EndAt,
                        AddressId = assignment.AddressId,
                        ServiceObjectId = assignment.ServiceObjectId,
                        SourceBacklogItemId = assignment.SourceBacklogItemId
                    }))
            };

        private static bool TryParseDate(string? value, out DateTime parsed)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var resolved))
            {
                parsed = resolved.Date;
                return true;
            }

            parsed = default;
            return false;
        }

        private static ServiceObjectEntity? ResolveServiceObject(
            CreateTaskRequestDto item,
            IReadOnlyDictionary<string, ServiceObjectEntity> serviceObjectByName,
            IReadOnlyDictionary<string, ServiceObjectEntity> serviceObjectByAddress)
        {
            if (!string.IsNullOrWhiteSpace(item.ObjectName) &&
                serviceObjectByName.TryGetValue(item.ObjectName, out var byName))
            {
                return byName;
            }

            if (!string.IsNullOrWhiteSpace(item.ObjectAddress) &&
                serviceObjectByAddress.TryGetValue(item.ObjectAddress, out var byAddress))
            {
                return byAddress;
            }

            return null;
        }

        private static IReadOnlyList<ProposedRouteStop> BuildSimulatedStops(
            IEnumerable<TaskEntity> previewTasks,
            long technicianId,
            DateTime dayStartUtc)
        {
            return previewTasks
                .Where(task => task.TechnicianId == technicianId && task.StartAt.Date == dayStartUtc.Date)
                .Where(task => task.ServiceObject != null && WorkloadCalculator.CountsTowardsWorkload(task.Status))
                .OrderBy(task => task.StartAt)
                .Select(task => new ProposedRouteStop
                {
                    StartAt = task.StartAt,
                    EndAt = task.EndAt,
                    AddressId = task.ServiceObject!.AddressId
                })
                .ToList();
        }
    }
}
