using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

/// <summary>
/// Request DTO for saving a backlog item.
/// Inherits shared item fields from BacklogItemDto.
/// Priority is optional (nullable) since backlog items may not have a priority yet.
/// </summary>
public class SaveBacklogItemRequestDto : BacklogItemDto
{
    public TaskPriority? Priority { get; set; }
}
