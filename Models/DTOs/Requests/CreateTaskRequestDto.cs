using ServiceTaskPlanningSystem.Data;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for creating a new task.
/// Frontend sends local date/time + timezone offset for proper UTC conversion.
/// </summary>
public class CreateTaskRequestDto
{
    [Required(ErrorMessage = "Darbuotojas yra privalomas.")]
    public long TechnicianId { get; set; }

    [Required(ErrorMessage = "Data yra privaloma.")]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Datos formatas turi būti yyyy-MM-dd.")]
    public string TargetDate { get; set; } = string.Empty;

    [Range(0, 1440, ErrorMessage = "Pradžios laikas netinkamas.")]
    public int StartMinutes { get; set; }

    [Range(0, 1440, ErrorMessage = "Pabaigos laikas netinkamas.")]
    public int EndMinutes { get; set; }

    [Required(ErrorMessage = "Pavadinimas yra privalomas.")]
    [StringLength(20, ErrorMessage = "Pavadinimas negali viršyti 20 simbolių.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vieta yra privaloma.")]
    [StringLength(200, ErrorMessage = "Vieta negali viršyti 200 simbolių.")]
    public string Location { get; set; } = string.Empty;

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

    [Required(ErrorMessage = "Kvalifikacijos lygis yra privalomas.")]
    [StringLength(100, ErrorMessage = "Kvalifikacijos lygis negali viršyti 100 simbolių.")]
    public string SkillLevel { get; set; } = string.Empty;

    [Required(ErrorMessage = "Objekto pavadinimas yra privalomas.")]
    [StringLength(200, ErrorMessage = "Objekto pavadinimas negali viršyti 200 simbolių.")]
    public string ObjectName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Objekto adresas yra privalomas.")]
    [StringLength(200, ErrorMessage = "Objekto adresas negali viršyti 200 simbolių.")]
    public string ObjectAddress { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "Instrukcijos negali viršyti 2000 simbolių.")]
    public string? Instructions { get; set; }
}
