using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

public class BulkSchedulePreviewResponseDto
{
    public List<BulkSchedulePreviewItemDto> Items { get; set; } = new();
}

public class BulkSchedulePreviewItemDto
{
    public int Index { get; set; }
    public long? SourceBacklogItemId { get; set; }
    public long TechnicianId { get; set; }
    public string TechnicianName { get; set; } = string.Empty;
    public string TargetDate { get; set; } = string.Empty;
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectAddress { get; set; } = string.Empty;
    public string LoadState { get; set; } = string.Empty;
    public bool HasConflict { get; set; }
    public bool IsDailyLimitExceeded { get; set; }
    public bool IsWeeklyLimitExceeded { get; set; }
    public double DailyHours { get; set; }
    public double WeeklyHours { get; set; }
    public double DailyLimit { get; set; }
    public double WeeklyLimit { get; set; }
    public double DailyUtilization { get; set; }
    public double WeeklyUtilization { get; set; }
    public double TravelTimeHours { get; set; }
    public int? RequiredInboundTravelMinutes { get; set; }
    public bool IsPlacementAllowed { get; set; } = true;
    public string? RejectionCode { get; set; }
    public string? RejectionMessage { get; set; }
}
