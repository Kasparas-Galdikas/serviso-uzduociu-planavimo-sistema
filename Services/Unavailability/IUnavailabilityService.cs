using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Data;
namespace ServiceTaskPlanningSystem.Services.Unavailability
{
    public interface IUnavailabilityService
    {
        Task<List<TechnicianUnavailabilityEntity>> GetPeriodsAsync(
            long technicianId,
            CancellationToken cancellationToken = default);
    }
}
