using System;

namespace ServiceTaskPlanningSystem.Services.Time
{
    /// <summary>
    /// Implementation of IScheduleTimeService.
    /// Encapsulates all time conversion and comparison logic for the schedule system.
    /// </summary>
    public class ScheduleTimeService : IScheduleTimeService
    {
        public DateTime GetCurrentUtcTime()
        {
            return DateTime.UtcNow;
        }

        public DateTime GetLocalToday()
        {
            return DateTime.Today;
        }

        public DateTime ConvertLocalToUtc(DateTime localDateTime, int timezoneOffsetMinutes)
        {
            // Local time is: UTC + offset
            // So to get UTC: Local - offset
            var utcDateTime = localDateTime.AddMinutes(-timezoneOffsetMinutes);
            return DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        }

        public DateTime ConvertUtcToLocal(DateTime utcDateTime, int timezoneOffsetMinutes)
        {
            // UTC + offset = Local time
            var localDateTime = utcDateTime.AddMinutes(timezoneOffsetMinutes);
            return DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        }

        public bool IsTaskStillEditable(DateTime taskEndUtc)
        {
            var now = GetCurrentUtcTime();
            return taskEndUtc > now;
        }

        public bool IsDateInPast(DateTime dateToCheck)
        {
            var today = GetLocalToday();
            return dateToCheck.Date < today.Date;
        }

        public bool IsDateTimeInPastUtc(DateTime dateTimeUtc)
        {
            var now = GetCurrentUtcTime();
            return dateTimeUtc < now;
        }

        public DateTime GetWeekStart(DateTime date)
        {
            var dayOfWeek = (int)date.DayOfWeek;
            // Convert: Sunday=0 to Monday=1, ..., Saturday=6
            var daysFromMonday = (dayOfWeek == 0 ? 7 : dayOfWeek) - 1;
            return date.AddDays(-daysFromMonday).Date;
        }

        public DateTime GetWeekEnd(DateTime date)
        {
            var weekStart = GetWeekStart(date);
            return weekStart.AddDays(6);
        }

        public string MinutesToTimeString(int minutes)
        {
            var mm = Math.Max(0, minutes | 0);
            var h = String.Format("{0:D2}", Math.Floor((double)mm / 60));
            var min = String.Format("{0:D2}", mm % 60);
            return $"{h}:{min}";
        }
    }
}
