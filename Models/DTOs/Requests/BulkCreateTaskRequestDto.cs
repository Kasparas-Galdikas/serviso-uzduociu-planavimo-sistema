using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.DTOs.Requests;

public class BulkCreateTaskRequestDto
{
    [Required, MinLength(1)]
    public List<CreateTaskRequestDto> Items { get; set; } = new();
}
