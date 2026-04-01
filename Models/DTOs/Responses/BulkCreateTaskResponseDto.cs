using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Models.DTOs.Responses;

public class BulkCreateTaskResponseDto
{
    public bool AllSucceeded { get; set; }
    public List<long> TaskIds { get; set; } = new();
    public List<BulkCreateItemErrorDto> Errors { get; set; } = new();
}

public class BulkCreateItemErrorDto
{
    public int Index { get; set; }
    public string Message { get; set; } = string.Empty;
}
