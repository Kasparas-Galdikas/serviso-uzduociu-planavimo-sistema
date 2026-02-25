using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses
{
    public sealed class DragPreviewResponseDto
    {
        public long DraggedTaskId { get; set; }
        public List<DragPreviewStateDto> PreviewStates { get; set; } = new();
    }

    public sealed class DragPreviewStateDto
    {
        public long TechnicianId { get; set; }
        public string LoadState { get; set; } = string.Empty;

        // Debug information
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
    }
}
