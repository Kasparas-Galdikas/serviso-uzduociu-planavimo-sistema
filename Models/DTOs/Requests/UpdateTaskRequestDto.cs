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
    [Required(ErrorMessage = "Pavadinimas yra privalomas.")]
    [StringLength(20, ErrorMessage = "Pavadinimas negali viršyti 20 simbolių.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vieta yra privaloma.")]
    [StringLength(200, ErrorMessage = "Vieta negali viršyti 200 simbolių.")]
    public string Location { get; set; } = string.Empty;

    [Required(ErrorMessage = "Darbuotojas yra privalomas.")]
    [Range(1, long.MaxValue, ErrorMessage = "Darbuotojas turi būti pasirinktas.")]
    public long TechnicianId { get; set; }

    [Required(ErrorMessage = "Pradžios laikas yra privalomas.")]
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

    [StringLength(2000, ErrorMessage = "Darbuotojo komentaras negali viršyti 2000 simbolių.")]
    public string? TechnicianComment { get; set; }
}
