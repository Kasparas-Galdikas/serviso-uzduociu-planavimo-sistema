namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

public class TaskDeletedResponseDto
{
    public long DeletedTaskId { get; set; }
    public uint Version { get; set; }

    /// <summary>
    /// Human-readable message describing the operation result (from TaskHistoryMessages).
    /// Use this for toast notifications to match what is saved in history.
    /// </summary>
    public string? Message { get; set; }

    public LoadSnapshotDto? Load { get; set; }
}
