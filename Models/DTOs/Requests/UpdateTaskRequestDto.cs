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
    [StringLength(20, ErrorMessage = "Pavadinimas negali virÅ¡yti 20 simboliÅ³.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Darbuotojas yra privalomas.")]
    [Range(1, long.MaxValue, ErrorMessage = "Darbuotojas turi bÅ«ti pasirinktas.")]
    public long TechnicianId { get; set; }

    [Required(ErrorMessage = "PradÅ¾ios laikas yra privalomas.")]
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
    [StringLength(100, ErrorMessage = "Kvalifikacijos lygis negali virÅ¡yti 100 simboliÅ³.")]
    public string SkillLevel { get; set; } = string.Empty;

    [Required(ErrorMessage = "Objekto pavadinimas yra privalomas.")]
    [StringLength(500, ErrorMessage = "Objekto pavadinimas negali virÅ¡yti 500 simboliÅ³.")]
    public string ObjectName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Objekto adresas yra privalomas.")]
    [StringLength(500, ErrorMessage = "Objekto adresas negali virÅ¡yti 500 simboliÅ³.")]
    public string ObjectAddress { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "Instrukcijos negali virÅ¡yti 2000 simboliÅ³.")]
    public string? Instructions { get; set; }

    [StringLength(2000, ErrorMessage = "Darbuotojo komentaras negali virÅ¡yti 2000 simboliÅ³.")]
    public string? TechnicianComment { get; set; }
}

