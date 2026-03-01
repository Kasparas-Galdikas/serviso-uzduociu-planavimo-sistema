namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class Technician
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string RulesSummary { get; set; } = string.Empty;
        public int WeeklyHours { get; set; }
        public int WeeklyLimit { get; set; }
        public int DailyHours { get; set; }
        public int DailyLimit { get; set; }
    }
}
