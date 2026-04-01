using System;
using FluentAssertions;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.Time;

public class ScheduleTimeServiceTests
{
    [Fact]
    public void CeilingToSlot_RoundsUpToNearestSlot()
    {
        var sut = new ScheduleTimeService();

        sut.CeilingToSlot(601, 30).Should().Be(630);
        sut.CeilingToSlot(630, 30).Should().Be(630);
        sut.CeilingToSlot(0, 30).Should().Be(0);
    }

    [Fact]
    public void GetEarliestCreatableStartMinutes_Today_UsesRoundedNow()
    {
        var sut = new FakeScheduleTimeService(new DateTime(2026, 3, 10, 9, 7, 0, DateTimeKind.Utc));

        var result = sut.GetEarliestCreatableStartMinutes(
            targetDate: new DateTime(2026, 3, 10),
            timezoneOffsetMinutes: 0,
            slotSizeMinutes: 30,
            dayStartMinutes: ScheduleGrid.DayStartMinutes);

        result.Should().Be(570); // 09:30
    }

    [Fact]
    public void GetEarliestCreatableStartMinutes_FutureDate_UsesDayStart()
    {
        var sut = new FakeScheduleTimeService(new DateTime(2026, 3, 10, 20, 1, 0, DateTimeKind.Utc));

        var result = sut.GetEarliestCreatableStartMinutes(
            targetDate: new DateTime(2026, 3, 11),
            timezoneOffsetMinutes: 0,
            slotSizeMinutes: 30,
            dayStartMinutes: ScheduleGrid.DayStartMinutes);

        result.Should().Be(ScheduleGrid.DayStartMinutes);
    }

    [Fact]
    public void GetEarliestCreatablePlacement_TodayAfterDayEnd_RollsToTomorrowDayStart()
    {
        var sut = new FakeScheduleTimeService(new DateTime(2026, 3, 10, 20, 1, 0, DateTimeKind.Utc));

        var result = sut.GetEarliestCreatablePlacement(
            targetDate: new DateTime(2026, 3, 10),
            timezoneOffsetMinutes: 0,
            slotSizeMinutes: 30,
            dayStartMinutes: ScheduleGrid.DayStartMinutes,
            dayEndMinutes: ScheduleGrid.DayEndMinutes);

        result.TargetDate.Should().Be(new DateTime(2026, 3, 11));
        result.StartMinutes.Should().Be(ScheduleGrid.DayStartMinutes);
    }

    private sealed class FakeScheduleTimeService : ScheduleTimeService
    {
        private readonly DateTime _nowUtc;

        public FakeScheduleTimeService(DateTime nowUtc)
        {
            _nowUtc = nowUtc;
        }

        public override DateTime GetCurrentUtcTime() => _nowUtc;
    }
}
