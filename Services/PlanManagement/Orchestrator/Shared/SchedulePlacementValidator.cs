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
using ServiceTaskPlanningSystem.Services.Unavailability;

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
        private readonly TechnicianUnavailabilityReader _technicianAvailabilityPolicy;
        private readonly ILogger<SchedulePlacementValidator> _logger;

        public SchedulePlacementValidator(
            ScheduleDbContext db,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            IScheduleRepairService scheduleRepairService,
            PlanScopeResolver planScopeResolver,
            TechnicianUnavailabilityReader technicianAvailabilityPolicy,
            ILogger<SchedulePlacementValidator> logger)
        {
            _db = db;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _travelFeasibilityService = travelFeasibilityService;
            _scheduleRepairService = scheduleRepairService;
            _planScopeResolver = planScopeResolver;
            _technicianAvailabilityPolicy = technicianAvailabilityPolicy;
            _logger = logger;
        }

        internal sealed record WorkingWindow(int StartMinutes, int EndMinutes);
        internal sealed record PlacementTaskSlot(long TaskId, DateTime StartAt, DateTime EndAt);
        internal sealed class PlacementValidationContext
        {
            public Dictionary<long, string> TechnicianCityById { get; } = new();
            public Dictionary<long, List<PlacementTaskSlot>> ExistingTaskSlotsByTechnician { get; } = new();
            public Dictionary<long, List<TechnicianBlockedInterval>> ExistingBlockedSlotsByTechnician { get; } = new();
        }

        internal static string FormatDurationLabel(int totalMinutes)
        {
            var minutes = Math.Max(0, totalMinutes);
            var hours = minutes / 60;
            var remainder = minutes % 60;
            return hours > 0 ? $"{hours} val. {remainder} min." : $"{remainder} min.";
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

        internal enum TravelRepairCause
        {
            Generic,
            BlockedBeforeStart,
            BlockedAfterEnd
        }

        internal readonly record struct TravelRepairContext(
            TravelRepairCause Cause,
            TechnicianBlockedInterval? BlockedInterval,
            int? InboundTravelMinutes,
            int? OutboundTravelMinutes)
        {
            public static TravelRepairContext Generic(int? inboundTravelMinutes, int? outboundTravelMinutes) =>
                new(TravelRepairCause.Generic, null, inboundTravelMinutes, outboundTravelMinutes);
        }

        internal readonly record struct TimingTravelValidationResult(
            TimingTravelViolationKind ViolationKind,
            string? Message,
            ScheduleRepairPlan? RepairPlan,
            TravelRepairContext RepairContext)
        {
            public bool IsValid => ViolationKind == TimingTravelViolationKind.None;
            public bool IsInvalidWindow => ViolationKind == TimingTravelViolationKind.InvalidWindow;
            public static TimingTravelValidationResult Success => new(
                TimingTravelViolationKind.None,
                null,
                null,
                TravelRepairContext.Generic(null, null));
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
                return new TimingTravelValidationResult(
                    TimingTravelViolationKind.InvalidWindow,
                    "Negalima planuoti praeityje.",
                    null,
                    TravelRepairContext.Generic(null, null));
            }

            var earliestStartMinutes = _timeService.GetEarliestCreatableStartMinutes(
                taskDateLocal,
                timezoneOffsetMinutes,
                ScheduleGrid.SlotSizeMinutes,
                ScheduleGrid.DayStartMinutes);
            if (taskDateLocal == userLocalToday && startMinutesLocal < earliestStartMinutes)
            {
                var allowedFrom = FormatClockLabel(taskDateLocal, earliestStartMinutes);
                return new TimingTravelValidationResult(
                    TimingTravelViolationKind.InvalidWindow,
                    $"Per ankstyvas planavimo laikas: galima nuo {allowedFrom}.",
                    null,
                    TravelRepairContext.Generic(null, null));
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
                var repairContext = await ResolveTravelRepairContextAsync(
                    technicianId,
                    startAtUtc,
                    endAtUtc,
                    travelCheck.InboundTravelMinutes,
                    travelCheck.OutboundTravelMinutes,
                    cancellationToken);

                return new TimingTravelValidationResult(
                    TimingTravelViolationKind.TravelInfeasible,
                    travelCheck.Message ?? "Nepakanka laiko kelionei.",
                    null,
                    repairContext);
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
                    null,
                    TravelRepairContext.Generic(inboundTravelMinutes, travelCheck.OutboundTravelMinutes));
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
                var repairPlan = await BuildTimingTravelRepairPlanAsync(
                    technicianId,
                    startAtUtc,
                    endAtUtc,
                    serviceAddressId,
                    timezoneOffsetMinutes,
                    excludeTaskId,
                    result.RepairContext,
                    cancellationToken);
                return result with { RepairPlan = repairPlan };
            }

            return result;
        }

        internal async Task<ScheduleRepairPlan> BuildTimingTravelRepairPlanAsync(
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            int timezoneOffsetMinutes,
            long? excludeTaskId,
            TravelRepairContext repairContext,
            CancellationToken cancellationToken)
        {
            if (repairContext.Cause == TravelRepairCause.BlockedBeforeStart &&
                repairContext.BlockedInterval != null &&
                repairContext.InboundTravelMinutes.GetValueOrDefault() > 0)
            {
                var inboundTravelMinutes = repairContext.InboundTravelMinutes ?? 0;
                var candidateStartAtUtc = AlignUtcToSlotCeiling(
                    repairContext.BlockedInterval.EndAtUtc.AddMinutes(inboundTravelMinutes),
                    timezoneOffsetMinutes);
                var candidateEndAtUtc = candidateStartAtUtc + (endAtUtc - startAtUtc);

                var blockedAwarePlan = await TryBuildPlacementRepairPlanAsync(
                    technicianId,
                    candidateStartAtUtc,
                    candidateEndAtUtc,
                    serviceAddressId,
                    timezoneOffsetMinutes,
                    excludeTaskId,
                    cancellationToken,
                    allowBlockedIntervalShift: true,
                    simulatedBlockedIntervalStart: repairContext.BlockedInterval.StartAtUtc,
                    simulatedBlockedIntervalEnd: repairContext.BlockedInterval.EndAtUtc);

                if (blockedAwarePlan.Success)
                {
                    return blockedAwarePlan;
                }
            }

            if (repairContext.Cause == TravelRepairCause.BlockedAfterEnd &&
                repairContext.BlockedInterval != null)
            {
                var blockedAwarePlan = await TryBuildPlacementRepairPlanAsync(
                    technicianId,
                    startAtUtc,
                    endAtUtc,
                    serviceAddressId,
                    timezoneOffsetMinutes,
                    excludeTaskId,
                    cancellationToken,
                    allowBlockedIntervalShift: true,
                    simulatedBlockedIntervalStart: repairContext.BlockedInterval.StartAtUtc,
                    simulatedBlockedIntervalEnd: repairContext.BlockedInterval.EndAtUtc);

                if (blockedAwarePlan.Success)
                {
                    return blockedAwarePlan;
                }
            }

            return await TryBuildPlacementRepairPlanAsync(
                technicianId,
                startAtUtc,
                endAtUtc,
                serviceAddressId,
                timezoneOffsetMinutes,
                excludeTaskId,
                cancellationToken);
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
            CancellationToken cancellationToken,
            bool allowBlockedIntervalShift = false,
            DateTime? simulatedBlockedIntervalStart = null,
            DateTime? simulatedBlockedIntervalEnd = null)
        {
            return _scheduleRepairService.BuildPlanAsync(
                new ScheduleRepairRequest
                {
                    ScenarioKind = ScheduleRepairScenarioKind.Move,
                    TechnicianId = technicianId,
                    DayUtc = startAtUtc.Date,
                    TimezoneOffsetMinutes = timezoneOffsetMinutes,
                    ExcludeTaskId = excludeTaskId,
                    AllowBlockedIntervalShift = allowBlockedIntervalShift,
                    SimulatedBlockedIntervalStart = simulatedBlockedIntervalStart,
                    SimulatedBlockedIntervalEnd = simulatedBlockedIntervalEnd,
                    ProposedPlacement = new ScheduleRepairPlacement(
                        0,
                        serviceAddressId,
                        startAtUtc,
                        endAtUtc)
                },
                cancellationToken);
        }

        internal async Task<ScheduleRepairPlan> TryBuildUnavailabilityRepairPlanAsync(
            long technicianId,
            DateTime taskStartAtUtc,
            DateTime taskEndAtUtc,
            DateTime effectiveStartAtUtc,
            DateTime blockingPeriodEndUtc,
            long serviceAddressId,
            int timezoneOffsetMinutes,
            long? excludeTaskId,
            CancellationToken cancellationToken,
            DateTime? blockingPeriodStartUtc = null)
        {
            var duration = taskEndAtUtc - taskStartAtUtc;
            if (duration <= TimeSpan.Zero)
            {
                return ScheduleRepairPlan.Fail("Netinkama uzduoties trukme.");
            }

            var overlapShift = blockingPeriodEndUtc > effectiveStartAtUtc
                ? blockingPeriodEndUtc - effectiveStartAtUtc
                : TimeSpan.Zero;

            var shiftedLocalStart = _timeService.ConvertUtcToLocal(taskStartAtUtc.Add(overlapShift), timezoneOffsetMinutes);
            var alignedStartMinutes = _timeService.CeilingToSlot((int)shiftedLocalStart.TimeOfDay.TotalMinutes, ScheduleGrid.SlotSizeMinutes);
            var candidateStartAtUtc = _timeService.CreateUtcFromDateAndMinutes(
                shiftedLocalStart.Date,
                alignedStartMinutes,
                timezoneOffsetMinutes);
            var candidateEndAtUtc = candidateStartAtUtc.Add(duration);

            var workingWindow = GetWorkingWindow(technicianId, shiftedLocalStart.Date);
            var candidateEndLocal = _timeService.ConvertUtcToLocal(candidateEndAtUtc, timezoneOffsetMinutes);
            if ((int)candidateEndLocal.TimeOfDay.TotalMinutes > workingWindow.EndMinutes)
            {
                return ScheduleRepairPlan.Fail("Po techniko neaktyvumo nerasta tinkamo laiko tame paciame darbo lange.");
            }

            return await TryBuildPlacementRepairPlanAsync(
                technicianId,
                candidateStartAtUtc,
                candidateEndAtUtc,
                serviceAddressId,
                timezoneOffsetMinutes,
                excludeTaskId,
                cancellationToken,
                allowBlockedIntervalShift: true,
                simulatedBlockedIntervalStart: blockingPeriodStartUtc,
                simulatedBlockedIntervalEnd: blockingPeriodEndUtc);
        }

        internal async Task<ScheduleRepairPlan?> TryBuildUnavailabilityRepairForSlotAsync(
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken)
        {
            var blockedPeriods = await _technicianAvailabilityPolicy.GetBlockedIntervalsAsync(
                technicianId,
                startAtUtc.AddDays(-1),
                endAtUtc.AddDays(1),
                cancellationToken);

            var blockingPeriod = blockedPeriods
                .OrderBy(p => p.StartAtUtc)
                .FirstOrDefault(p => _technicianAvailabilityPolicy.TouchesOrOverlaps(p, startAtUtc, endAtUtc));

            if (blockingPeriod == null)
                return null;
            
            _logger.LogInformation(
                "[REPAIR-FIND-BLOCK] techId={TechId} requestedTime={StartAt:yyyy-MM-dd HH:mm:ss}-{EndAt:yyyy-MM-dd HH:mm:ss} blockingPeriod={BlockStart:yyyy-MM-dd HH:mm:ss}-{BlockEnd:yyyy-MM-dd HH:mm:ss}",
                technicianId, startAtUtc, endAtUtc, blockingPeriod.StartAtUtc, blockingPeriod.EndAtUtc);

            return await TryBuildUnavailabilityRepairPlanAsync(
                technicianId,
                startAtUtc,
                endAtUtc,
                startAtUtc,
                blockingPeriod.EndAtUtc,
                serviceAddressId,
                timezoneOffsetMinutes,
                null,
                cancellationToken,
                blockingPeriodStartUtc: blockingPeriod.StartAtUtc);
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

        internal async Task<TravelRepairContext> ResolveTravelRepairContextAsync(
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            int? inboundTravelMinutes,
            int? outboundTravelMinutes,
            CancellationToken cancellationToken)
        {
            var blockedPeriods = await _technicianAvailabilityPolicy.GetBlockedIntervalsAsync(
                technicianId,
                startAtUtc.Date,
                startAtUtc.Date.AddDays(1),
                cancellationToken);

            if (inboundTravelMinutes.GetValueOrDefault() > 0)
            {
                var inboundWindowStartUtc = startAtUtc.AddMinutes(-(inboundTravelMinutes ?? 0));
                var blockedBeforeStart = blockedPeriods
                    .Where(period => period.EndAtUtc <= startAtUtc && period.EndAtUtc > inboundWindowStartUtc)
                    .OrderByDescending(period => period.EndAtUtc)
                    .FirstOrDefault();

                if (blockedBeforeStart != null)
                {
                    return new TravelRepairContext(
                        TravelRepairCause.BlockedBeforeStart,
                        blockedBeforeStart,
                        inboundTravelMinutes,
                        outboundTravelMinutes);
                }
            }

            if (outboundTravelMinutes.GetValueOrDefault() > 0)
            {
                var outboundWindowEndUtc = endAtUtc.AddMinutes(outboundTravelMinutes ?? 0);
                var blockedAfterEnd = blockedPeriods
                    .Where(period => period.StartAtUtc >= endAtUtc && period.StartAtUtc < outboundWindowEndUtc)
                    .OrderBy(period => period.StartAtUtc)
                    .FirstOrDefault();

                if (blockedAfterEnd != null)
                {
                    return new TravelRepairContext(
                        TravelRepairCause.BlockedAfterEnd,
                        blockedAfterEnd,
                        inboundTravelMinutes,
                        outboundTravelMinutes);
                }
            }

            return TravelRepairContext.Generic(inboundTravelMinutes, outboundTravelMinutes);
        }

        internal async Task<bool> TryApplyPlacementRepairAsync(
            TaskEntity task,
            PlacementValidationResult validation,
            CancellationToken cancellationToken)
        {
            if (!validation.HasRepairPlan)
            {
                return false;
            }

            var applyResult = await _scheduleRepairService.ApplyPlanAsync(validation.RepairPlan!, task.Id, cancellationToken);
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
            BlockedByUnavailability,
            TechnicianNotFound,
            NotEditable,
            Forbidden
        }

        internal readonly record struct PlacementValidationResult(
            PlacementError Error,
            string Message,
            DateTime StartAtUtc,
            DateTime EndAtUtc,
            string City,
            ScheduleRepairPlan? RepairPlan,
            bool IsBlockedByUnavailability)
        {
            public bool IsValid => Error == PlacementError.None;
            public bool HasRepairPlan => RepairPlan != null && RepairPlan.Success;
            public static PlacementValidationResult Success(DateTime startAtUtc, DateTime endAtUtc, string city) =>
                new(PlacementError.None, string.Empty, startAtUtc, endAtUtc, city, null, false);
            public static PlacementValidationResult Failure(
                PlacementError error,
                string message,
                DateTime startAtUtc = default,
                DateTime endAtUtc = default,
                string city = "",
                ScheduleRepairPlan? repairPlan = null,
                bool isBlockedByUnavailability = false) =>
                new(error, message, startAtUtc, endAtUtc, city, repairPlan, isBlockedByUnavailability);
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

            if (validation.Error == PlacementError.None)
                return PlacementValidationResult.Success(validation.StartAt, validation.EndAt, validation.City);

            if (validation.Error == PlacementError.BlockedByUnavailability)
                return PlacementValidationResult.Failure(validation.Error, validation.Message, validation.StartAt, validation.EndAt, validation.City, isBlockedByUnavailability: true);

            return PlacementValidationResult.Failure(validation.Error, validation.Message);
        }

        internal static TaskCreateResult MapPlacementErrorForCreate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                PlacementError.TechnicianNotFound => new TaskCreateResult(TaskCreateError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap or PlacementError.BlockedByUnavailability => new TaskCreateResult(TaskCreateError.Overlap, validation.Message),
                _ => new TaskCreateResult(TaskCreateError.InvalidWindow, validation.Message)
            };

        internal static TaskUpdateResult MapPlacementErrorForUpdate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskUpdateResult(TaskUpdateError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskUpdateResult(TaskUpdateError.NotEditable, validation.Message),
                PlacementError.TechnicianNotFound => new TaskUpdateResult(TaskUpdateError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap or PlacementError.BlockedByUnavailability => new TaskUpdateResult(TaskUpdateError.Overlap, validation.Message),
                _ => new TaskUpdateResult(TaskUpdateError.InvalidWindow, validation.Message)
            };

        internal static TaskMoveResult MapPlacementErrorForMove(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskMoveResult(TaskMoveError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskMoveResult(TaskMoveError.NotEditable, "Neturite teisių perkelti šios užduoties."),
                PlacementError.TechnicianNotFound => new TaskMoveResult(TaskMoveError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap or PlacementError.BlockedByUnavailability => new TaskMoveResult(TaskMoveError.Overlap, validation.Message),
                _ => new TaskMoveResult(TaskMoveError.InvalidWindow, validation.Message)
            };

        internal static TaskTimeUpdateResult MapPlacementErrorForTimeUpdate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, validation.Message),
                PlacementError.Forbidden => new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, validation.Message),
                PlacementError.Overlap or PlacementError.BlockedByUnavailability => new TaskTimeUpdateResult(TaskTimeUpdateError.Overlap, validation.Message),
                _ => new TaskTimeUpdateResult(TaskTimeUpdateError.InvalidWindow, validation.Message)
            };

        internal static TaskSwapResult MapPlacementErrorForSwap(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.Forbidden => new TaskSwapResult(TaskSwapError.NotEditable, validation.Message),
                PlacementError.Overlap or PlacementError.BlockedByUnavailability => new TaskSwapResult(TaskSwapError.Overlap, validation.Message),
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
                var overlapQuery = _db.Tasks
                    .AsNoTracking()
                    .Where(t => ignoreTaskIds == null || !ignoreTaskIds.Contains(t.Id))
                    .Where(t => t.TechnicianId == targetTechnicianId)
                    .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                    .Where(t => t.StartAt < endAt && t.EndAt > startAt);

                if (plan?.Id != null)
                {
                    var targetPlanId = plan.Id;
                    overlapQuery = overlapQuery.Where(t => !t.PlanId.HasValue || t.PlanId == targetPlanId);
                }
                else
                {
                    overlapQuery = overlapQuery.Where(t => !t.PlanId.HasValue);
                }

                hasOverlap = await overlapQuery.AnyAsync(cancellationToken);
            }

            if (hasOverlap)
            {
                return (PlacementError.Overlap, "Laiko langas užimtas (persidengia užduotys).", default, default, string.Empty);
            }

            bool hasBlockedOverlap;
            if (context != null && context.ExistingBlockedSlotsByTechnician.TryGetValue(targetTechnicianId, out var blockedSlots))
            {
                hasBlockedOverlap = _technicianAvailabilityPolicy.HasBlockedOverlap(blockedSlots, startAt, endAt);
            }
            else
            {
                var blockedPeriods = await _technicianAvailabilityPolicy.GetBlockedIntervalsAsync(
                    targetTechnicianId,
                    startAt.Date.AddDays(-1),
                    endAt.AddDays(1),
                    cancellationToken);

                hasBlockedOverlap = _technicianAvailabilityPolicy.HasBlockedOverlap(blockedPeriods, startAt, endAt);
            }

            if (hasBlockedOverlap)
            {
                return (PlacementError.BlockedByUnavailability, "Pasirinktas laikas pažymėtas kaip techniko neaktyvumas.", startAt, endAt, city);
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

            var blockedSlotsByTechnician = await _technicianAvailabilityPolicy.GetBlockedIntervalsByTechnicianAsync(
                normalizedTechIds,
                rangeStartUtc,
                rangeEndUtc,
                cancellationToken);

            foreach (var blockedSlot in blockedSlotsByTechnician)
            {
                context.ExistingBlockedSlotsByTechnician[blockedSlot.Key] = blockedSlot.Value;
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

        private int AlignToSlot(int minutes)
        {
            var slot = ScheduleGrid.SlotSizeMinutes;
            if (minutes <= 0)
            {
                return 0;
            }

            return ((minutes + slot - 1) / slot) * slot;
        }

        private DateTime AlignUtcToSlotCeiling(DateTime utc, int timezoneOffsetMinutes)
        {
            var local = _timeService.ConvertUtcToLocal(utc, timezoneOffsetMinutes);
            var alignedMinutes = AlignToSlot((int)local.TimeOfDay.TotalMinutes);
            return _timeService.CreateUtcFromDateAndMinutes(local.Date, alignedMinutes, timezoneOffsetMinutes);
        }

    }
}
