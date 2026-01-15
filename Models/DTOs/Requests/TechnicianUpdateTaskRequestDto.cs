using ServiceTaskPlanningSystem.Data;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for technicians updating their own tasks.
/// Technicians can only update Status and Comment fields.
/// </summary>
public class TechnicianUpdateTaskRequestDto
{
    [Required(ErrorMessage = "Status is required.")]
    public ScheduleTaskStatus Status { get; set; }

    [StringLength(2000, ErrorMessage = "Comment cannot exceed 2000 characters.")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Optimistic concurrency control - prevent lost updates.
    /// </summary>
    [Required(ErrorMessage = "Version is required for concurrency control.")]
    public uint Version { get; set; }

    /// <summary>
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Timezone offset must be between -1440 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
