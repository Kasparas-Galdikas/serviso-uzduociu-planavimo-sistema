using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data;
using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.Scheduling
{
    public interface IScheduleProvider
    {
        Task<SchedulePageViewModel> GetScheduleAsync(
            string view,
            string? date,
            string? location,
            string? status,
            long? technicianId,
            PlanEntity? activePlan,
            PlanEntity? draftPlan,
            string role,
            int timezoneOffsetMinutes);
    }
}
