using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class TaskEntity
    {
        public long Id { get; set; }
        public long? PlanId { get; set; }
        public long TechnicianId { get; set; }
        public long ServiceObjectId { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public TaskPriority Priority { get; set; }
        public ScheduleTaskStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public string TechnicianComment { get; set; } = string.Empty; // Technician can update this field
        public string SkillLevel { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public long? SourceTaskId { get; set; }
        public long? SourceBacklogItemId { get; set; }

        /// <summary>
        /// Set when this task was pushed forward by a repair during unavailability creation.
        /// Cleared when the unavailability is deleted and the task is moved back to its original slot.
        /// </summary>
        public long? SourceUnavailabilityId { get; set; }

        /// <summary>Pre-repair start time. Populated alongside SourceUnavailabilityId.</summary>
        public DateTime? OriginalStartAt { get; set; }

        /// <summary>Pre-repair end time. Populated alongside SourceUnavailabilityId.</summary>
        public DateTime? OriginalEndAt { get; set; }

        /// <summary>
        /// Set when a technician changes the task status (Completed or Conflict).
        /// Represents the real-world moment the task ended, as recorded by the server.
        /// Null for tasks still in progress or auto-expired without technician action.
        /// </summary>
        public DateTime? ActualEndAt { get; set; }

        public PlanEntity? Plan { get; set; }
        public TechnicianEntity Technician { get; set; } = default!;
        public ServiceObjectEntity ServiceObject { get; set; } = default!;
        public TaskEvaluationEntity? Evaluation { get; set; }
    }
}
