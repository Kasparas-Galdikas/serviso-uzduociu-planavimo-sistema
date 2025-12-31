namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class DailyTableLayout
    {
        public string HeaderLabel { get; set; } = string.Empty;
        public bool IsToday { get; set; }
        public TableShellModel Table { get; set; } = new TableShellModel();
    }
}
