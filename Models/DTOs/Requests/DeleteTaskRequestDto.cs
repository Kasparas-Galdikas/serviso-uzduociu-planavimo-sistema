using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class DeleteTaskRequestDto
{
    [Required]
    public uint Version { get; set; }

    public bool AutoCompact { get; set; } = true;

    /// <summary>
    /// Browser timezone offset in minutes (negative for UTC+, positive for UTC-).
    /// Example: UTC+2 (EET) = -120, UTC-5 (EST) = 300
    /// </summary>
    [Range(-1440, 1440, ErrorMessage = "Timezone offset must be between -1440 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
