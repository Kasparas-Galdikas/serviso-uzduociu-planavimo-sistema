using ServiceTaskPlanningSystem.Data;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for updating an existing task.
/// Includes version for optimistic concurrency control and timezone offset for time conversion.
/// </summary>
public class UpdateTaskRequestDto
{
    [Required(ErrorMessage = "Title is required.")]
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters.")]
    public string Location { get; set; } = string.Empty;

    [Required(ErrorMessage = "Start time is required.")]
    public DateTime StartAt { get; set; }

    [Required(ErrorMessage = "End time is required.")]
    public DateTime EndAt { get; set; }

    [Required(ErrorMessage = "Priority is required.")]
    public TaskPriority Priority { get; set; }

    [Required(ErrorMessage = "Status is required.")]
    public ScheduleTaskStatus Status { get; set; }

    /// <summary>
    /// Optimistic concurrency control - prevent lost updates.
    /// Must match the current version returned by GET or previous UPDATE.
    /// </summary>
    [Required(ErrorMessage = "Version is required for concurrency control.")]
    public uint Version { get; set; }

    /// <summary>
    /// Browser timezone offset in minutes (from Date.getTimezoneOffset()).
    /// Used to convert local StartAt/EndAt times to UTC for storage.
    /// </summary>
    [Range(0, 1440, ErrorMessage = "Timezone offset must be between 0 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
