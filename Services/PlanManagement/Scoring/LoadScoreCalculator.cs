using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Computes LoadScore for candidate plan scenarios using configurable weights.
    /// 
    /// Scoring philosophy:
    /// - Hard constraints (conflicts, violations): Heavy negative penalties
    /// - Soft constraints (utilization): Proportional penalties
    /// - Lower score = better candidate
    /// - Negative scores = constraint violations (allowed but flagged)
    /// 
    /// Design:
    /// - Weights are injected (can come from config/DB/strategy)
    /// - Scoring logic is deterministic and testable
    /// - Future: could support weighted multi-objective optimization
    /// </summary>
    public static class LoadScoreCalculator
    {
        /// <summary>
        /// Computes optimization score for a load context using provided weights.
        /// </summary>
        /// <param name="context">Load score metrics</param>
        /// <param name="weights">Configurable penalty weights</param>
        /// <returns>Score (lower is better, negative = violations)</returns>
        public static double ComputeLoadScore(LoadScoreContext context, LoadScoreWeights? weights = null)
        {
            weights ??= LoadScoreWeights.CreateDefault();
            double score = 0.0;

            // === Hard constraint penalties ===
            // Negative and large enough that an overloaded slot can never be rescued
            // by any combination of soft bonuses (max soft ≈ +400).
            // ConflictPenalty, DailyOverPenalty, WeeklyOverPenalty must stay below -500
            // to remain safely outside the soft scoring range.
            if (context.HasConflict)
            {
                score += weights.ConflictPenalty;
            }

            if (context.IsDailyLimitExceeded)
            {
                score += weights.DailyOverPenalty;
                score += context.DailyOverHours * weights.DailyOverHoursPenalty;
            }

            if (context.IsWeeklyLimitExceeded)
            {
                score += weights.WeeklyOverPenalty;
                score += context.WeeklyOverHours * weights.WeeklyOverHoursPenalty;
            }

            // Hard violations short-circuit — soft constraints do not apply
            if (score < 0)
            {
                return score;
            }

            // === Soft utilization tiers ===
            // Calibrated so no utilization signal alone crosses OneDayUnit (100),
            // keeping workload pressure below the cost of a one-day scheduling delay.
            double dailyPenalty = ComputeUtilizationPenalty(context.DailyUtilization, weights);
            double weeklyPenalty = ComputeUtilizationPenalty(context.WeeklyUtilization, weights);

            score += (dailyPenalty * weights.DailyLoadWeight) +
                     (weeklyPenalty * weights.WeeklyLoadWeight);

            return System.Math.Max(0.0, score);
        }

        private static double ComputeUtilizationPenalty(double utilization, LoadScoreWeights weights)
        {
            if (utilization <= 0.3)
                return weights.LowUtilizationPenalty;
            if (utilization <= 0.5)
                return 0.0;
            if (utilization <= 0.75)
                return weights.MediumUtilizationPenalty;
            if (utilization <= 0.9)
                return weights.HighUtilizationPenalty;
            return weights.VeryHighUtilizationPenalty;
        }

        
        /// Determines LoadState based on context metrics.
        public static LoadState DetermineLoadState(LoadScoreContext context)
        {
            return context.ComputeLoadState();
        }

    
        /// Calculates confidence level as: (originalScore - candidateScore) / originalScore.
        /// Represents how much improvement this candidate offers.
        /// 
        /// Examples:
        /// - If original score was 50 and candidate is 25: confidence = 0.5 (50% improvement)
        /// - If original score was 50 and candidate is 10: confidence = 0.8 (80% improvement)
        /// - If original score was 50 and candidate is 50: confidence = 0 (no improvement)
        /// <param name="originalScore">Current plan's score</param>
        /// <param name="candidateScore">Candidate plan's score</param>
        public static double CalculateConfidence(double originalScore, double candidateScore)
        {
            if (originalScore <= 0)
                return 0.0; // No baseline to measure against

            var improvement = (originalScore - candidateScore) / originalScore;
            return System.Math.Clamp(improvement, 0.0, 1.0);
        }
    }
}
