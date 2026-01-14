using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using Microsoft.Extensions.Logging;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public class TaskEvaluationEngine : IEvaluationEngine
    {
        private readonly ScheduleDbContext _db;
        private readonly ILogger<TaskEvaluationEngine> _logger;

        public TaskEvaluationEngine(ScheduleDbContext db, ILogger<TaskEvaluationEngine> logger)
        {
            _db = db;
            _logger = logger;
        }

        private sealed class LoadScoreContext
        {
            public bool HasConflict { get; init; }
            public bool IsDailyLimitExceeded { get; init; }
            public bool IsWeeklyLimitExceeded { get; init; }

            public double DailyOverHours { get; init; }
            public double WeeklyOverHours { get; init; }

            public int SwitchCount { get; init; }

            public double DailyUtil { get; init; }
            public double WeeklyUtil { get; init; }
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
                    .Include(t => t.Technician))
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
            var now = DateTime.UtcNow;
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
            var switchExceeded = 0;
            foreach (var techGroup in tasksByTech)
            {
                if (!techLookup.TryGetValue(techGroup.Key, out var technician))
                {
                    continue;
                }

                var dailyLimit = technician.DailyLimit > 0 ? (double)technician.DailyLimit : 8d;
                var weeklyLimit = technician.WeeklyLimit > 0 ? (double)technician.WeeklyLimit : 40d;
                var switchLimit = technician.TravelSwitchLimit < 0 ? 0 : technician.TravelSwitchLimit;

                var tasksByDay = techGroup
                    .OrderBy(t => t.StartAt)
                    .GroupBy(t => t.StartAt.Date)
                    .ToList();

                var weeklyHours = techGroup.Sum(t => (t.EndAt - t.StartAt).TotalHours);
                var weeklyOver = Math.Max(0d, weeklyHours - weeklyLimit);
                var isWeeklyLimitExceeded = weeklyOver > 0d;
                var weeklyUtil = weeklyLimit > 0 ? weeklyHours / weeklyLimit : 1d;
                foreach (var dayGroup in tasksByDay)
                {
                    var dayTasks = dayGroup.OrderBy(t => t.StartAt).ToList();
                    var dailyHours = dayTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
                    var dailyOver = Math.Max(0d, dailyHours - dailyLimit);
                    var isDailyLimitExceeded = dailyOver > 0d;
                    var dailyUtil = dailyLimit > 0 ? dailyHours / dailyLimit : 1d;
                    var overloadHours = Math.Max(Math.Max(dailyOver, weeklyOver), 0d);
                    overloadTotal += (double)overloadHours;

                    var switchCount = CalculateSwitchCount(dayTasks);
                    var switchesExceeded = switchCount > switchLimit;
                    if (switchesExceeded) switchExceeded++;

                    var conflictSet = DetectConflicts(dayTasks);
                    conflictCount += conflictSet.Count;

                    foreach (var task in dayTasks)
                    {
                        var hasConflict = conflictSet.Contains(task.Id);
                        var status = EvaluateLoadState(
                            hasConflict,
                            isDailyLimitExceeded,
                            isWeeklyLimitExceeded,
                            dailyUtil,
                            weeklyUtil,
                            switchesExceeded);
                        var taskDuration = (task.EndAt - task.StartAt).TotalHours;

                        var eval = trackedLookup.TryGetValue(task.Id, out var existing)
                            ? existing
                            : new TaskEvaluationEntity { TaskId = task.Id };

                        eval.HasConflict = status == LoadState.Conflict;
                        eval.ConflictReason = hasConflict ? "Overlapping tasks" : null;
                        eval.DailyHours = (decimal)Math.Round(dailyHours, 2);
                        eval.WeeklyHours = (decimal)Math.Round(weeklyHours, 2);
                        eval.OverloadHours = overloadHours > 0d ? (decimal)Math.Round(overloadHours, 2) : null;
                        eval.SwitchCount = switchCount;
                        eval.Status = status;
                        eval.IsConflict = status == LoadState.Conflict;
                        eval.IsOverloaded = status != LoadState.Normal;
                        eval.TravelRequired = switchesExceeded;
                        eval.LoadHours = (decimal)Math.Round(taskDuration, 2);
                        eval.RiskLevel = status == LoadState.Conflict
                            ? RiskLevel.High
                            : status == LoadState.Load ? RiskLevel.Medium : (RiskLevel?)null;
                        eval.EvaluatedAt = now;

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

            _logger.LogInformation("svc.eval.recalculate tasks={Tasks} techs={Techs} conflicts={Conflicts} overloadHours={OverloadHours} switchesExceeded={SwitchExceeded}", tasks.Count, tasksByTech.Count, conflictCount, overloadTotal, switchExceeded);
        }

        private static LoadState EvaluateLoadState(
            bool hasConflict,
            bool isDailyLimitExceeded,
            bool isWeeklyLimitExceeded,
            double dailyUtil,
            double weeklyUtil,
            bool switchesExceeded)
        {
            // Hard constraints – planas sugeneruojamas, bet laikomas nepriimtinu
            if (hasConflict || isDailyLimitExceeded || isWeeklyLimitExceeded)
            {
                return LoadState.Conflict;
            }

            // Soft constraints – leidžiama, bet signalizuojama apkrova
            if (dailyUtil >= 0.5d || weeklyUtil >= 0.5d || switchesExceeded)
            {
                return LoadState.Load;
            }

            return LoadState.Normal;
        }

        private static double EvaluateLoadScore(LoadScoreContext ctx)
        {
            double score = 0;

            // HARD – huge penalties
            if (ctx.HasConflict) score += 1000;
            if (ctx.IsDailyLimitExceeded) score += 500;
            if (ctx.IsWeeklyLimitExceeded) score += 300;

            // SOFT – proportional penalties
            score += ctx.DailyOverHours * 20;
            score += ctx.WeeklyOverHours * 10;

            score += ctx.SwitchCount * 5;

            score += UtilPenalty(ctx.DailyUtil);
            score += UtilPenalty(ctx.WeeklyUtil);

            return score;
        }
        
        // Utilization penalty function
        private static double UtilPenalty(double util)
        {
            if (util > 1.0)
                return (util - 1.0) * 50;

            if (util < 0.3)
                return (0.3 - util) * 10;

            return 0;
        }

        private static HashSet<long> DetectConflicts(List<TaskEntity> tasks)
        {
            var conflicts = new HashSet<long>();

            if (tasks.Count < 2)
            {
                return conflicts;
            }

            // Last task that doesn't conflict
            var active = tasks[0];

            for (var i = 1; i < tasks.Count; i++)
            {
                var current = tasks[i];

                // if current starts before active ends, there's a conflict
                if (current.StartAt < active.EndAt)
                {
                    conflicts.Add(active.Id);
                    conflicts.Add(current.Id);

                    // keep the one that ends later as active
                    if (current.EndAt > active.EndAt)
                    {
                        active = current;
                    }
                }
                else
                {
                    // if no conflict, move active forward
                    active = current;
                }
            }

            return conflicts;
        }

        // Expects tasks ordered by StartAt ascending
        private static int CalculateSwitchCount(List<TaskEntity> tasks)
        {
            if (tasks.Count == 0)
            {
                return 0;
            }

            var switches = 0;
            var lastLocation = tasks[0].Location;

            for (var i = 1; i < tasks.Count; i++)
            {
                var current = tasks[i].Location;
                if (!string.Equals(current, lastLocation, StringComparison.OrdinalIgnoreCase))
                {
                    switches++;
                }
                lastLocation = current;
            }

            return switches;
        }
    }
}
