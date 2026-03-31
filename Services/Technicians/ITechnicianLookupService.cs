using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.Technicians
{
    public interface ITechnicianLookupService
    {
        Task<List<TechnicianLookupDto>> GetAllAsync(CancellationToken cancellationToken = default);
    }
}
