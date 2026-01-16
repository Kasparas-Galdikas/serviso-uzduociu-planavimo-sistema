using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Data;
using System.Threading.Tasks;
using ServiceTaskPlanningSystem.Services.Authorization.Models;

namespace ServiceTaskPlanningSystem.Services.Scheduling
{
    public interface IScheduleProvider
    {
        Task<SchedulePageViewModel> GetScheduleAsync(
            string view,
            string? date,
            string? location,
            string? workload,
            long? technicianId,
            PlanEntity? activePlan,
            PlanEntity? draftPlan,
            UserContext user,
            int timezoneOffsetMinutes);
    }
}
