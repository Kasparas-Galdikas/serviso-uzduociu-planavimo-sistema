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
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// Used to convert local times to UTC for storage.
    /// Example: UTC+2 (EET) = -120, UTC-5 (EST) = 300
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Timezone offset must be between -1440 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }

    // Additional fields for comprehensive task creation
    [StringLength(50, ErrorMessage = "Shift cannot exceed 50 characters.")]
    public string? Shift { get; set; }

    [StringLength(100, ErrorMessage = "Task type cannot exceed 100 characters.")]
    public string? TaskType { get; set; }

    [StringLength(100, ErrorMessage = "Skill level cannot exceed 100 characters.")]
    public string? SkillLevel { get; set; }

    [StringLength(200, ErrorMessage = "Object name cannot exceed 200 characters.")]
    public string? ObjectName { get; set; }

    [StringLength(100, ErrorMessage = "Object type cannot exceed 100 characters.")]
    public string? ObjectType { get; set; }

    [StringLength(2000, ErrorMessage = "Instructions cannot exceed 2000 characters.")]
    public string? Instructions { get; set; }

    [StringLength(100, ErrorMessage = "Safety requirements cannot exceed 100 characters.")]
    public string? SafetyRequirements { get; set; }
}
