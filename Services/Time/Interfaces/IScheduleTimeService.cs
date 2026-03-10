using System;
using Microsoft.AspNetCore.Http;

namespace ServiceTaskPlanningSystem.Services.Time
{
    /// <summary>
    /// Central service for all time-related operations.
    /// Handles conversions between UTC (storage) and local time (display/user input).
    /// Single source of truth for timezone logic across the application.
    /// </summary>
    public interface IScheduleTimeService
    {
        DateTime GetCurrentUtcTime();

        DateTime GetLocalToday();

        /// <summary>
        /// Converts local datetime to UTC.
        /// </summary>
        /// <param name="localDateTime">Local datetime from user input</param>
        /// <param name="timezoneOffsetMinutes">User's timezone offset in minutes (e.g., +120 for UTC+2)</param>
        /// <returns>UTC datetime</returns>
        DateTime ConvertLocalToUtc(DateTime localDateTime, int timezoneOffsetMinutes);

        /// <summary>
        /// Converts UTC datetime to local time.
        /// </summary>
        /// <param name="utcDateTime">UTC datetime from storage</param>
        /// <param name="timezoneOffsetMinutes">User's timezone offset in minutes</param>
        /// <returns>Local datetime for display</returns>
        DateTime ConvertUtcToLocal(DateTime utcDateTime, int timezoneOffsetMinutes);

        /// <summary>
        /// Checks if a task can be edited based on its end time.
        /// A task is editable if it hasn't ended yet (EndAt > now).
        /// </summary>
        /// <param name="taskEndUtc">Task end time in UTC</param>
        /// <returns>True if task can still be edited</returns>
        bool IsTaskStillEditable(DateTime taskEndUtc);

        /// <summary>
        /// Checks that the travel leg leading into a task would not start in the past.
        /// Returns true if inboundTravelMinutes is zero/negative (no travel required)
        /// or if taskStartUtc - inboundTravelMinutes >= now.
        /// </summary>
        bool IsTravelStartFeasible(DateTime taskStartUtc, int inboundTravelMinutes);

        /// <summary>
        /// Rounds minutes up to the nearest slot boundary.
        /// Example: 10:01 with 30-min slot -> 10:30 (630 minutes).
        /// </summary>
        int CeilingToSlot(int minutesSinceMidnight, int slotSizeMinutes);

        /// <summary>
        /// Returns the earliest local start-minute allowed for task creation on the selected date.
        /// For today (in user's timezone), this is "now" rounded up to the slot boundary.
        /// For future dates, returns dayStartMinutes.
        /// </summary>
        int GetEarliestCreatableStartMinutes(DateTime targetDate, int timezoneOffsetMinutes, int slotSizeMinutes, int dayStartMinutes);

        /// <summary>
        /// Gets the start of week (Monday) for a given date.
        /// </summary>
        DateTime GetWeekStart(DateTime date);

        /// <summary>
        /// Gets the end of week (Sunday) for a given date.
        /// </summary>
        DateTime GetWeekEnd(DateTime date);

        /// <summary>
        /// Gets the user's local "today" (current date in their timezone).
        /// </summary>
        /// <param name="timezoneOffsetMinutes">User's timezone offset in minutes</param>
        /// <returns>Current date in user's local timezone</returns>
        DateTime GetUserLocalToday(int timezoneOffsetMinutes);

        /// <summary>
        /// Creates a UTC datetime from a date and minutes since midnight (local time).
        /// Used for task creation/updates where user selects date + time slot.
        /// </summary>
        /// <param name="targetDate">The target date (local)</param>
        /// <param name="minutesSinceMidnight">Minutes since midnight (0-1440)</param>
        /// <param name="timezoneOffsetMinutes">User's timezone offset</param>
        /// <returns>UTC datetime ready for database storage</returns>
        DateTime CreateUtcFromDateAndMinutes(DateTime targetDate, int minutesSinceMidnight, int timezoneOffsetMinutes);

        /// <summary>
        /// Extracts browser timezone offset from HTTP request headers or cookies.
        /// Checks X-Timezone-Offset header first, then timezone_offset cookie.
        /// </summary>
        /// <param name="request">The HTTP request</param>
        /// <returns>Timezone offset in minutes, or 0 if not found</returns>
        int GetBrowserTimezoneOffset(HttpRequest request);
    }
}
