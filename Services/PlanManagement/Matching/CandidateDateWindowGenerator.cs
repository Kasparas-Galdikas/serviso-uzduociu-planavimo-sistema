using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed class CandidateDateWindowGenerator
{
    // Generates a list of candidate dates starting from 'startDate' up to 'maxDateInclusive', with a maximum of 'horizonDays' days.
    public IReadOnlyList<DateTime> GenerateDates(DateTime startDate, DateTime maxDateInclusive, int horizonDays)
    {
        var safeStart = startDate.Date;
        var safeMax = maxDateInclusive.Date;
        var safeHorizon = Math.Max(1, horizonDays);
        var dates = new List<DateTime>(safeHorizon);

        for (var offset = 0; offset < safeHorizon; offset++)
        {
            var candidateDate = safeStart.AddDays(offset);
            if (candidateDate > safeMax)
            {
                break;
            }

            dates.Add(candidateDate);
        }

        return dates;
    }
}
