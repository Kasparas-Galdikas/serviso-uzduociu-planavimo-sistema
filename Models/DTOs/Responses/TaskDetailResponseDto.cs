using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

/// <summary>
/// Response DTO for GET /tasks/{id} endpoint.
/// Provides complete task details with versioning for concurrency control.
/// All dates/times are in local time (browser timezone).
/// </summary>
public class TaskDetailResponseDto
{
    public long Id { get; set; }

    /// <summary>
    /// Entity version (from PostgreSQL xmin) for optimistic concurrency control.
    /// Include with next UPDATE/MOVE request.
    /// </summary>
    public uint Version { get; set; }

    public long? PlanId { get; set; }
    public long TechnicianId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// Task start time in local timezone (not UTC).
    /// Frontend should use this value directly in datetime-local inputs.
    /// </summary>
    public DateTime StartAt { get; set; }

    /// <summary>
    /// Task end time in local timezone (not UTC).
    /// Frontend should use this value directly in datetime-local inputs.
    /// </summary>
    public DateTime EndAt { get; set; }

    public TaskPriority Priority { get; set; }
    public ScheduleTaskStatus Status { get; set; }

    /// <summary>
    /// Technician's comment about the task (editable by technician).
    /// </summary>
    public string TechnicianComment { get; set; } = string.Empty;

    /// <summary>
    /// Status of the parent plan (Active, Draft, etc.)
    /// </summary>
    public string PlanStatus { get; set; } = string.Empty;

    /// <summary>
    /// Whether the current user can edit this task.
    /// Based on user role and task editability rules.
    /// </summary>
    public bool IsEditable { get; set; }

    /// <summary>
    /// Browser timezone offset in minutes (from Date.getTimezoneOffset()).
    /// Return to frontend for consistency and transparency.
    /// Frontend can use this to verify time conversions.
    /// </summary>
    public int TimezoneOffsetMinutes { get; set; }
}
