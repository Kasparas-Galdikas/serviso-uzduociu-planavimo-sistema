using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Time
{
    public interface IPlanningCalendar
    {
        Task<PlanningBoundary> GetBoundaryAsync(CancellationToken cancellationToken = default);
        Task<PlanningWindow> BuildWindowAsync(DateTime? requestedStart, DateTime? requestedEnd, int? requestedWeeks, int fallbackWeeks, CancellationToken cancellationToken = default);
    }
}
