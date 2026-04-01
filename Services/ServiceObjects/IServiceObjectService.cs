using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.ServiceObjects
{
    public interface IServiceObjectService
    {
        Task<List<ObjectLookupResponseDto>> GetAllAsync(CancellationToken cancellationToken = default);
    }
}
