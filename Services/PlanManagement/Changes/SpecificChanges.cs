using System;

namespace ServiceTaskPlanningSystem.Services.PlanManagement
{
    /// <summary>
    /// Change descriptor for moving task to different time on same day.
    /// </summary>
    public sealed class TimeShiftChange : PlanChange
    {
        public override ChangeType Type => ChangeType.TimeShift;
        public override string Description => 
            $"Move to {SuggestedStartAt:HH:mm}-{SuggestedEndAt:HH:mm}";
        public override string Rationale => 
            $"Reduce peak hours pressure by {ShiftMinutes / 60.0:F1} hours";

        /// <summary>
        /// New start time in UTC.
        /// </summary>
        public DateTime SuggestedStartAt { get; init; }

        /// <summary>
        /// New end time in UTC.
        /// </summary>
        public DateTime SuggestedEndAt { get; init; }

        /// <summary>
        /// Minutes shifted (negative = earlier, positive = later).
        /// </summary>
        public int ShiftMinutes { get; init; }
    }

    /// <summary>
    /// Change descriptor for assigning task to different technician.
    /// </summary>
    public sealed class TechnicianSwapChange : PlanChange
    {
        public override ChangeType Type => ChangeType.TechnicianSwap;
        public override string Description => 
            $"Assign to technician {SuggestedTechnicianId}";
        public override string Rationale => 
            "Distribute workload to reduce individual load";

        /// <summary>
        /// Technician ID to assign task to.
        /// </summary>
        public long SuggestedTechnicianId { get; init; }
    }

    /// <summary>
    /// Change descriptor for moving task to different day.
    /// </summary>
    public sealed class DayShiftChange : PlanChange
    {
        public override ChangeType Type => ChangeType.DayShift;
        public override string Description => 
            $"Move to {SuggestedStartAt:dddd, dd MMMM}";
        public override string Rationale => 
            "Balance weekly workload distribution";

        /// <summary>
        /// New start time on different day (UTC).
        /// </summary>
        public DateTime SuggestedStartAt { get; init; }

        /// <summary>
        /// New end time on different day (UTC).
        /// </summary>
        public DateTime SuggestedEndAt { get; init; }

        /// <summary>
        /// Days shifted (1 = tomorrow, -1 = yesterday).
        /// </summary>
        public int DaysShifted { get; init; }
    }
}
