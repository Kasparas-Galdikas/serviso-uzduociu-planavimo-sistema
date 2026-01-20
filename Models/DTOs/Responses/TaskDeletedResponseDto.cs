namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

public class TaskDeletedResponseDto
{
    public long DeletedTaskId { get; set; }
    public uint Version { get; set; }
    public LoadSnapshotDto? Load { get; set; }
}
