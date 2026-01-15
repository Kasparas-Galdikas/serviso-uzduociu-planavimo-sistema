using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests
{
    public sealed class DragPreviewRequestDto
    {
        [Required]
        public long TaskId { get; set; }

        [Required]
        public long TargetTechnicianId { get; set; }

        /// <summary>
        /// Target start time in UTC for the hovered drop slot.
        /// </summary>
        [Required]
        public DateTime TargetStartAt { get; set; }

        /// <summary>
        /// Target end time in UTC for the hovered drop slot.
        /// </summary>
        [Required]
        public DateTime TargetEndAt { get; set; }
    }
}
