using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for moving a task to a different technician and/or time slot.
/// Includes version for optimistic concurrency control and timezone offset.
/// </summary>
public class MoveTaskRequestDto
{
    [Required(ErrorMessage = "Target technician ID is required.")]
    public long TargetTechnicianId { get; set; }

    [Required(ErrorMessage = "Target date is required.")]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be in yyyy-MM-dd format.")]
    public string TargetDate { get; set; } = string.Empty;

    [Range(0, 1440, ErrorMessage = "Target slot start minutes must be between 0 and 1440.")]
    public int TargetSlotStartMinutes { get; set; }

    /// <summary>
    /// Optimistic concurrency control - prevent lost updates when multiple users edit.
    /// Must match the current version of the task.
    /// </summary>
    [Required(ErrorMessage = "Version is required for concurrency control.")]
    public uint Version { get; set; }

    /// <summary>
    /// Browser timezone offset in minutes (from Date.getTimezoneOffset()).
    /// Used for consistent time handling when moving tasks.
    /// </summary>
    [Range(0, 1440, ErrorMessage = "Timezone offset must be between 0 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
