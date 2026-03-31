using System;
using Microsoft.AspNetCore.Http;

namespace ServiceTaskPlanningSystem.Services.Time
{
    /// <summary>
    /// Implementation of IScheduleTimeService.
    /// Encapsulates all time conversion and comparison logic for the schedule system.
    /// </summary>
    public class ScheduleTimeService : IScheduleTimeService
    {
        public virtual DateTime GetCurrentUtcTime()
        {
            return DateTime.UtcNow;
        }

        public DateTime GetLocalToday()
        {
            return DateTime.Today;
        }

        public DateTime ConvertLocalToUtc(DateTime localDateTime, int timezoneOffsetMinutes)
        {
            if (localDateTime.Kind == DateTimeKind.Utc)
            {
                return localDateTime;
            }

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

        public bool IsTravelStartFeasible(DateTime taskStartUtc, int inboundTravelMinutes)
        {
            if (inboundTravelMinutes <= 0) return true;
            var travelStartUtc = taskStartUtc.AddMinutes(-inboundTravelMinutes);
            return travelStartUtc >= GetCurrentUtcTime();
        }

        public int CeilingToSlot(int minutesSinceMidnight, int slotSizeMinutes)
        {
            if (slotSizeMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(slotSizeMinutes));
            if (minutesSinceMidnight < 0) return 0;
            return ((minutesSinceMidnight + slotSizeMinutes - 1) / slotSizeMinutes) * slotSizeMinutes;
        }

        public CreatablePlacement GetEarliestCreatablePlacement(
            DateTime targetDate,
            int timezoneOffsetMinutes,
            int slotSizeMinutes,
            int dayStartMinutes,
            int dayEndMinutes)
        {
            var userLocalNow = ConvertUtcToLocal(GetCurrentUtcTime(), timezoneOffsetMinutes);
            var localToday = userLocalNow.Date;
            var requestedDate = targetDate.Date;

            if (requestedDate != localToday)
            {
                return new CreatablePlacement(requestedDate, dayStartMinutes);
            }

            var nowMinutes = (int)userLocalNow.TimeOfDay.TotalMinutes;
            var roundedNow = CeilingToSlot(nowMinutes, slotSizeMinutes);
            var earliestToday = Math.Max(dayStartMinutes, roundedNow);
            if (earliestToday <= dayEndMinutes)
            {
                return new CreatablePlacement(localToday, earliestToday);
            }

            return new CreatablePlacement(localToday.AddDays(1), dayStartMinutes);
        }

        public int GetEarliestCreatableStartMinutes(DateTime targetDate, int timezoneOffsetMinutes, int slotSizeMinutes, int dayStartMinutes)
        {
            return GetEarliestCreatablePlacement(
                targetDate,
                timezoneOffsetMinutes,
                slotSizeMinutes,
                dayStartMinutes,
                int.MaxValue).StartMinutes;
        }

        public int GetNextCreatableStartMinutes(DateTime targetDate, int timezoneOffsetMinutes, int slotSizeMinutes, int dayStartMinutes, int? afterEndMinutes = null)
        {
            var earliest = GetEarliestCreatableStartMinutes(targetDate, timezoneOffsetMinutes, slotSizeMinutes, dayStartMinutes);
            var candidate = afterEndMinutes.HasValue ? Math.Max(earliest, afterEndMinutes.Value) : earliest;
            return CeilingToSlot(candidate, slotSizeMinutes);
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

        public DateTime GetUserLocalToday(int timezoneOffsetMinutes)
        {
            var userLocalNow = ConvertUtcToLocal(GetCurrentUtcTime(), timezoneOffsetMinutes);
            return userLocalNow.Date;
        }

        public DateTime CreateUtcFromDateAndMinutes(DateTime targetDate, int minutesSinceMidnight, int timezoneOffsetMinutes)
        {
            // targetDate is a local calendar day marker; ignore Kind (Utc/Local/Unspecified)
            // to avoid treating local day boundaries as already-UTC timestamps.
            var localDate = DateTime.SpecifyKind(targetDate.Date, DateTimeKind.Unspecified);
            var localDateTime = localDate.AddMinutes(minutesSinceMidnight);

            // Convert to UTC for storage
            return ConvertLocalToUtc(localDateTime, timezoneOffsetMinutes);
        }

        public int GetBrowserTimezoneOffset(HttpRequest request)
        {
            // Try header first (for AJAX requests)
            if (request.Headers.TryGetValue("X-Timezone-Offset", out var tzHeader) &&
                int.TryParse(tzHeader.ToString(), out var offset))
            {
                return offset;
            }

            // Fallback to cookie (for initial page loads)
            if (request.Cookies.TryGetValue("timezone_offset", out var tzCookie) &&
                int.TryParse(tzCookie, out var cookieOffset))
            {
                return cookieOffset;
            }

            return 0;
        }
    }
}

