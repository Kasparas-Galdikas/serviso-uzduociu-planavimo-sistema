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
        private readonly ILogger<PlanService> _logger;

        public PlanService(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IPlanningCalendar planningCalendar,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IRouteEvaluationService routeEvaluationService,
            ITaskTravelFeasibilityService travelFeasibilityService,
            ILogger<PlanService> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _planningCalendar = planningCalendar;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _routeEvaluationService = routeEvaluationService;
            _travelFeasibilityService = travelFeasibilityService;
            _logger = logger;
        }

        private sealed record WorkingWindow(int StartMinutes, int EndMinutes);

        private static WorkingWindow GetWorkingWindow(long technicianId, DateTime date) =>
            new WorkingWindow(ScheduleGrid.DayStartMinutes, ScheduleGrid.DayEndMinutes);

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

            // Prevent placing tasks with start time in the past
            if (!_timeService.IsTaskStillEditable(startAt))
            {
                return (PlacementError.InvalidWindow, "Negalima nustatyti pradÅ¾ios laiko praeityje.", default, default, string.Empty);
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
            {
                await _db.SaveChangesAsync(cancellationToken);

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

            // Calculate user's local "today" from server UTC (security: prevent clock manipulation)
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);

            // Validate: Cannot create tasks in the past
            if (taskDate < userLocalToday)
            {
                return new TaskCreateResult(TaskCreateError.InvalidWindow, "Negalima kurti uÅ¾duoÄiÅ³ praeityje.");
            }

            // Get all plans to determine target plan
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

            // Determine target plan (Priority: Draft > Active > Superseded > Extend Active > Create Active)
            PlanEntity? targetPlan = null;

            if (draftPlan != null && taskDate >= draftPlan.PeriodStart.Date && taskDate <= draftPlan.PeriodEnd.Date)
            {
                targetPlan = draftPlan; // Create in Draft
                _logger.LogInformation("svc.plan.create-task.target plan=Draft planId={PlanId}", draftPlan.Id);
            }
            else if (activePlan != null && taskDate >= activePlan.PeriodStart.Date && taskDate <= activePlan.PeriodEnd.Date)
            {
                targetPlan = activePlan; // Create in Active
                _logger.LogInformation("svc.plan.create-task.target plan=Active planId={PlanId}", activePlan.Id);
            }
            else
            {
                // Try to find matching Superseded plan
                var matchingSuperseded = supersededPlans
                    .FirstOrDefault(p => taskDate >= p.PeriodStart.Date && taskDate <= p.PeriodEnd.Date);
                if (matchingSuperseded != null)
                {
                    targetPlan = matchingSuperseded; // Create in Superseded
                    _logger.LogInformation("svc.plan.create-task.target plan=Superseded planId={PlanId}", matchingSuperseded.Id);
                }
                else
                {
                    // Extend Active or create new Active plan
                    targetPlan = await GetOrExtendActivePlanAsync(activePlan, taskDate, userLocalToday, cancellationToken);
                    _logger.LogInformation("svc.plan.create-task.target plan=Extended/New planId={PlanId}", targetPlan.Id);
                }
            }

            var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Nauja uzduotis" : title.Trim();
            var normalizedSkill = string.IsNullOrWhiteSpace(skillLevel) ? string.Empty : skillLevel.Trim();
            var normalizedInstructions = string.IsNullOrWhiteSpace(instructions) ? string.Empty : instructions.Trim();

            var validation = await ValidatePlacementAsync(
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

            if (validation.Error != PlacementError.None)
            {
                return validation.Error switch
                {
                    PlacementError.NotEditable => new TaskCreateResult(TaskCreateError.NotEditable, validation.Message),
                    PlacementError.TechnicianNotFound => new TaskCreateResult(TaskCreateError.TechnicianNotFound, validation.Message),
                    PlacementError.Overlap => new TaskCreateResult(TaskCreateError.Overlap, validation.Message),
                    _ => new TaskCreateResult(TaskCreateError.InvalidWindow, validation.Message)
                };
            }

            // Find or create service object
            var serviceObjectId = await FindOrCreateServiceObjectAsync(objectName, objectAddress, validation.City, cancellationToken);
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
                StartAt = validation.StartAt,
                EndAt = validation.EndAt,
                Priority = priority,
                Status = status,
                CreatedAt = DateTime.UtcNow
            };

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(cancellationToken);

            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "create", cancellationToken);

            var travelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                task.TechnicianId,
                task.StartAt,
                task.EndAt,
                serviceObjectMeta.AddressId,
                startMinutes,
                null,
                checkOverlap: false,
                cancellationToken);
            if (!travelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                return new TaskCreateResult(TaskCreateError.TravelInfeasible, travelCheck.Message ?? "Nepakanka laiko kelionei.");
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

            // Find or create service object if objectName/objectAddress provided
            if (!string.IsNullOrWhiteSpace(objectName) || !string.IsNullOrWhiteSpace(objectAddress))
            {
                var city = await _db.Technicians
                    .Where(t => t.Id == task.TechnicianId)
                    .Select(t => t.WorkplaceAddress.City)
                    .FirstAsync(cancellationToken);
                var serviceObjectId = await FindOrCreateServiceObjectAsync(objectName, objectAddress, city, cancellationToken);
                task.ServiceObjectId = serviceObjectId;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "update", cancellationToken);

            // Resolve final address and check travel feasibility after route evaluation.
            var finalAddressId = await _db.ServiceObjects
                .AsNoTracking()
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);

            var startMinutesLocal = (int)task.StartAt.AddMinutes(timezoneOffsetMinutes).TimeOfDay.TotalMinutes;
            var travelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                task.TechnicianId,
                task.StartAt,
                task.EndAt,
                finalAddressId,
                startMinutesLocal,
                taskId,
                checkOverlap: false,
                cancellationToken);
            if (!travelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                return new TaskUpdateResult(TaskUpdateError.TravelInfeasible, travelCheck.Message ?? "Nepakanka laiko kelionei.");
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

            var validation = await ValidatePlacementAsync(
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

            if (validation.Error != PlacementError.None)
            {
                return validation.Error switch
                {
                    PlacementError.NotEditable => new TaskMoveResult(TaskMoveError.NotEditable, "Neturite teisiu perkelti sios uzduoties."),
                    PlacementError.TechnicianNotFound => new TaskMoveResult(TaskMoveError.TechnicianNotFound, validation.Message),
                    PlacementError.Overlap => new TaskMoveResult(TaskMoveError.Overlap, validation.Message),
                    _ => new TaskMoveResult(TaskMoveError.InvalidWindow, validation.Message)
                };
            }

            var oldTechnicianId = task.TechnicianId;
            var oldDay = task.StartAt.Date;

            task.TechnicianId = targetTechnicianId;
            task.StartAt = validation.StartAt;
            task.EndAt = validation.EndAt;

            await using var transaction = await BeginTransactionIfSupportedAsync(cancellationToken);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            await TriggerRouteEvaluationSafelyAsync(task.TechnicianId, task.StartAt.Date, "move", cancellationToken);

            var moveServiceAddressId = await _db.ServiceObjects
                .Where(o => o.Id == task.ServiceObjectId)
                .Select(o => o.AddressId)
                .FirstAsync(cancellationToken);

            var moveTravelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                targetTechnicianId,
                validation.StartAt,
                validation.EndAt,
                moveServiceAddressId,
                targetSlotStartMinutes,
                taskId,
                checkOverlap: false,
                cancellationToken);
            if (!moveTravelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                return new TaskMoveResult(TaskMoveError.TravelInfeasible, moveTravelCheck.Message ?? "Nepakanka laiko kelionei.");
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

            if (targetTechnicianId != oldTechnicianId || targetDate.Date != oldDay)
            {
                await TriggerRouteEvaluationSafelyAsync(oldTechnicianId, oldDay, "move-old-day", cancellationToken);
            }

            return new TaskMoveResult(TaskMoveError.None, Task: task, CurrentVersion: _db.Entry(task).Property<uint>("xmin").CurrentValue);
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

            // Single authorization check - TaskPermissionService handles time editability internally
            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, "Neturite teisiu keisti uzduoties laiko.");
            }

            var validation = await ValidatePlacementAsync(
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

            if (validation.Error != PlacementError.None)
            {
                return validation.Error switch
                {
                    PlacementError.NotEditable => new TaskTimeUpdateResult(TaskTimeUpdateError.NotEditable, validation.Message),
                    PlacementError.Overlap => new TaskTimeUpdateResult(TaskTimeUpdateError.Overlap, validation.Message),
                    _ => new TaskTimeUpdateResult(TaskTimeUpdateError.InvalidWindow, validation.Message)
                };
            }

            var oldDay = task.StartAt.Date;

            task.StartAt = validation.StartAt;
            task.EndAt = validation.EndAt;

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

            var timeTravelCheck = await _travelFeasibilityService.EvaluatePlacementAsync(
                task.TechnicianId,
                validation.StartAt,
                validation.EndAt,
                timeServiceAddressId,
                targetSlotStartMinutes,
                task.Id,
                checkOverlap: false,
                cancellationToken);
            if (!timeTravelCheck.IsFeasible)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                return new TaskTimeUpdateResult(TaskTimeUpdateError.TravelInfeasible, timeTravelCheck.Message ?? "Nepakanka laiko kelionei.");
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
            DateTime targetStartAt,
            DateTime targetEndAt,
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
                return new DragPreviewResult(DragPreviewError.NoPlan, "UÅ¾duotis neturi plano.");
            }

            var hoverTech = await _db.Technicians
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == targetTechnicianId, cancellationToken);

            if (hoverTech == null)
            {
                return new DragPreviewResult(DragPreviewError.TechnicianNotFound, "Technikas nerastas.");
            }

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

            var response = new Models.DTOs.Responses.DragPreviewResponseDto
            {
                DraggedTaskId = targetTask.Id
            };

            var travelRoutes = await _db.TravelRoutes
                .AsNoTracking()
                .Select(r => new { r.FromAddressId, r.ToAddressId, r.DurationMinutes })
                .ToListAsync(cancellationToken);
            var routeLookup = travelRoutes
                .GroupBy(r => (r.FromAddressId, r.ToAddressId))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.DurationMinutes).First().DurationMinutes);

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
                    RequiredInboundTravelMinutes = requiredInboundTravelMinutes
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
