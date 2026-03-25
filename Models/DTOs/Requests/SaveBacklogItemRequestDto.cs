using System.ComponentModel.DataAnnotations;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for saving a backlog item.
/// Inherits shared item fields from BacklogItemDto.
/// Priority is optional (nullable) since backlog items may not have a priority yet.
/// ItemType is required — user must explicitly choose Temporary or Permanent.
/// </summary>
public class SaveBacklogItemRequestDto : BacklogItemDto
{
    public TaskPriority? Priority { get; set; }

    [Required(ErrorMessage = "Šablono tipas yra privalomas.")]
    public BacklogItemType ItemType { get; set; }
}
