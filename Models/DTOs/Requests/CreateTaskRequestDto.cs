using ServiceTaskPlanningSystem.Data;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for creating a new task.
/// Frontend sends local date/time + timezone offset for proper UTC conversion.
/// </summary>
public class CreateTaskRequestDto
{
    [Required(ErrorMessage = "Technician ID is required.")]
    public int TechnicianId { get; set; }

    [Required(ErrorMessage = "Target date is required.")]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be in yyyy-MM-dd format.")]
    public string TargetDate { get; set; } = string.Empty;

    [Range(0, 1440, ErrorMessage = "Start minutes must be between 0 and 1440.")]
    public int StartMinutes { get; set; }

    [Range(0, 1440, ErrorMessage = "End minutes must be between 0 and 1440.")]
    public int EndMinutes { get; set; }

    [Required(ErrorMessage = "Title is required.")]
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    [StringLength(200, ErrorMessage = "Location cannot exceed 200 characters.")]
    public string Location { get; set; } = string.Empty;

    [Required(ErrorMessage = "Priority is required.")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [Required(ErrorMessage = "Status is required.")]
    public ScheduleTaskStatus Status { get; set; } = ScheduleTaskStatus.Planned;

    /// <summary>
    /// Browser timezone offset in minutes (from Date.getTimezoneOffset()).
    /// Absolute value - used to convert local times to UTC for storage.
    /// </summary>
    [Range(0, 1440, ErrorMessage = "Timezone offset must be between 0 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
