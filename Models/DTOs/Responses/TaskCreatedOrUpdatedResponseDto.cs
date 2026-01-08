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
}
