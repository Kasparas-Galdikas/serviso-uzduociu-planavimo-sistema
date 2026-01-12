using ServiceTaskPlanningSystem.Data;
using System;
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

    [Required(ErrorMessage = "Technician is required.")]
    [Range(1, long.MaxValue, ErrorMessage = "Technician must be a valid id.")]
    public long TechnicianId { get; set; }

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
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// Used to convert local StartAt/EndAt times to UTC for storage.
    /// Example: UTC+2 (EET) = -120, UTC-5 (EST) = 300
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Timezone offset must be between -1440 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }

    // Additional fields for comprehensive task updates
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

    [StringLength(2000, ErrorMessage = "Technician comment cannot exceed 2000 characters.")]
    public string? TechnicianComment { get; set; }
}
