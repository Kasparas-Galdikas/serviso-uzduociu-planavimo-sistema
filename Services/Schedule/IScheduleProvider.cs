using ServiceTaskPlanningSystem.Models.Planning;

namespace ServiceTaskPlanningSystem.Services.Schedule
{
    public interface IScheduleProvider
    {
        SchedulePageViewModel GetSchedule(string view, string? date, string? location, string? status);
    }
}
