using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class SwapSmartPreviewRequestDto
{
    [Required(ErrorMessage = "Target task ID is required.")]
    public long TargetTaskId { get; set; }

    [Range(-1440, 1440, ErrorMessage = "Timezone offset must be between -1440 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
