using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class BulkCreateTaskRequestDto
{
    /// <summary>
    /// Planning mode: "rankinis" (manual, default) or "automatinis" (automatic technician selection)
    /// </summary>
    public string PlanMode { get; set; } = "rankinis";

    [Required, MinLength(1)]
    public List<CreateTaskRequestDto> Items { get; set; } = new();
}
