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

    [Display(Name = "Reikalingas kvalifikacijos lygis")]
    public string Skill { get; set; } = "Basic";

    [Display(Name = "Prioritetas")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [Display(Name = "Statusas")]
    public ScheduleTaskStatus Status { get; set; } = ScheduleTaskStatus.InProgress;

    [Display(Name = "Objektas")]
    public string? ObjectName { get; set; }

    [Display(Name = "Objekto adresas")]
    public string? ObjectAddress { get; set; }
}

