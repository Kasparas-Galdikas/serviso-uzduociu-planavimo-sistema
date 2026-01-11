using System;
using System.Threading.Tasks;

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
        /// Checks if a date is in the past (before today).
        /// </summary>
        /// <param name="dateToCheck">Date to check</param>
        /// <returns>True if date is in the past</returns>
        bool IsDateInPast(DateTime dateToCheck);

        /// <summary>
        /// Checks if a datetime (in UTC) is in the past (before now).
        /// </summary>
        /// <param name="dateTimeUtc">UTC datetime to check</param>
        /// <returns>True if datetime is in the past</returns>
        bool IsDateTimeInPastUtc(DateTime dateTimeUtc);

        /// <summary>
        /// Gets the start of week (Monday) for a given date.
        /// </summary>
        DateTime GetWeekStart(DateTime date);

        /// <summary>
        /// Gets the end of week (Sunday) for a given date.
        /// </summary>
        DateTime GetWeekEnd(DateTime date);

        /// <summary>
        /// Converts minutes since midnight to HH:mm format.
        /// </summary>
        /// <param name="minutes">Minutes since midnight (0-1440)</param>
        /// <returns>Time string in HH:mm format</returns>
        string MinutesToTimeString(int minutes);

        /// <summary>
        /// Gets the user's local "now" (current datetime in their timezone).
        /// </summary>
        /// <param name="timezoneOffsetMinutes">User's timezone offset in minutes</param>
        /// <returns>Current datetime in user's local timezone</returns>
        DateTime GetUserLocalNow(int timezoneOffsetMinutes);

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
        /// Checks if a date is in the past from the user's local perspective.
        /// </summary>
        /// <param name="dateToCheck">Date to check</param>
        /// <param name="timezoneOffsetMinutes">User's timezone offset</param>
        /// <returns>True if date is before user's local today</returns>
        bool IsDateInPastForUser(DateTime dateToCheck, int timezoneOffsetMinutes);
    }
}
