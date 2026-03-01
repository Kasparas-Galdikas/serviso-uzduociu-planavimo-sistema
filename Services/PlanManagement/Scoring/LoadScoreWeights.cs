namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// Configurable weights for LoadScore calculation.
    /// Controls how much different constraint violations/improvements affect the final score.
    /// 
    /// Future sources:
    /// - appsettings.json (environment-specific)
    /// - Database (runtime reconfigurable)
    /// - Strategy profiles ("aggressive", "conservative", "balanced")
    public sealed class LoadScoreWeights
    {
        // Hard constraint penalties (negative scores)
        public double ConflictPenalty { get; init; } = -1000.0;
        public double DailyOverPenalty { get; init; } = -500.0;
        public double WeeklyOverPenalty { get; init; } = -300.0;

        // Per-hour overage penalties
        public double DailyOverHoursPenalty { get; init; } = -50.0;
        public double WeeklyOverHoursPenalty { get; init; } = -30.0;

        // Soft constraint penalties (utilization-based)
        public double LowUtilizationPenalty { get; init; } = -10.0;  // < 30%
        public double MediumUtilizationPenalty { get; init; } = -10.0; // 50-75%
        public double HighUtilizationPenalty { get; init; } = -25.0;   // 75-90%
        public double VeryHighUtilizationPenalty { get; init; } = -50.0; // 90-100%

        // Weighting for multi-objective optimization
        public double DailyLoadWeight { get; init; } = 1.5;
        public double WeeklyLoadWeight { get; init; } = 1.5;
        public double UtilizationWeight { get; init; } = 1.0;

        /// Creates default balanced weights suitable for typical planning scenarios.
        public static LoadScoreWeights CreateDefault() => new();

        /// Creates aggressive weights prioritizing daily load reduction.
        /// Useful when daily overload is critical concern.
        public static LoadScoreWeights CreateAggressive() => new()
        {
            DailyLoadWeight = 2.0,
            WeeklyLoadWeight = 1.0,
            ConflictPenalty = -2000.0,
            DailyOverPenalty = -1000.0
        };

        /// Creates conservative weights prioritizing stability and minimal changes.
        /// Useful when system is heavily loaded.
        public static LoadScoreWeights CreateConservative() => new()
        {
            DailyLoadWeight = 1.2,
            WeeklyLoadWeight = 2.0,
            ConflictPenalty = -500.0,
            DailyOverPenalty = -300.0
        };
    }
}
