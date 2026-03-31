using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// Captures a snapshot of the current plan state for fitness evaluation.
    /// Describes WHAT needs improvement, in WHAT scope, and with WHAT constraints.
    /// This is an immutable context object that the fitness engine receives.
    /// All data is read-only database snapshots - no modifications happen here.
    public sealed class FitnessContext
    {
        /// Task that triggered evaluation.
        /// This is the problematic or selected task to optimize.
        public long TargetTaskId { get; init; }

        /// Scope constraints for evaluation (same day, same week, any technician, etc.)
        public FitnessScope Scope { get; init; }

        /// Maximum number of candidate plans to generate and return.
        /// Defaults to 5.
        public int MaxCandidates { get; init; } = 5;

        /// Minimum confidence level for suggestions to be included (0.0-1.0).
        /// Suggestions below this threshold are filtered out.
        /// Defaults to 0.5 (50% confidence minimum).
        public double MinConfidence { get; init; } = 0.5;

        /// Whether to include suggestions that have hard constraint violations.
        /// When true: Conflict state suggestions are included but flagged.
        /// When false: Only Normal/Load suggestions are returned.
        public bool AllowConflictSuggestions { get; init; } = true;

        /// Optional: Specific technicians to consider for swaps.
        /// If empty, all qualified technicians are considered.
        public IReadOnlyList<long> CandidateTechnicianIds { get; init; } = Array.Empty<long>();

        /// Optional: Time window for task movements (days into future).
        /// If 0, only same-day moves are considered.
        /// If 7, moves up to 7 days ahead are considered.
        public int TimeWindowDays { get; init; } = 1;

        /// Priority: prefer solutions that reduce daily load?
        /// When true: Daily overload reduction is weighted higher in scoring.
        public bool PrioritizeDailyLoad { get; init; } = true;

        /// Priority: prefer solutions that reduce weekly load?
        /// When true: Weekly overload reduction is weighted higher in scoring.
        public bool PrioritizeWeeklyLoad { get; init; } = true;

        /// Priority: prefer solutions that minimize technician swaps?
        /// When true: Same-technician solutions get higher scores.
        public bool PreferMinimalSwaps { get; init; } = false;

        /// When this context was created for auditing purposes.
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }
}
