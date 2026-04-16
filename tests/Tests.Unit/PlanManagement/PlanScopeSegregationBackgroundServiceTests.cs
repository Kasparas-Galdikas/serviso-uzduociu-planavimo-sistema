using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.BackgroundServices;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class PlanScopeSegregationBackgroundServiceTests
{
    private static readonly DateTime CurrentWeekStart = new(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentWeekEndExclusive = CurrentWeekStart.AddDays(7);

    [Fact]
    public async Task PlanScopeSegregationBackgroundService_WhenWorkPerformed_RecalculatesActiveAndSupersededPlans()
    {
        await using var db = CreateContext();
        var time = CreateTimeService();
        SeedActivePlan(db, periodStart: CurrentWeekStart.AddDays(-7), periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddDays(-1).AddHours(8), endAt: CurrentWeekStart.AddDays(-1).AddHours(9));
        await db.SaveChangesAsync();

        var evaluator = Substitute.For<IEvaluationEngine>();
        var service = CreateBackgroundService(db, time, evaluator);

        await InvokeSegregateAsync(service);

        await evaluator.Received(1).RecalculatePlanAsync(1, Arg.Any<CancellationToken>());
        await evaluator.Received(1).RecalculatePlanAsync(Arg.Is<long>(id => id != 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanScopeSegregationBackgroundService_WhenNoWorkPerformed_DoesNotRecalculate()
    {
        await using var db = CreateContext();
        var time = CreateTimeService();
        SeedActivePlan(db, periodStart: CurrentWeekStart, periodEnd: CurrentWeekStart.AddDays(6));
        SeedTask(db, id: 100, planId: 1, startAt: CurrentWeekStart.AddHours(8), endAt: CurrentWeekStart.AddHours(9));
        await db.SaveChangesAsync();

        var evaluator = Substitute.For<IEvaluationEngine>();
        var service = CreateBackgroundService(db, time, evaluator);

        await InvokeSegregateAsync(service);

        await evaluator.DidNotReceive().RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanScopeSegregationBackgroundService_WhenOneRecalculationFails_ContinuesOtherPlans()
    {
        await using var db = CreateContext();
        var time = CreateTimeService();
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

        var evaluator = Substitute.For<IEvaluationEngine>();
        evaluator.RecalculatePlanAsync(1, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var service = CreateBackgroundService(db, time, evaluator);

        await InvokeSegregateAsync(service);

        await evaluator.Received(1).RecalculatePlanAsync(1, Arg.Any<CancellationToken>());
        await evaluator.Received(1).RecalculatePlanAsync(2, Arg.Any<CancellationToken>());
    }

    private static IScheduleTimeService CreateTimeService()
    {
        var time = Substitute.For<IScheduleTimeService>();
        time.GetCurrentWeekBoundary().Returns(new WeekBoundary(
            CurrentWeekStart,
            CurrentWeekEndExclusive,
            CurrentWeekStart,
            CurrentWeekEndExclusive));
        time.GetCurrentUtcTime().Returns(new DateTime(2026, 4, 16, 9, 0, 0, DateTimeKind.Utc));
        return time;
    }

    private static PlanScopeSegregationBackgroundService CreateBackgroundService(
        ScheduleDbContext db,
        IScheduleTimeService time,
        IEvaluationEngine evaluator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PlanScopeSegregationService(
            db,
            time,
            NullLogger<PlanScopeSegregationService>.Instance));
        services.AddSingleton(evaluator);
        var provider = services.BuildServiceProvider();

        return new PlanScopeSegregationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PlanScopeSegregationBackgroundService>.Instance);
    }

    private static async Task InvokeSegregateAsync(PlanScopeSegregationBackgroundService service)
    {
        var method = typeof(PlanScopeSegregationBackgroundService)
            .GetMethod("SegregateAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var task = method!.Invoke(service, new object[] { CancellationToken.None }) as Task;
        task.Should().NotBeNull();
        await task!;
    }

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
