namespace ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation
{
    public sealed class RouteEvaluationRequest
    {
        public long TaskId { get; init; }
        public long? PlanId { get; init; }
        public long TechnicianId { get; init; }
        public long ServiceAddressId { get; init; }
        public long WorkplaceAddressId { get; init; }
        public string? ServiceAddressText { get; init; }
        public string? WorkplaceAddressText { get; init; }
        public bool AddressChanged { get; init; }
    }
}
