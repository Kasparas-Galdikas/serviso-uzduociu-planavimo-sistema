using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class PlanScopeSegregationServiceTests
{
    private static readonly DateTime CurrentWeekStart = new(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentWeekEndExclusive = CurrentWeekStart.AddDays(7);

    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly ILogger<PlanScopeSegregationService> _logger = NullLogger<PlanScopeSegregationService>.Instance;

    public PlanScopeSegregationServiceTests()
    {
        _time.GetCurrentWeekBoundary().Returns(new WeekBoundary(
            CurrentWeekStart,
            CurrentWeekEndExclusive,
            CurrentWeekStart,
            CurrentWeekEndExclusive));
        _time.GetCurrentUtcTime().Returns(new DateTime(2026, 4, 16, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_MovesOldActiveTasksToSupersededPlan()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddDays(-1).AddHours(8), endAt: CurrentWeekStart.AddDays(-1).AddHours(9));
        SeedTask(db, id: 101, planId: 1, startAt: CurrentWeekStart.AddHours(8), endAt: CurrentWeekStart.AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.WorkPerformed.Should().BeTrue();
        result.ActivePlanId.Should().Be(1);
        result.MovedTaskCount.Should().Be(1);
        result.CreatedSupersededPlanCount.Should().Be(1);
        result.ReusedSupersededPlanCount.Should().Be(0);
        result.AffectedSupersededPlanIds.Should().ContainSingle();

        var tasks = await db.Tasks.OrderBy(t => t.Id).ToListAsync();
        tasks.Single(t => t.Id == 100).PlanId.Should().Be(result.AffectedSupersededPlanIds.Single());
        tasks.Single(t => t.Id == 101).PlanId.Should().Be(1);

        var activePlan = await db.Plans.SingleAsync(p => p.Id == 1);
        activePlan.PeriodStart.Date.Should().Be(CurrentWeekStart.Date);

        var supersededPlan = await db.Plans.SingleAsync(p => p.Status == PlanStatus.Superseded);
        supersededPlan.PeriodStart.Date.Should().Be(CurrentWeekStart.AddDays(-1).Date);
        supersededPlan.PeriodEnd.Date.Should().Be(CurrentWeekStart.AddDays(-1).Date);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_ReusesExistingSupersededPlan()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        db.Plans.Add(new PlanEntity
        {
            Id = 2,
            PeriodStart = CurrentWeekStart.AddDays(-10),
            PeriodEnd = CurrentWeekStart.AddDays(-8),
            Weeks = 1,
            Status = PlanStatus.Superseded,
            SupersededByPlanId = 1,
            CreatedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddDays(-1).AddHours(8), endAt: CurrentWeekStart.AddDays(-1).AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.WorkPerformed.Should().BeTrue();
        result.CreatedSupersededPlanCount.Should().Be(0);
        result.ReusedSupersededPlanCount.Should().Be(1);
        result.AffectedSupersededPlanIds.Should().Equal(2);

        var movedTask = await db.Tasks.SingleAsync(t => t.Id == 100);
        movedTask.PlanId.Should().Be(2);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_ExpandsSupersededPlanSpanToCoverMovedTasks()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        db.Plans.Add(new PlanEntity
        {
            Id = 2,
            PeriodStart = CurrentWeekStart.AddDays(-10),
            PeriodEnd = CurrentWeekStart.AddDays(-9),
            Weeks = 1,
            Status = PlanStatus.Superseded,
            SupersededByPlanId = 1,
            CreatedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddDays(-3).AddHours(8), endAt: CurrentWeekStart.AddDays(-3).AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        var supersededPlan = await db.Plans.SingleAsync(p => p.Id == 2);
        supersededPlan.PeriodStart.Date.Should().Be(CurrentWeekStart.AddDays(-10).Date);
        supersededPlan.PeriodEnd.Date.Should().Be(CurrentWeekStart.AddDays(-3).Date);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_NormalizesActivePlanStartToCurrentWeek_WhenHistoricalWindowExists()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-10), periodEnd: CurrentWeekStart.AddDays(10));
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddDays(-2).AddHours(8), endAt: CurrentWeekStart.AddDays(-2).AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        var activePlan = await db.Plans.SingleAsync(p => p.Id == 1);
        activePlan.PeriodStart.Date.Should().Be(CurrentWeekStart.Date);
        activePlan.PeriodEnd.Date.Should().Be(CurrentWeekStart.AddDays(10).Date);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_DoesNotMoveTasksOnCurrentWeekBoundary()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddDays(-1).AddHours(8), endAt: CurrentWeekStart.AddDays(-1).AddHours(9));
        SeedTask(db, id: 101, planId: 1, startAt: CurrentWeekStart.AddHours(8), endAt: CurrentWeekStart.AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.MovedTaskCount.Should().Be(1);
        var boundaryTask = await db.Tasks.SingleAsync(t => t.Id == 101);
        boundaryTask.PlanId.Should().Be(1);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_ReassignsDetachedAndOrphanedCurrentWeekTasksToActive()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: null, startAt: CurrentWeekStart.AddHours(8), endAt: CurrentWeekStart.AddHours(9));
        SeedTask(db, id: 101, planId: 999, startAt: CurrentWeekStart.AddDays(1).AddHours(8), endAt: CurrentWeekStart.AddDays(1).AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.WorkPerformed.Should().BeTrue();
        result.MovedTaskCount.Should().Be(2);
        result.AffectedSupersededPlanIds.Should().BeEmpty();

        var tasks = await db.Tasks.OrderBy(t => t.Id).ToListAsync();
        tasks.Should().OnlyContain(t => t.PlanId == 1);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_SupersedesDetachedAndOrphanedHistoricalTasks()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: null, startAt: CurrentWeekStart.AddDays(-1).AddHours(8), endAt: CurrentWeekStart.AddDays(-1).AddHours(9));
        SeedTask(db, id: 101, planId: 999, startAt: CurrentWeekStart.AddDays(-2).AddHours(8), endAt: CurrentWeekStart.AddDays(-2).AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.WorkPerformed.Should().BeTrue();
        result.MovedTaskCount.Should().Be(2);
        result.AffectedSupersededPlanIds.Should().ContainSingle();

        var supersededPlanId = result.AffectedSupersededPlanIds.Single();
        var tasks = await db.Tasks.OrderBy(t => t.Id).ToListAsync();
        tasks.Should().OnlyContain(t => t.PlanId == supersededPlanId);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_WhenNoHistoricalTasks_OnlyNormalizesActivePlan()
    {
        await using var db = CreateContext();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-2), periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddHours(8), endAt: CurrentWeekStart.AddHours(9));
        await db.SaveChangesAsync();

        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.WorkPerformed.Should().BeTrue();
        result.MovedTaskCount.Should().Be(0);
        result.CreatedSupersededPlanCount.Should().Be(0);
        result.ReusedSupersededPlanCount.Should().Be(0);
        result.AffectedSupersededPlanIds.Should().BeEmpty();

        var activePlan = await db.Plans.SingleAsync(p => p.Id == 1);
        activePlan.PeriodStart.Date.Should().Be(CurrentWeekStart.Date);
        activePlan.PeriodEnd.Date.Should().Be(CurrentWeekStart.AddDays(6).Date);
    }

    [Fact]
    public async Task EnsureActivePlanScopedAsync_WhenNoActivePlan_ReturnsNoWork()
    {
        await using var db = CreateContext();
        var sut = CreateService(db);

        var result = await sut.EnsureActivePlanScopedAsync(CancellationToken.None);

        result.WorkPerformed.Should().BeFalse();
        result.ActivePlanId.Should().BeNull();
        result.MovedTaskCount.Should().Be(0);
        result.AffectedSupersededPlanIds.Should().BeEmpty();
        db.Plans.Should().BeEmpty();
        db.Tasks.Should().BeEmpty();
    }

    private PlanScopeSegregationService CreateService(ScheduleDbContext db) =>
        new(db, _time, _logger);

    private static void SeedActivePlan(ScheduleDbContext db, DateTime periodStart, DateTime periodEnd)
    {
        db.Plans.Add(new PlanEntity
        {
            Id = 1,
            PeriodStart = periodStart.Date,
            PeriodEnd = periodEnd.Date,
            Weeks = Math.Max(1, (int)Math.Ceiling(((periodEnd.Date - periodStart.Date).Days + 1) / 7.0)),
            Status = PlanStatus.Active,
            CreatedAt = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc)
        });
    }

    private static void SeedTask(ScheduleDbContext db, long id, long? planId, DateTime startAt, DateTime endAt)
    {
        db.Tasks.Add(new TaskEntity
        {
            Id = id,
            PlanId = planId,
            TechnicianId = 1,
            ServiceObjectId = 1,
            Title = $"Task {id}",
            StartAt = startAt,
            EndAt = endAt,
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc)
        });
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ScheduleDbContext(options);
    }
}
