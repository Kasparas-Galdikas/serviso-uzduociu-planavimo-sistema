using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Planning;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.Authorization
{
    public class TaskPermissionService : ITaskPermissionService
    {
        private readonly ScheduleDbContext _db;
        private readonly IPlanningCalendar _planningCalendar;
        private readonly IScheduleTimeService _timeService;

        public TaskPermissionService(ScheduleDbContext db, IPlanningCalendar planningCalendar, IScheduleTimeService timeService)
        {
            _db = db;
            _planningCalendar = planningCalendar;
            _timeService = timeService;
        }

        public async Task<bool> CanEditTaskAsync(TaskEntity task, string role, CancellationToken cancellationToken = default)
        {
            var isManager = role == "manager" || role == "planuotojas" || string.IsNullOrWhiteSpace(role);
            if (!isManager) return false;

            // Check if task has already ended (single source of truth)
            if (!_timeService.IsTaskStillEditable(task.EndAt))
            {
                return false;
            }

            // Occupied horizon = max PeriodEnd across Active + Superseded plans
            var occupiedEnd = await _db.Plans
                .Where(p => p.Status == PlanStatus.Active || p.Status == PlanStatus.Superseded)
                .MaxAsync(p => (DateTime?)p.PeriodEnd, cancellationToken);

            var hasOccupiedPlans = occupiedEnd.HasValue;
            var occupiedEndDate = hasOccupiedPlans ? occupiedEnd!.Value.Date : _timeService.GetLocalToday();
            var firstFreeDate = hasOccupiedPlans ? occupiedEndDate.AddDays(1) : _timeService.GetLocalToday();

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

                return task.StartAt.Date >= _timeService.GetLocalToday() && task.StartAt.Date <= occupiedEndDate;
            }

            return false;
        }
    }
}
