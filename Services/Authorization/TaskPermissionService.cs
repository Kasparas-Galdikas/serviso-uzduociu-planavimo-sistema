using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Planning;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public class TaskPermissionService : ITaskPermissionService
    {
        private readonly ScheduleDbContext _db;
        private readonly IPlanningCalendar _planningCalendar;

        public TaskPermissionService(ScheduleDbContext db, IPlanningCalendar planningCalendar)
        {
            _db = db;
            _planningCalendar = planningCalendar;
        }

        public async Task<bool> CanEditTaskAsync(TaskEntity task, string role, CancellationToken cancellationToken = default)
        {
            var isManager = role == "manager" || role == "planuotojas" || string.IsNullOrWhiteSpace(role);
            if (!isManager) return false;

            var now = DateTime.UtcNow;
            if (task.EndAt <= now)
            {
                return false;
            }

            // Occupied horizon = max PeriodEnd across Active + Superseded plans
            var occupiedEnd = await _db.Plans
                .Where(p => p.Status == PlanStatus.Active || p.Status == PlanStatus.Superseded)
                .MaxAsync(p => (DateTime?)p.PeriodEnd, cancellationToken);

            var hasOccupiedPlans = occupiedEnd.HasValue;
            var occupiedEndDate = hasOccupiedPlans ? occupiedEnd!.Value.Date : now.Date;
            var firstFreeDate = hasOccupiedPlans ? occupiedEndDate.AddDays(1) : now.Date;

            var draftExists = await _db.Plans.AnyAsync(p => p.Status == PlanStatus.Draft, cancellationToken);

            var planStatus = task.Plan?.Status ?? PlanStatus.Active;

            if (planStatus == PlanStatus.Draft)
            {
                // Draft tasks allowed only on/after firstFreeDate (and not in real past)
                return task.StartAt.Date >= firstFreeDate.Date;
            }

            // Active or Superseded share the same rule
            if (planStatus == PlanStatus.Active || planStatus == PlanStatus.Superseded)
            {
                if (draftExists)
                {
                    return false;
                }

                return task.StartAt.Date >= now.Date && task.StartAt.Date <= occupiedEndDate;
            }

            return false;
        }
    }
}
