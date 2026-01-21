using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class UpdateTaskTimeRequestDto
{
    [Required(ErrorMessage = "Target date is required.")]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be in yyyy-MM-dd format.")]
    public string TargetDate { get; set; } = string.Empty;

    [Range(0, 1440, ErrorMessage = "Start minutes must be between 0 and 1440.")]
    public int TargetSlotStartMinutes { get; set; }

    [Range(0, 1440, ErrorMessage = "End minutes must be between 0 and 1440.")]
    public int TargetSlotEndMinutes { get; set; }

    [Required]
    public uint Version { get; set; }

    /// <summary>
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// Example: UTC+2 (EET) = -120, UTC-5 (EST) = 300
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Timezone offset must be between -1440 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
