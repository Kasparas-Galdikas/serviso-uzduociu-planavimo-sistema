using System.ComponentModel.DataAnnotations;
using ServiceTaskPlanningSystem.Models.Validation;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Shared base for backlog item fields used by both task creation and backlog saving.
/// Title is required; remaining fields are optional at the DTO level —
/// CreateTaskRequestDto enforces them via controller guard clauses.
/// </summary>
public class BacklogItemDto
{
    [Required(ErrorMessage = "Pavadinimas yra privalomas.")]
    [StringLength(20, ErrorMessage = "Pavadinimas negali virsyti 20 simboliu.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Reikalingas kvalifikacijos lygis yra privalomas.")]
    [StringLength(100, ErrorMessage = "Kvalifikacijos lygis negali virsyti 100 simboliu.")]
    public string SkillLevel { get; set; } = string.Empty;

    [Required(ErrorMessage = "Objektas yra privalomas.")]
    [StringLength(500, ErrorMessage = "Objekto pavadinimas negali virsyti 500 simboliu.")]
    public string ObjectName { get; set; } = string.Empty;

    public long? ObjectId { get; set; }

    [Required(ErrorMessage = "Adresas yra privalomas.")]
    [StringLength(500, ErrorMessage = "Objekto adresas negali virsyti 500 simboliu.")]
    public string ObjectAddress { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "Instrukcijos negali virsyti 2000 simboliu.")]
    public string? Instructions { get; set; }

    [NotInPast]
    public DateTime? DueDate { get; set; }
}
