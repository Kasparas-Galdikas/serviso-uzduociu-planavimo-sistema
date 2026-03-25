using System;

namespace ServiceTaskPlanningSystem.Data
{
    public class BacklogItemEntity
    {
        public long Id { get; set; }
        public long CreatedByUserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public TaskPriority Priority { get; set; }
        public BacklogItemType ItemType { get; set; }
        public string SkillLevel { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public string ObjectAddress { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        public UserEntity CreatedBy { get; set; } = default!;
    }
}
