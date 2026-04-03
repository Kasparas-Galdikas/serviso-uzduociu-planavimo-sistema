using System;
using System.Collections.Generic;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum DisplaceTasksError
    {
        None,
        TechnicianNotFound,
        Unauthorized,
        InvalidPeriod,
        NotFound,
        Stale,
        RestoreFailed
    }

    public sealed record DisplaceTasksResult(
        DisplaceTasksError Error,
        int DisplacedCount = 0,
        int RepairedCount = 0,
        int RestoredCount = 0,
        int BacklogRemainingCount = 0,
        string? Message = null)
    {
        public bool Ok => Error == DisplaceTasksError.None;
    }

    public enum UnavailabilityTaskResolution
    {
        Repair,
        Backlog
    }

    public sealed record AffectedTaskSummary(
        long TaskId,
        string Title,
        DateTime StartAt,
        DateTime EndAt,
        TaskPriority Priority,
        string ObjectName,
        string ObjectAddress,
        UnavailabilityTaskResolution Resolution);

    public sealed record UnavailabilityPreviewResult(
        List<AffectedTaskSummary> AffectedTasks);
}
