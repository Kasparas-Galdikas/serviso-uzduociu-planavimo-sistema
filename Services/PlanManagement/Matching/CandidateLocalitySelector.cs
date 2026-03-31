using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal enum CandidateLocalityTier
{
    GlobalFallback = 0,
    SameCity = 1
}

internal sealed record CandidateLocalityPool(
    IReadOnlyList<TechnicianEntity> Technicians,
    CandidateLocalityTier Tier,
    string Label);

internal sealed class CandidateLocalitySelector
{
    public CandidateLocalityPool SelectPool(
        IReadOnlyList<TechnicianEntity> eligibleTechnicians,
        ServiceObjectEntity serviceObject)
    {
        var targetCity = serviceObject.Address?.City?.Trim();
        if (!string.IsNullOrWhiteSpace(targetCity))
        {
            var sameCity = eligibleTechnicians
                .Where(t => string.Equals(
                    t.WorkplaceAddress?.City?.Trim(),
                    targetCity,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Id)
                .ToList();

            if (sameCity.Count > 0)
            {
                return new CandidateLocalityPool(sameCity, CandidateLocalityTier.SameCity, targetCity);
            }
        }

        return new CandidateLocalityPool(
            eligibleTechnicians.OrderBy(t => t.Id).ToList(),
            CandidateLocalityTier.GlobalFallback,
            "global");
    }
}
