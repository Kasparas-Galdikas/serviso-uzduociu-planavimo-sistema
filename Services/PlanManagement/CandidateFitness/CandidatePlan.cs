using System;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Represents a hypothetical plan modification suggestion.
    /// This is NOT a persisted entity - only used for analysis and planner recommendations.
    ///
    /// Design philosophy:
    /// - Flexible: supports any PlanChange type (not just concrete fields)
    /// - Scored: ranked by actual improvement calculation
    /// - Auditable: includes original vs. candidate state
    /// </summary>
    public sealed class CandidatePlan
    {
        /// <summary>
        /// Unique identifier for the target task being optimized.
        /// </summary>
        public long TargetTaskId { get; init; }

        /// <summary>
        /// The specific change being suggested.
        /// Can be TimeShiftChange, TechnicianSwapChange, DayShiftChange, etc.
        /// </summary>
        public PlanChange Change { get; init; } = null!;

        /// <summary>
        /// Final load state after applying this modification.
        /// Conflict = hard constraint violation (allowed but flagged)
        /// Load = high utilization (> 80%)
        /// Normal = acceptable utilization
        /// </summary>
        public LoadState LoadState { get; init; }

        /// <summary>
        /// Optimization score after change: lower is better.
        /// Negative score = hard constraint violation.
        /// Used for ranking candidates.
        /// </summary>
        public double CandidateScore { get; init; }

        /// <summary>
        /// Current plan's load state before modification.
        /// Used for comparison and confidence calculation.
        /// </summary>
        public LoadState OriginalLoadState { get; init; }

        /// <summary>
        /// Current plan's score before modification.
        /// Used for confidence calculation: confidence = (originalScore - candidateScore) / originalScore
        /// </summary>
        public double OriginalScore { get; init; }

        /// <summary>
        /// Number of tasks affected by this modification.
        /// </summary>
        public int AffectedTasksCount { get; init; }

        /// <summary>
        /// IDs of affected tasks (for detailed view in UI).
        /// </summary>
        public long[] AffectedTaskIds { get; init; } = Array.Empty<long>();

        /// <summary>
        /// Confidence level of this suggestion (0.0-1.0).
        /// Derived as: (originalScore - candidateScore) / originalScore
        /// - 1.0 = 100% improvement
        /// - 0.5 = 50% improvement
        /// - 0.0 = no improvement
        /// </summary>
        public double ConfidenceLevel
        {
            get
            {
                if (OriginalScore <= 0)
                    return 0.0;

                var improvement = (OriginalScore - CandidateScore) / OriginalScore;
                return Math.Clamp(improvement, 0.0, 1.0);
            }
        }

        /// <summary>
        /// When this suggestion was generated.
        /// </summary>
        public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Human-readable description (delegated to Change object).
        /// </summary>
        public string Description => Change?.Description ?? "Unknown modification";

        /// <summary>
        /// Reasoning behind suggestion (delegated to Change object).
        /// </summary>
        public string Rationale => Change?.Rationale ?? string.Empty;
    }
}
