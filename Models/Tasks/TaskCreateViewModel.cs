using System.ComponentModel.DataAnnotations;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Models.Tasks;

public class TaskCreateViewModel
{
    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Display(Name = "Priskirtas darbuotojas")]
    public string? Assignee { get; set; }

    [Display(Name = "Pradzia")]
    public string? StartAt { get; set; }

    [Display(Name = "Pabaiga")]
    public string? EndAt { get; set; }

    [Display(Name = "Pamaina")]
    public string Shift { get; set; } = "Day";

    [Range(0.5, 24)]
    [Display(Name = "Trukme (val.)")]
    public decimal? DurationHours { get; set; }

    [Display(Name = "Uzduoties tipas")]
    public string Type { get; set; } = "Patrol";

    [Display(Name = "Reikalingas kvalifikacijos lygis")]
    public string Skill { get; set; } = "Basic";

    [Display(Name = "Prioritetas")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [Display(Name = "Statusas")]
    public ScheduleTaskStatus Status { get; set; } = ScheduleTaskStatus.Planned;

    [Display(Name = "Objektas")]
    public string? ObjectName { get; set; }

    [Display(Name = "Objekto tipas")]
    public string ObjectType { get; set; } = "Building";

    [Display(Name = "Lokacija / pastabos")]
    public string? Location { get; set; }
}

