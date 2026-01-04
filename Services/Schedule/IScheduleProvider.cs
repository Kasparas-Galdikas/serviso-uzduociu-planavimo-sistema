using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    public interface IScheduleProvider
    {
        SchedulePageViewModel GetSchedule(
            string view,
            string? date,
            string? location,
            string? status,
            PlanEntity? activePlan,
            PlanEntity? draftPlan);
    }
}
