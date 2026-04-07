using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests
{
    public class CreateUnavailabilityRequestDto
    {
        [Required]
        public long TechnicianId { get; set; }

        /// <summary>yyyy-MM-ddTHH:mm in browser local time.</summary>
        [Required]
        [RegularExpression(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$",
            ErrorMessage = "Naudokite yyyy-MM-ddTHH:mm formata.")]
        public string PeriodStart { get; set; } = string.Empty;

        /// <summary>yyyy-MM-ddTHH:mm in browser local time.</summary>
        [Required]
        [RegularExpression(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$",
            ErrorMessage = "Naudokite yyyy-MM-ddTHH:mm formata.")]
        public string PeriodEnd { get; set; } = string.Empty;
    }
}
