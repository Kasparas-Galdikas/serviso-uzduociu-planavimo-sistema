using ServiceTaskPlanningSystem.Data;
using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for updating an existing task.
/// Inherits shared content fields (Title, SkillLevel, ObjectName, ObjectAddress, Instructions)
/// from BacklogItemDto. Adds scheduling, versioning, and technician-specific fields.
/// </summary>
public class UpdateTaskRequestDto : BacklogItemDto
{
    [Required(ErrorMessage = "Darbuotojas yra privalomas.")]
    [Range(1, long.MaxValue, ErrorMessage = "Darbuotojas turi buti pasirinktas.")]
    public long TechnicianId { get; set; }

    [Required(ErrorMessage = "Pradzios laikas yra privalomas.")]
    public DateTime StartAt { get; set; }

    [Required(ErrorMessage = "Pabaigos laikas yra privalomas.")]
    public DateTime EndAt { get; set; }

    [Required(ErrorMessage = "Prioritetas yra privalomas.")]
    public TaskPriority Priority { get; set; }

    [Required(ErrorMessage = "Statusas yra privalomas.")]
    public ScheduleTaskStatus Status { get; set; }

    /// <summary>
    /// Optimistic concurrency control - prevent lost updates.
    /// Must match the current version returned by GET or previous UPDATE.
    /// </summary>
    [Required(ErrorMessage = "Versija yra privaloma.")]
    public uint Version { get; set; }

    /// <summary>
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// Used to convert local StartAt/EndAt times to UTC for storage.
    /// Example: UTC+2 (EET) = -120, UTC-5 (EST) = 300
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Laiko juostos poslinkis netinkamas.")]
    public int TimezoneOffsetMinutes { get; set; }

    [StringLength(2000, ErrorMessage = "Darbuotojo komentaras negali virsyti 2000 simboliu.")]
    public string? TechnicianComment { get; set; }
}
