using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public class PlanService : IPlanService
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<PlanService> _logger;

        public PlanService(ScheduleDbContext db, IEvaluationEngine evaluationEngine, IPlanningCalendar planningCalendar, ITaskPermissionService taskPermissionService, IScheduleTimeService timeService, ILogger<PlanService> logger)
        {
            _db = db;
            _evaluationEngine = evaluationEngine;
            _planningCalendar = planningCalendar;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _logger = logger;
        }

        private sealed record WorkingWindow(int StartMinutes, int EndMinutes);

        private static WorkingWindow GetWorkingWindow(long technicianId, DateTime date) =>
            new WorkingWindow(ScheduleGrid.DayStartMinutes, ScheduleGrid.DayEndMinutes);

        private async Task<long?> FindOrCreateObjectAsync(string? objectName, string? objectAddress, long? regionId, CancellationToken cancellationToken = default)
        {
            var normalizedName = string.IsNullOrWhiteSpace(objectName) ? null : objectName.Trim();
            var normalizedAddress = string.IsNullOrWhiteSpace(objectAddress) ? null : objectAddress.Trim();

            // If both name and address are empty, return null (no object)
            if (string.IsNullOrEmpty(normalizedName) && string.IsNullOrEmpty(normalizedAddress))
            {
                return null;
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

            // Try to find existing object by name and address
            var existingObject = await _db.Objects
                .FirstOrDefaultAsync(o => o.Name == normalizedName && o.Address == normalizedAddress, cancellationToken);

            if (existingObject != null)
            {
                return existingObject.Id;
            }

            // Create new object
            var newObject = new ObjectEntity
            {
                Name = normalizedName,
                Address = normalizedAddress,
                RegionId = regionId,
                CreatedAt = DateTime.UtcNow
            };

            _db.Objects.Add(newObject);
            await _db.SaveChangesAsync(cancellationToken);

            return newObject.Id;
        }

        private enum PlacementError
        {
            None,
            InvalidWindow,
            Overlap,
            TechnicianNotFound,
            NotEditable
        }

        private async Task<(PlacementError Error, string Message, DateTime StartAt, DateTime EndAt, long? RegionId)> ValidatePlacementAsync(
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
                return (PlacementError.InvalidWindow, "Netinkamas laiko langas.", default, default, null);
            }

            if (startMinutes % ScheduleGrid.SlotSizeMinutes != 0)
            {
                return (PlacementError.InvalidWindow, "Laikas turi atitikti grafiko zingsni.", default, default, null);
            }

            if (requireEndAligned && (endMinutes % ScheduleGrid.SlotSizeMinutes != 0))
            {
                return (PlacementError.InvalidWindow, "Pabaigos laikas turi atitikti grafiko zingsni.", default, default, null);
            }

            var window = GetWorkingWindow(targetTechnicianId, targetDate);
            if (startMinutes < window.StartMinutes)
            {
                return (PlacementError.InvalidWindow, "Per ankstyvas laikas.", default, default, null);
            }

            if (endMinutes > window.EndMinutes)
            {
                return (PlacementError.InvalidWindow, "Uzduotis netelpa i darbo laika.", default, default, null);
            }

            // Convert local time to UTC for storage
            var startAt = _timeService.CreateUtcFromDateAndMinutes(targetDate, startMinutes, timezoneOffsetMinutes);
            var endAt = _timeService.CreateUtcFromDateAndMinutes(targetDate, endMinutes, timezoneOffsetMinutes);

            var targetTech = await _db.Technicians
                .AsNoTracking()
                .Select(t => new { t.Id, t.RegionId })
                .FirstOrDefaultAsync(t => t.Id == targetTechnicianId, cancellationToken);

            if (targetTech == null)
            {
                return (PlacementError.TechnicianNotFound, "Technikas nerastas.", default, default, null);
            }

            var permissionProbe = new TaskEntity
            {
                Id = ignoreTaskId ?? 0,
                PlanId = plan?.Id,
                Plan = plan,
                TechnicianId = targetTech.Id,
                RegionId = targetTech.RegionId,
                StartAt = startAt,
                EndAt = endAt,
                Status = status
            };

            var canEdit = await _taskPermissionService.CanEditTaskAsync(permissionProbe, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                return (PlacementError.NotEditable, "Neturite teisiu atlikti sio veiksmo.", default, default, null);
            }

            // Prevent placing tasks with start time in the past
            if (!_timeService.IsTaskStillEditable(startAt))
            {
                return (PlacementError.InvalidWindow, "Negalima nustatyti pradžios laiko praeityje.", default, default, null);
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
                return (PlacementError.Overlap, "Laiko langas uzimtas (persidengia uzduotys).", default, default, null);
            }

            return (PlacementError.None, string.Empty, startAt, endAt, targetTech.RegionId);
        }

        public Task<PlanEntity?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            _db.Plans
               .AsNoTracking()
               .FirstOrDefaultAsync(p => p.Status == PlanStatus.Active, cancellationToken);

        public Task<PlanEntity?> GetDraftPlanAsync(CancellationToken cancellationToken = default) =>
            _db.Plans
               .AsNoTracking()
               .FirstOrDefaultAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

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

            var technicians = await _db.Technicians
                .Include(t => t.Region)
                .ToListAsync(cancellationToken);

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

        public async Task<TaskCreateResult> CreateTaskAsync(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title, string location, TaskPriority priority, ScheduleTaskStatus status, string skillLevel, string objectName, string objectAddress, string instructions, UserContext user, int timezoneOffsetMinutes, CancellationToken cancellationToken = default)
        {
            var taskDate = targetDate.Date;

            // Calculate user's local "today" from server UTC (security: prevent clock manipulation)
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);

            // Validate: Cannot create tasks in the past
            if (taskDate < userLocalToday)
            {
                return new TaskCreateResult(TaskCreateError.InvalidWindow, "Negalima kurti užduočių praeityje.");
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
            var normalizedLocation = string.IsNullOrWhiteSpace(location) ? string.Empty : location.Trim();
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

            // Find or create object
            var objectId = await FindOrCreateObjectAsync(objectName, objectAddress, validation.RegionId, cancellationToken);

            var task = new TaskEntity
            {
                PlanId = targetPlan.Id,
                TechnicianId = technicianId,
                RegionId = validation.RegionId,
                ObjectId = objectId,
                Title = normalizedTitle,
                Location = normalizedLocation,
                SkillLevel = normalizedSkill,
                Instructions = normalizedInstructions,
                StartAt = validation.StartAt,
                EndAt = validation.EndAt,
                Priority = priority,
                Status = status,
                CreatedAt = DateTime.UtcNow
            };

            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(cancellationToken);

            await _evaluationEngine.RecalculatePlanAsync(targetPlan.Id, cancellationToken);
            return new TaskCreateResult(TaskCreateError.None, Task: task);
        }

        public async Task<TaskEntity> UpdateTaskAsync(long taskId, Action<TaskEntity> applyChanges, long technicianId, UserContext user, int timezoneOffsetMinutes, string? objectName, string? objectAddress, CancellationToken cancellationToken = default)
        {
            var task = await _db.Tasks
                .Include(t => t.Plan)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                throw new InvalidOperationException($"Task {taskId} was not found.");
            }

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit)
            {
                throw new InvalidOperationException("Neturite teisiu redaguoti sios uzduoties.");
            }

            if (technicianId != task.TechnicianId)
            {
                var targetTech = await _db.Technicians
                    .AsNoTracking()
                    .Select(t => new { t.Id, t.RegionId })
                    .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);

                if (targetTech == null)
                {
                    throw new InvalidOperationException("Technikas nerastas.");
                }

                task.TechnicianId = targetTech.Id;
                task.RegionId = targetTech.RegionId;
            }

            applyChanges(task);

            // Find or create object if objectName/objectAddress provided
            task.ObjectId = await FindOrCreateObjectAsync(objectName, objectAddress, task.RegionId, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
            if (task.PlanId.HasValue)
            {
                await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
            }
            return task;
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

            task.TechnicianId = targetTechnicianId;
            task.RegionId = validation.RegionId;
            task.StartAt = validation.StartAt;
            task.EndAt = validation.EndAt;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskMoveResult(TaskMoveError.Stale, "Uzduotis pasikeite (stale). Atnaujinkite lentele ir bandykite dar karta.", CurrentVersion: currentVersion);
            }

            if (task.PlanId.HasValue)
            {
                await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
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

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit || !_timeService.IsTaskStillEditable(task.EndAt))
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

            task.StartAt = validation.StartAt;
            task.EndAt = validation.EndAt;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new TaskTimeUpdateResult(TaskTimeUpdateError.Stale, "Uzduotis pasikeite (stale).", CurrentVersion: currentVersion);
            }

            if (task.PlanId.HasValue)
            {
                await AdjustPlanSpanAsync(task.Plan!, cancellationToken);
                await _evaluationEngine.RecalculatePlanAsync(task.PlanId.Value, cancellationToken);
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

            var canEdit = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffsetMinutes);
            if (!canEdit || !_timeService.IsTaskStillEditable(task.EndAt))
            {
                _logger.LogWarning("svc.task.delete.not-editable taskId={TaskId}", taskId);
                return new TaskDeleteResult(TaskDeleteError.NotEditable, "Neturite teisiu istrinti sios uzduoties.");
            }

            var planId = task.PlanId;
            var plan = task.Plan;

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
                .Include(t => t.Object)
                .Include(t => t.Region)
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
                        ObjectId = sourceTask.ObjectId, // Preserve object reference
                        RegionId = sourceTask.RegionId,
                        Title = sourceTask.Title,
                        Location = sourceTask.Location,
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
