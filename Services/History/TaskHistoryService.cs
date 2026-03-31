using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.History
{
    public class TaskHistoryService : ITaskHistoryWriter, ITaskHistoryReader
    {
        private const int BacklogHistoryDays = 7;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TaskHistoryService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public TaskHistoryService(IServiceScopeFactory scopeFactory, ILogger<TaskHistoryService> logger)
        {
            _scopeFactory = scopeFactory;
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
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

                var actorName = string.Empty;
                if (actorUserId.HasValue)
                {
                    actorName = await db.Users
                        .AsNoTracking()
                        .Where(u => u.Id == actorUserId.Value)
                        .Select(u => u.Name)
                        .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
                }

                var cutoff = DateTime.UtcNow.AddSeconds(-15);
                var duplicate = await db.TaskActionLogs
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
                    await db.SaveChangesAsync(cancellationToken);
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

                db.TaskActionLogs.Add(entry);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // History writes must never block or fail the main operation.
                _logger.LogWarning(ex, "history.write.fail taskId={TaskId} action={Action}", taskId, actionType);
            }
        }

        public async Task<List<TaskHistoryItemDto>> GetPlanHistoryAsync(
            long planId,
            int limit,
            bool includeBacklog,
            long? userId,
            CancellationToken cancellationToken = default)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

            var planLogs = await db.TaskActionLogs
                .AsNoTracking()
                .Where(l => l.PlanId == planId)
                .Select(l => new TaskHistoryItemDto
                {
                    Id = l.Id,
                    TaskId = l.TaskId,
                    ActionType = l.ActionType,
                    Outcome = l.Outcome,
                    Summary = l.Summary,
                    OccurredAt = l.OccurredAtUtc,
                    ActorDisplayName = l.ActorName,
                    ActorRole = l.ActorRole,
                    Source = l.Source,
                    DetailsJson = l.DetailsJson
                })
                .ToListAsync(cancellationToken);

            if (includeBacklog && userId.HasValue)
            {
                var cutoff = DateTime.UtcNow.AddDays(-BacklogHistoryDays);
                var backlogLogs = await db.TaskActionLogs
                    .AsNoTracking()
                    .Where(l => l.PlanId == null
                             && l.ActorUserId == userId.Value
                             && l.OccurredAtUtc >= cutoff)
                    .Select(l => new TaskHistoryItemDto
                    {
                        Id = l.Id,
                        TaskId = l.TaskId,
                        ActionType = l.ActionType,
                        Outcome = l.Outcome,
                        Summary = l.Summary,
                        OccurredAt = l.OccurredAtUtc,
                        ActorDisplayName = l.ActorName,
                        ActorRole = l.ActorRole,
                        Source = l.Source,
                        DetailsJson = l.DetailsJson
                    })
                    .ToListAsync(cancellationToken);

                return planLogs
                    .Concat(backlogLogs)
                    .OrderByDescending(l => l.OccurredAt)
                    .Take(limit)
                    .ToList();
            }

            return planLogs
                .OrderByDescending(l => l.OccurredAt)
                .Take(limit)
                .ToList();
        }
    }
}
