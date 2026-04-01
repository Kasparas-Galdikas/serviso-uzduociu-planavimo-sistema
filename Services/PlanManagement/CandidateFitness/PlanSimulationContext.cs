using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// In-memory snapshot of a plan state for simulation purposes.
    ///
    /// Used by AssignmentFitnessEngine to:
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

        private readonly IScheduleTimeService _timeService;
        private readonly Dictionary<(long From, long To), int>? _travelMinutes;

        private PlanSimulationContext(
            IReadOnlyList<TaskEntity> tasks,
            IReadOnlyList<TechnicianEntity> technicians,
            TaskEntity targetTask,
            LoadScoreContext originalState,
            IScheduleTimeService timeService,
            Dictionary<(long From, long To), int>? travelMinutes = null)
        {
            Tasks = tasks;
            Technicians = technicians;
            TargetTask = targetTask;
            OriginalState = originalState;
            _timeService = timeService;
            _travelMinutes = travelMinutes;
        }

        /// <summary>
        /// Creates simulation context for initial state.
        /// </summary>
        public static PlanSimulationContext Create(
            IEnumerable<TaskEntity> allTasks,
            IEnumerable<TechnicianEntity> allTechnicians,
            TaskEntity targetTask,
            IScheduleTimeService timeService,
            Dictionary<(long From, long To), int>? travelMinutes = null)
        {
            if (timeService == null)
                throw new ArgumentNullException(nameof(timeService));

            var tasksList = allTasks.ToList();
            var techList = allTechnicians.ToList();

            var originalState = EvaluateSnapshotState(targetTask, tasksList, techList, timeService, travelMinutes);

            return new PlanSimulationContext(
                tasksList.AsReadOnly(),
                techList.AsReadOnly(),
                targetTask,
                originalState,
                timeService,
                travelMinutes);
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
                OriginalState,
                _timeService,
                _travelMinutes);
        }

        /// <summary>
        /// Evaluates the current state of the target task.
        /// Returns a LoadScoreContext with real metrics.
        /// </summary>
        public LoadScoreContext EvaluateCurrentState()
        {
            return EvaluateSnapshotState(TargetTask, Tasks.ToList(), Technicians.ToList(), _timeService, _travelMinutes);
        }

        /// <summary>
        /// Computes LoadScoreContext for a given technician/day/week snapshot.
        /// Shared helper to keep drag-preview and simulation/evaluation logic consistent.
        /// </summary>
        public static LoadScoreContext EvaluateTechnicianSnapshot(
            long technicianId,
            DateTime dayStart,
            DateTime weekStart,
            List<TaskEntity> allTasks,
            List<TechnicianEntity> allTechnicians,
            Dictionary<(long From, long To), int>? travelMinutes = null)
        {
            var technician = allTechnicians.FirstOrDefault(t => t.Id == technicianId);
            if (technician == null)
                throw new InvalidOperationException($"Technician {technicianId} not found");

            var dayEnd = dayStart.AddDays(1);
            var weekEnd = weekStart.AddDays(7);

            var dayTasks = allTasks
                .Where(t => t.TechnicianId == technicianId &&
                           t.StartAt >= dayStart &&
                           t.StartAt < dayEnd)
                .OrderBy(t => t.StartAt)
                .ToList();

            var weekTasks = allTasks
                .Where(t => t.TechnicianId == technicianId &&
                           t.StartAt >= weekStart &&
                           t.StartAt < weekEnd)
                .ToList();

            // Convert route-based travel dict to task-based if provided
            var travelMinutesByTaskId = ConvertTravelDictToTaskBased(technician, dayTasks, weekTasks, travelMinutes);
            var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
            return snapshot.Context;
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

            return new DayWorkload(
                TechnicianId: technicianId,
                DayStart: dayStart,
                Tasks: dayTasks.AsReadOnly(),
                DailyHours: dailyHours,
                DailyLimit: dailyLimit);
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
            List<TechnicianEntity> allTechnicians,
            IScheduleTimeService timeService,
            Dictionary<(long From, long To), int>? travelMinutes = null)
        {
            var dayStart = targetTask.StartAt.Date;
            var weekStart = timeService.GetWeekStart(dayStart);
            return EvaluateTechnicianSnapshot(
                targetTask.TechnicianId,
                dayStart,
                weekStart,
                allTasks,
                allTechnicians,
                travelMinutes);
        }

        /// <summary>
        /// Converts route-based travel minutes to task-based by simulating technician chains.
        /// Only used when travel dict is route-based (from ITravelTimeResolver).
        /// </summary>
        private static Dictionary<long, int>? ConvertTravelDictToTaskBased(
            TechnicianEntity technician,
            List<TaskEntity> dayTasks,
            List<TaskEntity> weekTasks,
            Dictionary<(long From, long To), int>? travelMinutesByRoute)
        {
            if (travelMinutesByRoute == null || travelMinutesByRoute.Count == 0)
                return null;

            var result = new Dictionary<long, int>();

            // Only process day tasks (workload is evaluated per day)
            TaskEntity? prevTask = null;
            foreach (var task in dayTasks.OrderBy(t => t.StartAt))
            {
                if (task.ServiceObject == null)
                {
                    result[task.Id] = 0;
                    prevTask = task;
                    continue;
                }

                var fromAddressId = prevTask != null
                    ? (prevTask.ServiceObject?.AddressId ?? technician.WorkplaceAddressId)
                    : technician.WorkplaceAddressId;
                var toAddressId = task.ServiceObject.AddressId;

                if (travelMinutesByRoute.TryGetValue((fromAddressId, toAddressId), out var travelMinute))
                {
                    result[task.Id] = travelMinute;
                }
                else
                {
                    result[task.Id] = 0;
                }

                prevTask = task;
            }

            return result;
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
        double DailyLimit)
    {
        public double Utilization => DailyLimit > 0 ? DailyHours / DailyLimit : 1.0;
        public bool IsOverloaded => DailyHours > DailyLimit;
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
