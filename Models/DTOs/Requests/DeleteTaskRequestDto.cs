using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class DeleteTaskRequestDto
{
    [Required]
    public uint Version { get; set; }

    [Range(0, 1440, ErrorMessage = "Timezone offset must be between 0 and 1440 minutes.")]
    public int TimezoneOffsetMinutes { get; set; }
}
