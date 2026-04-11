using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator
{
    internal class PlanServiceTaskCreation
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IScheduleTimeService _timeService;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly ITaskHistoryWriter _historyWriter;
        private readonly SchedulePlacementValidator _placementValidator;
        private readonly ServiceLocationResolver _locationResolver;
        private readonly RouteEvaluationCaller _routeEvaluationCaller;
        private readonly PlanServicePlanLifecycle _planLifecycle;
        private readonly IPlanOperationInstrumentation _operationInstrumentation;
        private readonly ILogger<PlanServiceTaskCreation> _logger;

        public PlanServiceTaskCreation(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IScheduleTimeService timeService,
            IScheduleRepairService scheduleRepairService,
            ITaskHistoryWriter historyWriter,
            SchedulePlacementValidator placementValidator,
            ServiceLocationResolver locationResolver,
            RouteEvaluationCaller routeEvaluationCaller,
            PlanServicePlanLifecycle planLifecycle,
            IPlanOperationInstrumentation operationInstrumentation,
            ILogger<PlanServiceTaskCreation> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _timeService = timeService;
            _scheduleRepairService = scheduleRepairService;
            _historyWriter = historyWriter;
            _placementValidator = placementValidator;
            _locationResolver = locationResolver;
            _routeEvaluationCaller = routeEvaluationCaller;
            _planLifecycle = planLifecycle;
            _operationInstrumentation = operationInstrumentation;
            _logger = logger;
        }

        private Task<List<CreateTaskRequestDto>> PrepareBulkItemsForModeAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            CancellationToken cancellationToken)
        {
            var effectiveItems = items as List<CreateTaskRequestDto> ?? new List<CreateTaskRequestDto>(items);

            if (!"automatinis".Equals(planMode, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(effectiveItems);
            }

            _logger.LogInformation(
                "svc.plan.bulk.mode.automatinis.prepare itemCount={ItemCount} action=use-client-assignments",
                effectiveItems.Count);
            return Task.FromResult(effectiveItems);
        }

        private enum CreateTaskTargetScope
        {
            Active,
            Draft,
        }

        private async Task<(PlanEntity? plan, TaskCreateResult? failure)> ResolveCreateTargetPlanAsync(
            CreateTaskTargetScope targetScope,
            DateTime taskDate,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            if (targetScope == CreateTaskTargetScope.Draft)
            {
                var draftPlan = await _db.Plans
                    .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

                if (draftPlan == null)
                {
                    _logger.LogWarning("svc.plan.create-task.target draft-missing taskDate={TaskDate}", taskDate);
                    return (null, new TaskCreateResult(TaskCreateError.InvalidWindow, "Juodrastinis planas nerastas."));
                }

                if (taskDate < draftPlan.PeriodStart.Date || taskDate > draftPlan.PeriodEnd.Date)
                {
                    var nextStart = taskDate < draftPlan.PeriodStart.Date ? taskDate : draftPlan.PeriodStart.Date;
                    var nextEnd = taskDate > draftPlan.PeriodEnd.Date ? taskDate : draftPlan.PeriodEnd.Date;
                    var totalDays = (nextEnd - nextStart).Days + 1;

                    draftPlan.PeriodStart = nextStart;
                    draftPlan.PeriodEnd = nextEnd;
                    draftPlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));

                    _db.Plans.Update(draftPlan);
                    await _db.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation(
                        "svc.plan.create-task.target draft-window-expanded taskDate={TaskDate} planId={PlanId} planStart={PlanStart} planEnd={PlanEnd}",
                        taskDate,
                        draftPlan.Id,
                        draftPlan.PeriodStart,
                        draftPlan.PeriodEnd);
                }

                _logger.LogInformation("svc.plan.create-task.target plan=Draft planId={PlanId}", draftPlan.Id);
                return (draftPlan, null);
            }

            var activePlan = await _db.Plans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            if (activePlan != null && taskDate >= activePlan.PeriodStart.Date && taskDate <= activePlan.PeriodEnd.Date)
            {
                _logger.LogInformation("svc.plan.create-task.target plan=Active planId={PlanId}", activePlan.Id);
                return (activePlan, null);
            }

            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            var resolvedActivePlan = await _planLifecycle.GetOrExtendActivePlanAsync(activePlan, taskDate, userLocalToday, cancellationToken);
            _logger.LogInformation("svc.plan.create-task.target plan=Extended/New planId={PlanId}", resolvedActivePlan.Id);
            return (resolvedActivePlan, null);
        }

        private async Task<TaskCreateResult> CreateTaskInternalAsync(
            CreateTaskTargetScope targetScope,
            long technicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            string title,
            TaskPriority priority,
            ScheduleTaskStatus status,
            string skillLevel,
            string objectName,
            string objectAddress,
            string instructions,
            UserContext user,
            int timezoneOffsetMinutes,
            long? sourceBacklogItemId = null,
            CancellationToken cancellationToken = default)
        {
            var taskDate = targetDate.Date;
            var (targetPlan, targetPlanFailure) = await ResolveCreateTargetPlanAsync(targetScope, taskDate, timezoneOffsetMinutes, cancellationToken);
            if (targetPlan == null)
            {
                return targetPlanFailure ?? new TaskCreateResult(TaskCreateError.InvalidWindow, "Nepavyko nustatyti tikslinio plano.");
            }

        
            var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Nauja uzduotis" : title.Trim();
            var normalizedSkill = string.IsNullOrWhiteSpace(skillLevel) ? string.Empty : skillLevel.Trim();
            var normalizedInstructions = string.IsNullOrWhiteSpace(instructions) ? string.Empty : instructions.Trim();

            var placementValidation = await _placementValidator.ValidatePlacementForActionAsync(
                ignoreTaskIds: null,
                targetTechnicianId: technicianId,
                targetDate: targetDate,
                startMinutes: startMinutes,
                endMinutes: endMinutes,
                plan: targetPlan,
                status: status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (!placementValidation.IsValid)
            {
                if (placementValidation.IsBlockedByUnavailability)
                {
                    // Resolve service object now (needed for repair plan, also needed below if repair succeeds)
                    var preResolvedId = await _locationResolver.FindOrCreateServiceObjectAsync(objectName, objectAddress, placementValidation.City, cancellationToken);
                    var preResolvedMeta = await _db.ServiceObjects
                        .Where(o => o.Id == preResolvedId)
                        .Select(o => new { o.AddressId })
                        .FirstAsync(cancellationToken);

                    var unavailRepair = await _placementValidator.TryBuildUnavailabilityRepairForSlotAsync(
                        technicianId,
                        placementValidation.StartAtUtc,
                        placementValidation.EndAtUtc,
                        preResolvedMeta.AddressId,
                        timezoneOffsetMinutes,
                        cancellationToken);

                    _logger.LogInformation(
                        "[REPAIR-BUILD-RESULT] techId={TechId} repairPlan={RepairSuccess} primary={Primary}",
                        technicianId, unavailRepair?.Success ?? false, unavailRepair?.PrimaryPlacement != null);

                    if (unavailRepair?.Success == true && unavailRepair.PrimaryPlacement != null)
                    {
                        var repStartLocal = _timeService.ConvertUtcToLocal(unavailRepair.PrimaryPlacement.StartAt, timezoneOffsetMinutes);
                        var repEndLocal = _timeService.ConvertUtcToLocal(unavailRepair.PrimaryPlacement.EndAt, timezoneOffsetMinutes);
                        var repStartMin = (int)repStartLocal.TimeOfDay.TotalMinutes;
                        var repEndMin = (int)repEndLocal.TimeOfDay.TotalMinutes;

                        var repairedPlacement = await _placementValidator.ValidatePlacementForActionAsync(
                            ignoreTaskIds: null,
                            targetTechnicianId: technicianId,
                            targetDate: repStartLocal.Date,
                            startMinutes: repStartMin,
                            endMinutes: repEndMin,
                            plan: targetPlan,
                            status: status,
                            user: user,
                            timezoneOffsetMinutes: timezoneOffsetMinutes,
                            requireEndAligned: true,
                            cancellationToken: cancellationToken);

                        if (repairedPlacement.IsValid)
                        {
                            var repairedTask = new TaskEntity
                            {
                                PlanId = targetPlan.Id,
                                TechnicianId = technicianId,
                                ServiceObjectId = preResolvedId,
                                Title = normalizedTitle,
                                SkillLevel = normalizedSkill,
                                Instructions = normalizedInstructions,
                                StartAt = repairedPlacement.StartAtUtc,
                                EndAt = repairedPlacement.EndAtUtc,
                                Priority = priority,
                                Status = status,
                                SourceBacklogItemId = sourceBacklogItemId,
                                CreatedAt = DateTime.UtcNow
                            };

                            await using var repairTx = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
                            _db.Tasks.Add(repairedTask);
                            await _db.SaveChangesAsync(cancellationToken);
                            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(repairedTask.TechnicianId, repairedTask.StartAt.Date, "create", cancellationToken);
                            if (repairTx != null) await repairTx.CommitAsync(cancellationToken);

                            var repairVersion = _db.Entry(repairedTask).Property<uint>("xmin").CurrentValue;
                            await _evaluationEngine.RecalculatePlanAsync(targetPlan.Id, cancellationToken);
                            var repairMessage = _placementValidator.BuildRepairAppliedMessage(
                                "Pasirinktas laikas persidengia su techniko neaktyvumu.",
                                unavailRepair.PrimaryPlacement, timezoneOffsetMinutes);
                            var _rUnavail = new TaskCreateResult(TaskCreateError.None, repairMessage, repairedTask, repairVersion);
                            var (_mUnavail, _oUnavail) = TaskHistoryMessages.ForCreate(_rUnavail, repairedTask.Title);
                            await _historyWriter.RecordAsync(repairedTask.Id, repairedTask.PlanId, "create", _oUnavail, _mUnavail, user.UserId, user.Role.ToString().ToLower(),
                                new { technicianId = repairedTask.TechnicianId, startAt = repairedTask.StartAt, endAt = repairedTask.EndAt, unavailabilityRepairApplied = true }, cancellationToken: cancellationToken);
                            return _rUnavail;
                        }
                    }
                }

                var _r = SchedulePlacementValidator.MapPlacementErrorForCreate(placementValidation);
                var (_msg, _out) = TaskHistoryMessages.ForCreate(_r, normalizedTitle);
                await _historyWriter.RecordAsync(null, targetPlan.Id, "create", _out, _msg, user.UserId, user.Role.ToString().ToLower(),
                    new { technicianId = technicianId }, cancellationToken: cancellationToken);
                return _r;
            }

            var serviceObjectId = await _locationResolver.FindOrCreateServiceObjectAsync(objectName, objectAddress, placementValidation.City, cancellationToken);
            var serviceObjectMeta = await _db.ServiceObjects
                .Where(o => o.Id == serviceObjectId)
                .Select(o => new { o.AddressId })
                .FirstAsync(cancellationToken);

            var task = new TaskEntity
            {
                PlanId = targetPlan.Id,
                TechnicianId = technicianId,
                ServiceObjectId = serviceObjectId,
                Title = normalizedTitle,
                SkillLevel = normalizedSkill,
                Instructions = normalizedInstructions,
                StartAt = placementValidation.StartAtUtc,
                EndAt = placementValidation.EndAtUtc,
                Priority = priority,
                Status = status,
                SourceBacklogItemId = sourceBacklogItemId,
                CreatedAt = DateTime.UtcNow
            };

            await using var transaction = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(cancellationToken);

            await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "create", cancellationToken);
            var timingTravelValidation = await _placementValidator.ValidateTimingAndTravelAsync(
                taskDateLocal: taskDate,
                startMinutesLocal: startMinutes,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: task.TechnicianId,
                startAtUtc: task.StartAt,
                endAtUtc: task.EndAt,
                serviceAddressId: serviceObjectMeta.AddressId,
                excludeTaskId: task.Id,
                cancellationToken: cancellationToken,
                planId: task.PlanId);
            if (!timingTravelValidation.IsValid)
            {
                if (!timingTravelValidation.IsInvalidWindow && await _placementValidator.TryApplyTimingTravelRepairAsync(task, timingTravelValidation, cancellationToken))
                {
                    await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "create-repair", cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    var repairedVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                    await _evaluationEngine.RecalculatePlanAsync(targetPlan.Id, cancellationToken);
                    var _rRepair = new TaskCreateResult(
                        TaskCreateError.None,
                        _placementValidator.BuildRepairAppliedMessage(timingTravelValidation.Message, timingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task,
                        repairedVersion);
                    var (_msgRepair, _outRepair) = TaskHistoryMessages.ForCreate(_rRepair, task.Title);
                    await _historyWriter.RecordAsync(task.Id, task.PlanId, "create", _outRepair, _msgRepair, user.UserId, user.Role.ToString().ToLower(),
                        new { technicianId = task.TechnicianId, startAt = task.StartAt, endAt = task.EndAt, repairApplied = true }, cancellationToken: cancellationToken);
                    return _rRepair;
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var _rFail = new TaskCreateResult(
                    timingTravelValidation.IsInvalidWindow ? TaskCreateError.InvalidWindow : TaskCreateError.TravelInfeasible,
                    timingTravelValidation.Message);
                var (_msgFail, _outFail) = TaskHistoryMessages.ForCreate(_rFail, normalizedTitle);
                await _historyWriter.RecordAsync(null, targetPlan.Id, "create", _outFail, _msgFail, user.UserId, user.Role.ToString().ToLower(),
                    new { technicianId = technicianId }, cancellationToken: cancellationToken);
                return _rFail;
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var version = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            await _evaluationEngine.RecalculatePlanAsync(targetPlan.Id, cancellationToken);
            var _rOk = new TaskCreateResult(TaskCreateError.None, Task: task, CurrentVersion: version);
            var (_msgOk, _outOk) = TaskHistoryMessages.ForCreate(_rOk, task.Title);
            await _historyWriter.RecordAsync(task.Id, task.PlanId, "create", _outOk, _msgOk, user.UserId, user.Role.ToString().ToLower(),
                new { technicianId = task.TechnicianId, startAt = task.StartAt, endAt = task.EndAt }, cancellationToken: cancellationToken);
            return _rOk;
        }

        public Task<TaskCreateResult> CreateTaskAsync(
            long technicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            string title,
            TaskPriority priority,
            ScheduleTaskStatus status,
            string skillLevel,
            string objectName,
            string objectAddress,
            string instructions,
            UserContext user,
            int timezoneOffsetMinutes,
            long? sourceBacklogItemId = null,
            CancellationToken cancellationToken = default)
            => CreateTaskInternalAsync(
                CreateTaskTargetScope.Active,
                technicianId,
                targetDate,
                startMinutes,
                endMinutes,
                title,
                priority,
                status,
                skillLevel,
                objectName,
                objectAddress,
                instructions,
                user,
                timezoneOffsetMinutes,
                sourceBacklogItemId,
                cancellationToken);

        public Task<TaskCreateResult> CreateDraftTaskAsync(
            long technicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            string title,
            TaskPriority priority,
            ScheduleTaskStatus status,
            string skillLevel,
            string objectName,
            string objectAddress,
            string instructions,
            UserContext user,
            int timezoneOffsetMinutes,
            long? sourceBacklogItemId = null,
            CancellationToken cancellationToken = default)
            => CreateTaskInternalAsync(
                CreateTaskTargetScope.Draft,
                technicianId,
                targetDate,
                startMinutes,
                endMinutes,
                title,
                priority,
                status,
                skillLevel,
                objectName,
                objectAddress,
                instructions,
                user,
                timezoneOffsetMinutes,
                sourceBacklogItemId,
                cancellationToken);

        private sealed class BulkCreateContext
        {
            public required PlanEntity? DraftPlan { get; init; }
            public required PlanEntity? ActivePlan { get; set; }
            public required List<PlanEntity> SupersededPlans { get; init; }
            public required DateTime[] ParsedDates { get; init; }
            public required PlanEntity?[] TargetPlans { get; init; }
            public required SchedulePlacementValidator.PlacementValidationResult[] Placements { get; init; }
            public required long?[] KnownAddressIds { get; init; }
            public required SchedulePlacementValidator.PlacementValidationContext PlacementContext { get; init; }
            public required ServiceLocationResolver.ServiceAddressLookupCache AddressLookupCache { get; init; }
            public List<(long TechnicianId, long AddressId, DateTime StartAt, DateTime EndAt)> SimulatedBatchStops { get; } = new();
        }

        private sealed class BulkValidationOutcome
        {
            public List<BulkCreateItemErrorDto> Errors { get; } = new();
            public bool HasErrors => Errors.Count > 0;
        }

        private sealed class BulkTaskBuildResult
        {
            public required List<TaskEntity> Tasks { get; init; }
            public BulkCreateTaskResponseDto? FailureResponse { get; init; }
        }

        private sealed record BulkServiceObjectRequest(string ObjectName, string ObjectAddress, string City);
        private readonly record struct BulkServiceObjectResolution(long ServiceObjectId, long AddressId);

        private async Task<BulkCreateContext> LoadBulkCreateContextAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            CancellationToken cancellationToken)
        {
            var itemCount = items.Count;
            var parseableDates = items
                .Select(item => DateTime.TryParseExact(
                    item.TargetDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? (DateTime?)parsed.Date
                    : null)
                .Where(date => date.HasValue)
                .Select(date => date!.Value)
                .ToList();

            var techIds = items
                .Select(item => item.TechnicianId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var placementContext = parseableDates.Count > 0 && techIds.Count > 0
                ? await _placementValidator.BuildPlacementValidationContextAsync(
                    techIds,
                    parseableDates.Min(),
                    parseableDates.Max().AddDays(1),
                    cancellationToken)
                : new SchedulePlacementValidator.PlacementValidationContext();
            var addressLookupCache = await _locationResolver.BuildLookupCacheAsync(
                items.Select(item => new ServiceLocationResolver.ServiceLocationLookupCandidate(item.ObjectName, item.ObjectAddress)),
                cancellationToken);

            return new BulkCreateContext
            {
                DraftPlan = await _db.Plans.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken),
                ActivePlan = await _db.Plans.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken),
                SupersededPlans = await _db.Plans.AsNoTracking()
                    .Where(p => p.Status == PlanStatus.Superseded)
                    .OrderByDescending(p => p.CreatedAt)
                    .ToListAsync(cancellationToken),
                ParsedDates = new DateTime[itemCount],
                TargetPlans = new PlanEntity?[itemCount],
                Placements = new SchedulePlacementValidator.PlacementValidationResult[itemCount],
                KnownAddressIds = new long?[itemCount],
                PlacementContext = placementContext,
                AddressLookupCache = addressLookupCache
            };
        }

        private async Task<BulkValidationOutcome> ValidateBulkCreateRequestsAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            UserContext user,
            BulkCreateContext context,
            CancellationToken cancellationToken,
            string? planScope = null)
        {
            var outcome = new BulkValidationOutcome();
            var reservedSlots = new List<(long TechId, DateTime StartUtc, DateTime EndUtc)>();

            _logger.LogInformation(
                "svc.plan.bulk.validate.start itemCount={ItemCount} userId={UserId}",
                items.Count,
                user.UserId);

            for (int i = 0; i < items.Count; i++)
            {
                var dto = items[i];

                _logger.LogDebug(
                    "svc.plan.bulk.validate.item index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} targetDate={TargetDate} startMinutes={StartMinutes} endMinutes={EndMinutes}",
                    i,
                    dto.SourceBacklogItemId,
                    dto.TechnicianId,
                    dto.TargetDate,
                    dto.StartMinutes,
                    dto.EndMinutes);

                if (dto.TechnicianId <= 0)
                {
                    _logger.LogWarning(
                        "svc.plan.bulk.validate.manual-tech-required index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId}",
                        i,
                        dto.SourceBacklogItemId,
                        dto.TechnicianId);
                    outcome.Errors.Add(new BulkCreateItemErrorDto { Index = i, Message = "Priskirtas darbuotojas yra privalomas." });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(dto.SkillLevel) ||
                    string.IsNullOrWhiteSpace(dto.ObjectName) ||
                    string.IsNullOrWhiteSpace(dto.ObjectAddress))
                {
                    outcome.Errors.Add(new BulkCreateItemErrorDto { Index = i, Message = "Privalomi laukai turi būti užpildyti." });
                    continue;
                }

                if (!DateTime.TryParseExact(dto.TargetDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
                {
                    _logger.LogWarning(
                        "svc.plan.bulk.validate.invalid-date index={Index} backlogItemId={BacklogItemId} targetDate={TargetDate}",
                        i,
                        dto.SourceBacklogItemId,
                        dto.TargetDate);
                    outcome.Errors.Add(new BulkCreateItemErrorDto { Index = i, Message = "Netinkamas datos formatas." });
                    continue;
                }

                var targetDate = parsedDate.Date;
                context.ParsedDates[i] = targetDate;

                PlanEntity? targetPlan;
                if ("draft".Equals(planScope, StringComparison.OrdinalIgnoreCase))
                {
                    targetPlan = context.DraftPlan;
                }
                else
                {
                    targetPlan = null;
                    if (context.DraftPlan != null && targetDate >= context.DraftPlan.PeriodStart.Date && targetDate <= context.DraftPlan.PeriodEnd.Date)
                        targetPlan = context.DraftPlan;
                    else if (context.ActivePlan != null && targetDate >= context.ActivePlan.PeriodStart.Date && targetDate <= context.ActivePlan.PeriodEnd.Date)
                        targetPlan = context.ActivePlan;
                    else
                        targetPlan = context.SupersededPlans.FirstOrDefault(p => targetDate >= p.PeriodStart.Date && targetDate <= p.PeriodEnd.Date);
                }
                context.TargetPlans[i] = targetPlan;

                var ignoreIds = dto.IgnoreTaskId.HasValue
                    ? (IReadOnlyCollection<long>)new[] { dto.IgnoreTaskId.Value }
                    : null;
                var placement = await _placementValidator.ValidatePlacementForActionAsync(
                    ignoreTaskIds: ignoreIds,
                    targetTechnicianId: dto.TechnicianId,
                    targetDate: targetDate,
                    startMinutes: dto.StartMinutes,
                    endMinutes: dto.EndMinutes,
                    plan: targetPlan,
                    status: ScheduleTaskStatus.InProgress,
                    user: user,
                    timezoneOffsetMinutes: dto.TimezoneOffsetMinutes,
                    requireEndAligned: true,
                    cancellationToken: cancellationToken,
                    context: context.PlacementContext);

                if (!placement.IsValid)
                {
                    _logger.LogWarning(
                        "svc.plan.bulk.validate.invalid-placement index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} message={Message}",
                        i,
                        dto.SourceBacklogItemId,
                        dto.TechnicianId,
                        placement.Message);
                    outcome.Errors.Add(new BulkCreateItemErrorDto { Index = i, Message = placement.Message ?? "Netinkamas laiko langas." });
                    continue;
                }

                var hasOverlap = reservedSlots.Any(r =>
                    r.TechId == dto.TechnicianId &&
                    r.StartUtc < placement.EndAtUtc &&
                    r.EndUtc > placement.StartAtUtc);

                if (hasOverlap)
                {
                    var conflictingSlot = reservedSlots.First(r =>
                        r.TechId == dto.TechnicianId &&
                        r.StartUtc < placement.EndAtUtc &&
                        r.EndUtc > placement.StartAtUtc);

                    _logger.LogWarning(
                        "svc.plan.bulk.validate.reserved-overlap index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} timezoneOffsetMinutes={TimezoneOffsetMinutes} localDate={LocalDate} localStartMinutes={LocalStartMinutes} localEndMinutes={LocalEndMinutes} startAtUtc={StartAtUtc} endAtUtc={EndAtUtc} conflictingStartUtc={ConflictingStartUtc} conflictingEndUtc={ConflictingEndUtc}",
                        i,
                        dto.SourceBacklogItemId,
                        dto.TechnicianId,
                        dto.TimezoneOffsetMinutes,
                        targetDate,
                        dto.StartMinutes,
                        dto.EndMinutes,
                        placement.StartAtUtc,
                        placement.EndAtUtc,
                        conflictingSlot.StartUtc,
                        conflictingSlot.EndUtc);

                    outcome.Errors.Add(new BulkCreateItemErrorDto { Index = i, Message = "Laiko langas persidengė su kita šio paketo užduotimi." });
                    continue;
                }

                _logger.LogDebug(
                    "svc.plan.bulk.validate.placement-ok index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} startAtUtc={StartAtUtc} endAtUtc={EndAtUtc}",
                    i,
                    dto.SourceBacklogItemId,
                    dto.TechnicianId,
                    placement.StartAtUtc,
                    placement.EndAtUtc);
                context.Placements[i] = placement;

                var addressId = await _locationResolver.TryLookupServiceAddressIdAsync(
                    dto.ObjectName,
                    dto.ObjectAddress,
                    cancellationToken,
                    context.AddressLookupCache);
                context.KnownAddressIds[i] = addressId;

                if (!addressId.HasValue)
                {
                    _logger.LogDebug(
                        "svc.plan.bulk.validate.address-missing index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} objectName={ObjectName}",
                        i,
                        dto.SourceBacklogItemId,
                        dto.TechnicianId,
                        dto.ObjectName);
                    continue;
                }

                var travelCheck = await _placementValidator.CheckTimingAndTravelAsync(
                    taskDateLocal: targetDate,
                    startMinutesLocal: dto.StartMinutes,
                    timezoneOffsetMinutes: dto.TimezoneOffsetMinutes,
                    technicianId: dto.TechnicianId,
                    startAtUtc: placement.StartAtUtc,
                    endAtUtc: placement.EndAtUtc,
                    serviceAddressId: addressId.Value,
                    excludeTaskId: null,
                    cancellationToken: cancellationToken,
                    simulatedStops: BuildBulkSimulatedStops(context.SimulatedBatchStops, dto.TechnicianId, targetDate));

                if (!travelCheck.IsValid)
                {
                    _logger.LogWarning(
                        "svc.plan.bulk.validate.travel-failed index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} message={Message}",
                        i,
                        dto.SourceBacklogItemId,
                        dto.TechnicianId,
                        travelCheck.Message);
                    outcome.Errors.Add(new BulkCreateItemErrorDto { Index = i, Message = travelCheck.Message ?? "Nepakanka laiko kelionei." });
                    continue;
                }

                reservedSlots.Add((dto.TechnicianId, placement.StartAtUtc, placement.EndAtUtc));
                // Only add to simulated stops when the task doesn't already exist in the DB.
                // Already-materialized tasks are picked up by the route chain builder as real tasks,
                // so adding them here too would create duplicates that cause false travel failures.
                if (!dto.IgnoreTaskId.HasValue)
                {
                    context.SimulatedBatchStops.Add((dto.TechnicianId, addressId.Value, placement.StartAtUtc, placement.EndAtUtc));
                }
            }

            _logger.LogInformation(
                "svc.plan.bulk.validate.completed itemCount={ItemCount} errorCount={ErrorCount}",
                items.Count,
                outcome.Errors.Count);

            return outcome;
        }

        private static IReadOnlyList<ProposedRouteStop> BuildBulkSimulatedStops(
            IReadOnlyCollection<(long TechnicianId, long AddressId, DateTime StartAt, DateTime EndAt)> simulatedBatchStops,
            long technicianId,
            DateTime targetDate)
        {
            return simulatedBatchStops
                .Where(stop => stop.TechnicianId == technicianId && stop.StartAt.Date == targetDate.Date)
                .OrderBy(stop => stop.StartAt)
                .Select(stop => new ProposedRouteStop
                {
                    StartAt = stop.StartAt,
                    EndAt = stop.EndAt,
                    AddressId = stop.AddressId
                })
                .ToList();
        }

        private async Task<BulkTaskBuildResult> BuildTaskEntitiesAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            BulkCreateContext context,
            CancellationToken cancellationToken)
        {
            var createdTasks = new List<TaskEntity>(items.Count);
            var userLocalToday = _timeService.GetUserLocalToday(items[0].TimezoneOffsetMinutes);
            var serviceObjectResolutions = await BuildBulkServiceObjectResolutionsAsync(items, context, cancellationToken);

            _logger.LogInformation("svc.plan.bulk.build.start itemCount={ItemCount}", items.Count);

            for (int i = 0; i < items.Count; i++)
            {
                var dto = items[i];
                var targetDate = context.ParsedDates[i];

                var targetPlan = context.TargetPlans[i];
                if (targetPlan == null)
                {
                    context.ActivePlan = await _planLifecycle.GetOrExtendActivePlanAsync(context.ActivePlan, targetDate, userLocalToday, cancellationToken);
                    targetPlan = context.ActivePlan;
                    _logger.LogInformation(
                        "svc.plan.bulk.build.extended-plan index={Index} backlogItemId={BacklogItemId} planId={PlanId} targetDate={TargetDate}",
                        i,
                        dto.SourceBacklogItemId,
                        targetPlan?.Id,
                        targetDate);
                }

                if (targetPlan == null)
                {
                    throw new InvalidOperationException("Bulk task build could not resolve a target plan.");
                }

                var targetPlanId = targetPlan.Id;
                var placement = context.Placements[i];
                var serviceObject = ResolveBulkServiceObject(dto, serviceObjectResolutions);
                var technicianId = dto.TechnicianId;

                if (!context.KnownAddressIds[i].HasValue)
                {
                    var travelCheck = await _placementValidator.CheckTimingAndTravelAsync(
                        taskDateLocal: targetDate,
                        startMinutesLocal: dto.StartMinutes,
                        timezoneOffsetMinutes: dto.TimezoneOffsetMinutes,
                        technicianId: technicianId,
                        startAtUtc: placement.StartAtUtc,
                        endAtUtc: placement.EndAtUtc,
                        serviceAddressId: serviceObject.AddressId,
                        excludeTaskId: null,
                        cancellationToken: cancellationToken,
                        simulatedStops: BuildBulkSimulatedStops(context.SimulatedBatchStops, technicianId, targetDate));

                    if (!travelCheck.IsValid)
                    {
                        _logger.LogWarning(
                            "svc.plan.bulk.build.travel-failed index={Index} backlogItemId={BacklogItemId} technicianId={TechnicianId} message={Message}",
                            i,
                            dto.SourceBacklogItemId,
                            dto.TechnicianId,
                            travelCheck.Message);
                        return new BulkTaskBuildResult
                        {
                            Tasks = createdTasks,
                            FailureResponse = new BulkCreateTaskResponseDto
                            {
                                AllSucceeded = false,
                                Errors = [new BulkCreateItemErrorDto { Index = i, Message = travelCheck.Message ?? "Nepakanka laiko kelionei." }]
                            }
                        };
                    }
                }

                _logger.LogInformation(
                    "svc.plan.bulk.build.task index={Index} backlogItemId={BacklogItemId} planId={PlanId} technicianId={TechnicianId} startAtUtc={StartAtUtc} endAtUtc={EndAtUtc}",
                    i,
                    dto.SourceBacklogItemId,
                    targetPlanId,
                    technicianId,
                    placement.StartAtUtc,
                    placement.EndAtUtc);

                context.SimulatedBatchStops.Add((technicianId, serviceObject.AddressId, placement.StartAtUtc, placement.EndAtUtc));

                createdTasks.Add(new TaskEntity
                {
                    PlanId = targetPlanId,
                    TechnicianId = technicianId,
                    ServiceObjectId = serviceObject.ServiceObjectId,
                    Title = string.IsNullOrWhiteSpace(dto.Title) ? "Nauja uzduotis" : dto.Title.Trim(),
                    SkillLevel = dto.SkillLevel?.Trim() ?? string.Empty,
                    Instructions = dto.Instructions?.Trim() ?? string.Empty,
                    StartAt = placement.StartAtUtc,
                    EndAt = placement.EndAtUtc,
                    Priority = dto.Priority,
                    Status = ScheduleTaskStatus.InProgress,
                    SourceBacklogItemId = dto.SourceBacklogItemId,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _logger.LogInformation("svc.plan.bulk.build.completed createdTaskCount={CreatedTaskCount}", createdTasks.Count);
            return new BulkTaskBuildResult { Tasks = createdTasks };
        }

        private BulkServiceObjectResolution ResolveBulkServiceObject(
            CreateTaskRequestDto dto,
            IReadOnlyDictionary<(string ObjectName, string ObjectAddress), BulkServiceObjectResolution> resolutions)
        {
            var cacheKey = (
                dto.ObjectName?.Trim() ?? string.Empty,
                dto.ObjectAddress?.Trim() ?? string.Empty);

            if (resolutions.TryGetValue(cacheKey, out var resolution))
            {
                return resolution;
            }

            throw new InvalidOperationException($"Bulk task build could not resolve service object for '{cacheKey.Item1}' at '{cacheKey.Item2}'.");
        }

        private async Task<Dictionary<(string ObjectName, string ObjectAddress), BulkServiceObjectResolution>> BuildBulkServiceObjectResolutionsAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            BulkCreateContext context,
            CancellationToken cancellationToken)
        {
            var requests = items
                .Select((dto, index) => new BulkServiceObjectRequest(
                    dto.ObjectName?.Trim() ?? string.Empty,
                    dto.ObjectAddress?.Trim() ?? string.Empty,
                    string.IsNullOrWhiteSpace(context.Placements[index].City)
                        ? ServiceLocationResolver.ParseCity(dto.ObjectAddress?.Trim() ?? string.Empty)
                        : context.Placements[index].City.Trim()))
                .Where(request => !string.IsNullOrWhiteSpace(request.ObjectName) || !string.IsNullOrWhiteSpace(request.ObjectAddress))
                .Distinct()
                .ToList();

            var resolutions = new Dictionary<(string ObjectName, string ObjectAddress), BulkServiceObjectResolution>();
            if (requests.Count == 0)
            {
                return resolutions;
            }

            var addressesByFullAddress = await _db.Addresses
                .Where(address => requests.Select(request => request.ObjectAddress).Contains(address.FullAddress))
                .ToDictionaryAsync(address => address.FullAddress, cancellationToken);

            var newAddresses = new List<AddressEntity>();
            foreach (var request in requests)
            {
                if (addressesByFullAddress.ContainsKey(request.ObjectAddress))
                {
                    continue;
                }

                var normalizedCity = string.IsNullOrWhiteSpace(request.City)
                    ? ServiceLocationResolver.ParseCity(request.ObjectAddress)
                    : request.City.Trim();
                if (string.IsNullOrWhiteSpace(normalizedCity))
                {
                    normalizedCity = "Nezinomas";
                }

                var newAddress = new AddressEntity
                {
                    City = normalizedCity,
                    Street = ServiceLocationResolver.ParseStreet(request.ObjectAddress),
                    FullAddress = request.ObjectAddress,
                    CreatedAt = DateTime.UtcNow
                };
                addressesByFullAddress[request.ObjectAddress] = newAddress;
                newAddresses.Add(newAddress);
            }

            var persistedAddressIds = addressesByFullAddress.Values
                .Where(address => address.Id > 0)
                .Select(address => address.Id)
                .Distinct()
                .ToList();
            var objectNames = requests
                .Select(request => request.ObjectName)
                .Distinct()
                .ToList();

            var existingServiceObjects = persistedAddressIds.Count == 0 || objectNames.Count == 0
                ? new List<ServiceObjectEntity>()
                : await _db.ServiceObjects
                    .Where(serviceObject => persistedAddressIds.Contains(serviceObject.AddressId))
                    .Where(serviceObject => objectNames.Contains(serviceObject.Name))
                    .ToListAsync(cancellationToken);

            var serviceObjectsByKey = existingServiceObjects.ToDictionary(
                serviceObject => (serviceObject.Name, AddressId: serviceObject.AddressId));

            var newServiceObjects = new List<ServiceObjectEntity>();
            foreach (var request in requests)
            {
                var address = addressesByFullAddress[request.ObjectAddress];
                var addressId = address.Id;
                var serviceObjectKey = (request.ObjectName, AddressId: addressId);
                if (serviceObjectsByKey.ContainsKey(serviceObjectKey))
                {
                    continue;
                }

                var newServiceObject = new ServiceObjectEntity
                {
                    Name = request.ObjectName,
                    CreatedAt = DateTime.UtcNow
                };

                if (addressId > 0)
                {
                    newServiceObject.AddressId = addressId;
                    serviceObjectsByKey[serviceObjectKey] = newServiceObject;
                }
                else
                {
                    newServiceObject.Address = address;
                }

                newServiceObjects.Add(newServiceObject);
            }

            if (newAddresses.Count > 0 || newServiceObjects.Count > 0)
            {
                if (newAddresses.Count > 0)
                {
                    _db.Addresses.AddRange(newAddresses);
                }

                if (newServiceObjects.Count > 0)
                {
                    _db.ServiceObjects.AddRange(newServiceObjects);
                }

                await _db.SaveChangesAsync(cancellationToken);
            }

            foreach (var newServiceObject in newServiceObjects.Where(serviceObject => serviceObject.AddressId > 0))
            {
                serviceObjectsByKey[(newServiceObject.Name, newServiceObject.AddressId)] = newServiceObject;
            }

            foreach (var request in requests)
            {
                var addressId = addressesByFullAddress[request.ObjectAddress].Id;
                var serviceObject = serviceObjectsByKey[(request.ObjectName, addressId)];
                resolutions[(request.ObjectName, request.ObjectAddress)] =
                    new BulkServiceObjectResolution(serviceObject.Id, addressId);
            }

            return resolutions;
        }

        private async Task SaveBulkTasksAsync(List<TaskEntity> tasks, CancellationToken cancellationToken)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            _db.Tasks.AddRange(tasks);
            await _db.SaveChangesAsync(cancellationToken);
        }

        private async Task WriteBulkCreateHistoryAsync(
            IReadOnlyList<TaskEntity> tasks,
            UserContext user,
            CancellationToken cancellationToken)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            var occurredAtUtc = DateTime.UtcNow;
            var logs = new List<TaskActionLogEntity>(tasks.Count);
            foreach (var task in tasks)
            {
                var version = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                var resultForHistory = new TaskCreateResult(TaskCreateError.None, Task: task, CurrentVersion: version);
                var (summary, outcome) = TaskHistoryMessages.ForCreate(resultForHistory, task.Title);
                var detailsJson = JsonSerializer.Serialize(new
                {
                    technicianId = task.TechnicianId,
                    startAt = task.StartAt,
                    endAt = task.EndAt
                });

                logs.Add(new TaskActionLogEntity
                {
                    TaskId = task.Id,
                    PlanId = task.PlanId,
                    ActionType = "create",
                    Outcome = outcome,
                    Summary = summary,
                    ActorUserId = user.UserId,
                    ActorRole = user.Role.ToString().ToLower(),
                    ActorName = string.Empty,
                    OccurredAtUtc = occurredAtUtc,
                    DetailsJson = detailsJson,
                    Source = "user"
                });
            }

            _db.AddRange(logs);
            await _db.SaveChangesAsync(cancellationToken);
        }

        public async Task<BulkCreateTaskResponseDto> BulkValidateTasksAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            UserContext user,
            string? planScope = null,
            CancellationToken cancellationToken = default)
        {
            var effectiveItems = await PrepareBulkItemsForModeAsync(items, planMode, cancellationToken);

            _logger.LogInformation(
                "svc.plan.bulk.validate-request itemCount={ItemCount} userId={UserId} planMode={PlanMode} planScope={PlanScope}",
                effectiveItems.Count,
                user.UserId,
                planMode ?? "rankinis",
                planScope ?? "active");

            var context = await LoadBulkCreateContextAsync(effectiveItems, cancellationToken);
            var validationOutcome = await ValidateBulkCreateRequestsAsync(effectiveItems, user, context, cancellationToken, planScope);
            return new BulkCreateTaskResponseDto
            {
                AllSucceeded = !validationOutcome.HasErrors,
                Errors = validationOutcome.Errors
            };
        }

        public async Task<BulkCreateTaskResponseDto> BulkCreateTasksAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            UserContext user,
            CancellationToken cancellationToken = default)
        {
            await using var operation = _operationInstrumentation.BeginOperation(
                "svc.plan.bulk.create",
                new Dictionary<string, object?>
                {
                    ["itemCount"] = items.Count,
                    ["planMode"] = planMode ?? "rankinis",
                    ["userId"] = user.UserId
                });

            List<CreateTaskRequestDto> effectiveItems;
            await using (var preparePhase = operation.MeasurePhase("prepare"))
            {
                effectiveItems = await PrepareBulkItemsForModeAsync(items, planMode, cancellationToken);
                preparePhase.Complete();
            }

            _logger.LogInformation(
                "svc.plan.bulk.create.start itemCount={ItemCount} userId={UserId} planMode={PlanMode}",
                effectiveItems.Count,
                user.UserId,
                planMode ?? "rankinis");

            var success = false;
            var createdTaskCount = 0;
            var errorCount = 0;
            var affectedTechDateCount = 0;
            var affectedPlanCount = 0;
            var historyRecordCount = 0;

            {
                try
                {
                    var context = await LoadBulkCreateContextAsync(effectiveItems, cancellationToken);
                    BulkValidationOutcome validationOutcome;
                    await using (var validatePhase = operation.MeasurePhase("validate"))
                    {
                        validationOutcome = await ValidateBulkCreateRequestsAsync(effectiveItems, user, context, cancellationToken);
                        validatePhase.Complete();
                    }

                    if (validationOutcome.HasErrors)
                    {
                        errorCount = validationOutcome.Errors.Count;
                        _logger.LogWarning(
                            "svc.plan.bulk.create.validation-failed itemCount={ItemCount} errorCount={ErrorCount} errors={Errors}",
                            effectiveItems.Count,
                            validationOutcome.Errors.Count,
                            string.Join(" | ", validationOutcome.Errors.Select(e => $"index={e.Index};message={e.Message}")));
                        return new BulkCreateTaskResponseDto { AllSucceeded = false, Errors = validationOutcome.Errors };
                    }

                    await using var bulkTx = await _placementValidator.BeginTransactionIfSupportedAsync(cancellationToken);

                    try
                    {
                        BulkTaskBuildResult buildResult;
                        await using (var buildPhase = operation.MeasurePhase("build"))
                        {
                            buildResult = await BuildTaskEntitiesAsync(effectiveItems, context, cancellationToken);
                            buildPhase.Complete();
                        }

                        if (buildResult.FailureResponse != null)
                        {
                            errorCount = buildResult.FailureResponse.Errors?.Count ?? 0;
                            if (bulkTx != null)
                            {
                                await bulkTx.RollbackAsync(cancellationToken);
                            }

                            _logger.LogWarning("svc.plan.bulk.create.build-failed");
                            return buildResult.FailureResponse;
                        }

                        var builtTasks = buildResult.Tasks;
                        createdTaskCount = builtTasks.Count;

                        await using (var savePhase = operation.MeasurePhase("save"))
                        {
                            await SaveBulkTasksAsync(builtTasks, cancellationToken);
                            savePhase.Complete();
                        }

                        if (bulkTx != null)
                        {
                            await bulkTx.CommitAsync(cancellationToken);
                        }

                        var bulkAffectedTechDates = builtTasks
                            .Select(t => (t.TechnicianId, t.StartAt.Date))
                            .Distinct()
                            .ToList();
                        affectedTechDateCount = bulkAffectedTechDates.Count;
                        await using (var routePhase = operation.MeasurePhase(
                            "route-evaluation",
                            new Dictionary<string, object?> { ["affectedTechDateCount"] = affectedTechDateCount }))
                        {
                            await _routeEvaluationCaller.TriggerRouteEvaluationBatchSafelyAsync(
                                bulkAffectedTechDates,
                                "create-bulk",
                                recalculateEvaluations: false,
                                cancellationToken);
                            routePhase.Complete();
                        }

                        var affectedPlanIds = builtTasks
                            .Select(t => t.PlanId)
                            .Where(id => id.HasValue)
                            .Select(id => id!.Value)
                            .Distinct()
                            .ToList();
                        affectedPlanCount = affectedPlanIds.Count;
                        await using (var recalcPhase = operation.MeasurePhase(
                            "plan-recalculation",
                            new Dictionary<string, object?> { ["affectedPlanCount"] = affectedPlanCount }))
                        {
                            foreach (var planId in affectedPlanIds)
                            {
                                await _evaluationEngine.RecalculatePlanAsync(planId, cancellationToken);
                            }
                            recalcPhase.Complete();
                        }

                        historyRecordCount = builtTasks.Count;
                        await using (var historyPhase = operation.MeasurePhase(
                            "history",
                            new Dictionary<string, object?> { ["historyRecordCount"] = historyRecordCount }))
                        {
                            await WriteBulkCreateHistoryAsync(builtTasks, user, cancellationToken);
                            historyPhase.Complete();
                        }

                        _logger.LogInformation(
                            "svc.plan.bulk.create.completed createdTaskCount={CreatedTaskCount}",
                            builtTasks.Count);

                        success = true;
                        return new BulkCreateTaskResponseDto
                        {
                            AllSucceeded = true,
                            TaskIds = builtTasks.Select(t => t.Id).ToList()
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "svc.plan.bulk.create.failed itemCount={ItemCount}", effectiveItems.Count);
                        if (bulkTx != null)
                        {
                            try { await bulkTx.RollbackAsync(cancellationToken); } catch { }
                        }
                        throw;
                    }
                }
                finally
                {
                    await operation.CompleteAsync(
                        success,
                        new Dictionary<string, object?>
                        {
                            ["createdTaskCount"] = createdTaskCount,
                            ["errorCount"] = errorCount,
                            ["affectedTechDateCount"] = affectedTechDateCount,
                            ["affectedPlanCount"] = affectedPlanCount,
                            ["historyRecordCount"] = historyRecordCount
                        });
                }
            }
        }
    }
}
