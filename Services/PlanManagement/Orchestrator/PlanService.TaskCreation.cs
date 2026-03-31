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
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;

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
        private readonly PlanServiceAssignmentMatching _assignmentMatching;
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
            PlanServiceAssignmentMatching assignmentMatching,
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
            _assignmentMatching = assignmentMatching;
            _logger = logger;
        }

        private async Task<List<CreateTaskRequestDto>> PrepareBulkItemsForModeAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            CancellationToken cancellationToken)
        {
            var effectiveItems = items as List<CreateTaskRequestDto> ?? new List<CreateTaskRequestDto>(items);

            if (!"automatinis".Equals(planMode, StringComparison.OrdinalIgnoreCase))
            {
                return effectiveItems;
            }

            _logger.LogInformation("svc.plan.bulk.mode.automatinis.prepare itemCount={ItemCount}", effectiveItems.Count);

            foreach (var item in effectiveItems)
            {
                item.TechnicianId = 0;
            }

            await _assignmentMatching.ApplyAutomaticAssignmentsAsync(effectiveItems, cancellationToken);
            return effectiveItems;
        }

        public async Task<TaskCreateResult> CreateTaskAsync(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, long? sourceBacklogItemId = null, CancellationToken cancellationToken = default)
        {
            var taskDate = targetDate.Date;

            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);

            var draftPlan = await _db.Plans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);
            var activePlan = await _db.Plans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);
            var supersededPlans = await _db.Plans
                .AsNoTracking()
                .Where(p => p.Status == PlanStatus.Superseded)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(cancellationToken);

            PlanEntity? targetPlan = null;

            if (draftPlan != null && taskDate >= draftPlan.PeriodStart.Date && taskDate <= draftPlan.PeriodEnd.Date)
            {
                targetPlan = draftPlan;
                _logger.LogInformation("svc.plan.create-task.target plan=Draft planId={PlanId}", draftPlan.Id);
            }
            else if (activePlan != null && taskDate >= activePlan.PeriodStart.Date && taskDate <= activePlan.PeriodEnd.Date)
            {
                targetPlan = activePlan;
                _logger.LogInformation("svc.plan.create-task.target plan=Active planId={PlanId}", activePlan.Id);
            }
            else
            {
                var matchingSuperseded = supersededPlans
                    .FirstOrDefault(p => taskDate >= p.PeriodStart.Date && taskDate <= p.PeriodEnd.Date);
                if (matchingSuperseded != null)
                {
                    targetPlan = matchingSuperseded;
                    _logger.LogInformation("svc.plan.create-task.target plan=Superseded planId={PlanId}", matchingSuperseded.Id);
                }
                else
                {
                    targetPlan = await _planLifecycle.GetOrExtendActivePlanAsync(activePlan, taskDate, userLocalToday, cancellationToken);
                    _logger.LogInformation("svc.plan.create-task.target plan=Extended/New planId={PlanId}", targetPlan.Id);
                }
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
                cancellationToken: cancellationToken);
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

        private sealed class BulkCreateContext
        {
            public required PlanEntity? DraftPlan { get; init; }
            public required PlanEntity? ActivePlan { get; set; }
            public required List<PlanEntity> SupersededPlans { get; init; }
            public required DateTime[] ParsedDates { get; init; }
            public required PlanEntity?[] TargetPlans { get; init; }
            public required SchedulePlacementValidator.PlacementValidationResult[] Placements { get; init; }
            public required long?[] KnownAddressIds { get; init; }
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

        private readonly record struct BulkServiceObjectResolution(long ServiceObjectId, long AddressId);

        private async Task<BulkCreateContext> LoadBulkCreateContextAsync(int itemCount, CancellationToken cancellationToken)
        {
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
                KnownAddressIds = new long?[itemCount]
            };
        }

        private async Task<BulkValidationOutcome> ValidateBulkCreateRequestsAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            UserContext user,
            BulkCreateContext context,
            CancellationToken cancellationToken)
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

                PlanEntity? targetPlan = null;
                if (context.DraftPlan != null && targetDate >= context.DraftPlan.PeriodStart.Date && targetDate <= context.DraftPlan.PeriodEnd.Date)
                    targetPlan = context.DraftPlan;
                else if (context.ActivePlan != null && targetDate >= context.ActivePlan.PeriodStart.Date && targetDate <= context.ActivePlan.PeriodEnd.Date)
                    targetPlan = context.ActivePlan;
                else
                    targetPlan = context.SupersededPlans.FirstOrDefault(p => targetDate >= p.PeriodStart.Date && targetDate <= p.PeriodEnd.Date);
                context.TargetPlans[i] = targetPlan;

                var placement = await _placementValidator.ValidatePlacementForActionAsync(
                    ignoreTaskIds: null,
                    targetTechnicianId: dto.TechnicianId,
                    targetDate: targetDate,
                    startMinutes: dto.StartMinutes,
                    endMinutes: dto.EndMinutes,
                    plan: targetPlan,
                    status: ScheduleTaskStatus.InProgress,
                    user: user,
                    timezoneOffsetMinutes: dto.TimezoneOffsetMinutes,
                    requireEndAligned: true,
                    cancellationToken: cancellationToken);

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

                var addressId = await _locationResolver.TryLookupServiceAddressIdAsync(dto.ObjectName, dto.ObjectAddress, cancellationToken);
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
                    cancellationToken: cancellationToken);

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
                context.SimulatedBatchStops.Add((dto.TechnicianId, addressId.Value, placement.StartAtUtc, placement.EndAtUtc));
            }

            _logger.LogInformation(
                "svc.plan.bulk.validate.completed itemCount={ItemCount} errorCount={ErrorCount}",
                items.Count,
                outcome.Errors.Count);

            return outcome;
        }

        private async Task<BulkTaskBuildResult> BuildTaskEntitiesAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            BulkCreateContext context,
            CancellationToken cancellationToken)
        {
            var createdTasks = new List<TaskEntity>(items.Count);
            var userLocalToday = _timeService.GetUserLocalToday(items[0].TimezoneOffsetMinutes);
            var serviceObjectCache = new Dictionary<(string ObjectName, string ObjectAddress), BulkServiceObjectResolution>();

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
                var city = placement.City;
                var serviceObject = await ResolveBulkServiceObjectAsync(dto, city, serviceObjectCache, cancellationToken);
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
                        cancellationToken: cancellationToken);

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

        private async Task<BulkServiceObjectResolution> ResolveBulkServiceObjectAsync(
            CreateTaskRequestDto dto,
            string city,
            Dictionary<(string ObjectName, string ObjectAddress), BulkServiceObjectResolution> cache,
            CancellationToken cancellationToken)
        {
            var objectName = dto.ObjectName?.Trim() ?? string.Empty;
            var objectAddress = dto.ObjectAddress?.Trim() ?? string.Empty;
            var cacheKey = (objectName, objectAddress);

            if (cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var serviceObjectId = await _locationResolver.FindOrCreateServiceObjectAsync(objectName, objectAddress, city, cancellationToken);
            var addressId = await _db.ServiceObjects
                .Where(o => o.Id == serviceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);

            var resolved = new BulkServiceObjectResolution(serviceObjectId, addressId);
            cache[cacheKey] = resolved;
            return resolved;
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

        public async Task<BulkCreateTaskResponseDto> BulkValidateTasksAsync(
            IReadOnlyList<CreateTaskRequestDto> items,
            string? planMode,
            UserContext user,
            CancellationToken cancellationToken = default)
        {
            var effectiveItems = await PrepareBulkItemsForModeAsync(items, planMode, cancellationToken);

            _logger.LogInformation(
                "svc.plan.bulk.validate-request itemCount={ItemCount} userId={UserId} planMode={PlanMode}",
                effectiveItems.Count,
                user.UserId,
                planMode ?? "rankinis");

            var context = await LoadBulkCreateContextAsync(effectiveItems.Count, cancellationToken);
            var validationOutcome = await ValidateBulkCreateRequestsAsync(effectiveItems, user, context, cancellationToken);
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
            var effectiveItems = await PrepareBulkItemsForModeAsync(items, planMode, cancellationToken);

            _logger.LogInformation(
                "svc.plan.bulk.create.start itemCount={ItemCount} userId={UserId} planMode={PlanMode}",
                effectiveItems.Count,
                user.UserId,
                planMode ?? "rankinis");

            {
                var context = await LoadBulkCreateContextAsync(effectiveItems.Count, cancellationToken);
                var validationOutcome = await ValidateBulkCreateRequestsAsync(effectiveItems, user, context, cancellationToken);
                if (validationOutcome.HasErrors)
                {
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
                    var buildResult = await BuildTaskEntitiesAsync(effectiveItems, context, cancellationToken);
                    if (buildResult.FailureResponse != null)
                    {
                        if (bulkTx != null)
                        {
                            await bulkTx.RollbackAsync(cancellationToken);
                        }

                        _logger.LogWarning("svc.plan.bulk.create.build-failed");
                        return buildResult.FailureResponse;
                    }

                    var builtTasks = buildResult.Tasks;
                    await SaveBulkTasksAsync(builtTasks, cancellationToken);

                    if (bulkTx != null)
                    {
                        await bulkTx.CommitAsync(cancellationToken);
                    }

                    var bulkAffectedTechDates = builtTasks
                        .Select(t => (t.TechnicianId, t.StartAt.Date))
                        .Distinct();
                    foreach (var (techId, date) in bulkAffectedTechDates)
                        await _routeEvaluationCaller.TriggerRouteEvaluationSafelyAsync(techId, date, "create-bulk", cancellationToken);

                    foreach (var planId in builtTasks.Select(t => t.PlanId).Where(id => id.HasValue).Select(id => id!.Value).Distinct())
                        await _evaluationEngine.RecalculatePlanAsync(planId, cancellationToken);

                    foreach (var task in builtTasks)
                    {
                        var version = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                        var resultForHistory = new TaskCreateResult(TaskCreateError.None, Task: task, CurrentVersion: version);
                        var (msg, outcome) = TaskHistoryMessages.ForCreate(resultForHistory, task.Title);
                        await _historyWriter.RecordAsync(task.Id, task.PlanId, "create", outcome, msg,
                            user.UserId, user.Role.ToString().ToLower(),
                            new { technicianId = task.TechnicianId, startAt = task.StartAt, endAt = task.EndAt },
                            cancellationToken: cancellationToken);
                    }

                    _logger.LogInformation(
                        "svc.plan.bulk.create.completed createdTaskCount={CreatedTaskCount}",
                        builtTasks.Count);

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
        }
    }
}
