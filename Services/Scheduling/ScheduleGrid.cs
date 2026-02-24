namespace ServiceTaskPlanningSystem.Services.Scheduling
{
    // Defines the time grid for scheduling tasks, used for visualization and conflict detection.
    public static class ScheduleGrid
    {
        public const int DayStartMinutes = 8 * 60;  // 08:00
        public const int DayEndMinutes = 20 * 60;   // 20:00
        public const int SlotSizeMinutes = 30;      // 30 min
    }
}

