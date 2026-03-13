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
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public class PlanService : IPlanService
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly IRouteEvaluationService _routeEvaluationService;
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;
        private readonly IScheduleRepairService _scheduleRepairService;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly ILogger<PlanService> _logger;

        private sealed class NoopScheduleRepairService : IScheduleRepairService
        {
            public Task<ScheduleRepairPlan> BuildPlanAsync(ScheduleRepairRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ScheduleRepairPlan.Fail("Planavimo taisymo servisas nesukonfiguruotas."));
            }

            public Task<ScheduleRepairApplyResult> ApplyPlanAsync(ScheduleRepairPlan plan, long? primaryTaskId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ScheduleRepairApplyResult.Fail("Planavimo taisymo servisas nesukonfiguruotas."));
            }
        }

        public PlanService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IPlanningCalendar planningCalendar,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IRouteEvaluationService routeEvaluationService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            ITravelTimeResolver travelResolver,
            ILogger<PlanService> logger)
            : this(
                db,
                evaluationEngine,
                planningCalendar,
                taskPermissionService,
                timeService,
                routeEvaluationService,
                travelFeasibilityService,
                new NoopScheduleRepairService(),
                travelResolver,
                logger)
        {
        }

        public PlanService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IPlanningCalendar planningCalendar,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IRouteEvaluationService routeEvaluationService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            IScheduleRepairService scheduleRepairService,
            ITravelTimeResolver travelResolver,
            ILogger<PlanService> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _planningCalendar = planningCalendar;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _routeEvaluationService = routeEvaluationService;
            _travelFeasibilityService = travelFeasibilityService;
            _scheduleRepairService = scheduleRepairService;
            _travelResolver = travelResolver;
            _logger = logger;
        }
        private sealed record WorkingWindow(int StartMinutes, int EndMinutes);

        private static WorkingWindow GetWorkingWindow(long technicianId, DateTime date) =>
            new WorkingWindow(ScheduleGrid.DayStartMinutes, ScheduleGrid.DayEndMinutes);

        private enum TimingTravelViolationKind
        {
            None,
            InvalidWindow,
            TravelInfeasible
        }

        private readonly record struct TimingTravelValidationResult(TimingTravelViolationKind ViolationKind, string? Message, ScheduleRepairPlan? RepairPlan)
        {
            public bool IsValid => ViolationKind == TimingTravelViolationKind.None;
            public bool IsInvalidWindow => ViolationKind == TimingTravelViolationKind.InvalidWindow;
            public static TimingTravelValidationResult Success => new(TimingTravelViolationKind.None, null, null);
        }

        private async Task<TimingTravelValidationResult> CheckTimingAndTravelAsync(
            DateTime taskDateLocal,
            int startMinutesLocal,
            int timezoneOffsetMinutes,
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            long? excludeTaskId,
            CancellationToken cancellationToken)
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
                return new TimingTravelValidationResult(TimingTravelViolationKind.InvalidWindow, "Negalima planuoti ankstesniame nei artimiausias galimas 30 min. laiko langas.", null);
            }

            var travelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                technicianId,
                startAtUtc,
                endAtUtc,
                serviceAddressId,
                startMinutesLocal,
                excludeTaskId,
                checkOverlap: false,
                cancellationToken);

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
                return new TimingTravelValidationResult(TimingTravelViolationKind.TravelInfeasible, "Nepakanka laiko kelionei nuo dabartinio laiko.", null);
            }

            return TimingTravelValidationResult.Success;
        }

        private async Task<TimingTravelValidationResult> ValidateTimingAndTravelAsync(
            DateTime taskDateLocal,
            int startMinutesLocal,
            int timezoneOffsetMinutes,
            long technicianId,
            DateTime startAtUtc,
            DateTime endAtUtc,
            long serviceAddressId,
            long? excludeTaskId,
            CancellationToken cancellationToken)
        {
            var result = await CheckTimingAndTravelAsync(
                taskDateLocal, startMinutesLocal, timezoneOffsetMinutes,
                technicianId, startAtUtc, endAtUtc, serviceAddressId, excludeTaskId, cancellationToken);

            if (result.ViolationKind == TimingTravelViolationKind.TravelInfeasible)
            {
                var repairPlan = await TryBuildPlacementRepairPlanAsync(
                    technicianId, startAtUtc, endAtUtc, serviceAddressId,
                    timezoneOffsetMinutes, excludeTaskId, cancellationToken);
                return result with { RepairPlan = repairPlan };
            }

            return result;
        }

        private Task<ScheduleRepairPlan> BuildSwapRepairPlanAsync(long sourceTaskId, long targetTaskId, int timezoneOffsetMinutes, CancellationToken cancellationToken)
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

        private Task<ScheduleRepairPlan> TryBuildPlacementRepairPlanAsync(
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

        private async Task<bool> TryApplyTimingTravelRepairAsync(
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

        private string BuildRepairAppliedMessage(string? baseMessage, ScheduleRepairPlacement? primaryPlacement, int timezoneOffsetMinutes)
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

        private async Task<long> FindOrCreateServiceObjectAsync(string? objectName, string? objectAddress, string city, CancellationToken cancellationToken = default)
        {
            var normalizedName = string.IsNullOrWhiteSpace(objectName) ? null : objectName.Trim();
            var normalizedAddress = string.IsNullOrWhiteSpace(objectAddress) ? null : objectAddress.Trim();

            // If both name and address are empty, reject.
            if (string.IsNullOrEmpty(normalizedName) && string.IsNullOrEmpty(normalizedAddress))
            {
                throw new InvalidOperationException("Object name and address are required.");
            }

            // Use default values if one is missing
            if (string.IsNullOrEmpty(normalizedName))
            {
                normalizedName = "Objektas";
            }
            if (string.IsNullOrEmpty(normalizedAddress))
            {
                normalizedAddress = "Nežinomas adresas";
            }
            var normalizedCity = string.IsNullOrWhiteSpace(city) ? ParseCity(normalizedAddress) : city.Trim();
            if (string.IsNullOrWhiteSpace(normalizedCity))
            {
                normalizedCity = "Nezinomas";
            }

            var street = ParseStreet(normalizedAddress);
            // Prefer exact full-address reuse globally to avoid duplicate address rows
            // when city inference differs between operations.
            var existingAddress = await _db.Addresses
                .FirstOrDefaultAsync(a => a.FullAddress == normalizedAddress, cancellationToken);

            if (existingAddress == null)
            {
                existingAddress = await _db.Addresses
                    .FirstOrDefaultAsync(a =>
                        a.City == normalizedCity &&
                        a.Street == street,
                        cancellationToken);
            }

            if (existingAddress == null)
            {
                existingAddress = new AddressEntity
                {
                    City = normalizedCity,
                    Street = street,
                    FullAddress = normalizedAddress,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Addresses.Add(existingAddress);
            await _db.SaveChangesAsync(cancellationToken);
            }

            // Try to find existing service object by name and address
            var existingObject = await _db.ServiceObjects
                .FirstOrDefaultAsync(o => o.Name == normalizedName && o.AddressId == existingAddress.Id, cancellationToken);

            if (existingObject != null)
            {
                return existingObject.Id;
            }

            // Create new service object
            var newObject = new ServiceObjectEntity
            {
                Name = normalizedName,
                AddressId = existingAddress.Id,
                CreatedAt = DateTime.UtcNow
            };

            _db.ServiceObjects.Add(newObject);
            await _db.SaveChangesAsync(cancellationToken);

            return newObject.Id;
        }

        private static string ParseStreet(string fullAddress)
        {
            if (string.IsNullOrWhiteSpace(fullAddress))
            {
                return string.Empty;
            }

            var firstPart = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0];
            if (string.IsNullOrWhiteSpace(firstPart))
            {
                return fullAddress.Trim();
            }

            return firstPart.Trim();
        }

        private static string ParseCity(string fullAddress)
        {
            if (string.IsNullOrWhiteSpace(fullAddress))
            {
                return string.Empty;
            }

            var parts = fullAddress.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[^1] : string.Empty;
        }

        private enum PlacementError
        {
            None,
            InvalidWindow,
            Overlap,
            TechnicianNotFound,
            NotEditable
        }

        private readonly record struct PlacementValidationResult(
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

        private async Task<PlacementValidationResult> ValidatePlacementForActionAsync(
            long? ignoreTaskId,
            long targetTechnicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            PlanEntity? plan,
            ScheduleTaskStatus status,
            UserContext user,
            int timezoneOffsetMinutes,
            bool requireEndAligned,
            CancellationToken cancellationToken)
        {
            var validation = await ValidatePlacementAsync(
                ignoreTaskId,
                targetTechnicianId,
                targetDate,
                startMinutes,
                endMinutes,
                plan,
                status,
                user,
                timezoneOffsetMinutes,
                requireEndAligned,
                cancellationToken);

            return validation.Error == PlacementError.None
                ? PlacementValidationResult.Success(validation.StartAt, validation.EndAt, validation.City)
                : PlacementValidationResult.Failure(validation.Error, validation.Message);
        }

        private static TaskCreateResult MapPlacementErrorForCreate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                PlacementError.TechnicianNotFound => new TaskCreateResult(TaskCreateError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap => new TaskCreateResult(TaskCreateError.Overlap, validation.Message),
                _ => new TaskCreateResult(TaskCreateError.InvalidWindow, validation.Message)
            };

        private static TaskUpdateResult MapPlacementErrorForUpdate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskUpdateResult(TaskUpdateError.NotEditable, validation.Message),
                PlacementError.TechnicianNotFound => new TaskUpdateResult(TaskUpdateError.TechnicianNotFound, validation.Message),
                _ => new TaskUpdateResult(TaskUpdateError.InvalidWindow, validation.Message)
            };

        private static TaskMoveResult MapPlacementErrorForMove(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskMoveResult(TaskMoveError.NotEditable, "Neturite teisiu perkelti sios uzduoties."),
                PlacementError.TechnicianNotFound => new TaskMoveResult(TaskMoveError.TechnicianNotFound, validation.Message),
                PlacementError.Overlap => new TaskMoveResult(TaskMoveError.Overlap, validation.Message),
                _ => new TaskMoveResult(TaskMoveError.InvalidWindow, validation.Message)
            };

        private static TaskTimeUpdateResult MapPlacementErrorForTimeUpdate(PlacementValidationResult validation) =>
            validation.Error switch
            {
                PlacementError.NotEditable => new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, validation.Message),
                PlacementError.Overlap => new TaskTimeUpdateResult(TaskTimeUpdateError.Overlap, validation.Message),
                _ => new TaskTimeUpdateResult(TaskTimeUpdateError.InvalidWindow, validation.Message)
            };
        private async Task<(PlacementError Error, string Message, DateTime StartAt, DateTime EndAt, string City)> ValidatePlacementAsync(
            long? ignoreTaskId,
            long targetTechnicianId,
            DateTime targetDate,
            int startMinutes,
            int endMinutes,
            PlanEntity? plan,
            ScheduleTaskStatus status,
            UserContext user,
            int timezoneOffsetMinutes,
            bool requireEndAligned,
            CancellationToken cancellationToken)
        {
            if (startMinutes < 0 || startMinutes >= 24 * 60 || endMinutes <= 0 || endMinutes > 24 * 60 || endMinutes <= startMinutes)
            {
                return (PlacementError.InvalidWindow, "Netinkamas laiko langas.", default, default, string.Empty);
            }

            if (startMinutes % ScheduleGrid.SlotSizeMinutes != 0)
            {
                return (PlacementError.InvalidWindow, "Laikas turi atitikti grafiko zingsni.", default, default, string.Empty);
            }

            if (requireEndAligned && (endMinutes % ScheduleGrid.SlotSizeMinutes != 0))
            {
                return (PlacementError.InvalidWindow, "Pabaigos laikas turi atitikti grafiko zingsni.", default, default, string.Empty);
            }

            var window = GetWorkingWindow(targetTechnicianId, targetDate);
            if (startMinutes < window.StartMinutes)
            {
                return (PlacementError.InvalidWindow, "Per ankstyvas laikas.", default, default, string.Empty);
            }

            if (endMinutes > window.EndMinutes)
            {
                return (PlacementError.InvalidWindow, "Uzduotis netelpa i darbo laika.", default, default, string.Empty);
            }

            // Convert local time to UTC for storage
            var startAt = _timeService.CreateUtcFromDateAndMinutes(targetDate, startMinutes, timezoneOffsetMinutes);
            var endAt = _timeService.CreateUtcFromDateAndMinutes(targetDate, endMinutes, timezoneOffsetMinutes);

            var targetTech = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.WorkplaceAddress)
                .Select(t => new { t.Id, City = t.WorkplaceAddress.City })
                .FirstOrDefaultAsync(t => t.Id == targetTechnicianId, cancellationToken);

            if (targetTech == null)
            {
                return (PlacementError.TechnicianNotFound, "Technikas nerastas.", default, default, string.Empty);
            }

            var permissionProbe = new TaskEntity
            {
                Id = ignoreTaskId ?? 0,
                PlanId = plan?.Id,
                Plan = plan,
                TechnicianId = targetTech.Id,
                StartAt = startAt,
                EndAt = endAt,
                Status = status
            };

            var canEdit = await _taskPermissionService.CanEditTaskAsync(permissionProbe, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                return (PlacementError.NotEditable, "Neturite teisiu atlikti sio veiksmo.", default, default, string.Empty);
            }

            var hasOverlap = await _db.Tasks
                .AsNoTracking()
                .Where(t => ignoreTaskId == null || t.Id != ignoreTaskId.Value)
                .Where(t => t.TechnicianId == targetTech.Id)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < endAt && t.EndAt > startAt)
                .AnyAsync(cancellationToken);

            if (hasOverlap)
            {
                return (PlacementError.Overlap, "Laiko langas uzimtas (persidengia uzduotys).", default, default, string.Empty);
            }

            return (PlacementError.None, string.Empty, startAt, endAt, targetTech.City ?? string.Empty);
        }

        public Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            _db.Plans
               .AsNoTracking()
               .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

        public Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default) =>
            _db.Plans
               .AsNoTracking()
               .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

        public async Task<LoadSnapshotDto> GetWorkloadSnapshotAsync(long technicianId, DateTime dayStartUtc, CancellationToken cancellationToken = default)
        {
            var technician = await _db.Technicians
                .AsNoTracking()
                .Include(t => t.WorkplaceAddress)
                .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);

            if (technician == null)
            {
                return new LoadSnapshotDto();
            }

            var dayEnd = dayStartUtc.AddDays(1);
            var weekStart = _timeService.GetWeekStart(dayStartUtc);
            var weekEnd = weekStart.AddDays(7);

            var weekTasks = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .Where(t => t.TechnicianId == technicianId &&
                            t.StartAt >= weekStart &&
                            t.StartAt < weekEnd)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            var dayTasks = weekTasks
                .Where(t => t.StartAt >= dayStartUtc && t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var travelMinutesByTaskId = await BuildTravelMinutesByTaskIdAsync(technician, weekTasks, cancellationToken);
            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
            return MapToLoadSnapshotDto(technician, snapshot);
        }

        private static LoadSnapshotDto MapToLoadSnapshotDto(TechnicianEntity technician, WorkloadSnapshot snapshot)
        {
            var context = snapshot.Context;
            return new LoadSnapshotDto
            {
                LoadState = context.ComputeLoadState().ToString(),
                HasConflict = context.HasConflict,
                IsDailyLimitExceeded = context.IsDailyLimitExceeded,
                IsWeeklyLimitExceeded = context.IsWeeklyLimitExceeded,
                DailyHours = Math.Round(snapshot.DailyHours, 2),
                WeeklyHours = Math.Round(snapshot.WeeklyHours, 2),
                DailyLimit = (double)technician.DailyLimit,
                WeeklyLimit = (double)technician.WeeklyLimit,
                DailyUtilization = Math.Round(context.DailyUtilization, 2),
                WeeklyUtilization = Math.Round(context.WeeklyUtilization, 2),
                TravelTimeHours = Math.Round(context.TravelTimeHours, 2)
            };
        }

        public async Task<TaskDetailResult?> GetTaskDetailAsync(long taskId, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .AsNoTracking()
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                    .ThenInclude(o => o.Address)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return null;
            }

            var version = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);

            return new TaskDetailResult(task, version);
        }

        public async Task<uint> GetTaskVersionAsync(long taskId, CancellationToken cancellationToken = default)
        {
            return await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => EF.Property<uint>(t, "xmin"))
                .FirstAsync(cancellationToken);
        }

        public async Task<bool> TechnicianExistsAsync(long technicianId, CancellationToken cancellationToken = default)
        {
            return await _db.Technicians
                .AsNoTracking()
                .AnyAsync(t => t.Id == technicianId, cancellationToken);
        }

        public async Task<bool> HasTaskOverlapAsync(long excludeTaskId, long technicianId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        {
            return await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Id != excludeTaskId)
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < endUtc && t.EndAt > startUtc)
                .AnyAsync(cancellationToken);
        }

        private async Task<bool> HasTaskOverlapExcludingAsync(
            IReadOnlyCollection<long> excludeTaskIds,
            long technicianId,
            DateTime startUtc,
            DateTime endUtc,
            CancellationToken cancellationToken = default)
        {
            return await _db.Tasks
                .AsNoTracking()
                .Where(t => !excludeTaskIds.Contains(t.Id))
                .Where(t => t.TechnicianId == technicianId)
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => t.StartAt < endUtc && t.EndAt > startUtc)
                .AnyAsync(cancellationToken);
        }

        public async Task<TechnicianUpdateResult> TechnicianUpdateTaskAsync(
            long taskId,
            ScheduleTaskStatus status,
            string? comment,
            uint expectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            // Validate status - technician can only set Completed or Conflict
            if (status != ScheduleTaskStatus.Completed && status != ScheduleTaskStatus.Conflict)
            {
                return new TechnicianUpdateResult(TechnicianUpdateError.InvalidStatus, "Galite nustatyti tik 'Atlikta' arba 'Problema' statusus.");
            }

            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return new TechnicianUpdateResult(TechnicianUpdateError.TaskNotFound, "Uzduotis nerasta.");
            }

            // Single authorization check - TaskPermissionService is the sole authority
            // (handles ownership, time editability, and role-based rules)
            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                // Check if it's an ownership issue for technicians (for specific error message)
                if (user.IsTechnician && (!user.TechnicianId.HasValue || task.TechnicianId != user.TechnicianId.Value))
                {
                    return new TechnicianUpdateResult(TechnicianUpdateError.NotOwner, "Galite redaguoti tik savo uzduotis.");
                }
                return new TechnicianUpdateResult(TechnicianUpdateError.NotEditable, "Si uzduotis nebegali buti redaguojama.");
            }

            // Check version
            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (currentVersion != expectedVersion)
            {
                return new TechnicianUpdateResult(TechnicianUpdateError.Stale, "Uzduotis buvo pakeista kito vartotojo. Prasome atnaujinti puslapi.", CurrentVersion: currentVersion);
            }

            // Update task
            task.Status = status;
            task.TechnicianComment = comment ?? string.Empty;

            try
            {await _db.SaveChangesAsync(cancellationToken);

                // Recalculate evaluations when status changes
                if (task.PlanId.HasValue)
                {
                    await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                }

                var newVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                return new TechnicianUpdateResult(TechnicianUpdateError.None, Task: task, CurrentVersion: newVersion);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TechnicianUpdateResult(TechnicianUpdateError.Stale, "Uzduotis buvo pakeista kito vartotojo. Prasome atnaujinti puslapi.");
            }
        }

        public async Task<PlanEntity> CreateDraftFromActiveAsync(DateTime? requestedStart = null, DateTime? requestedEnd = null, int? requestedWeeks = null, CancellationToken cancellationToken = default)
        {
            var existingDraft = await GetDraftPlanAsync(cancellationToken);
            if (existingDraft != null)
            {
                throw new InvalidOperationException("Draft already exists. Cannot create another draft.");
            }

            var activePlan = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            if (activePlan == null)
            {
                throw new InvalidOperationException("No active plan found to clone.");
            }

            var technicians = await _db.Technicians.ToListAsync(cancellationToken);

            var fallbackWeeks = activePlan.Weeks > 0 ? activePlan.Weeks : 4;
            var window = await _planningCalendar.BuildWindowAsync(requestedStart, requestedEnd, requestedWeeks, fallbackWeeks, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            _logger.LogInformation("svc.plan.draft.create requestedStart={RequestedStart} requestedEnd={RequestedEnd} start={StartDate} end={EndDate} weeks={Weeks} activePlanId={ActivePlanId}", requestedStart?.Date, requestedEnd?.Date, window.Start, window.End, window.Weeks, activePlan.Id);

            var draftPlan = new PlanEntity
            {
                PeriodStart = window.Start,
                PeriodEnd = window.End, // will be set to actual task span after seeding
                Weeks = window.Weeks,
                Status = PlanStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = activePlan.CreatedBy
            };

            _db.Plans.Add(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);

            var generationEnd = window.End;
            var seededTasks = await GenerateTasksFromActivePlanAsync(activePlan, window.Start, generationEnd, draftPlan.Id, cancellationToken);
            if (seededTasks.Any())
            {
                _db.Tasks.AddRange(seededTasks);
            await _db.SaveChangesAsync(cancellationToken);

                // Align PeriodStart and PeriodEnd to actual task span (true inclusive horizon)
                var actualStart = seededTasks.Min(t => t.StartAt.Date);
                var actualEnd = seededTasks.Max(t => t.EndAt.Date);

                _logger.LogInformation("svc.plan.draft.task-span windowStart={WindowStart} windowEnd={WindowEnd} actualStart={ActualStart} actualEnd={ActualEnd}",
                    window.Start, window.End, actualStart, actualEnd);

                draftPlan.PeriodStart = actualStart;
                draftPlan.PeriodEnd = actualEnd;
                var totalDays = (draftPlan.PeriodEnd - draftPlan.PeriodStart).Days + 1;
                draftPlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                _db.Plans.Update(draftPlan);
            await _db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                _logger.LogWarning("svc.plan.draft.seed.empty planId={PlanId} start={Start} end={End}", draftPlan.Id, window.Start, window.End);
            }

            await transaction.CommitAsync(cancellationToken);
            await _evaluationEngine.RecalculatePlanAsync(draftPlan.Id, cancellationToken);
            _logger.LogInformation("svc.plan.draft.created planId={PlanId} tasksSeeded={TaskCount}", draftPlan.Id, seededTasks.Count());
            return draftPlan;
        }

        private async Task<PlanEntity> GetOrExtendActivePlanAsync(PlanEntity? activePlan, DateTime taskDate, DateTime userLocalToday, CancellationToken cancellationToken)
        {
            if (activePlan != null)
            {
                // Extend Active plan to cover the task date
                if (taskDate > activePlan.PeriodEnd.Date)
                {
                    activePlan.PeriodEnd = taskDate;
                    var totalDays = (activePlan.PeriodEnd - activePlan.PeriodStart).Days + 1;
                    activePlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                    _db.Plans.Update(activePlan);
            await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("svc.plan.extend-active planId={PlanId} newEnd={NewEnd}", activePlan.Id, activePlan.PeriodEnd);
                    return activePlan;
                }
                else if (taskDate < activePlan.PeriodStart.Date)
                {
                    activePlan.PeriodStart = taskDate;
                    var totalDays = (activePlan.PeriodEnd - activePlan.PeriodStart).Days + 1;
                    activePlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                    _db.Plans.Update(activePlan);
            await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("svc.plan.extend-active planId={PlanId} newStart={NewStart}", activePlan.Id, activePlan.PeriodStart);
                    return activePlan;
                }
                return activePlan;
            }
            else
            {
                // Create new Active plan
                var newActivePlan = new PlanEntity
                {
                    PeriodStart = userLocalToday,
                    PeriodEnd = taskDate > userLocalToday ? taskDate : userLocalToday.AddDays(27), // Default 4 weeks
                    Weeks = 4,
                    Status = PlanStatus.Active,
                    CreatedAt = DateTime.UtcNow
                };
                var totalDays = (newActivePlan.PeriodEnd - newActivePlan.PeriodStart).Days + 1;
                newActivePlan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));
                _db.Plans.Add(newActivePlan);
            await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("svc.plan.create-active planId={PlanId} start={Start} end={End}", newActivePlan.Id, newActivePlan.PeriodStart, newActivePlan.PeriodEnd);
                return newActivePlan;
            }
        }

        private async Task AdjustPlanSpanAsync(PlanEntity plan, CancellationToken cancellationToken)
        {
            var span = await _db.Tasks
                .Where(t => t.PlanId == plan.Id)
                .Select(t => new { Start = t.StartAt.Date, End = t.EndAt.Date })
                .ToListAsync(cancellationToken);

            if (!span.Any())
            {
                return;
            }

            var min = span.Min(t => t.Start);
            var max = span.Max(t => t.End);
            var totalDays = (max - min).Days + 1;

            plan.PeriodStart = min;
            plan.PeriodEnd = max;
            plan.Weeks = Math.Max(1, (int)Math.Ceiling(totalDays / 7.0));

            _db.Plans.Update(plan);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("svc.plan.span.adjust planId={PlanId} start={Start} end={End} weeks={Weeks}", plan.Id, plan.PeriodStart, plan.PeriodEnd, plan.Weeks);
        }

                public async Task<TaskCreateResult> CreateTaskAsync(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
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
                    targetPlan = await GetOrExtendActivePlanAsync(activePlan, taskDate, userLocalToday, cancellationToken);
                    _logger.LogInformation("svc.plan.create-task.target plan=Extended/New planId={PlanId}", targetPlan.Id);
                }
            }

            var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Nauja uzduotis" : title.Trim();
            var normalizedSkill = string.IsNullOrWhiteSpace(skillLevel) ? string.Empty : skillLevel.Trim();
            var normalizedInstructions = string.IsNullOrWhiteSpace(instructions) ? string.Empty : instructions.Trim();

            var placementValidation = await ValidatePlacementForActionAsync(
                ignoreTaskId: null,
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
                return MapPlacementErrorForCreate(placementValidation);
            }

            var serviceObjectId = await FindOrCreateServiceObjectAsync(objectName, objectAddress, placementValidation.City, cancellationToken);
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
                CreatedAt = DateTime.UtcNow
            };

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(cancellationToken);

            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "create", cancellationToken);
            var timingTravelValidation = await ValidateTimingAndTravelAsync(
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
                if (!timingTravelValidation.IsInvalidWindow && await TryApplyTimingTravelRepairAsync(task, timingTravelValidation, cancellationToken))
                {
                    await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "create-repair", cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    var repairedVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                    await _evaluationEngine.RecalculatePlanAsync(targetPlan.Id, cancellationToken);
                    return new TaskCreateResult(
                        TaskCreateError.None,
                        BuildRepairAppliedMessage(timingTravelValidation.Message, timingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task,
                        repairedVersion);
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new TaskCreateResult(
                    timingTravelValidation.IsInvalidWindow ? TaskCreateError.InvalidWindow : TaskCreateError.TravelInfeasible,
                    timingTravelValidation.Message);
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var version = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            await _evaluationEngine.RecalculatePlanAsync(targetPlan.Id, cancellationToken);
            return new TaskCreateResult(TaskCreateError.None, Task: task, CurrentVersion: version);
        }

        public async Task<TaskUpdateResult> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, long technicianId, UserContext user, int timezoneOffsetMinutes, string? objectName, string? objectAddress, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return new TaskUpdateResult(TaskUpdateError.NotFound, "Uzduotis nerasta.");
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                return new TaskUpdateResult(TaskUpdateError.NotEditable, "Neturite teisiu redaguoti sios uzduoties.");
            }

            if (technicianId != task.TechnicianId)
            {
                var targetTech = await _db.Technicians
                    .AsNoTracking()
                    .Select(t => new { t.Id })
                    .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);

                if (targetTech == null)
                {
                    return new TaskUpdateResult(TaskUpdateError.TechnicianNotFound, "Technikas nerastas.");
                }

                task.TechnicianId = targetTech.Id;
            }

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);

            applyChanges(task);

            if (!string.IsNullOrWhiteSpace(objectName) || !string.IsNullOrWhiteSpace(objectAddress))
            {
                var city = await _db.Technicians
                    .Where(t => t.Id == task.TechnicianId)
                    .Select(t => t.WorkplaceAddress.City)
                    .FirstAsync(cancellationToken);
                var serviceObjectId = await FindOrCreateServiceObjectAsync(objectName, objectAddress, city, cancellationToken);
                task.ServiceObjectId = serviceObjectId;
            }
            var localPlacementStartAt = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var localPlacementEndAt = _timeService.ConvertUtcToLocal(task.EndAt, timezoneOffsetMinutes);
            var updatePlacementValidation = await ValidatePlacementForActionAsync(
                ignoreTaskId: taskId,
                targetTechnicianId: task.TechnicianId,
                targetDate: localPlacementStartAt.Date,
                startMinutes: (int)localPlacementStartAt.TimeOfDay.TotalMinutes,
                endMinutes: (int)localPlacementEndAt.TimeOfDay.TotalMinutes,
                plan: task.Plan,
                status: task.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (!updatePlacementValidation.IsValid)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return MapPlacementErrorForUpdate(updatePlacementValidation);
            }

            task.StartAt = updatePlacementValidation.StartAtUtc;
            task.EndAt = updatePlacementValidation.EndAtUtc;

            await _db.SaveChangesAsync(cancellationToken);
            await TriggerTaskRouteImpactSafelyAsync(task.Id, "update", cancellationToken);
            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "update", cancellationToken);

            var finalAddressId = await _db.ServiceObjects
                .AsNoTracking()
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);
            var localStartAt = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffsetMinutes);
            var startMinutesLocal = (int)localStartAt.TimeOfDay.TotalMinutes;
            var timingTravelValidation = await ValidateTimingAndTravelAsync(
                taskDateLocal: localStartAt.Date,
                startMinutesLocal: startMinutesLocal,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: task.TechnicianId,
                startAtUtc: task.StartAt,
                endAtUtc: task.EndAt,
                serviceAddressId: finalAddressId,
                excludeTaskId: taskId,
                cancellationToken: cancellationToken);
            if (!timingTravelValidation.IsValid)
            {
                if (!timingTravelValidation.IsInvalidWindow && await TryApplyTimingTravelRepairAsync(task, timingTravelValidation, cancellationToken))
                {
                    await TriggerTaskRouteImpactSafelyAsync(task.Id, "update-repair", cancellationToken);
                    await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "update-repair", cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    if (task.PlanId.HasValue)
                    {
                        await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                        await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                    }

                    return new TaskUpdateResult(
                        TaskUpdateError.None,
                        BuildRepairAppliedMessage(timingTravelValidation.Message, timingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task);
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                return new TaskUpdateResult(timingTravelValidation.IsInvalidWindow ? TaskUpdateError.InvalidWindow : TaskUpdateError.TravelInfeasible, timingTravelValidation.Message);
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (task.PlanId.HasValue)
            {
                await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            return new TaskUpdateResult(TaskUpdateError.None, Task: task);
        }
                public async Task<TaskMoveResult> MoveTaskAsync(long taskId, long targetTechnicianId, DateTime targetDate, int targetSlotStartMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return new TaskMoveResult(TaskMoveError.NotFound, $"Task {taskId} was not found.");
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                return new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            var duration = task.EndAt - task.StartAt;
            var durationMinutesRaw = duration.TotalMinutes;
            if (durationMinutesRaw <= 0 || Math.Abs(durationMinutesRaw - Math.Round(durationMinutesRaw)) > 0.0001)
            {
                return new TaskMoveResult(TaskMoveError.InvalidWindow, "Netinkama uzduoties trukme.");
            }

            var durationMinutes = (int)Math.Round(durationMinutesRaw);
            var endMinutes = targetSlotStartMinutes + durationMinutes;
            var placementValidation = await ValidatePlacementForActionAsync(
                ignoreTaskId: taskId,
                targetTechnicianId: targetTechnicianId,
                targetDate: targetDate,
                startMinutes: targetSlotStartMinutes,
                endMinutes: endMinutes,
                plan: task.Plan,
                status: task.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: false,
                cancellationToken: cancellationToken);

            if (!placementValidation.IsValid)
            {
                return MapPlacementErrorForMove(placementValidation);
            }

            var oldTechnicianId = task.TechnicianId;
            var oldDay = task.StartAt.Date;

            task.TechnicianId = targetTechnicianId;
            task.StartAt = placementValidation.StartAtUtc;
            task.EndAt = placementValidation.EndAtUtc;

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            var moveServiceAddressId = await _db.ServiceObjects
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);
            var moveTimingTravelValidation = await ValidateTimingAndTravelAsync(
                taskDateLocal: targetDate.Date,
                startMinutesLocal: targetSlotStartMinutes,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: targetTechnicianId,
                startAtUtc: placementValidation.StartAtUtc,
                endAtUtc: placementValidation.EndAtUtc,
                serviceAddressId: moveServiceAddressId,
                excludeTaskId: taskId,
                cancellationToken: cancellationToken);
            if (!moveTimingTravelValidation.IsValid)
            {
                if (!moveTimingTravelValidation.IsInvalidWindow && await TryApplyTimingTravelRepairAsync(task, moveTimingTravelValidation, cancellationToken))
                {
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                    await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "move-repair", cancellationToken);

                    if (task.PlanId.HasValue)
                    {
                        await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                        await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                    }

                    if (targetTechnicianId != oldTechnicianId || targetDate.Date != oldDay)
                    {
                        await TriggerRouteEvaluationSafelyAsync(oldTechnicianId, oldDay, "move-old-day", cancellationToken);
                    }

                    return new TaskMoveResult(
                        TaskMoveError.None,
                        BuildRepairAppliedMessage(moveTimingTravelValidation.Message, moveTimingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task,
                        _db.Entry(task).Property<uint>("xmin").CurrentValue);
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new TaskMoveResult(
                    moveTimingTravelValidation.IsInvalidWindow ? TaskMoveError.InvalidWindow : TaskMoveError.TravelInfeasible,
                    moveTimingTravelValidation.Message);
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "move", cancellationToken);

            if (task.PlanId.HasValue)
            {
                await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            if (targetTechnicianId != oldTechnicianId || targetDate.Date != oldDay)
            {
                await TriggerRouteEvaluationSafelyAsync(oldTechnicianId, oldDay, "move-old-day", cancellationToken);
            }

            return new TaskMoveResult(TaskMoveError.None, Task: task, CurrentVersion: _db.Entry(task).Property<uint>("xmin").CurrentValue);
        }
        public async Task<TaskSwapResult> SwapTasksAsync(
            long sourceTaskId,
            long targetTaskId,
            uint sourceExpectedVersion,
            uint targetExpectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            if (sourceTaskId == targetTaskId)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Negalima sukeisti uzduoties su pacia savimi.");
            }

            var source = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == sourceTaskId, cancellationToken);
            if (source == null)
            {
                return new TaskSwapResult(TaskSwapError.NotFound, $"Task {sourceTaskId} was not found.");
            }

            var target = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == targetTaskId, cancellationToken);
            if (target == null)
            {
                return new TaskSwapResult(TaskSwapError.TargetNotFound, $"Task {targetTaskId} was not found.");
            }

            var sourceCurrentVersion = _db.Entry(source).Property<uint>("xmin").CurrentValue;
            var targetCurrentVersion = _db.Entry(target).Property<uint>("xmin").CurrentValue;
            if (sourceExpectedVersion == 0 || sourceExpectedVersion != sourceCurrentVersion ||
                targetExpectedVersion == 0 || targetExpectedVersion != targetCurrentVersion)
            {
                return new TaskSwapResult(
                    TaskSwapError.Stale,
                    "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.",
                    CurrentSourceVersion: sourceCurrentVersion,
                    CurrentTargetVersion: targetCurrentVersion);
            }

            var canEditSource = await _taskPermissionService.CanEditTaskAsync(source, user, timezoneOffsetMinutes);
            var canEditTarget = await _taskPermissionService.CanEditTaskAsync(target, user, timezoneOffsetMinutes);
            if (!canEditSource || !canEditTarget)
            {
                return new TaskSwapResult(TaskSwapError.NotEditable, "Neturite teisiu sukeisti siu uzduociu.");
            }

            if (source.TechnicianId != target.TechnicianId)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Sukeitimas galimas tik to paties techniko uzduotims.");
            }

            if (source.StartAt.Date != target.StartAt.Date)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Sukeitimas galimas tik tame paciame dienos grafike.");
            }

            if (source.PlanId != target.PlanId)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Sukeitimas galimas tik to paties plano viduje.");
            }

            if (source.Status == ScheduleTaskStatus.Canceled || target.Status == ScheduleTaskStatus.Canceled)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Atsauktu uzduociu sukeisti negalima.");
            }

            var sourceDuration = source.EndAt - source.StartAt;
            var targetDuration = target.EndAt - target.StartAt;
            if (sourceDuration <= TimeSpan.Zero || targetDuration <= TimeSpan.Zero)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Netinkama uzduoties trukme.");
            }

            var originalSourceStartAt = source.StartAt;
            var originalSourceEndAt = source.EndAt;
            var originalTargetStartAt = target.StartAt;
            var originalTargetEndAt = target.EndAt;

            var sourceNewStartAt = target.StartAt;
            var sourceNewEndAt = sourceNewStartAt + sourceDuration;
            var targetNewStartAt = source.StartAt;
            var targetNewEndAt = targetNewStartAt + targetDuration;

            var sourceNewStartLocal = sourceNewStartAt.AddMinutes(timezoneOffsetMinutes);
            var sourceNewEndLocal = sourceNewEndAt.AddMinutes(timezoneOffsetMinutes);
            var targetNewStartLocal = targetNewStartAt.AddMinutes(timezoneOffsetMinutes);
            var targetNewEndLocal = targetNewEndAt.AddMinutes(timezoneOffsetMinutes);

            if (sourceNewStartLocal.Date != sourceNewEndLocal.Date || targetNewStartLocal.Date != targetNewEndLocal.Date)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Uzduotis turi tilpti viena diena.",
                    InvalidSwapReason: InvalidSwapReason.SpansMidnight);
            }

            var sourceStartMinutes = (int)sourceNewStartLocal.TimeOfDay.TotalMinutes;
            var sourceEndMinutes = (int)sourceNewEndLocal.TimeOfDay.TotalMinutes;
            var targetStartMinutes = (int)targetNewStartLocal.TimeOfDay.TotalMinutes;
            var targetEndMinutes = (int)targetNewEndLocal.TimeOfDay.TotalMinutes;

            var window = GetWorkingWindow(source.TechnicianId, sourceNewStartLocal.Date);
            if (sourceStartMinutes < window.StartMinutes || sourceEndMinutes > window.EndMinutes ||
                targetStartMinutes < window.StartMinutes || targetEndMinutes > window.EndMinutes)
            {
                return new TaskSwapResult(TaskSwapError.InvalidSwap, "Uzduotis netelpa i darbo laika.",
                    InvalidSwapReason: InvalidSwapReason.WorkWindow);
            }

            var sourceCheck = await CheckTimingAndTravelAsync(
                sourceNewStartLocal.Date, sourceStartMinutes, timezoneOffsetMinutes,
                source.TechnicianId, sourceNewStartAt, sourceNewEndAt,
                source.ServiceObject?.AddressId ?? 0, source.Id, cancellationToken);
            if (!sourceCheck.IsValid)
            {
                return new TaskSwapResult(
                    sourceCheck.IsInvalidWindow ? TaskSwapError.InvalidSwap : TaskSwapError.TravelInfeasible,
                    sourceCheck.Message,
                    InvalidSwapReason: sourceCheck.IsInvalidWindow ? InvalidSwapReason.PastTime : InvalidSwapReason.None);
            }

            var targetCheck = await CheckTimingAndTravelAsync(
                targetNewStartLocal.Date, targetStartMinutes, timezoneOffsetMinutes,
                target.TechnicianId, targetNewStartAt, targetNewEndAt,
                target.ServiceObject?.AddressId ?? 0, target.Id, cancellationToken);
            if (!targetCheck.IsValid)
            {
                return new TaskSwapResult(
                    targetCheck.IsInvalidWindow ? TaskSwapError.InvalidSwap : TaskSwapError.TravelInfeasible,
                    targetCheck.Message,
                    InvalidSwapReason: targetCheck.IsInvalidWindow ? InvalidSwapReason.PastTime : InvalidSwapReason.None);
            }

            var excludedTaskIds = new[] { source.Id, target.Id };
            var sourceHasOverlap = await HasTaskOverlapExcludingAsync(
                excludedTaskIds,
                source.TechnicianId,
                sourceNewStartAt,
                sourceNewEndAt,
                cancellationToken);
            if (sourceHasOverlap)
            {
                return new TaskSwapResult(TaskSwapError.Overlap, "Laiko langas uzimtas (persidengia uzduotys).");
            }

            var targetHasOverlap = await HasTaskOverlapExcludingAsync(
                excludedTaskIds,
                target.TechnicianId,
                targetNewStartAt,
                targetNewEndAt,
                cancellationToken);
            if (targetHasOverlap)
            {
                return new TaskSwapResult(TaskSwapError.Overlap, "Laiko langas uzimtas (persidengia uzduotys).");
            }

            source.StartAt = sourceNewStartAt;
            source.EndAt = sourceNewEndAt;
            target.StartAt = targetNewStartAt;
            target.EndAt = targetNewEndAt;

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);

            async Task RevertSwapIfNeededAsync()
            {
                source.StartAt = originalSourceStartAt;
                source.EndAt = originalSourceEndAt;
                target.StartAt = originalTargetStartAt;
                target.EndAt = originalTargetEndAt;

                if (transaction == null)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);

                var affectedTaskCount = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.TechnicianId == source.TechnicianId)
                    .Where(t => t.StartAt >= source.StartAt.Date && t.StartAt < source.StartAt.Date.AddDays(1))
                    .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                    .Where(t => !source.PlanId.HasValue || t.PlanId == source.PlanId)
                    .CountAsync(cancellationToken);
                if (affectedTaskCount > 2)
                {
                    var swapAdjustPlan = await _scheduleRepairService.BuildPlanAsync(
                        new ScheduleRepairRequest
                        {
                            ScenarioKind = ScheduleRepairScenarioKind.SwapAdjust,
                            TechnicianId = source.TechnicianId,
                            DayUtc = source.StartAt.Date,
                            PlanId = source.PlanId,
                            FilterByPlanId = source.PlanId.HasValue,
                            TimezoneOffsetMinutes = timezoneOffsetMinutes,
                            SourceTaskId = source.Id,
                            TargetTaskId = target.Id
                        }, cancellationToken);
                    if (!swapAdjustPlan.Success)
                    {
                        if (transaction != null)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                        }

                        await RevertSwapIfNeededAsync();
                        return new TaskSwapResult(TaskSwapError.InvalidSwap, swapAdjustPlan.Message ?? "Sukeitimo koregavimas nepavyko.");
                    }

                    var swapAdjustApply = await _scheduleRepairService.ApplyPlanAsync(swapAdjustPlan, null, cancellationToken);
                    if (!swapAdjustApply.Success)
                    {
                        if (transaction != null)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                        }

                        await RevertSwapIfNeededAsync();
                        return new TaskSwapResult(TaskSwapError.InvalidSwap, swapAdjustApply.Message ?? "Sukeitimo koregavimas nepavyko.");
                    }
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskSwapResult(
                    TaskSwapError.Stale,
                    "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.",
                    CurrentSourceVersion: sourceCurrentVersion,
                    CurrentTargetVersion: targetCurrentVersion);
            }

            await TriggerRouteEvaluationSafelyAsync(source.TechnicianId, source.StartAt.Date, "swap", cancellationToken);

            var addressIds = await _db.ServiceObjects
                .AsNoTracking()
                .Where(o => o.Id == source.ServiceObjectId || o.Id == target.ServiceObjectId)
                .ToDictionaryAsync(o => o.Id, o => o.AddressId, cancellationToken);

            var sourceAddressId = addressIds[source.ServiceObjectId];
            var sourceTravelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                source.TechnicianId,
                source.StartAt,
                source.EndAt,
                sourceAddressId,
                sourceStartMinutes,
                source.Id,
                checkOverlap: false,
                cancellationToken);
            if (!sourceTravelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new TaskSwapResult(TaskSwapError.TravelInfeasible, sourceTravelCheck.Message ?? "Nepakanka laiko kelionei.");
            }

            var targetAddressId = addressIds[target.ServiceObjectId];
            var targetTravelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                target.TechnicianId,
                target.StartAt,
                target.EndAt,
                targetAddressId,
                targetStartMinutes,
                target.Id,
                checkOverlap: false,
                cancellationToken);
            if (!targetTravelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new TaskSwapResult(TaskSwapError.TravelInfeasible, targetTravelCheck.Message ?? "Nepakanka laiko kelionei.");
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (source.PlanId.HasValue && source.Plan != null)
            {
                await AdjustPlanSpanAsync(source.Plan, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(source.PlanId.Value, cancellationToken);
            }

            return new TaskSwapResult(
                TaskSwapError.None,
                SourceTask: source,
                TargetTask: target,
                CurrentSourceVersion: _db.Entry(source).Property<uint>("xmin").CurrentValue,
                CurrentTargetVersion: _db.Entry(target).Property<uint>("xmin").CurrentValue);
        }

        public async Task<TaskSwapResult> SwapTasksSmartAsync(
            long sourceTaskId,
            long targetTaskId,
            uint sourceExpectedVersion,
            uint targetExpectedVersion,
            UserContext user,
            int timezoneOffsetMinutes,
            CancellationToken cancellationToken = default)
        {
            // Try strict swap first for the fastest path.
            var strict = await SwapTasksAsync(
                sourceTaskId,
                targetTaskId,
                sourceExpectedVersion,
                targetExpectedVersion,
                user,
                timezoneOffsetMinutes,
                cancellationToken);

            if (strict.Ok)
            {
                return strict;
            }

            _logger.LogInformation(
                "swap-smart.strict-fail sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} reason={Reason} message={Message}",
                sourceTaskId,
                targetTaskId,
                strict.Error,
                strict.Message ?? string.Empty);

            // Allow smart-planner fallback for recoverable failures:
            //    TravelInfeasible  adjacent travel times don't fit; planner can reorder.
            //    Overlap         another task is in the way; planner can shift it.
            //   WorkWindow      longer task overflows window at target slot; planner
            //   can pull it back to its earliest feasible slot.
            var isRecoverable =
                strict.Error == TaskSwapError.TravelInfeasible ||
                strict.Error == TaskSwapError.Overlap ||
                (strict.Error == TaskSwapError.InvalidSwap &&
                 strict.InvalidSwapReason == InvalidSwapReason.WorkWindow);

            if (!isRecoverable)
            {
                return strict;
            }

            // Strict swap may have updated tracked entities before rolling back.
            // Reset tracked state so smart planning/version checks read actual DB values.
            _db.ChangeTracker.Clear();

            var repairPlan = await BuildSwapRepairPlanAsync(sourceTaskId, targetTaskId, timezoneOffsetMinutes, cancellationToken);

            if (!repairPlan.Success || repairPlan.Adjustments.Count == 0)
            {
                _logger.LogWarning(
                    "swap-smart.plan-fail sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} reason={Reason}",
                    sourceTaskId,
                    targetTaskId,
                    repairPlan.Message ?? strict.Message ?? string.Empty);
                return new TaskSwapResult(
                    strict.Error,
                    repairPlan.Message ?? strict.Message,
                    CurrentSourceVersion: strict.CurrentSourceVersion,
                    CurrentTargetVersion: strict.CurrentTargetVersion);
            }

            var source = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == sourceTaskId, cancellationToken);
            if (source == null)
            {
                return new TaskSwapResult(TaskSwapError.NotFound, $"Task {sourceTaskId} was not found.");
            }

            var target = await _db.Tasks
                .Include(t => t.Plan)
                .Include(t => t.ServiceObject)
                .FirstOrDefaultAsync(t => t.Id == targetTaskId, cancellationToken);
            if (target == null)
            {
                return new TaskSwapResult(TaskSwapError.TargetNotFound, $"Task {targetTaskId} was not found.");
            }

            var sourceCurrentVersion = _db.Entry(source).Property<uint>("xmin").CurrentValue;
            var targetCurrentVersion = _db.Entry(target).Property<uint>("xmin").CurrentValue;
            if (sourceExpectedVersion == 0 || sourceExpectedVersion != sourceCurrentVersion ||
                targetExpectedVersion == 0 || targetExpectedVersion != targetCurrentVersion)
            {
                return new TaskSwapResult(
                    TaskSwapError.Stale,
                    "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.",
                    CurrentSourceVersion: sourceCurrentVersion,
                    CurrentTargetVersion: targetCurrentVersion);
            }

                        var affectedIds = repairPlan.Adjustments.Select(x => x.TaskId).Distinct().ToList();

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                var applyResult = await _scheduleRepairService.ApplyPlanAsync(repairPlan, null, cancellationToken);
                if (!applyResult.Success)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return new TaskSwapResult(TaskSwapError.InvalidSwap, applyResult.Message ?? "Smart swap koregavimo pritaikyti nepavyko.");
                }

                affectedIds = applyResult.AppliedTaskIds.ToList();
                var affectedTasks = await _db.Tasks
                    .Include(t => t.ServiceObject)
                    .Where(t => affectedIds.Contains(t.Id))
                    .ToListAsync(cancellationToken);
                if (affectedTasks.Count != affectedIds.Count)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return new TaskSwapResult(TaskSwapError.InvalidSwap, "Smart swap koregavimui truksta uzduociu.");
                }


                await TriggerRouteEvaluationSafelyAsync(source.TechnicianId, source.StartAt.Date, "swap-smart", cancellationToken);

                foreach (var task in affectedTasks)
                {
                    var addressId = task.ServiceObject.AddressId;
                    var startMinutesLocal = (int)task.StartAt.AddMinutes(timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
                    var travelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                        task.TechnicianId,
                        task.StartAt,
                        task.EndAt,
                        addressId,
                        startMinutesLocal,
                        task.Id,
                        checkOverlap: false,
                        cancellationToken);
                    if (!travelCheck.IsFeasible)
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        return new TaskSwapResult(TaskSwapError.TravelInfeasible, travelCheck.Message ?? "Nepakanka laiko kelionei.");
                    }

                    var smartSwapInbound = Math.Max(0, travelCheck.InboundTravelMinutes ?? 0);
                    if (!_timeService.IsTravelStartFeasible(task.StartAt, smartSwapInbound))
                    {
                        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
                        return new TaskSwapResult(TaskSwapError.TravelInfeasible, "Nepakanka laiko kelionei nuo dabartinio laiko.");
                    }
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                if (source.PlanId.HasValue && source.Plan != null)
                {
                    await AdjustPlanSpanAsync(source.Plan, cancellationToken);
                    await _evaluationEngine.RecalculatePlanAsync(source.PlanId.Value, cancellationToken);
                }

                return new TaskSwapResult(
                    TaskSwapError.None,
                    SourceTask: source,
                    TargetTask: target,
                    CurrentSourceVersion: _db.Entry(source).Property<uint>("xmin").CurrentValue,
                    CurrentTargetVersion: _db.Entry(target).Property<uint>("xmin").CurrentValue);
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
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

            var window = GetWorkingWindow(source.TechnicianId, sourceNewStartLocal.Date);
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
            var plan = await BuildSwapRepairPlanAsync(sourceTaskId, targetTaskId, timezoneOffsetMinutes, cancellationToken);

            if (plan.Success && plan.Adjustments.Count > 0)
                return SwapSmartPreviewResult.Ok(smartRequired: true);

            return SwapSmartPreviewResult.Fail(plan.Message ?? "Nepakanka laisvo laiko.");
        }

                public async Task<TaskTimeUpdateResult> UpdateTaskTimeAsync(long taskId, DateTime targetDate, int targetSlotStartMinutes, int targetSlotEndMinutes, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.NotFound, $"Task {taskId} was not found.");
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            if (targetSlotEndMinutes <= targetSlotStartMinutes)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.InvalidWindow, "Pabaiga turi buti veliau nei pradzia.");
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, "Neturite teisiu keisti uzduoties laiko.");
            }

            var placementValidation = await ValidatePlacementForActionAsync(
                ignoreTaskId: task.Id,
                targetTechnicianId: task.TechnicianId,
                targetDate: targetDate,
                startMinutes: targetSlotStartMinutes,
                endMinutes: targetSlotEndMinutes,
                plan: task.Plan,
                status: task.Status,
                user: user,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                requireEndAligned: true,
                cancellationToken: cancellationToken);

            if (!placementValidation.IsValid)
            {
                return MapPlacementErrorForTimeUpdate(placementValidation);
            }

            var oldDay = task.StartAt.Date;

            task.StartAt = placementValidation.StartAtUtc;
            task.EndAt = placementValidation.EndAtUtc;

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.Stale, "Uzduotis pasikeite (stale).", CurrentVersion: currentVersion);
            }

            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "reschedule", cancellationToken);

            var timeServiceAddressId = await _db.ServiceObjects
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);
            var timeTimingTravelValidation = await ValidateTimingAndTravelAsync(
                taskDateLocal: targetDate.Date,
                startMinutesLocal: targetSlotStartMinutes,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                technicianId: task.TechnicianId,
                startAtUtc: placementValidation.StartAtUtc,
                endAtUtc: placementValidation.EndAtUtc,
                serviceAddressId: timeServiceAddressId,
                excludeTaskId: task.Id,
                cancellationToken: cancellationToken);
            if (!timeTimingTravelValidation.IsValid)
            {
                if (!timeTimingTravelValidation.IsInvalidWindow && await TryApplyTimingTravelRepairAsync(task, timeTimingTravelValidation, cancellationToken))
                {
                    await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "reschedule-repair", cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    if (task.PlanId.HasValue)
                    {
                        await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                        await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
                    }

                    if (targetDate.Date != oldDay)
                    {
                        await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, oldDay, "reschedule-old-day", cancellationToken);
                    }

                    var repairedVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
                    return new TaskTimeUpdateResult(
                        TaskTimeUpdateError.None,
                        BuildRepairAppliedMessage(timeTimingTravelValidation.Message, timeTimingTravelValidation.RepairPlan?.PrimaryPlacement, timezoneOffsetMinutes),
                        task,
                        repairedVersion);
                }

                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new TaskTimeUpdateResult(
                    timeTimingTravelValidation.IsInvalidWindow ? TaskTimeUpdateError.InvalidWindow : TaskTimeUpdateError.TravelInfeasible,
                    timeTimingTravelValidation.Message);
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (task.PlanId.HasValue)
            {
                await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }

            if (targetDate.Date != oldDay)
            {
                await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, oldDay, "reschedule-old-day", cancellationToken);
            }

            var newVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            return new TaskTimeUpdateResult(TaskTimeUpdateError.None, Task: task, CurrentVersion: newVersion);
        }
        public async Task<TaskDeleteResult> DeleteTaskAsync(long taskId, uint expectedVersion, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("svc.task.delete.requested taskId={TaskId} expectedVersion={Version}", taskId, expectedVersion);

            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                _logger.LogWarning("svc.task.delete.not-found taskId={TaskId}", taskId);
                return new TaskDeleteResult(TaskDeleteError.NotFound, $"Task {taskId} was not found.");
            }

            var currentVersion = _db.Entry(task).Property<uint>("xmin").CurrentValue;
            if (expectedVersion == 0 || expectedVersion != currentVersion)
            {
                _logger.LogWarning("svc.task.delete.stale taskId={TaskId} expectedVersion={Expected} currentVersion={Current}", taskId, expectedVersion, currentVersion);
                return new TaskDeleteResult(TaskDeleteError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            var canDelete = await _taskPermissionService.CanDeleteTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canDelete)
            {
                _logger.LogWarning("svc.task.delete.not-editable taskId={TaskId}", taskId);
                return new TaskDeleteResult(TaskDeleteError.NotEditable, "Neturite teisiu istrinti sios uzduoties.");
            }

            var planId = task.PlanId;
            var plan = task.Plan;
            var deletedTechnicianId = task.TechnicianId;
            var deletedDay = task.StartAt.Date;

            _db.Tasks.Remove(task);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("svc.task.delete.stale-on-save taskId={TaskId}", taskId);
                return new TaskDeleteResult(TaskDeleteError.Stale, "Uzduotis pasikeite (stale).", CurrentVersion: currentVersion);
            }

            var compactPlan = await _scheduleRepairService.BuildPlanAsync(
                new ScheduleRepairRequest
                {
                    ScenarioKind = ScheduleRepairScenarioKind.Delete,
                    TechnicianId = deletedTechnicianId,
                    DayUtc = deletedDay,
                    PlanId = planId,
                    FilterByPlanId = planId.HasValue,
                    TimezoneOffsetMinutes = timezoneOffsetMinutes
                },
                cancellationToken);
            if (compactPlan.Success)
            {
                await _scheduleRepairService.ApplyPlanAsync(compactPlan, null, cancellationToken);
            }

            if (planId.HasValue && plan != null)
            {
                await AdjustPlanSpanAsync(plan, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(planId.Value, cancellationToken);
            }

            await TriggerRouteEvaluationSafelyAsync(deletedTechnicianId, deletedDay, "delete", cancellationToken);

            _logger.LogInformation("svc.task.delete.ok taskId={TaskId}", taskId);
            return new TaskDeleteResult(TaskDeleteError.None, Task: task, CurrentVersion: 0);
        }

        public async Task PublishDraftAsync(CancellationToken cancellationToken = default)
        {
            var draft = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            if (draft == null)
            {
                throw new InvalidOperationException("No draft plan to publish.");
            }

            var active = await _db.Plans
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

            // Validation hook: place draft validation here before flipping states.

            try
            {
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recalculate evaluations for draft plan {PlanId} during publish.", draft.Id);
                throw;
            }

            var draftTasksCount = draft.Tasks.Count;
            var draftTaskIds = draft.Tasks.Select(t => t.Id).ToList();
            var draftEvalCount = draftTaskIds.Count == 0
                ? 0
                : await _db.TaskEvaluations.CountAsync(e => draftTaskIds.Contains(e.TaskId), cancellationToken);

            if (draftTasksCount > 0 && draftEvalCount < draftTasksCount)
            {
                // Try one more recalculation in case evaluations were stale.
                await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);
                draftEvalCount = await _db.TaskEvaluations.CountAsync(e => draftTaskIds.Contains(e.TaskId), cancellationToken);
                if (draftEvalCount < draftTasksCount)
                {
                    _logger.LogWarning("Draft plan {PlanId} has {EvalCount}/{TaskCount} evaluations; proceeding with publish.", draft.Id, draftEvalCount, draftTasksCount);
                }
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            _logger.LogInformation("svc.plan.publish draftId={DraftId} draftTasks={DraftTasks} draftEvals={DraftEvals} activeId={ActiveId}", draft.Id, draftTasksCount, draftEvalCount, active?.Id);

            if (active != null)
            {
                active.Status = PlanStatus.Superseded;
                active.SupersededByPlanId = draft.Id;
                _db.Plans.Update(active);
            }

            draft.Status = PlanStatus.Active;
            _db.Plans.Update(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("svc.plan.publish.ok draftId={DraftId} superseded={ActiveId}", draft.Id, active?.Id);
        }

        public async Task DiscardDraftAsync(CancellationToken cancellationToken = default)
        {
            var draft = await _db.Plans
                .Include(p => p.Tasks)
                .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            if (draft == null)
            {
                return;
            }

            await _evaluationEngine.RecalculatePlanAsync(draft.Id, cancellationToken);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            if (draft.Tasks.Any())
            {
                _db.Tasks.RemoveRange(draft.Tasks);
            }

            _db.Plans.Remove(draft);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("svc.plan.discard draftId={DraftId} removedTasks={Tasks}", draft.Id, draft.Tasks.Count);
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
                .Distinct();
            
            var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);

            var response = new Models.DTOs.Responses.DragPreviewResponseDto
            {
                DraggedTaskId = targetTask.Id
            };

            var targetPreviewValidation = TimingTravelValidationResult.Success;
            {
                var localTargetStartAt = _timeService.ConvertUtcToLocal(targetStartAt, timezoneOffsetMinutes);
                var localTargetStartMinutes = (int)localTargetStartAt.TimeOfDay.TotalMinutes;

                targetPreviewValidation = await ValidateTimingAndTravelAsync(
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

        private LoadSnapshotDto BuildLoadSnapshotForSimulation(
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
            return MapToLoadSnapshotDto(technician, snapshot);
        }

        private static Dictionary<long, int>? BuildTravelMinutesByTaskIdFromLookup(
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

        private double? ComputeTravelHoursForSimulation(
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

        private static int? ComputeRequiredInboundTravelMinutesForTask(
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

        private async Task TriggerRouteEvaluationSafelyAsync(long technicianId, DateTime day, string action, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "route.evaluation.triggered action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                action, technicianId, day);
            try
            {
                await _routeEvaluationService.EvaluateTechnicianDayAsync(technicianId, day, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "route.evaluation.trigger.failed action={Action} technicianId={TechnicianId} day={Day:yyyy-MM-dd}",
                    action, technicianId, day);
            }
        }

        private async Task TriggerTaskRouteImpactSafelyAsync(long taskId, string action, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "route.evaluation.task-impact.triggered action={Action} taskId={TaskId}",
                action, taskId);
            try
            {
                await _routeEvaluationService.EvaluateTaskRouteImpactAsync(taskId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "route.evaluation.task-impact.trigger.failed action={Action} taskId={TaskId}",
                    action, taskId);
            }
        }

        private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync(CancellationToken cancellationToken)
        {
            if (string.Equals(_db.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await _db.Database.BeginTransactionAsync(cancellationToken);
        }

        private async Task<Dictionary<long, int>> BuildTravelMinutesByTaskIdAsync(
            TechnicianEntity technician,
            List<TaskEntity> weekTasks,
            CancellationToken cancellationToken)
        {
            var routes = await _db.TravelRoutes
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var routeLookup = routes.ToDictionary(r => (r.FromAddressId, r.ToAddressId), r => r);

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
                    var fromAddressId = prev != null
                        ? prev.ServiceObject!.AddressId
                        : technician.WorkplaceAddressId;
                    var toAddressId = task.ServiceObject!.AddressId;

                    if (routeLookup.TryGetValue((fromAddressId, toAddressId), out var route))
                    {
                        result[task.Id] = route.DurationMinutes;
                    }
                    else
                    {
                        result[task.Id] = 0;
                    }

                    prev = task;
                }
            }

            return result;
        }

        /// <summary>
        /// Generates tasks for a draft plan by cloning tasks from the active plan.
        /// Tasks are adjusted to fit the new date range while preserving all relationships.
        /// </summary>
        private async Task<List<TaskEntity>> GenerateTasksFromActivePlanAsync(
            PlanEntity activePlan,
            DateTime draftStart,
            DateTime draftEnd,
            long draftPlanId,
            CancellationToken cancellationToken)
        {
            var result = new List<TaskEntity>();

            _logger.LogInformation(
                "svc.plan.draft.clone.start activePlanId={ActivePlanId} draftStart={DraftStart} draftEnd={DraftEnd} activePeriod={ActiveStart}-{ActiveEnd}",
                activePlan.Id, draftStart, draftEnd, activePlan.PeriodStart, activePlan.PeriodEnd);

            // Load active plan tasks with all relationships
            var activeTasks = await _db.Tasks
                .Include(t => t.ServiceObject)
                .Where(t => t.PlanId == activePlan.Id)
                .OrderBy(t => t.StartAt)
                .ToListAsync(cancellationToken);

            if (!activeTasks.Any())
            {
                _logger.LogWarning("svc.plan.draft.clone no tasks in active plan activePlanId={PlanId}", activePlan.Id);
                return result;
            }

            _logger.LogInformation(
                "svc.plan.draft.clone.tasks-loaded count={TaskCount} firstTask={FirstStart} lastTask={LastEnd}",
                activeTasks.Count, activeTasks.First().StartAt, activeTasks.Last().EndAt);

            // Calculate how many days the active task span covers (robust even if plan metadata is stale)
            var sourceStart = activeTasks.Min(t => t.StartAt.Date);
            var sourceEnd = activeTasks.Max(t => t.EndAt.Date);
            var sourceSpanDays = (sourceEnd - sourceStart).Days + 1;
            var draftDays = (draftEnd.Date - draftStart.Date).Days + 1;

            _logger.LogInformation(
                "svc.plan.draft.clone.dates sourceSpanDays={SourceDays} draftDays={DraftDays} sourceStart={SourceStart} sourceEnd={SourceEnd}",
                sourceSpanDays, draftDays, sourceStart, sourceEnd);

            // Group tasks by day offset from plan start
            var tasksByDayOffset = activeTasks
                .GroupBy(t => (t.StartAt.Date - sourceStart).Days)
                .ToDictionary(g => g.Key, g => g.ToList());

            _logger.LogInformation(
                "svc.plan.draft.clone.grouped dayOffsets={DayOffsets} totalTasksGrouped={TotalTasks}",
                string.Join(",", tasksByDayOffset.Keys.OrderBy(k => k)),
                tasksByDayOffset.Sum(kvp => kvp.Value.Count));

            // Clone tasks for each day in the draft period
            for (var dayOffset = 0; dayOffset < draftDays; dayOffset++)
            {
                var targetDate = draftStart.Date.AddDays(dayOffset);

                // Use modulo to cycle through the active plan's task pattern
                var sourceDayOffset = dayOffset % sourceSpanDays;

                if (!tasksByDayOffset.TryGetValue(sourceDayOffset, out var sourceTasks))
                {
                    _logger.LogDebug(
                        "svc.plan.draft.clone.skip-day dayOffset={DayOffset} targetDate={TargetDate} sourceDayOffset={SourceDayOffset} reason=no-source-tasks",
                        dayOffset, targetDate, sourceDayOffset);
                    continue; // No tasks for this day of week pattern
                }

                _logger.LogDebug(
                    "svc.plan.draft.clone.day dayOffset={DayOffset} targetDate={TargetDate} sourceDayOffset={SourceDayOffset} sourceTaskCount={Count}",
                    dayOffset, targetDate, sourceDayOffset, sourceTasks.Count);

                foreach (var sourceTask in sourceTasks)
                {
                    // Calculate time offset from the source day
                    var timeOfDay = sourceTask.StartAt.TimeOfDay;
                    var duration = sourceTask.EndAt - sourceTask.StartAt;

                    // Create new task with adjusted dates
                    var newTask = new TaskEntity
                    {
                        PlanId = draftPlanId,
                        TechnicianId = sourceTask.TechnicianId,
                        ServiceObjectId = sourceTask.ServiceObjectId, // Preserve service object reference
                        Title = sourceTask.Title,
                        StartAt = DateTime.SpecifyKind(targetDate.Add(timeOfDay), DateTimeKind.Utc),
                        EndAt = DateTime.SpecifyKind(targetDate.Add(timeOfDay).Add(duration), DateTimeKind.Utc),
                        Priority = sourceTask.Priority,
                        Status = ScheduleTaskStatus.InProgress,
                        SkillLevel = sourceTask.SkillLevel,
                        Instructions = sourceTask.Instructions,
                        TechnicianComment = string.Empty, // Clear comment for new plan
                        CreatedAt = DateTime.UtcNow
                    };

                    result.Add(newTask);
                }
            }

            _logger.LogInformation(
                "svc.plan.draft.clone generated {TaskCount} tasks from {SourceTaskCount} source tasks, activePlanId={ActivePlanId}",
                result.Count, activeTasks.Count, activePlan.Id);

            return result;
        }
    }
}


