using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class BulkCreateTaskRequestDto
{
    /// <summary>
    /// Planning mode: "rankinis" (manual, default) or "automatinis" (automatic technician selection)
    /// </summary>
    public string PlanMode { get; set; } = "rankinis";

    /// <summary>
    /// Plan scope: "draft" targets the draft plan directly; any other value targets active/normal plans.
    /// When "draft", validation routes all items to the draft plan by ID rather than by date range.
    /// </summary>
    public string? PlanScope { get; set; }

    [Required, MinLength(1)]
    public List<CreateTaskRequestDto> Items { get; set; } = new();
}
