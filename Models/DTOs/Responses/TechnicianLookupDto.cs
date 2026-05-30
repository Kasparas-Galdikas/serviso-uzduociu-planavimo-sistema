using ServiceTaskPlanningSystem.Data.Enums;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses
{
    public class TechnicianLookupDto
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? City { get; init; }
        public ExperienceLevel ExperienceLevel { get; init; }
    }
}
