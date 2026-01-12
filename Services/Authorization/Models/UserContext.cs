using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.Authorization.Models
{
    public sealed class UserContext
    {
        public long? UserId { get; init; }
        public UserRole Role { get; init; } = UserRole.Technician;

        public bool IsManager => Role == UserRole.Manager;
        public bool IsTechnician => Role == UserRole.Technician;
    }
}
