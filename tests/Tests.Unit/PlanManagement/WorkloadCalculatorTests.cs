using System;
using System.Collections.Generic;
using FluentAssertions;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class WorkloadCalculatorTests
{
    [Fact]
    public void Evaluate_ExcludesCanceledTasks_AndTheirTravelFromWorkload()
    {
        var technician = CreateTechnician();
        var day = DateTime.UtcNow.Date.AddDays(1);

        var active = CreateTask(1, day.AddHours(8), day.AddHours(10), ScheduleTaskStatus.InProgress);
        var canceled = CreateTask(2, day.AddHours(10), day.AddHours(12), ScheduleTaskStatus.Canceled);
        var travel = new Dictionary<long, int>
        {
            [1] = 30,
            [2] = 120
        };

        var snapshot = WorkloadCalculator.Evaluate(technician, new List<TaskEntity> { active, canceled }, new List<TaskEntity> { active, canceled }, travel);

        snapshot.DailyHours.Should().Be(2.5);  // 2h task + 0.5h travel
        snapshot.WeeklyHours.Should().Be(2.5);
        snapshot.Context.IsDailyLimitExceeded.Should().BeFalse();
        snapshot.Context.IsWeeklyLimitExceeded.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenTravelPushesDailyOverLimit_ReturnsConflict()
    {
        var technician = CreateTechnician();
        var day = DateTime.UtcNow.Date.AddDays(1);

        var t1 = CreateTask(1, day.AddHours(8), day.AddHours(12), ScheduleTaskStatus.InProgress);
        var t2 = CreateTask(2, day.AddHours(13), day.AddHours(17), ScheduleTaskStatus.InProgress);
        var travel = new Dictionary<long, int>
        {
            [1] = 30,
            [2] = 30
        };

        var snapshot = WorkloadCalculator.Evaluate(technician, new List<TaskEntity> { t1, t2 }, new List<TaskEntity> { t1, t2 }, travel);

        snapshot.DailyHours.Should().Be(9); // 8h task + 1h travel
        snapshot.Context.IsDailyLimitExceeded.Should().BeTrue();
        snapshot.Context.ComputeLoadState().Should().Be(LoadState.Conflict);
    }

    [Fact]
    public void DetectTravelViolations_FlagsOnlyTasksWhereRequiredExceedsGap()
    {
        var day = DateTime.UtcNow.Date.AddDays(1);
        var t1 = CreateTask(1, day.AddHours(8), day.AddHours(9), ScheduleTaskStatus.InProgress);
        var t2 = CreateTask(2, day.AddHours(9).AddMinutes(30), day.AddHours(10).AddMinutes(30), ScheduleTaskStatus.InProgress);
        var t3 = CreateTask(3, day.AddHours(11), day.AddHours(12), ScheduleTaskStatus.InProgress);
        var travel = new Dictionary<long, int>
        {
            [2] = 45, // available 30 => violation
            [3] = 20  // available 30 => ok
        };

        var violations = WorkloadCalculator.DetectTravelViolations(new List<TaskEntity> { t1, t2, t3 }, travel);

        violations.Should().ContainKey(2);
        violations[2].Required.Should().Be(45);
        violations[2].Available.Should().Be(30);
        violations.Should().NotContainKey(3);
    }

    private static TechnicianEntity CreateTechnician() =>
        new()
        {
            Id = 1,
            DailyLimit = 8,
            WeeklyLimit = 40
        };

    private static TaskEntity CreateTask(long id, DateTime start, DateTime end, ScheduleTaskStatus status) =>
        new()
        {
            Id = id,
            StartAt = DateTime.SpecifyKind(start, DateTimeKind.Utc),
            EndAt = DateTime.SpecifyKind(end, DateTimeKind.Utc),
            Status = status
        };
}
