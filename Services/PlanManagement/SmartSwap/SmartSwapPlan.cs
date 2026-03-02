using System;
using System.Collections.Generic;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.SmartSwap
{
    public sealed record SmartSwapAdjustment(
        long TaskId,
        DateTime StartAt,
        DateTime EndAt);

    public sealed record SmartSwapPlan(
        bool IsSuccess,
        string? Message,
        IReadOnlyList<SmartSwapAdjustment> Adjustments)
    {
        public static SmartSwapPlan Fail(string message) =>
            new(false, message, Array.Empty<SmartSwapAdjustment>());
    }
}

