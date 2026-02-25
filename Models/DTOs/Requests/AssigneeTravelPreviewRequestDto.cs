using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests
{
    public class AssigneeTravelPreviewRequestDto
    {
        [Required]
        [RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
        public string TargetDate { get; set; } = string.Empty;

        [Range(0, 1440)]
        public int StartMinutes { get; set; }

        [Range(0, 1440)]
        public int EndMinutes { get; set; }

        [Required]
        [StringLength(500)]
        public string ObjectAddress { get; set; } = string.Empty;

        [Range(-1440, 1440)]
        public int TimezoneOffsetMinutes { get; set; }
    }
}
