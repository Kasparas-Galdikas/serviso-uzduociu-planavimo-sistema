using System;

namespace ServiceTaskPlanningSystem.Services.Planning
{
    public record PlanningWindow(DateTime Start, DateTime End, int Days, int Weeks, DateTime FirstFreeDate);

    public record PlanningBoundary(DateTime FirstFreeDate, DateTime? LastOccupiedDate);
}
