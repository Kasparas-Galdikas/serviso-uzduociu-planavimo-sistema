namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

/// <summary>
/// Snapshot of technician load for a specific day/week window.
/// Derived from WorkloadCalculator (single source of truth).
/// </summary>
public sealed class LoadSnapshotDto
{
    public string LoadState { get; set; } = string.Empty;
    public bool HasConflict { get; set; }
    public bool IsDailyLimitExceeded { get; set; }
    public bool IsWeeklyLimitExceeded { get; set; }
    public bool IsSwitchLimitExceeded { get; set; }
    public double DailyHours { get; set; }
    public double WeeklyHours { get; set; }
    public double DailyLimit { get; set; }
    public double WeeklyLimit { get; set; }
    public double DailyUtilization { get; set; }
    public double WeeklyUtilization { get; set; }
    public int SwitchCount { get; set; }
}
