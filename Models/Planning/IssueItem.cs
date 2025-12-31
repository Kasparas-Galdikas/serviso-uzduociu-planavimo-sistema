using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class IssueItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public IEnumerable<DecisionOption> Decisions { get; set; } = new List<DecisionOption>();
    }
}
