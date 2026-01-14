using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// In-memory snapshot of a plan state for simulation purposes.
    /// 
    /// Used by TaskGenerator to:
    /// 1. Clone current tasks
    /// 2. Apply a proposed PlanChange
    /// 3. Recalculate metrics on the modified snapshot
    /// 4. Compute scores WITHOUT touching the database
    /// 
    /// This is immutable after creation - changes are applied via WithModifiedTasks().
    /// </summary>
    public sealed class PlanSimulationContext
    {
        /// <summary>
        /// All tasks in the scope (current state).
        /// </summary>
        public IReadOnlyList<TaskEntity> Tasks { get; }

        /// <summary>
        /// All technicians with their constraints.
        /// </summary>
        public IReadOnlyList<TechnicianEntity> Technicians { get; }

        /// <summary>
        /// The target task being modified.
        /// </summary>
        public TaskEntity TargetTask { get; }

        /// <summary>
        /// Original state before any changes (for comparison).
        /// </summary>
        public LoadScoreContext OriginalState { get; }

        private PlanSimulationContext(
            IReadOnlyList<TaskEntity> tasks,
            IReadOnlyList<TechnicianEntity> technicians,
            TaskEntity targetTask,
            LoadScoreContext originalState)
        {
            Tasks = tasks;
            Technicians = technicians;
            TargetTask = targetTask;
            OriginalState = originalState;
        }

        /// <summary>
        /// Creates simulation context for initial state.
        /// </summary>
        public static PlanSimulationContext Create(
            IEnumerable<TaskEntity> allTasks,
            IEnumerable<TechnicianEntity> allTechnicians,
            TaskEntity targetTask)
        {
            var tasksList = allTasks.ToList();
            var techList = allTechnicians.ToList();
            
            var originalState = EvaluateSnapshotState(targetTask, tasksList, techList);

            return new PlanSimulationContext(
                tasksList.AsReadOnly(),
                techList.AsReadOnly(),
                targetTask,
                originalState);
        }

        /// <summary>
        /// Creates a new simulation context with modified tasks.
        /// Used after applying a PlanChange.
        /// </summary>
        public PlanSimulationContext WithModifiedTasks(Func<List<TaskEntity>, List<TaskEntity>> modifier)
        {
            var modifiedTasks = modifier(Tasks.ToList());
            return new PlanSimulationContext(
                modifiedTasks.AsReadOnly(),
                Technicians,
                TargetTask,
                OriginalState);
        }

        /// <summary>
        /// Evaluates the current state of the target task.
        /// Returns a LoadScoreContext with real metrics.
        /// </summary>
        public LoadScoreContext EvaluateCurrentState()
        {
            return EvaluateSnapshotState(TargetTask, Tasks.ToList(), Technicians.ToList());
        }

        /// <summary>
        /// Evaluates a specific technician's workload on a given day.
        /// </summary>
        public DayWorkload GetTechnicianDayWorkload(long technicianId, DateTime dayStart)
        {
            var dayEnd = dayStart.AddDays(1);
            var dayTasks = Tasks
                .Where(t => t.TechnicianId == technicianId &&
                           t.StartAt >= dayStart &&
                           t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var technician = Technicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
                throw new InvalidOperationException($"Technician {technicianId} not found");

            var dailyHours = dayTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var dailyLimit = (double)technician.DailyLimit;
            var switchCount = CalculateSwitchCount(dayTasks);

            return new DayWorkload(
                TechnicianId: technicianId,
                DayStart: dayStart,
                Tasks: dayTasks.AsReadOnly(),
                DailyHours: dailyHours,
                DailyLimit: dailyLimit,
                SwitchCount: switchCount,
                SwitchLimit: technician.TravelSwitchLimit);
        }

        /// <summary>
        /// Evaluates a specific technician's weekly workload.
        /// </summary>
        public WeeklyWorkload GetTechnicianWeeklyWorkload(long technicianId, DateTime weekStart)
        {
            var weekEnd = weekStart.AddDays(7);
            var weekTasks = Tasks
                .Where(t => t.TechnicianId == technicianId &&
                           t.StartAt >= weekStart &&
                           t.StartAt < weekEnd)
                .ToList();

            var technician = Technicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
                throw new InvalidOperationException($"Technician {technicianId} not found");

            var weeklyHours = weekTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var weeklyLimit = (double)technician.WeeklyLimit;

            return new WeeklyWorkload(
                TechnicianId: technicianId,
                WeekStart: weekStart,
                Tasks: weekTasks.AsReadOnly(),
                WeeklyHours: weeklyHours,
                WeeklyLimit: weeklyLimit);
        }

        /// <summary>
        /// Detects time conflicts for a task on a given day.
        /// </summary>
        public IReadOnlyList<TaskEntity> DetectConflictsForTask(TaskEntity task)
        {
            var conflicts = new List<TaskEntity>();
            var dayStart = task.StartAt.Date;
            var dayEnd = dayStart.AddDays(1);

            var dayTasks = Tasks
                .Where(t => t.Id != task.Id &&
                           t.TechnicianId == task.TechnicianId &&
                           t.StartAt >= dayStart &&
                           t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            foreach (var other in dayTasks)
            {
                if (task.StartAt < other.EndAt && task.EndAt > other.StartAt)
                {
                    conflicts.Add(other);
                }
            }

            return conflicts.AsReadOnly();
        }

        /// <summary>
        /// Internal: Evaluates state of a snapshot for a given target task.
        /// </summary>
        private static LoadScoreContext EvaluateSnapshotState(
            TaskEntity targetTask,
            List<TaskEntity> allTasks,
            List<TechnicianEntity> allTechnicians)
        {
            var technician = allTechnicians.FirstOrDefault(t => t.Id == targetTask.TechnicianId);
            if (technician == null)
                throw new InvalidOperationException($"Technician {targetTask.TechnicianId} not found");

            var dayStart = targetTask.StartAt.Date;
            var dayEnd = dayStart.AddDays(1);
            var weekStart = GetWeekStart(dayStart);
            var weekEnd = weekStart.AddDays(7);

            // Daily metrics
            var dayTasks = allTasks
                .Where(t => t.TechnicianId == targetTask.TechnicianId &&
                           t.StartAt >= dayStart &&
                           t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var dailyHours = dayTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var dailyLimit = (double)technician.DailyLimit;
            var dailyUtil = dailyLimit > 0 ? dailyHours / dailyLimit : 1.0;

            // Weekly metrics
            var weekTasks = allTasks
                .Where(t => t.TechnicianId == targetTask.TechnicianId &&
                           t.StartAt >= weekStart &&
                           t.StartAt < weekEnd)
                .ToList();

            var weeklyHours = weekTasks.Sum(t => (t.EndAt - t.StartAt).TotalHours);
            var weeklyLimit = (double)technician.WeeklyLimit;
            var weeklyUtil = weeklyLimit > 0 ? weeklyHours / weeklyLimit : 1.0;

            // Conflicts
            var hasConflict = dayTasks.Any(t => 
                t.Id != targetTask.Id &&
                targetTask.StartAt < t.EndAt && 
                targetTask.EndAt > t.StartAt);

            // Switches
            var switchCount = CalculateSwitchCount(dayTasks);
            var switchLimit = technician.TravelSwitchLimit;

            return new LoadScoreContext
            {
                HasConflict = hasConflict,
                IsDailyLimitExceeded = dailyHours > dailyLimit,
                IsWeeklyLimitExceeded = weeklyHours > weeklyLimit,
                DailyOverHours = Math.Max(0, dailyHours - dailyLimit),
                WeeklyOverHours = Math.Max(0, weeklyHours - weeklyLimit),
                SwitchCount = switchCount,
                IsSwitchLimitExceeded = switchCount > switchLimit,
                DailyUtilization = dailyUtil,
                WeeklyUtilization = weeklyUtil
            };
        }

        private static int CalculateSwitchCount(List<TaskEntity> dayTasks)
        {
            if (dayTasks.Count == 0) return 0;

            var switches = 0;
            var lastLocation = dayTasks[0].Location;

            for (int i = 1; i < dayTasks.Count; i++)
            {
                if (!string.Equals(dayTasks[i].Location, lastLocation, StringComparison.OrdinalIgnoreCase))
                {
                    switches++;
                }
                lastLocation = dayTasks[i].Location;
            }

            return switches;
        }

        private static DateTime GetWeekStart(DateTime date)
        {
            var dayOfWeek = date.DayOfWeek;
            var diff = dayOfWeek - DayOfWeek.Monday;
            if (diff < 0) diff += 7;
            return date.AddDays(-diff).Date;
        }
    }

    /// <summary>
    /// Daily workload snapshot for a technician.
    /// </summary>
    public sealed record DayWorkload(
        long TechnicianId,
        DateTime DayStart,
        IReadOnlyList<TaskEntity> Tasks,
        double DailyHours,
        double DailyLimit,
        int SwitchCount,
        int SwitchLimit)
    {
        public double Utilization => DailyLimit > 0 ? DailyHours / DailyLimit : 1.0;
        public bool IsOverloaded => DailyHours > DailyLimit;
        public bool SwitchesExceeded => SwitchCount > SwitchLimit;
    }

    /// <summary>
    /// Weekly workload snapshot for a technician.
    /// </summary>
    public sealed record WeeklyWorkload(
        long TechnicianId,
        DateTime WeekStart,
        IReadOnlyList<TaskEntity> Tasks,
        double WeeklyHours,
        double WeeklyLimit)
    {
        public double Utilization => WeeklyLimit > 0 ? WeeklyHours / WeeklyLimit : 1.0;
        public bool IsOverloaded => WeeklyHours > WeeklyLimit;
    }
}
