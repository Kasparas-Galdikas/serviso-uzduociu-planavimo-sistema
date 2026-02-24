using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Centralized workload metric calculator used by evaluation, previews, and generators.
    /// Produces a LoadScoreContext plus conflict/switch metadata from a technician snapshot.
    ///
    /// Workload calculation rules:
    /// - InProgress tasks: Counted in workload (scheduled and pending)
    /// - Conflict tasks: Counted in workload (technician reported issue but task still occupies time)
    /// - Completed tasks: Counted in workload (work was done, time was occupied)
    /// - Canceled tasks: EXCLUDED from workload (task didn't happen, time is freed)
    /// </summary>
    public static class WorkloadCalculator
    {
        private const double DefaultDailyLimit = 8d;
        private const double DefaultWeeklyLimit = 40d;

        /// <summary>
        /// Determines if a task should count towards workload calculation.
        /// Only Canceled tasks are excluded - all other statuses count towards workload.
        /// </summary>
        public static bool CountsTowardsWorkload(ScheduleTaskStatus status) =>
            status != ScheduleTaskStatus.Canceled;

        public static WorkloadSnapshot Evaluate(
            TechnicianEntity technician,
            List<TaskEntity> dayTasks,
            List<TaskEntity> weekTasks,
            IReadOnlyDictionary<long, int>? travelMinutesByTaskId = null)
        {
            // Ensure deterministic ordering for conflict detection.
            dayTasks = dayTasks.OrderBy(t => t.StartAt).ToList();
            weekTasks = weekTasks.OrderBy(t => t.StartAt).ToList();

            // Filter tasks that count towards workload (exclude only Canceled)
            var activeDayTasks = dayTasks.Where(t => CountsTowardsWorkload(t.Status)).ToList();
            var activeWeekTasks = weekTasks.Where(t => CountsTowardsWorkload(t.Status)).ToList();

            var dailyLimit = technician.DailyLimit > 0 ? (double)technician.DailyLimit : DefaultDailyLimit;
            var weeklyLimit = technician.WeeklyLimit > 0 ? (double)technician.WeeklyLimit : DefaultWeeklyLimit;

            // Calculate task duration hours from active tasks (not Canceled)
            var dailyTaskHours = activeDayTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var weeklyTaskHours = activeWeekTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);

            // Calculate travel time hours from active tasks (source: travel_routes)
            var dailyTravelHours = activeDayTasks.Sum(t =>
                travelMinutesByTaskId != null && travelMinutesByTaskId.TryGetValue(t.Id, out var minutes)
                    ? minutes / 60.0
                    : 0.0);
            var weeklyTravelHours = activeWeekTasks.Sum(t =>
                travelMinutesByTaskId != null && travelMinutesByTaskId.TryGetValue(t.Id, out var minutes)
                    ? minutes / 60.0
                    : 0.0);

            // Total workload includes both task duration and travel time
            var dailyHours = dailyTaskHours + dailyTravelHours;
            var weeklyHours = weeklyTaskHours + weeklyTravelHours;
            var dailyOver = Math.Max(0d, dailyHours - dailyLimit);
            var weeklyOver = Math.Max(0d, weeklyHours - weeklyLimit);

            // Conflict detection from active tasks
            var conflicts = DetectConflicts(activeDayTasks);

            var context = new LoadScoreContext
            {
                HasConflict = conflicts.Count > 0,
                IsDailyLimitExceeded = dailyOver > 0,
                IsWeeklyLimitExceeded = weeklyOver > 0,
                DailyOverHours = dailyOver,
                WeeklyOverHours = weeklyOver,
                TravelTimeHours = dailyTravelHours,
                DailyUtilization = dailyLimit > 0 ? dailyHours / dailyLimit : 1d,
                WeeklyUtilization = weeklyLimit > 0 ? weeklyHours / weeklyLimit : 1d
            };

            return new WorkloadSnapshot(context, conflicts, dailyHours, weeklyHours);
        }

        /// <summary>
        /// Detects tasks that cannot be reached in time given stored travel durations.
        /// Checks consecutive task pairs: the gap between prev.EndAt and next.StartAt must be
        /// >= the stored travel time to reach next from prev.
        /// Returns a map of task ID → (RequiredMinutes, AvailableMinutes) for each infeasible task.
        /// </summary>
        public static Dictionary<long, (int Required, int Available)> DetectTravelViolations(
            List<TaskEntity> orderedActiveDayTasks,
            IReadOnlyDictionary<long, int> travelMinutesByTaskId)
        {
            var violations = new Dictionary<long, (int Required, int Available)>();

            for (var i = 1; i < orderedActiveDayTasks.Count; i++)
            {
                var task = orderedActiveDayTasks[i];
                var prev = orderedActiveDayTasks[i - 1];

                if (!travelMinutesByTaskId.TryGetValue(task.Id, out var required) || required == 0)
                {
                    continue;
                }

                var available = (int)(task.StartAt - prev.EndAt).TotalMinutes;
                if (required > available)
                {
                    violations[task.Id] = (required, available);
                }
            }

            return violations;
        }

        public static HashSet<long> DetectConflicts(List<TaskEntity> orderedTasks)
        {
            var conflicts = new HashSet<long>();
            if (orderedTasks.Count < 2)
            {
                return conflicts;
            }

            var active = orderedTasks[0];
            for (var i = 1; i < orderedTasks.Count; i++)
            {
                var current = orderedTasks[i];
                if (current.StartAt < active.EndAt)
                {
                    conflicts.Add(active.Id);
                    conflicts.Add(current.Id);

                    if (current.EndAt > active.EndAt)
                    {
                        active = current;
                    }
                }
                else
                {
                    active = current;
                }
            }

            return conflicts;
        }
    }

    public sealed record WorkloadSnapshot(
        LoadScoreContext Context,
        HashSet<long> Conflicts,
        double DailyHours,
        double WeeklyHours);
}
