using System;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Interfaces;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization.Policies
{
    /// <summary>
    /// Edit policy for Draft plans.
    /// Draft plans are staging areas - only managers can edit (no time restriction).
    /// Technicians cannot edit draft tasks as they are not yet published.
    /// </summary>
    public class DraftPlanEditPolicy : IPlanStatusEditPolicy
    {
        public PlanStatus AppliesTo => PlanStatus.Draft;

        public bool CanEdit(TaskEntity task, UserContext user, DateTime taskLocalDate, DateTime userLocalToday)
        {
            // Draft: Only managers can edit (staging/planning area)
            // Technicians cannot edit draft tasks
            return user.IsManager;
        }
    }
}
