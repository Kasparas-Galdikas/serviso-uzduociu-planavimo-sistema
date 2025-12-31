namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class WeeklyTableLayout
    {
        public string HeaderLabel { get; set; } = string.Empty;
        public TableShellModel Table { get; set; } = new TableShellModel();
    }
}
