namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

/// <summary>
/// Response DTO for PATCH /tasks/{id}/move endpoint.
/// Provides updated task details after moving to a different technician/time slot.
/// All dates/times are in local time (browser timezone).
/// </summary>
public class TaskMovedResponseDto
{
    public long Id { get; set; }

    /// <summary>
    /// Updated entity version (from PostgreSQL xmin) for optimistic concurrency control.
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// New technician ID the task was moved to.
    /// </summary>
    public long TechnicianId { get; set; }

    /// <summary>
    /// Updated task start time in local timezone (not UTC).
    /// </summary>
    public DateTime StartAt { get; set; }

    /// <summary>
    /// Updated task end time in local timezone (not UTC).
    /// </summary>
    public DateTime EndAt { get; set; }

    /// <summary>
    /// Snapshot of technician load after the move.
    /// </summary>
    public LoadSnapshotDto? Load { get; set; }
}
