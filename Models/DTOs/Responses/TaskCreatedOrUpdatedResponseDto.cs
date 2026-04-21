namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

/// <summary>
/// Response DTO for POST /tasks and PATCH /tasks/{id} endpoints.
/// Provides minimal information after task creation/update.
/// Include version for optimistic concurrency on subsequent operations.
/// </summary>
public class TaskCreatedOrUpdatedResponseDto
{
    /// <summary>
    /// The task ID (newly created or updated).
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Updated entity version (from PostgreSQL xmin) for optimistic concurrency control.
    /// Frontend should store this and include with next UPDATE/MOVE request.
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// Human-readable message describing the operation result (from TaskHistoryMessages).
    /// Use this for toast notifications to match what is saved in history.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Snapshot of technician load after the operation.
    /// </summary>
    public LoadSnapshotDto? Load { get; set; }

    /// <summary>
    /// Minutes saved vs. scheduled end time when technician completed early.
    /// Null if not a technician status update. Zero or negative if on-time/overran (shouldn't happen
    /// since status change is blocked after EndAt, but kept as int? for safety).
    /// </summary>
    public int? SavedMinutes { get; set; }
}
