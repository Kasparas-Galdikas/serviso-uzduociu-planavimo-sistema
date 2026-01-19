using System;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization.Interfaces
{
    /// <summary>
    /// Strategy interface for plan-status-specific edit rules.
    /// Each PlanStatus has its own implementation with custom business logic.
    /// </summary>
    public interface IPlanStatusEditPolicy
    {
        PlanStatus AppliesTo { get; }

        bool CanEdit(TaskEntity task, UserContext user, DateTime taskLocalDate, DateTime userLocalToday);
    }
}
