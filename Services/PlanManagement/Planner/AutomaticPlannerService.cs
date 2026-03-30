using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Planner
{
    /// <summary>
    /// Automatically suggests the best technician and time slot for a task during bulk creation.
    /// 
    /// Uses the same scoring logic as TaskGenerator (WorkloadCalculator + LoadScoreCalculator)
    /// but simplified: instead of returning 5 alternative candidates, returns the single best fit.
    /// 
    /// The goal during bulk creation is to auto-assign technician + time, reducing manual work
    /// while maintaining consistent load balancing across the team.
    /// </summary>
    public class AutomaticPlannerService
    {
        private readonly ScheduleDbContext _db;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly IScheduleTimeService _timeService;
        private readonly IPlanService _planService;
        private readonly ILogger<AutomaticPlannerService> _logger;

        public AutomaticPlannerService(
            ScheduleDbContext db,
            ITravelTimeResolver travelResolver,
            IScheduleTimeService timeService,
            IPlanService planService,
            ILogger<AutomaticPlannerService> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _travelResolver = travelResolver ?? throw new ArgumentNullException(nameof(travelResolver));
            _timeService = timeService ?? throw new ArgumentNullException(nameof(timeService));
            _planService = planService ?? throw new ArgumentNullException(nameof(planService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Suggests the best technician for a task based on current workload and constraints.
        /// Returns a single best-fit technician ID.
        /// </summary>
        public Task<long> GetBestTechnicianAsync(
            TaskEntity newTask,
            List<TechnicianEntity> allTechs,
            List<TaskEntity> existingTasksInPlan,
            Dictionary<(long From, long To), int>? travelMinutesByRoute = null,
            CancellationToken cancellationToken = default)
        {
            if (newTask == null)
                throw new ArgumentNullException(nameof(newTask));
            if (allTechs == null || allTechs.Count == 0)
                throw new ArgumentException("Technician list cannot be empty", nameof(allTechs));
            if (existingTasksInPlan == null)
                throw new ArgumentNullException(nameof(existingTasksInPlan));

            _logger.LogInformation(
                "svc.plan.auto.best-tech.start taskId={TaskId} planId={PlanId} techCount={TechCount} existingTaskCount={ExistingTaskCount}",
                newTask.Id,
                newTask.PlanId,
                allTechs.Count,
                existingTasksInPlan.Count);

            var dayStart = newTask.StartAt.Date;
            var weekStart = _timeService.GetWeekStart(dayStart);
            var weights = LoadScoreWeights.CreateDefault();

            var scores = new Dictionary<long, double>();

            // Build travelMinutesByTaskId from route-based travelMinutesByRoute
            var travelMinutesByTaskId = BuildTravelMinutesByTaskId(
                allTechs, 
                existingTasksInPlan,
                travelMinutesByRoute);

            _logger.LogDebug(
                "svc.plan.auto.best-tech.travel-map taskId={TaskId} mappedTaskCount={MappedTaskCount} routeCount={RouteCount}",
                newTask.Id,
                travelMinutesByTaskId.Count,
                travelMinutesByRoute?.Count ?? 0);

            foreach (var tech in allTechs)
            {
                _logger.LogDebug(
                    "svc.plan.auto.best-tech.evaluate taskId={TaskId} technicianId={TechnicianId}",
                    newTask.Id,
                    tech.Id);

                // Build simulated task list with this tech assigned
                var simulatedTasks = existingTasksInPlan.ToList();
                simulatedTasks.Add(new TaskEntity
                {
                    Id = newTask.Id,
                    PlanId = newTask.PlanId,
                    TechnicianId = tech.Id,
                    Title = newTask.Title,
                    ServiceObjectId = newTask.ServiceObjectId,
                    StartAt = newTask.StartAt,
                    EndAt = newTask.EndAt,
                    Priority = newTask.Priority,
                    Status = newTask.Status,
                    ServiceObject = newTask.ServiceObject,
                    Technician = tech
                });

                // Get day and week tasks for this technician
                var dayTasks = simulatedTasks
                    .Where(t => t.TechnicianId == tech.Id &&
                               t.StartAt >= dayStart &&
                               t.StartAt < dayStart.AddDays(1))
                    .OrderBy(t => t.StartAt)
                    .ToList();

                var weekTasks = simulatedTasks
                    .Where(t => t.TechnicianId == tech.Id &&
                               t.StartAt >= weekStart &&
                               t.StartAt < weekStart.AddDays(7))
                    .OrderBy(t => t.StartAt)
                    .ToList();

                // Evaluate workload with travel time (centralized resolver)
                var snapshot = WorkloadCalculator.Evaluate(tech, dayTasks, weekTasks, travelMinutesByTaskId);
                var loadScore = LoadScoreCalculator.ComputeLoadScore(snapshot.Context, weights);

                scores[tech.Id] = loadScore;

                _logger.LogDebug(
                    "svc.plan.auto.best-tech.score taskId={TaskId} technicianId={TechnicianId} score={Score} dayTaskCount={DayTaskCount} weekTaskCount={WeekTaskCount}",
                    newTask.Id,
                    tech.Id,
                    loadScore,
                    dayTasks.Count,
                    weekTasks.Count);
            }

            // Return tech with best (lowest) score
            var bestTechId = scores.OrderBy(x => x.Value).First().Key;

            _logger.LogInformation(
                "svc.plan.auto.best-tech.selected taskId={TaskId} technicianId={TechnicianId} score={Score}",
                newTask.Id,
                bestTechId,
                scores[bestTechId]);

            return Task.FromResult(bestTechId);
        }

        /// <summary>
        /// Converts route-based travel minutes to task-based by simulating technician chains.
        /// This matches the logic in TaskEvaluationEngine.BuildTravelMinutesByTaskIdAsync.
        /// </summary>
        private Dictionary<long, int> BuildTravelMinutesByTaskId(
            List<TechnicianEntity> allTechs,
            List<TaskEntity> tasks,
            Dictionary<(long From, long To), int>? travelMinutesByRoute = null)
        {
            if (travelMinutesByRoute == null || travelMinutesByRoute.Count == 0)
                return new Dictionary<long, int>();

            var result = new Dictionary<long, int>();
            
            // Group tasks by technician and day
            foreach (var tech in allTechs)
            {
                var techTasks = tasks
                    .Where(t => t.TechnicianId == tech.Id && t.ServiceObject != null)
                    .OrderBy(t => t.StartAt)
                    .GroupBy(t => t.StartAt.Date)
                    .ToList();

                foreach (var dayGroup in techTasks)
                {
                    TaskEntity? prevTask = null;
                    foreach (var task in dayGroup.OrderBy(t => t.StartAt))
                    {
                        var fromAddressId = prevTask != null
                            ? prevTask.ServiceObject!.AddressId
                            : tech.WorkplaceAddressId;
                        var toAddressId = task.ServiceObject!.AddressId;

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
                }
            }

            return result;
        }

        /// <summary>
        /// Suggests the best technician AND time slot for a task during bulk creation.
        /// Returns technician ID; caller must use IPlanService.GetSuggestedNextSlotAsync for time.
        /// </summary>
        public async Task<AutomaticAssignmentSuggestion> SuggestTechAndTimeAsync(
            TaskEntity newTask,
            long planId,
            List<TechnicianEntity> allTechs,
            List<TaskEntity> existingTasksInPlan,
            DateTime targetDate,
            CancellationToken cancellationToken = default)
        {
            if (newTask == null)
                throw new ArgumentNullException(nameof(newTask));
            if (newTask.ServiceObject == null)
                throw new ArgumentException("Task must have ServiceObject loaded", nameof(newTask));
            if (allTechs == null || allTechs.Count == 0)
                throw new ArgumentException("Technician list cannot be empty", nameof(allTechs));
            if (existingTasksInPlan == null)
                throw new ArgumentNullException(nameof(existingTasksInPlan));

            _logger.LogInformation(
                "svc.plan.auto.suggest.start taskId={TaskId} planId={PlanId} targetDate={TargetDate} techCount={TechCount} existingTaskCount={ExistingTaskCount}",
                newTask.Id,
                planId,
                targetDate,
                allTechs.Count,
                existingTasksInPlan.Count);

            // Get travel times using centralized resolver (single source of truth)
            var allAddresses = existingTasksInPlan
                .Where(t => t.ServiceObject != null)
                .Select(t => t.ServiceObject!.AddressId)
                .Concat(allTechs.Select(t => t.WorkplaceAddressId))
                .Concat(new[] { newTask.ServiceObject.AddressId })
                .Distinct()
                .ToList();

            var travelMinutesByRoute = await _travelResolver.GetTravelMinutesAsync(allAddresses, cancellationToken);

            _logger.LogDebug(
                "svc.plan.auto.suggest.travel-routes taskId={TaskId} addressCount={AddressCount} routeCount={RouteCount}",
                newTask.Id,
                allAddresses.Count,
                travelMinutesByRoute.Count);

            // Get best technician using travel-aware scoring
            var bestTechId = await GetBestTechnicianAsync(
                newTask, 
                allTechs, 
                existingTasksInPlan, 
                travelMinutesByRoute,
                cancellationToken);

            // Return just the technician ID; caller will get time via GetSuggestedNextSlotAsync
            var result = new AutomaticAssignmentSuggestion
            {
                TechnicianId = bestTechId,
                StartAt = targetDate, // Placeholder; will be replaced by caller
                EndAt = targetDate.AddHours(1) // Placeholder; will be replaced by caller
            };

            _logger.LogInformation(
                "svc.plan.auto.suggest.success taskId={TaskId} technicianId={TechnicianId} startAt={StartAt} endAt={EndAt}",
                newTask.Id,
                bestTechId,
                result.StartAt,
                result.EndAt);

            return result;
        }
    }

    /// <summary>
    /// Result of automatic planning: suggested technician and time slot.
    /// </summary>
    public class AutomaticAssignmentSuggestion
    {
        /// <summary>
        /// ID of the best-fit technician.
        /// </summary>
        public long TechnicianId { get; set; }

        /// <summary>
        /// Suggested start time for the task.
        /// </summary>
        public DateTime StartAt { get; set; }

        /// <summary>
        /// Suggested end time for the task.
        /// </summary>
        public DateTime EndAt { get; set; }
    }
}
