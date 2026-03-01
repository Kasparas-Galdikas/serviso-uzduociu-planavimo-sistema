using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// Encapsulates scoring metrics for a candidate plan scenario.
    /// Used to evaluate and rank hypothetical plan modifications.
    /// 
    /// This is an immutable snapshot of metrics used for scoring.
    /// It's created by EvaluationEngine and consumed by LoadScoreCalculator.
    public sealed class LoadScoreContext
    {
        /// Whether the scenario has scheduling conflicts (overlapping tasks).
        /// Hard constraint violation.
        public bool HasConflict { get; init; }

        /// Whether daily technician limit is exceeded.
        /// Hard constraint violation.
        public bool IsDailyLimitExceeded { get; init; }

        /// Whether weekly technician limit is exceeded.
        /// Hard constraint violation.
        public bool IsWeeklyLimitExceeded { get; init; }

        /// Number of hours exceeding daily limit for the technician.
        /// 0 if within limit.
        public double DailyOverHours { get; init; }

        /// Number of hours exceeding weekly limit for the technician.
        /// 0 if within limit.
        public double WeeklyOverHours { get; init; }

        /// Total travel time in hours for the workload assessment period.
        public double TravelTimeHours { get; init; }

        /// Daily utilization ratio (daily hours / daily limit).
        /// Values > 1.0 indicate overload.
        public double DailyUtilization { get; init; }

        /// Weekly utilization ratio (weekly hours / weekly limit).
        /// Values > 1.0 indicate overload.
        public double WeeklyUtilization { get; init; }

        /// Computes the LoadState (hard/soft constraint status) for this context.
        public LoadState ComputeLoadState()
        {
            // Hard constraint violations = Conflict
            if (HasConflict ||
                IsDailyLimitExceeded ||
                IsWeeklyLimitExceeded)
            {
                return LoadState.Conflict;
            }

            // High utilization (>80%) = Load
            if (DailyUtilization > 0.8 || WeeklyUtilization > 0.8)
            {
                return LoadState.Load;
            }

            // Otherwise normal
            return LoadState.Normal;
        }
    }
}
