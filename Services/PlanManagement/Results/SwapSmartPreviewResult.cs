namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Reason codes returned by the swap-smart preview endpoint so the UI can
    /// show a meaningful message without hitting the execute path.
    /// </summary>
    public enum SwapPreviewReason
    {
        Ok,
        /// <summary>Strict slot-swap works with no adjustments needed.</summary>
        StrictOk,
        /// <summary>Smart planner found a valid schedule with minor time adjustments.</summary>
        SmartOk,
        /// <summary>Hard constraint: different technician, day, plan, canceled, etc.</summary>
        HardConstraint,
        /// <summary>No feasible schedule found even after smart planning.</summary>
        Infeasible
    }

    public sealed record SwapSmartPreviewResult(
        bool Feasible,
        SwapPreviewReason Reason,
        string? Message = null,
        bool RequiresSmartAdjustment = false)
    {
        public static SwapSmartPreviewResult Ok(bool smartRequired = false) =>
            new(true, smartRequired ? SwapPreviewReason.SmartOk : SwapPreviewReason.StrictOk,
                RequiresSmartAdjustment: smartRequired);

        public static SwapSmartPreviewResult Hard(string message) =>
            new(false, SwapPreviewReason.HardConstraint, message);

        public static SwapSmartPreviewResult Fail(string? message = null) =>
            new(false, SwapPreviewReason.Infeasible, message);
    }
}
