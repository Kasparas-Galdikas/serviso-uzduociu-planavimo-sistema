using System.Threading.Tasks;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Interfaces
{
    public interface ITaskGenerator
    {
        Task<CandidatePlan> GenerateTasksAsync(GeneratorContext context);
    }
}
