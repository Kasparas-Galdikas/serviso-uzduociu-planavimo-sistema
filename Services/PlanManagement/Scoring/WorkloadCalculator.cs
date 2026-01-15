using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Centralized workload metric calculator used by evaluation, previews, and generators.
    /// Produces a LoadScoreContext plus conflict/switch metadata from a technician snapshot.
    /// </summary>
    public static class WorkloadCalculator
    {
        private const double DefaultDailyLimit = 8d;
        private const double DefaultWeeklyLimit = 40d;

        public static WorkloadSnapshot Evaluate(
            TechnicianEntity technician,
            List<TaskEntity> dayTasks,
            List<TaskEntity> weekTasks)
        {
            // Ensure deterministic ordering for conflict/switch detection.
            dayTasks = dayTasks.OrderBy(t => t.StartAt).ToList();
            weekTasks = weekTasks.OrderBy(t => t.StartAt).ToList();

            var dailyLimit = technician.DailyLimit > 0 ? (double)technician.DailyLimit : DefaultDailyLimit;
            var weeklyLimit = technician.WeeklyLimit > 0 ? (double)technician.WeeklyLimit : DefaultWeeklyLimit;
            var switchLimit = technician.TravelSwitchLimit < 0 ? 0 : technician.TravelSwitchLimit;

            var dailyHours = dayTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var weeklyHours = weekTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var dailyOver = Math.Max(0d, dailyHours - dailyLimit);
            var weeklyOver = Math.Max(0d, weeklyHours - weeklyLimit);

            var switchCount = CalculateSwitchCount(dayTasks);
            var conflicts = DetectConflicts(dayTasks);

            var context = new LoadScoreContext
            {
                HasConflict = conflicts.Count > 0,
                IsDailyLimitExceeded = dailyOver > 0,
                IsWeeklyLimitExceeded = weeklyOver > 0,
                DailyOverHours = dailyOver,
                WeeklyOverHours = weeklyOver,
                SwitchCount = switchCount,
                IsSwitchLimitExceeded = switchCount > switchLimit,
                DailyUtilization = dailyLimit > 0 ? dailyHours / dailyLimit : 1d,
                WeeklyUtilization = weeklyLimit > 0 ? weeklyHours / weeklyLimit : 1d
            };

            return new WorkloadSnapshot(context, conflicts, dailyHours, weeklyHours, switchCount);
        }

        public static int CalculateSwitchCount(List<TaskEntity> orderedDayTasks)
        {
            if (orderedDayTasks.Count == 0)
            {
                return 0;
            }

            var switches = 0;
            var lastLocation = orderedDayTasks[0].Location;

            for (var i = 1; i < orderedDayTasks.Count; i++)
            {
                var current = orderedDayTasks[i].Location;
                if (!string.Equals(current, lastLocation, StringComparison.OrdinalIgnoreCase))
                {
                    switches++;
                }
                lastLocation = current;
            }

            return switches;
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
        double WeeklyHours,
        int SwitchCount);
}
