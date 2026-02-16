using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.ExternalServices.Common
{
    public interface IExternalCallPolicy
    {
        Task<T> ExecuteAsync<T>(
            string endpointKey,
            int perMinuteLimit,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default);
    }
}
