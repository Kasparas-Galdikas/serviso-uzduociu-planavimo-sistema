using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared
{
    internal class SchedulePlacementValidator
    {
        private readonly ScheduleDbContext _db;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly PlanScopeResolver _planScopeResolver;
        private readonly ILogger<SchedulePlacementValidator> _logger;

        public SchedulePlacementValidator(
            ScheduleDbContext db,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            IScheduleRepairService scheduleRepairService,
            PlanScopeResolver planScopeResolver,
            ILogger<SchedulePlacementValidator> logger)
        {
            _db = db;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _travelFeasibilityService = travelFeasibilityService;
            _scheduleRepairService = scheduleRepairService;
            _planScopeResolver = planScopeResolver;
            _logger = logger;
        }

        internal sealed record WorkingWindow(int StartMinutes, int EndMinutes);
        internal sealed record PlacementTaskSlot(long TaskId, DateTime StartAt, DateTime EndAt);

        internal sealed class PlacementValidationContext
        {
            public Dictionary<long, string> TechnicianCityById { get; } = new();
            public Dictionary<long, List<PlacementTaskSlot>> ExistingTaskSlotsByTechnician { get; } = new();
        }

        internal static string FormatDurationLabel(int totalMinutes)
        {
            var minutes = Math.Max(0, totalMinutes);
            var hours = minutes / 60;
            var remainder = minutes % 60;
            return $"{hours:00}:{remainder:00}";
        }

        internal static string FormatClockLabel(DateTime dateLocal, int minutesLocal)
        {
            var safeMinutes = Math.Max(0, minutesLocal);
            return dateLocal.Date.AddMinutes(safeMinutes).ToString("HH:mm");
        }

        internal static WorkingWindow GetWorkingWindow(long technicianId, DateTime date) =>
            new WorkingWindow(ScheduleGrid.DayStartMinutes, ScheduleGrid.DayEndMinutes);

        internal enum TimingTravelViolationKind
        {
            None,
            InvalidWindow,
            TravelInfeasible
        }

        internal readonly record struct TimingTravelValidationResult(TimingTravelViolationKind ViolationKind, string? Message, ScheduleRepairPlan? RepairPlan)
        {
            public bool IsValid => ViolationKind == TimingTravelViolationKind.None;
            public bool IsInvalidWindow => ViolationKind == TimingTravelViolationKind.InvalidWindow;
            public static TimingTravelValidationResult Success => new(TimingTravelViolationKind.None, null, null);
        }

        internal async Task<TimingTravelValidationResult> CheckTimingAndTravelAsync(
            DateTime taskDateLocal,
            int startMinutesLocal,
            int timezoneOffsetMinutes,
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            long? excludeTaskId,
            CancellationToken cancellationToken,
            IReadOnlyList<ProposedRouteStop>? simulatedStops = null,
            bool checkOverlap = false)
        {
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            if (taskDateLocal < userLocalToday)
            {
                return new TimingTravelValidationResult(TimingTravelViolationKind.InvalidWindow, "Negalima planuoti praeityje.", null);
            }

            var earliestStartMinutes = _timeService.GetEarliestCreatableStartMinutes(
                taskDateLocal,
                timezoneOffsetMinutes,
                ScheduleGrid.SlotSizeMinutes,
                ScheduleGrid.DayStartMinutes);
            if (taskDateLocal == userLocalToday && startMinutesLocal < earliestStartMinutes)
            {
                var allowedFrom = FormatClockLabel(taskDateLocal, earliestStartMinutes);
                return new TimingTravelValidationResult(TimingTravelViolationKind.InvalidWindow, $"Per ankstyvas planavimo laikas: galima nuo {allowedFrom}.", null);
            }

            var travelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                technicianId,
                startAtUtc,
                endAtUtc,
                serviceAddressId,
                startMinutesLocal,
                excludeTaskId,
                checkOverlap,
                cancellationToken,
                simulatedStops);

            if (!travelCheck.IsFeasible)
            {
                return new TimingTravelValidationResult(
                    TimingTravelViolationKind.TravelInfeasible,
                    travelCheck.Message ?? "Nepakanka laiko kelionei.",
                    null);
            }

            var inboundTravelMinutes = Math.Max(0, travelCheck.InboundTravelMinutes ?? 0);
            if (!_timeService.IsTravelStartFeasible(startAtUtc, inboundTravelMinutes))
            {
                var availableFromNowMinutes = Math.Max(0, (int)Math.Floor((startAtUtc - _timeService.GetCurrentUtcTime()).TotalMinutes));
                var requiredDuration = FormatDurationLabel(inboundTravelMinutes);
                var availableDuration = FormatDurationLabel(availableFromNowMinutes);
                return new TimingTravelValidationResult(
                    TimingTravelViolationKind.TravelInfeasible,
                    $"Nepakanka laiko kelionei: kelionei reikia {requiredDuration}, nuo dabar laisvo laiko yra {availableDuration}.",
                    null);
            }

            return TimingTravelValidationResult.Success;
        }

        internal async Task<TimingTravelValidationResult> ValidateTimingAndTravelAsync(
            DateTime taskDateLocal,
            int startMinutesLocal,
            int timezoneOffsetMinutes,
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            long? excludeTaskId,
            CancellationToken cancellationToken,
            IReadOnlyList<ProposedRouteStop>? simulatedStops = null,
            bool checkOverlap = false)
        {
            var result = await CheckTimingAndTravelAsync(
                taskDateLocal, startMinutesLocal, timezoneOffsetMinutes,
                technicianId, startAtUtc, endAtUtc, serviceAddressId, excludeTaskId, cancellationToken,
                simulatedStops, checkOverlap);

            if (result.ViolationKind == TimingTravelViolationKind.TravelInfeasible)
            {
                var repairPlan = await TryBuildPlacementRepairPlanAsync(
                    technicianId, startAtUtc, endAtUtc, serviceAddressId,
                    timezoneOffsetMinutes, excludeTaskId, cancellationToken);
                return result with { RepairPlan = repairPlan };
            }

            return result;
        }

        internal Task<ScheduleRepairPlan> BuildSwapRepairPlanAsync(long sourceTaskId, long targetTaskId, int timezoneOffsetMinutes, CancellationToken cancellationToken)
        {
            return _scheduleRepairService.BuildPlanAsync(
                new ScheduleRepairRequest
                {
                    ScenarioKind = ScheduleRepairScenarioKind.Swap,
                    SourceTaskId = sourceTaskId,
                    TargetTaskId = targetTaskId,
                    TimezoneOffsetMinutes = timezoneOffsetMinutes
                },
                cancellationToken);
        }

        internal Task<ScheduleRepairPlan> TryBuildPlacementRepairPlanAsync(
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            int timezoneOffsetMinutes,
            long? excludeTaskId,
            CancellationToken cancellationToken)
        {
            return _scheduleRepairService.BuildPlanAsync(
                new ScheduleRepairRequest
                {
                    ScenarioKind = ScheduleRepairScenarioKind.Move,
                    TechnicianId = technicianId,
                    DayUtc = startAtUtc.Date,
                    TimezoneOffsetMinutes = timezoneOffsetMinutes,
                    ExcludeTaskId = excludeTaskId,
                    ProposedPlacement = new ScheduleRepairPlacement(
                        0,
                        serviceAddressId,
                        startAtUtc,
                        endAtUtc)
                },
                cancellationToken);
        }

        internal async Task<bool> TryApplyTimingTravelRepairAsync(
            TaskEntity task,
            TimingTravelValidationResult validation,
            CancellationToken cancellationToken)
        {
            if (validation.RepairPlan == null || !validation.RepairPlan.Success)
            {
                return false;
            }

            var applyResult = await _scheduleRepairService.ApplyPlanAsync(validation.RepairPlan, task.Id, cancellationToken);
            if (!applyResult.Success)
            {
                _logger.LogWarning("repair.apply-fail taskId={TaskId} reason={Reason}", task.Id, applyResult.Message ?? string.Empty);
                return false;
            }

            return true;
        }

        internal string BuildRepairAppliedMessage(string? baseMessage, ScheduleRepairPlacement? primaryPlacement, int timezoneOffsetMinutes)
        {
            var prefix = string.IsNullOrWhiteSpace(baseMessage)
                ? "Pradinis laikas buvo pakoreguotas."
                : baseMessage.Trim();

            if (primaryPlacement == null)
            {
                return prefix;
            }

            var localStart = _timeService.ConvertUtcToLocal(primaryPlacement.StartAt, timezoneOffsetMinutes);
            var localEnd = _timeService.ConvertUtcToLocal(primaryPlacement.EndAt, timezoneOffsetMinutes);
            return $"{prefix} Pritaikytas artimiausias galimas laikas: {localStart:yyyy-MM-dd HH:mm} - {localEnd:HH:mm}.";
        }

        internal enum PlacementError
        {
            None,
            InvalidWindow,
            Overlap,
            TechnicianNotFound,
            NotEditable,
            Forbidden
        }

        internal readonly record struct PlacementValidationResult(
            PlacementError Error,
            string Message,
            DateTime StartAtUtc,
            DateTime EndAtUtc,
            string City)
        {
            public bool IsValid => Error == PlacementError.None;
            public static PlacementValidationResult Success(DateTime startAtUtc, DateTime endAtUtc, string city) =>
                new(PlacementError.None, string.Empty, startAtUtc, endAtUtc, city);
            public static PlacementValidationResult Failure(PlacementError error, string message) =>
                new(error, message, default, default, string.Empty);
        }

        internal async Task<PlacementValidationResult> ValidatePlacementForActionAsync(
            IReadOnlyCollection<long>? ignoreTaskIds,
            long targetTechnicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            PlanEntity? plan,
            ScheduleTaskStatus status,
            UserContext user,
            int timezoneOffsetMinutes,
            bool requireEndAligned,
            CancellationToken cancellationToken,
            PlacementValidationContext? context = null)
        {
            var validation = await ValidatePlacementAsync(
                ignoreTaskIds,
                targetTechnicianId,
                targetDate,
                startMinutes,
                endMinutes,
                plan,
                status,
                user,
                timezoneOffsetMinutes,
                requireEndAligned,
                cancellationToken,
                context);

            return validation.Error == PlacementError.None
                ? PlacementValidationResult.Success(validation.StartAt, validation.EndAt, validation.City)
                : PlacementValidationResult.Failure(validation.Error, validation.Message);
        }

        internal static TaskCreateResult MapPlacementErrorForCreate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                PlacementError.TechnicianNotFound => new TaskCreateResult(TaskCreateError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap => new TaskCreateResult(TaskCreateError.Overlap, validation.Message),
                _ => new TaskCreateResult(TaskCreateError.InvalidWindow, validation.Message)
            };

        internal static TaskUpdateResult MapPlacementErrorForUpdate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskUpdateResult(TaskUpdateError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskUpdateResult(TaskUpdateError.NotEditable, validation.Message),
                PlacementError.TechnicianNotFound => new TaskUpdateResult(TaskUpdateError.TechnicianNotFound, validation.Message),
                _ => new TaskUpdateResult(TaskUpdateError.InvalidWindow, validation.Message)
            };

        internal static TaskMoveResult MapPlacementErrorForMove(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskMoveResult(TaskMoveError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskMoveResult(TaskMoveError.NotEditable, "Neturite teisių perkelti šios užduoties."),
                PlacementError.TechnicianNotFound => new TaskMoveResult(TaskMoveError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap => new TaskMoveResult(TaskMoveError.Overlap, validation.Message),
                _ => new TaskMoveResult(TaskMoveError.InvalidWindow, validation.Message)
            };

        internal static TaskTimeUpdateResult MapPlacementErrorForTimeUpdate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, validation.Message),
                PlacementError.Overlap => new TaskTimeUpdateResult(TaskTimeUpdateError.Overlap, validation.Message),
                _ => new TaskTimeUpdateResult(TaskTimeUpdateError.InvalidWindow, validation.Message)
            };

        internal static TaskSwapResult MapPlacementErrorForSwap(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.Forbidden => new TaskSwapResult(TaskSwapError.NotEditable, validation.Message),
                PlacementError.Overlap => new TaskSwapResult(TaskSwapError.Overlap, validation.Message),
                PlacementError.NotEditable => new TaskSwapResult(TaskSwapError.NotEditable, validation.Message),
                _ => new TaskSwapResult(TaskSwapError.InvalidSwap, validation.Message,
                    InvalidSwapReason: InvalidSwapReason.WorkWindow)
            };

        internal async Task<(PlacementError Error, string Message, DateTime StartAt, DateTime EndAt, string City)> ValidatePlacementAsync(
            IReadOnlyCollection<long>? ignoreTaskIds,
            long targetTechnicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            PlanEntity? plan,
            ScheduleTaskStatus status,
            UserContext user,
            int timezoneOffsetMinutes,
            bool requireEndAligned,
            CancellationToken cancellationToken,
            PlacementValidationContext? context = null)
        {
            if (startMinutes < 0 || startMinutes >= 24 * 60 || endMinutes <= 0 || endMinutes > 24 * 60 || endMinutes <= startMinutes)
            {
                return (PlacementError.InvalidWindow, "Netinkamas laiko langas.", default, default, string.Empty);
            }

            if (startMinutes % ScheduleGrid.SlotSizeMinutes != 0)
            {
                return (PlacementError.InvalidWindow, "Laikas turi atitikti grafiko žingsni.", default, default, string.Empty);
            }

            if (requireEndAligned && (endMinutes % ScheduleGrid.SlotSizeMinutes != 0))
            {
                return (PlacementError.InvalidWindow, "Pabaigos laikas turi atitikti grafiko žingsnį.", default, default, string.Empty);
            }

            var window = GetWorkingWindow(targetTechnicianId, targetDate);
            if (startMinutes < window.StartMinutes)
            {
                return (PlacementError.InvalidWindow, "Per ankstyvas laikas.", default, default, string.Empty);
            }

            if (endMinutes > window.EndMinutes)
            {
                return (PlacementError.InvalidWindow, "Užduotis netelpa i darbo laiką.", default, default, string.Empty);
            }

            // Convert local time to UTC for storage
            var startAt = _timeService.CreateUtcFromDateAndMinutes(targetDate, startMinutes, timezoneOffsetMinutes);
            var endAt = _timeService.CreateUtcFromDateAndMinutes(targetDate, endMinutes, timezoneOffsetMinutes);

            string city;
            if (context != null && context.TechnicianCityById.TryGetValue(targetTechnicianId, out var cachedCity))
            {
                city = cachedCity;
            }
            else
            {
                var targetTech = await _db.Technicians
                    .AsNoTracking()
                    .Include(t => t.WorkplaceAddress)
                    .Select(t => new { t.Id, City = t.WorkplaceAddress.City })
                    .FirstOrDefaultAsync(t => t.Id == targetTechnicianId, cancellationToken);

                if (targetTech == null)
                {
                    return (PlacementError.TechnicianNotFound, "Technikas nerastas.", default, default, string.Empty);
                }

                city = targetTech.City ?? string.Empty;
                context?.TechnicianCityById.TryAdd(targetTechnicianId, city);
            }

            var permissionProbe = new TaskEntity
            {
                Id = ignoreTaskIds?.FirstOrDefault() ?? 0,
                PlanId = plan?.Id,
                Plan = plan,
                TechnicianId = targetTechnicianId,
                StartAt = startAt,
                EndAt = endAt,
                Status = status
            };

            if (!_timeService.IsTaskStillEditable(permissionProbe.EndAt))
            {
                return (PlacementError.InvalidWindow, "Negalima planuoti praeityje.", default, default, string.Empty);
            }

            if (permissionProbe.Status != ScheduleTaskStatus.InProgress)
            {
                return (PlacementError.NotEditable, "Užduoties nebegalima redaguoti, nes pakeistas jos statusas.", default, default, string.Empty);
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(permissionProbe, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                return (PlacementError.Forbidden, "Neturite teisių atlikti šio veiksmo.", default, default, string.Empty);
            }

            bool hasOverlap;
            if (context != null && context.ExistingTaskSlotsByTechnician.TryGetValue(targetTechnicianId, out var slots))
            {
                hasOverlap = slots.Any(t =>
                    (ignoreTaskIds == null || !ignoreTaskIds.Contains(t.TaskId)) &&
                    t.StartAt < endAt &&
                    t.EndAt > startAt);
            }
            else
            {
                var constraintScope = await _planScopeResolver.ResolveConstraintScopeAsync(startAt, endAt, cancellationToken);
                hasOverlap = await _planScopeResolver.ApplyConstraintScope(
                        _db.Tasks.AsNoTracking(),
                        constraintScope)
                    .Where(t => ignoreTaskIds == null || !ignoreTaskIds.Contains(t.Id))
                    .Where(t => t.TechnicianId == targetTechnicianId)
                    .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                    .Where(t => t.StartAt < endAt && t.EndAt > startAt)
                    .AnyAsync(cancellationToken);
            }

            if (hasOverlap)
            {
                return (PlacementError.Overlap, "Laiko langas užimtas (persidengia užduotys).", default, default, string.Empty);
            }

            return (PlacementError.None, string.Empty, startAt, endAt, city);
        }

        internal async Task<PlacementValidationContext> BuildPlacementValidationContextAsync(
            IReadOnlyCollection<long> technicianIds,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            CancellationToken cancellationToken)
        {
            var context = new PlacementValidationContext();
            if (rangeEndUtc <= rangeStartUtc)
            {
                return context;
            }

            var normalizedTechIds = technicianIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (normalizedTechIds.Count == 0)
            {
                return context;
            }

            var technicians = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.WorkplaceAddress)
                .Where(t => normalizedTechIds.Contains(t.Id))
                .Select(t => new { t.Id, City = t.WorkplaceAddress.City })
                .ToListAsync(cancellationToken);

            foreach (var technician in technicians)
            {
                context.TechnicianCityById[technician.Id] = technician.City ?? string.Empty;
            }

            var constraintScope = await _planScopeResolver.ResolveConstraintScopeAsync(rangeStartUtc, rangeEndUtc, cancellationToken);

            var existingTasks = await _planScopeResolver.ApplyConstraintScope(
                    _db.Tasks.AsNoTracking(),
                    constraintScope)
                .Where(t => normalizedTechIds.Contains(t.TechnicianId))
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < rangeEndUtc && t.EndAt > rangeStartUtc)
                .Select(t => new { t.Id, t.TechnicianId, t.StartAt, t.EndAt })
                .ToListAsync(cancellationToken);

            foreach (var task in existingTasks)
            {
                if (!context.ExistingTaskSlotsByTechnician.TryGetValue(task.TechnicianId, out var slots))
                {
                    slots = new List<PlacementTaskSlot>();
                    context.ExistingTaskSlotsByTechnician[task.TechnicianId] = slots;
                }

                slots.Add(new PlacementTaskSlot(task.Id, task.StartAt, task.EndAt));
            }

            return context;
        }

        internal async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync(CancellationToken cancellationToken)
        {
            if (string.Equals(_db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await _db.Database.BeginTransactionAsync(cancellationToken);
        }
    }
}
