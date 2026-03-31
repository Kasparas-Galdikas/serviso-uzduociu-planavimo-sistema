namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

public class AutoAssignmentSuggestionDto
{
    public long TechnicianId { get; set; }
    public string TechnicianName { get; set; } = string.Empty;
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
}
