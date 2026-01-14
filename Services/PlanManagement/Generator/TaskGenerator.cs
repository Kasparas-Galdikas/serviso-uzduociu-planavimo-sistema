using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Generates candidate plan improvement suggestions.
    /// Does NOT modify database or apply changes.
    /// </summary>
    public sealed class TaskGenerator
    {
        /// <summary>
        /// Generates hypothetical candidate plans for improving the current plan.
        /// Returned candidates are NOT applied automatically.
        /// </summary>
        public IReadOnlyList<CandidatePlan> GenerateCandidates(GeneratorContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // Skeleton only – real generation logic will be added later
            return Array.Empty<CandidatePlan>();
        }
    }
}
