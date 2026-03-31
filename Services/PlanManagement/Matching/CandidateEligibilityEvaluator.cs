using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed class CandidateEligibilityEvaluator
{
    public IReadOnlyList<TechnicianEntity> GetEligibleTechnicians(
        IEnumerable<TechnicianEntity> technicians,
        string requiredSkillLevel)
    {
        // Skill matching is intentionally unsupported until technician capabilities exist.
        _ = requiredSkillLevel;

        return technicians
            .Where(t => t.WorkplaceAddressId > 0)
            .OrderBy(t => t.Id)
            .ToList();
    }
}
