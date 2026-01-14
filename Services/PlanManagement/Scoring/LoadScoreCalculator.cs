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

            // Apply hard constraint penalties
            if (context.HasConflict)
            {
                score += weights.ConflictPenalty;
            }

            if (context.IsDailyLimitExceeded)
            {
                score += weights.DailyOverPenalty;
                // Additional proportional penalty for degree of violation
                score += context.DailyOverHours * weights.DailyOverHoursPenalty;
            }

            if (context.IsWeeklyLimitExceeded)
            {
                score += weights.WeeklyOverPenalty;
                // Additional proportional penalty for degree of violation
                score += context.WeeklyOverHours * weights.WeeklyOverHoursPenalty;
            }

            if (context.IsSwitchLimitExceeded)
            {
                score += weights.SwitchOverPenalty;
            }

            // If hard constraints are violated, return immediately
            // (soft constraints don't matter when hard constraints fail)
            if (score < 0)
            {
                return score;
            }

            // Soft constraints: utilization penalties (only when within hard limits)
            double dailyPenalty = ComputeUtilizationPenalty(context.DailyUtilization, weights);
            double weeklyPenalty = ComputeUtilizationPenalty(context.WeeklyUtilization, weights);

            // Apply weights
            score += (dailyPenalty * weights.DailyLoadWeight) + 
                     (weeklyPenalty * weights.WeeklyLoadWeight);

            // Switch count penalty
            score += context.SwitchCount * weights.SwitchCountPenalty;

            // Ensure non-negative for soft constraints only
            return System.Math.Max(0.0, score);
        }

        /// Computes utilization-based penalty (soft constraint).
        /// Higher utilization = higher penalty (worse score).
        private static double ComputeUtilizationPenalty(double utilization, LoadScoreWeights weights)
        {
            if (utilization <= 0.3) 
                return weights.LowUtilizationPenalty;  // Underutilized
            if (utilization <= 0.5) 
                return 0.0;                             // Optimal
            if (utilization <= 0.75) 
                return weights.MediumUtilizationPenalty; // Moderate
            if (utilization <= 0.9) 
                return weights.HighUtilizationPenalty;   // High
            
            // Over 100% but within hard limits (shouldn't happen)
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
        /// <returns>Confidence (0.0-1.0), clamped</returns>
        public static double CalculateConfidence(double originalScore, double candidateScore)
        {
            if (originalScore <= 0)
                return 0.0; // No baseline to measure against

            var improvement = (originalScore - candidateScore) / originalScore;
            return System.Math.Clamp(improvement, 0.0, 1.0);
        }
    }
}
