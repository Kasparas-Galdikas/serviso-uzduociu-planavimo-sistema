using ServiceTaskPlanningSystem.Data;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for creating a new task.
/// Inherits shared item fields (Title, SkillLevel, ObjectName, ObjectAddress, Instructions)
/// from BacklogItemDto and adds scheduling-specific fields.
/// Frontend sends local date/time + timezone offset for proper UTC conversion.
/// </summary>
public class CreateTaskRequestDto : BacklogItemDto
{
    [Required(ErrorMessage = "Darbuotojas yra privalomas.")]
    public long TechnicianId { get; set; }

    [Required(ErrorMessage = "Data yra privaloma.")]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Datos formatas turi buti yyyy-MM-dd.")]
    public string TargetDate { get; set; } = string.Empty;

    [Range(0, 1440, ErrorMessage = "Pradzios laikas netinkamas.")]
    public int StartMinutes { get; set; }

    [Range(0, 1440, ErrorMessage = "Pabaigos laikas netinkamas.")]
    public int EndMinutes { get; set; }

    [Required(ErrorMessage = "Prioritetas yra privalomas.")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [Required(ErrorMessage = "Statusas yra privalomas.")]
    public ScheduleTaskStatus Status { get; set; } = ScheduleTaskStatus.InProgress;

    /// <summary>
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// Used to convert local times to UTC for storage.
    /// Example: UTC+2 (EET) = -120, UTC-5 (EST) = 300
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Laiko juostos poslinkis netinkamas.")]
    public int TimezoneOffsetMinutes { get; set; }

    public long? SourceBacklogItemId { get; set; }
}
