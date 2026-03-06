using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public class TaskEvaluationEngine : IEvaluationEngine
    {
        private readonly ScheduleDbContext _db;
        private readonly IScheduleTimeService _timeService;
        private readonly ILogger<TaskEvaluationEngine> _logger;

        public TaskEvaluationEngine(ScheduleDbContext db, IScheduleTimeService timeService, ILogger<TaskEvaluationEngine> logger)
        {
            _db = db;
            _timeService = timeService;
            _logger = logger;
        }

        public Task RecalculateTasksAsync(IEnumerable<long> taskIds, CancellationToken cancellationToken = default)
        {
            var ids = taskIds?.Distinct().ToList() ?? new List<long>();
            if (ids.Count == 0)
            {
                return Task.CompletedTask;
            }

            return RecalculateAsync(q => q.Where(t => ids.Contains(t.Id)), cancellationToken);
        }

        public Task RecalculatePlanAsync(long planId, CancellationToken cancellationToken = default) =>
            RecalculateAsync(q => q.Where(t => t.PlanId == planId), cancellationToken);

        private async Task RecalculateAsync(Func<IQueryable<TaskEntity>, IQueryable<TaskEntity>> scope, CancellationToken cancellationToken)
        {
            var tasks = await scope(_db.Tasks
                    .Include(t => t.Evaluation)
                    .Include(t => t.Technician)
                        .ThenInclude(t => t.WorkplaceAddress)
                    .Include(t => t.ServiceObject)
                        .ThenInclude(o => o.Address))
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            if (tasks.Count == 0)
            {
                return;
            }

            var techLookup = tasks
                .Select(t => t.Technician)
                .Where(t => t != null)
                .GroupBy(t => t!.Id)
                .ToDictionary(g => g.Key, g => g.First()!);
            var evaluations = new List<TaskEvaluationEntity>();
            var tracked = await _db.TaskEvaluations
                .Where(e => tasks.Select(t => t.Id).Contains(e.TaskId))
                .ToListAsync(cancellationToken);
            var trackedLookup = tracked.ToDictionary(e => e.TaskId);

            var tasksByTech = tasks
                .Where(t => t.Technician != null)
                .GroupBy(t => t.TechnicianId)
                .ToList();

            var conflictCount = 0;
            var overloadTotal = 0d;
            var travelMinutesByTaskId = await BuildTravelMinutesByTaskIdAsync(tasks, cancellationToken);

            foreach (var techGroup in tasksByTech)
            {
                if (!techLookup.TryGetValue(techGroup.Key, out var technician))
                {
                    continue;
                }

                var tasksByDay = techGroup
                    .OrderBy(t => t.StartAt)
                    .GroupBy(t => t.StartAt.Date)
                    .ToList();

                foreach (var dayGroup in tasksByDay)
                {
                    var dayTasks = dayGroup.OrderBy(t => t.StartAt).ToList();
                    var weekStart = _timeService.GetWeekStart(dayGroup.Key);
                    var weekEnd = weekStart.AddDays(7);
                    var weekTasks = techGroup
                        .Where(t => t.StartAt.Date >= weekStart && t.StartAt.Date < weekEnd)
                        .OrderBy(t => t.StartAt)
                        .ToList();

                    var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
                    var status = snapshot.Context.ComputeLoadState();
                    var overloadHours = Math.Max(snapshot.Context.DailyOverHours, snapshot.Context.WeeklyOverHours);
                    overloadTotal += overloadHours;

                    var conflictSet = snapshot.Conflicts;
                    conflictCount += conflictSet.Count;

                    foreach (var task in dayTasks)
                    {
                        var eval = trackedLookup.TryGetValue(task.Id, out var existing)
                            ? existing
                            : new TaskEvaluationEntity { TaskId = task.Id };
                        eval.OverloadHours = overloadHours > 0d ? (decimal)Math.Round(overloadHours, 2) : null;
                        eval.Status = status;

                        evaluations.Add(eval);
                    }
                }
            }

            // Upsert evaluations in one transaction.
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var existingIds = trackedLookup.Keys.ToHashSet();
            var toUpdate = evaluations.Where(e => existingIds.Contains(e.TaskId)).ToList();
            var toInsert = evaluations.Where(e => !existingIds.Contains(e.TaskId)).ToList();

            if (toUpdate.Count > 0)
            {
                _db.TaskEvaluations.UpdateRange(toUpdate);
            }

            if (toInsert.Count > 0)
            {
                await _db.TaskEvaluations.AddRangeAsync(toInsert, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("svc.eval.recalculate tasks={Tasks} techs={Techs} conflicts={Conflicts} overloadHours={OverloadHours}", tasks.Count, tasksByTech.Count, conflictCount, overloadTotal);
        }

        private async Task<Dictionary<long, int>> BuildTravelMinutesByTaskIdAsync(
            List<TaskEntity> tasks,
            CancellationToken cancellationToken)
        {
            var routes = await _db.TravelRoutes
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var routeLookup = routes.ToDictionary(r => (r.FromAddressId, r.ToAddressId), r => r);

            var result = new Dictionary<long, int>();
            var tasksByTech = tasks
                .Where(t => t.Technician != null && t.ServiceObject != null)
                .GroupBy(t => t.TechnicianId)
                .ToList();

            foreach (var techGroup in tasksByTech)
            {
                var technician = techGroup.First().Technician!;
                var workplaceAddressId = technician.WorkplaceAddressId;

                foreach (var dayGroup in techGroup.OrderBy(t => t.StartAt).GroupBy(t => t.StartAt.Date))
                {
                    TaskEntity? prev = null;
                    foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                    {
                        var fromAddressId = prev != null
                            ? prev.ServiceObject!.AddressId
                            : workplaceAddressId;
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
            }

            return result;
        }
    }
}
