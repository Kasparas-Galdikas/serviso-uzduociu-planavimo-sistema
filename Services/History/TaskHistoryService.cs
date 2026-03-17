using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.History
{
    public class TaskHistoryService : ITaskHistoryWriter
    {
        private readonly ScheduleDbContext _db;
        private readonly ILogger<TaskHistoryService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public TaskHistoryService(ScheduleDbContext db, ILogger<TaskHistoryService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task RecordAsync(
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
            CancellationToken cancellationToken = default)
        {
            try
            {
                var actorName = string.Empty;
                if (actorUserId.HasValue)
                {
                    actorName = await _db.Users
                        .AsNoTracking()
                        .Where(u => u.Id == actorUserId.Value)
                        .Select(u => u.Name)
                        .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
                }

                var cutoff = DateTime.UtcNow.AddSeconds(-15);
                var duplicate = await _db.TaskActionLogs
                    .Where(l => l.PlanId == planId
                             && l.TaskId == taskId
                             && l.ActionType == actionType
                             && l.Outcome == outcome
                             && l.Summary == summary
                             && l.OccurredAtUtc >= cutoff)
                    .OrderByDescending(l => l.OccurredAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);

                if (duplicate != null)
                {
                    duplicate.OccurredAtUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(cancellationToken);
                    return;
                }

                var entry = new TaskActionLogEntity
                {
                    TaskId = taskId,
                    PlanId = planId,
                    ActionType = actionType,
                    Outcome = outcome,
                    Summary = summary,
                    ActorUserId = actorUserId,
                    ActorName = actorName,
                    ActorRole = actorRole,
                    OccurredAtUtc = DateTime.UtcNow,
                    DetailsJson = details != null
                        ? JsonSerializer.Serialize(details, _jsonOptions)
                        : null,
                    Source = source,
                    CorrelationId = correlationId
                };

                _db.TaskActionLogs.Add(entry);
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // History writes must never block or fail the main operation.
                _logger.LogWarning(ex, "history.write.fail taskId={TaskId} action={Action}", taskId, actionType);
            }
        }
    }
}
