using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    public interface IScheduleProvider
    {
        Task<SchedulePageViewModel> GetScheduleAsync(
            string view,
            string? date,
            string? location,
            string? status,
            PlanEntity? activePlan,
            PlanEntity? draftPlan);
    }
}
