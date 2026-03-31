using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Interfaces
{
    /// Rates how well each candidate technician fits a given task based on workload simulation.
    ///
    /// Responsibilities:
    /// - Analyzes current plan using evaluation engine results
    /// - Simulates alternative scenarios (time shifts, technician swaps, day shifts)
    /// - Scores and ranks candidates
    /// - Returns ranked suggestions for planner review
    public interface IAssignmentFitnessEngine
    {
        /// Generates candidate plan improvement suggestions for a given context.
        /// The engine:
        /// 1. Loads current plan snapshot from database
        /// 2. Builds evaluation context using the evaluation engine
        /// 3. Generates multiple hypothetical scenarios
        /// 4. Evaluates each scenario
        /// 5. Scores and ranks candidates
        /// 6. Returns suggestions in descending quality order
        /// <param name="context">Fitness scope and target task information</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Ranked list of candidate plans (best first)</returns>
        Task<IReadOnlyList<CandidatePlan>> GenerateCandidatesAsync(
            FitnessContext context,
            CancellationToken cancellationToken = default);
    }
}
