using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement.Interfaces;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{

    /// Generates candidate plan improvement suggestions.
    /// Strategy:
    /// 1. Load current plan snapshot from database (read-only)
    /// 2. Retrieve evaluation results for all tasks
    /// 3. Generate multiple candidate scenarios:
    ///    - Time Shift: Move task earlier/later in same day
    ///    - Technician Swap: Assign to different qualified technician
    ///    - Day Shift: Move to different day in time window
    /// 4. Evaluate each scenario using LoadScoreCalculator
    /// 5. Score and rank candidates (best first)
    /// 6. Return top N suggestions
    /// Does NOT modify database or apply changes.
    public sealed class TaskGenerator : ITaskGenerator
    {
        private readonly ScheduleDbContext _db;
        private readonly IEvaluationEngine _evaluationEngine;
        private readonly ILogger<TaskGenerator> _logger;
        private readonly IScheduleTimeService _timeService;

        public TaskGenerator(
            ScheduleDbContext db,
            IEvaluationEngine evaluationEngine,
            IScheduleTimeService timeService,
            ILogger<TaskGenerator> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _evaluationEngine = evaluationEngine ?? throw new ArgumentNullException(nameof(evaluationEngine));
            _timeService = timeService ?? throw new ArgumentNullException(nameof(timeService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<CandidatePlan>> GenerateCandidatesAsync(
            GeneratorContext context,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            _logger.LogInformation(
                "Generating candidates for task {TaskId} in scope {Scope}",
                context.TargetTaskId,
                context.Scope);

            try
            {
                // Load scoring weights (future: from config/DB)
                var weights = LoadScoreWeights.CreateDefault();

                // Load the target task
                var targetTask = await _db.Tasks
                    .AsNoTracking()
                    .Include(t => t.Technician)
                    .ThenInclude(t => t.User)
                    .Include(t => t.Evaluation)
                    .FirstOrDefaultAsync(t => t.Id == context.TargetTaskId, cancellationToken);

                if (targetTask == null)
                {
                    _logger.LogWarning("Target task {TaskId} not found", context.TargetTaskId);
                    return Array.Empty<CandidatePlan>();
                }

                // Load all tasks in relevant scope (same plan, around same time window)
                var planId = targetTask.PlanId;
                var scopeTasks = await LoadScopedTasksAsync(targetTask, context, planId, cancellationToken);

                // Load all technicians
                var allTechnicians = await _db.Technicians
                    .AsNoTracking()
                    .Include(t => t.User)
                    .ToListAsync(cancellationToken);

                // Compute original score using simulation for accurate baseline
                var originalScore = ComputeTaskScore(targetTask, scopeTasks, allTechnicians, weights);

                // Create simulation context for real evaluation
                var simulation = PlanSimulationContext.Create(scopeTasks, allTechnicians, targetTask, _timeService);

                // Generate candidate scenarios
                var candidates = new List<CandidatePlan>();

                // 1. Time shift candidates (same technician, same day)
                candidates.AddRange(GenerateTimeShiftCandidates(
                    targetTask, scopeTasks, context, originalScore, weights, simulation));

                // 2. Technician swap candidates (same time, different technician)
                candidates.AddRange(GenerateTechnicianSwapCandidates(
                    targetTask, scopeTasks, allTechnicians, context, originalScore, weights, simulation));

                // 3. Day shift candidates (different day, same technician)
                candidates.AddRange(GenerateDayShiftCandidates(
                    targetTask, scopeTasks, context, originalScore, weights, simulation));

                if (candidates.Count == 0)
                {
                    _logger.LogInformation("No candidate improvements found for task {TaskId}", context.TargetTaskId);
                    return Array.Empty<CandidatePlan>();
                }

                // Rank candidates by score and confidence
                var ranked = candidates
                    .OrderBy(c => c.CandidateScore)
                    .ThenByDescending(c => c.ConfidenceLevel)
                    .Where(c => c.ConfidenceLevel >= context.MinConfidence)
                    .Take(context.MaxCandidates)
                    .ToList();

                _logger.LogInformation(
                    "Generated {Count} ranked candidates for task {TaskId}",
                    ranked.Count,
                    context.TargetTaskId);

                return ranked;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating candidates for task {TaskId}", context.TargetTaskId);
                throw;
            }
        }

        private async Task<List<TaskEntity>> LoadScopedTasksAsync(
            TaskEntity targetTask,
            GeneratorContext context,
            long? planId,
            CancellationToken cancellationToken)
        {
            var dayStart = targetTask.StartAt.Date;
            var dayEnd = dayStart.AddDays(1);

            // Load tasks in the same plan, same day or nearby
            var query = _db.Tasks
                .AsNoTracking()
                .Include(t => t.Technician)
                .Include(t => t.Evaluation)
                .Where(t => t.PlanId == planId)
                .Where(t => t.StartAt >= dayStart.AddDays(-context.TimeWindowDays) &&
                            t.StartAt < dayEnd.AddDays(context.TimeWindowDays));

            return await query.ToListAsync(cancellationToken);
        }

        /// Generates time-shift candidates: move task earlier or later on the same day.
        /// Uses real simulation to compute actual scores.
        private List<CandidatePlan> GenerateTimeShiftCandidates(
            TaskEntity targetTask,
            List<TaskEntity> scopeTasks,
            GeneratorContext context,
            double originalScore,
            LoadScoreWeights weights,
            PlanSimulationContext simulation)
        {
            var candidates = new List<CandidatePlan>();
            var taskDuration = targetTask.EndAt - targetTask.StartAt;
            var dayStart = targetTask.StartAt.Date;
            var dayEnd = dayStart.AddDays(1);

            // Generate shift opportunities: -2h, -1h, +1h, +2h
            var shiftIntervals = new[] { -120, -60, 60, 120 }; // minutes

            foreach (var shiftMinutes in shiftIntervals)
            {
                var newStart = targetTask.StartAt.AddMinutes(shiftMinutes);
                var newEnd = newStart.Add(taskDuration);

                // Validate: stay within day boundary
                if (newStart < dayStart || newEnd > dayEnd)
                    continue;

                // Apply change to simulation
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
                            TechnicianId = targetTask.TechnicianId,
                            Title = targetTask.Title,
                            RegionId = targetTask.RegionId,
                            ServiceObjectId = targetTask.ServiceObjectId,
                            StartAt = newStart,
                            EndAt = newEnd,
                            Priority = targetTask.Priority,
                            Status = targetTask.Status,
                            CreatedAt = targetTask.CreatedAt,
                            Plan = targetTask.Plan,
                            Technician = targetTask.Technician,
                            Region = targetTask.Region,
                            Evaluation = targetTask.Evaluation
                        };
                    }
                    return modified;
                });

                // Evaluate modified state
                var candidateState = modifiedSim.EvaluateCurrentState();
                var candidateScore = LoadScoreCalculator.ComputeLoadScore(candidateState, weights);
                var confidence = LoadScoreCalculator.CalculateConfidence(originalScore, candidateScore);

                // Only include if meets confidence threshold
                if (confidence < context.MinConfidence)
                    continue;

                var change = new TimeShiftChange
                {
                    TargetTaskId = targetTask.Id,
                    SuggestedStartAt = newStart,
                    SuggestedEndAt = newEnd,
                    ShiftMinutes = shiftMinutes
                };

                var candidate = new CandidatePlan
                {
                    TargetTaskId = targetTask.Id,
                    Change = change,
                    OriginalLoadState = simulation.OriginalState.ComputeLoadState(),
                    LoadState = candidateState.ComputeLoadState(),
                    CandidateScore = candidateScore,
                    OriginalScore = originalScore,
                    AffectedTasksCount = 1,
                    AffectedTaskIds = new[] { targetTask.Id }
                };

                candidates.Add(candidate);
            }

            return candidates;
        }

        /// Generates technician-swap candidates: assign task to different technician.
        /// Uses real simulation to compute actual scores.
        private List<CandidatePlan> GenerateTechnicianSwapCandidates(
            TaskEntity targetTask,
            List<TaskEntity> scopeTasks,
            List<TechnicianEntity> allTechnicians,
            GeneratorContext context,
            double originalScore,
            LoadScoreWeights weights,
            PlanSimulationContext simulation)
        {
            var candidates = new List<CandidatePlan>();

            // Filter candidate technicians
            var candidateTechs = string.IsNullOrEmpty(context.CandidateTechnicianIds.ToString())
                ? allTechnicians
                : allTechnicians.Where(t => context.CandidateTechnicianIds.Contains(t.Id)).ToList();

            foreach (var tech in candidateTechs)
            {
                // Skip if same technician
                if (tech.Id == targetTask.TechnicianId)
                    continue;

                // Apply change to simulation
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
                            TechnicianId = tech.Id,  // Changed technician
                            Title = targetTask.Title,
                            RegionId = targetTask.RegionId,
                            ServiceObjectId = targetTask.ServiceObjectId,
                            StartAt = targetTask.StartAt,
                            EndAt = targetTask.EndAt,
                            Priority = targetTask.Priority,
                            Status = targetTask.Status,
                            CreatedAt = targetTask.CreatedAt,
                            Plan = targetTask.Plan,
                            Technician = tech,
                            Region = targetTask.Region,
                            Evaluation = targetTask.Evaluation
                        };
                    }
                    return modified;
                });

                // Evaluate modified state
                var candidateState = modifiedSim.EvaluateCurrentState();
                var candidateScore = LoadScoreCalculator.ComputeLoadScore(candidateState, weights);
                var confidence = LoadScoreCalculator.CalculateConfidence(originalScore, candidateScore);

                // Only include if meets confidence threshold
                if (confidence < context.MinConfidence)
                    continue;

                var technicianName = tech.User?.Name ?? $"Technician {tech.Id}";
                var change = new TechnicianSwapChange
                {
                    TargetTaskId = targetTask.Id,
                    SuggestedTechnicianId = tech.Id
                };

                var candidate = new CandidatePlan
                {
                    TargetTaskId = targetTask.Id,
                    Change = change,
                    OriginalLoadState = simulation.OriginalState.ComputeLoadState(),
                    LoadState = candidateState.ComputeLoadState(),
                    CandidateScore = candidateScore,
                    OriginalScore = originalScore,
                    AffectedTasksCount = 1,
                    AffectedTaskIds = new[] { targetTask.Id }
                };

                candidates.Add(candidate);
            }

            return candidates;
        }

        /// Generates day-shift candidates: move task to different day in time window.
        /// Uses real simulation to compute actual scores.
        private List<CandidatePlan> GenerateDayShiftCandidates(
            TaskEntity targetTask,
            List<TaskEntity> scopeTasks,
            GeneratorContext context,
            double originalScore,
            LoadScoreWeights weights,
            PlanSimulationContext simulation)
        {
            var candidates = new List<CandidatePlan>();
            var taskDuration = targetTask.EndAt - targetTask.StartAt;

            // Generate candidates for future days (1-5 days ahead)
            for (int daysAhead = 1; daysAhead <= Math.Min(context.TimeWindowDays, 5); daysAhead++)
            {
                var dayStart = targetTask.StartAt.Date.AddDays(daysAhead);
                var newStart = dayStart.Add(targetTask.StartAt.TimeOfDay);
                var newEnd = newStart.Add(taskDuration);

                // Apply change to simulation
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
                            TechnicianId = targetTask.TechnicianId,
                            Title = targetTask.Title,
                            RegionId = targetTask.RegionId,
                            ServiceObjectId = targetTask.ServiceObjectId,
                            StartAt = newStart,  // Changed day
                            EndAt = newEnd,      // Changed day
                            Priority = targetTask.Priority,
                            Status = targetTask.Status,
                            CreatedAt = targetTask.CreatedAt,
                            Plan = targetTask.Plan,
                            Technician = targetTask.Technician,
                            Region = targetTask.Region,
                            Evaluation = targetTask.Evaluation
                        };
                    }
                    return modified;
                });

                // Evaluate modified state
                var candidateState = modifiedSim.EvaluateCurrentState();
                var candidateScore = LoadScoreCalculator.ComputeLoadScore(candidateState, weights);
                var confidence = LoadScoreCalculator.CalculateConfidence(originalScore, candidateScore);

                // Only include if meets confidence threshold
                if (confidence < context.MinConfidence)
                    continue;

                var dayName = dayStart.DayOfWeek.ToString();
                var change = new DayShiftChange
                {
                    TargetTaskId = targetTask.Id,
                    SuggestedStartAt = newStart,
                    SuggestedEndAt = newEnd,
                    DaysShifted = daysAhead
                };

                var candidate = new CandidatePlan
                {
                    TargetTaskId = targetTask.Id,
                    Change = change,
                    OriginalLoadState = simulation.OriginalState.ComputeLoadState(),
                    LoadState = candidateState.ComputeLoadState(),
                    CandidateScore = candidateScore,
                    OriginalScore = originalScore,
                    AffectedTasksCount = 1,
                    AffectedTaskIds = new[] { targetTask.Id }
                };

                candidates.Add(candidate);
            }

            return candidates;
        }

        /// <summary>
        /// Helper: check if two time ranges overlap.
        /// </summary>
        private static bool HasTimeOverlap(
            DateTime start1, DateTime end1,
            DateTime start2, DateTime end2)
        {
            return start1 < end2 && end1 > start2;
        }

        /// <summary>
        /// Computes initial score for a task based on simulation.
        /// This is the baseline used to measure candidate improvement.
        /// Uses PlanSimulationContext to ensure accurate metrics from real data.
        /// </summary>
        private double ComputeTaskScore(
            TaskEntity task,
            List<TaskEntity> allTasks,
            List<TechnicianEntity> allTechnicians,
            LoadScoreWeights weights)
        {
            // Create a simulation to get real metrics
            var sim = PlanSimulationContext.Create(allTasks, allTechnicians, task, _timeService);
            var state = sim.EvaluateCurrentState();
            return LoadScoreCalculator.ComputeLoadScore(state, weights);
        }

        /// <summary>
        /// Fallback: Computes initial score from task evaluation if simulation not available.
        /// Used only when full context not available.
        /// </summary>
        private double ComputeTaskScore(TaskEntity task, LoadScoreWeights weights)
        {
            if (task.Evaluation == null)
                return 0.0;

            // Build a LoadScoreContext from existing evaluation
            var ctx = new LoadScoreContext
            {
                HasConflict = task.Evaluation.IsConflict,
                IsDailyLimitExceeded = task.Evaluation.IsOverloaded && task.Evaluation.Status == LoadState.Conflict,
                IsWeeklyLimitExceeded = false, // Would need full week context
                DailyOverHours = (double)(task.Evaluation.OverloadHours ?? 0),
                WeeklyOverHours = 0,
                TravelTimeHours = 0,
                DailyUtilization = task.Evaluation.IsOverloaded ? 0.9 : 0.6,
                WeeklyUtilization = task.Evaluation.IsOverloaded ? 0.85 : 0.5
            };

            return LoadScoreCalculator.ComputeLoadScore(ctx, weights);
        }
    }
}

