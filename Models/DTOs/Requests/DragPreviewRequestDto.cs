using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests
{
    public sealed class DragPreviewRequestDto
    {
        [Required]
        public long TaskId { get; set; }

        [Required]
        public long TargetTechnicianId { get; set; }

        [Required]
        public string TargetDate { get; set; } = string.Empty;

        [Range(0, 24 * 60 - 1)]
        public int TargetSlotStartMinutes { get; set; }

        public int TimezoneOffsetMinutes { get; set; }
    }
}
