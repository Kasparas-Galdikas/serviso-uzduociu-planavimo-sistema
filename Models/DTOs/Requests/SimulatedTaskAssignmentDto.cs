using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class SimulatedTaskAssignmentDto
{
    [Required]
    public long TechnicianId { get; set; }

    [Required]
    public string StartAt { get; set; } = string.Empty;

    [Required]
    public string EndAt { get; set; } = string.Empty;

    public long AddressId { get; set; }

    public long? ServiceObjectId { get; set; }

    public long? SourceBacklogItemId { get; set; }
}
