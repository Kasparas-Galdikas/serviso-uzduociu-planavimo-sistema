namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

public class TaskSwappedResponseDto
{
    public long SourceTaskId { get; set; }
    public uint SourceVersion { get; set; }
    public DateTime SourceStartAt { get; set; }
    public DateTime SourceEndAt { get; set; }

    public long TargetTaskId { get; set; }
    public uint TargetVersion { get; set; }
    public DateTime TargetStartAt { get; set; }
    public DateTime TargetEndAt { get; set; }

    public long TechnicianId { get; set; }

    /// <summary>
    /// Human-readable message describing the operation result (from TaskHistoryMessages).
    /// Use this for toast notifications to match what is saved in history.
    /// </summary>
    public string? Message { get; set; }

    public LoadSnapshotDto? Load { get; set; }
}