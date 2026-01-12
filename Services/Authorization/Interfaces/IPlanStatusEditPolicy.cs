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
        /// <summary>
        /// The plan status this policy applies to.
        /// </summary>
        PlanStatus AppliesTo { get; }

        /// <summary>
        /// Determines if a task can be edited based on plan-status-specific rules.
        /// </summary>
        /// <param name="task">The task to check</param>
        /// <param name="user">The user attempting the edit</param>
        /// <param name="taskLocalDate">Task start date in user's local timezone</param>
        /// <param name="userLocalToday">Today's date in user's local timezone</param>
        /// <returns>True if editing is allowed by this policy's rules</returns>
        bool CanEdit(TaskEntity task, UserContext user, DateTime taskLocalDate, DateTime userLocalToday);
    }
}
