namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class DecisionOption
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = "vidutinis";
        public string? LoadImpact { get; set; }
        public string? TravelImpact { get; set; }
        public bool IsRecommended { get; set; }
    }
}
