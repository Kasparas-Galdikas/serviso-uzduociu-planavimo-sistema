using System;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Interfaces;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Authorization.Policies
{
    /// <summary>
    /// Edit policy for Active plans.
    /// Active plans represent the current published schedule - only future tasks can be edited.
    /// Managers can edit all future tasks; technicians can only edit their own future tasks.
    /// </summary>
    public class ActivePlanEditPolicy : IPlanStatusEditPolicy
    {
        public PlanStatus AppliesTo => PlanStatus.Active;

        public bool CanEdit(TaskEntity task, UserContext user, DateTime taskLocalDate, DateTime userLocalToday)
        {
            // Rule 1: Past tasks are never editable (preserve historical data)
            if (taskLocalDate < userLocalToday)
            {
                return false;
            }

            // Rule 2: Managers can edit all future tasks
            if (user.IsManager)
            {
                return true;
            }

            // Rule 3: Technicians can only edit their OWN future tasks
            if (user.IsTechnician && user.TechnicianId.HasValue && task.TechnicianId == user.TechnicianId.Value)
            {
                return true;
            }

            // Rule 4: Deny everything else
            return false;
        }
    }
}
