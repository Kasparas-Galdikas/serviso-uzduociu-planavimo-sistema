using ServiceTaskPlanningSystem.Models.DTOs.Responses;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    public enum DragPreviewError
    {
        None,
        TaskNotFound,
        NotEditable,
        NoPlan,
        TechnicianNotFound
    }

    public sealed record DragPreviewResult(
        DragPreviewError Error,
        string? Message = null,
        DragPreviewResponseDto? Response = null)
    {
        public bool Ok => Error == DragPreviewError.None;
    }
}
