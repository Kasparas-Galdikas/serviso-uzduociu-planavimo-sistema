namespace ServiceTaskPlanningSystem.Models.Common
{
    public sealed record PagedResult<T>(
        System.Collections.Generic.List<T> Items,
        int TotalCount,
        int Page,
        int PageSize)
    {
        public bool HasMore => Page * PageSize < TotalCount;
    }
}
