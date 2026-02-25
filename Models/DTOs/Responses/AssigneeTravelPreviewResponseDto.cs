using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses
{
    public class AssigneeTravelPreviewResponseDto
    {
        public List<AssigneeTravelPreviewItemDto> Items { get; set; } = new();
    }

    public class AssigneeTravelPreviewItemDto
    {
        public long TechnicianId { get; set; }
        public int? TravelMinutes { get; set; }
    }
}
