using System;

namespace ServiceTaskPlanningSystem.Data
{
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
}
