using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class DayScheduleFitCoreTests
{
    private readonly DayScheduleFitCore _sut = new();

    [Fact]
    public async Task FitAsync_WhenTravelGapTooSmall_ShiftsSuffixForward()
    {
        var day = DateTime.UtcNow.Date.AddDays(1);
        var result = await _sut.FitAsync(new DayScheduleFitRequest(
            Tasks:
            [
                new ScheduleRepairTaskSnapshot(1, 10, day.AddHours(8), TimeSpan.FromHours(1)),
                new ScheduleRepairTaskSnapshot(2, 20, day.AddHours(9), TimeSpan.FromHours(1))
            ],
            Anchors: Array.Empty<ScheduleRepairAnchor>(),
            RouteLookup: new Dictionary<(long From, long To), int>
            {
                [(1, 10)] = 0,
                [(10, 20)] = 90
            },
            WorkplaceAddressId: 1,
            DayStartUtc: day.AddHours(8),
            DayEndUtc: day.AddHours(17),
            TimezoneOffsetMinutes: 0));

        result.Success.Should().BeTrue();
        result.RepairKind.Should().Be(ScheduleRepairKind.Shifted);
        result.Adjustments.Should().ContainSingle();
        result.Adjustments.Single().TaskId.Should().Be(2);
        result.Adjustments.Single().StartAt.Should().Be(day.AddHours(10.5));
    }

    [Fact]
    public async Task FitAsync_WhenAnchoredTaskOverflows_PullsTaskBack()
    {
        var day = DateTime.UtcNow.Date.AddDays(1);
        var result = await _sut.FitAsync(new DayScheduleFitRequest(
            Tasks:
            [
                new ScheduleRepairTaskSnapshot(1, 10, day.AddHours(16), TimeSpan.FromHours(2))
            ],
            Anchors:
            [
                new ScheduleRepairAnchor(1, day.AddHours(16))
            ],
            RouteLookup: new Dictionary<(long From, long To), int>
            {
                [(1, 10)] = 0
            },
            WorkplaceAddressId: 1,
            DayStartUtc: day.AddHours(8),
            DayEndUtc: day.AddHours(17),
            TimezoneOffsetMinutes: 0));

        result.Success.Should().BeTrue();
        result.Adjustments.Should().ContainSingle();
        result.Adjustments.Single().StartAt.Should().Be(day.AddHours(8));
    }

    [Fact]
    public async Task FitAsync_WhenRouteMissing_Fails()
    {
        var day = DateTime.UtcNow.Date.AddDays(1);
        var result = await _sut.FitAsync(new DayScheduleFitRequest(
            Tasks:
            [
                new ScheduleRepairTaskSnapshot(1, 10, day.AddHours(8), TimeSpan.FromHours(1))
            ],
            Anchors: Array.Empty<ScheduleRepairAnchor>(),
            RouteLookup: new Dictionary<(long From, long To), int>(),
            WorkplaceAddressId: 1,
            DayStartUtc: day.AddHours(8),
            DayEndUtc: day.AddHours(17),
            TimezoneOffsetMinutes: 0));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("truksta marsruto duomenu");
    }
}
