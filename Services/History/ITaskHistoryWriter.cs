using System.Threading;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.History
{
    public interface ITaskHistoryWriter
    {
        Task RecordAsync(
            long? taskId,
            long? planId,
            string actionType,
            string outcome,
            string summary,
            long? actorUserId,
            string actorRole,
            object? details = null,
            string source = "user",
            string? correlationId = null,
            CancellationToken cancellationToken = default);
    }
}
