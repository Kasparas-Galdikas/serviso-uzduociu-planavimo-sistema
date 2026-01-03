namespace ServiceTaskPlanningSystem.Data
{
    public enum UserRole
    {
        Manager,
        Technician
    }

    public enum PlanStatus
    {
        Draft,
        Preview,
        Active,
        Superseded
    }

    public enum TaskPriority
    {
        Low,
        Medium,
        High
    }

    public enum TaskStatus
    {
        Planned,
        InProgress,
        Completed,
        Canceled
    }

    public enum RiskLevel
    {
        Low,
        Medium,
        High
    }

    public enum LoadState
    {
        Normal,
        Load,
        Conflict
    }
}
