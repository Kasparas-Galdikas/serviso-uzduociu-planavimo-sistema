using System.Collections.Generic;
using System.Linq;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed class CandidateRankingService
{
    public RankedTechnicianCandidate? SelectBest(IEnumerable<RankedTechnicianCandidate> candidates)
    {
        return candidates
            .OrderBy(c => c.ScoreBreakdown.TotalScore)
            .ThenBy(c => c.StartMinutes)
            .ThenBy(c => c.Technician.Id)
            .FirstOrDefault();
    }
}
