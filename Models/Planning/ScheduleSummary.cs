namespace ServiceTaskPlanningSystem.Models.Planning
{
    public class ScheduleSummary
    {
        public int TotalTasks { get; set; }
        public int ConflictTasks { get; set; }
        public decimal OverloadHours { get; set; }
        public int SwitchExceedances { get; set; }
    }
}
