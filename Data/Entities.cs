using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Data
{
    public class UserEntity
    {
        public long Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public UserRole Role { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }

    public class RegionEntity
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int PriorityWeight { get; set; }
        public DateTime CreatedAt { get; set; }

        public ICollection<TechnicianEntity> Technicians { get; set; } = new List<TechnicianEntity>();
    }

    public class TechnicianEntity
    {
        public long Id { get; set; }
        public long? UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long? RegionId { get; set; }
        public decimal DailyLimit { get; set; }
        public decimal WeeklyLimit { get; set; }
        public int TravelSwitchLimit { get; set; }
        public string? RulesSummary { get; set; }
        public DateTime CreatedAt { get; set; }

        public UserEntity? User { get; set; }
        public RegionEntity? Region { get; set; }
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }

    public class PlanEntity
    {
        public long Id { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int Weeks { get; set; }
        public long? CreatedBy { get; set; }
        public PlanStatus Status { get; set; }
        public long? SupersededByPlanId { get; set; }
        public DateTime CreatedAt { get; set; }

        public UserEntity? CreatedByUser { get; set; }
        public PlanEntity? SupersededByPlan { get; set; }
        public ICollection<PlanEntity> SupersededPlans { get; set; } = new List<PlanEntity>();
        public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    }

    public class TaskEntity
    {
        public long Id { get; set; }
        public long? PlanId { get; set; }
        public long TechnicianId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public long? RegionId { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public TaskPriority Priority { get; set; }
        public TaskStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }

        public PlanEntity? Plan { get; set; }
        public TechnicianEntity Technician { get; set; } = default!;
        public RegionEntity? Region { get; set; }
        public TaskEvaluationEntity? Evaluation { get; set; }
    }

    public class TaskEvaluationEntity
    {
        public long TaskId { get; set; }
        public RiskLevel? RiskLevel { get; set; }
        public bool IsConflict { get; set; }
        public bool IsOverloaded { get; set; }
        public bool TravelRequired { get; set; }
        public decimal? LoadHours { get; set; }
        public DateTime EvaluatedAt { get; set; }

        public TaskEntity Task { get; set; } = default!;
    }
}
