using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TaskEntity
    {
        public long Id { get; set; }
        public long? PlanId { get; set; }
        public long TechnicianId { get; set; }
        public long? ObjectId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public long? RegionId { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public TaskPriority Priority { get; set; }
        public ScheduleTaskStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public string TechnicianComment { get; set; } = string.Empty; // Technician can update this field
        public string SkillLevel { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;

        public PlanEntity? Plan { get; set; }
        public TechnicianEntity Technician { get; set; } = default!;
        public RegionEntity? Region { get; set; }
        public ObjectEntity? Object { get; set; }
        public TaskEvaluationEntity? Evaluation { get; set; }
    }
}
