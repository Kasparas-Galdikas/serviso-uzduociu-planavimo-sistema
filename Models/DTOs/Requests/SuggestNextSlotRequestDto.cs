using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class SuggestNextSlotRequestDto
{
    [Range(1, long.MaxValue, ErrorMessage = "Nenurodytas darbuotojas.")]
    public long TechnicianId { get; set; }

    [Required(ErrorMessage = "Data yra privaloma.")]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Datos formatas turi buti yyyy-MM-dd.")]
    public string TargetDate { get; set; } = string.Empty;

    public int? AfterEndMinutes { get; set; }

    public long? PreviousServiceObjectId { get; set; }

    public long? NewServiceObjectId { get; set; }

    public bool AppendAfterLastTask { get; set; }

    [Range(-1440, 1440, ErrorMessage = "Laiko juostos poslinkis netinkamas.")]
    public int TimezoneOffsetMinutes { get; set; }

    public List<SimulatedTaskAssignmentDto>? SimulatedAssignments { get; set; }
}
