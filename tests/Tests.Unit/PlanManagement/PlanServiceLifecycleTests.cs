using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class PlanServiceLifecycleTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
    private readonly IScheduleRepairService _repairService = Substitute.For<IScheduleRepairService>();
    private readonly ITravelTimeResolver _travelResolver = Substitute.For<ITravelTimeResolver>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();

    public PlanServiceLifecycleTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _time.GetLocalToday().Returns(new DateTime(2026, 4, 11));
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var requestedStart = (DateTime?)call[0];
                var requestedEnd = (DateTime?)call[1];
                var weeks = (int?)call[2];
                var fallbackWeeks = (int)call[3];
                var start = (requestedStart ?? DateTime.Today).Date;
                var end = (requestedEnd ?? start.AddDays(((weeks ?? fallbackWeeks) * 7) - 1)).Date;
                var totalDays = (end - start).Days + 1;
                return new PlanningWindow(start, end, totalDays, Math.Max(1, (int)Math.Ceiling(totalDays / 7.0)), start);
            });
        _time.GetCurrentUtcTime().Returns(new DateTime(2026, 4, 11, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RecalculateCurrentPlansAsync_RecalculatesActiveAndDraftPlans()
    {
        using var db = CreateContext();
        db.Plans.Add(new PlanEntity
        {
            Id = 10,
            PeriodStart = DateTime.Today,
            PeriodEnd = DateTime.Today.AddDays(6),
            Weeks = 1,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.Plans.Add(new PlanEntity
        {
            Id = 20,
            PeriodStart = DateTime.Today,
            PeriodEnd = DateTime.Today.AddDays(6),
            Weeks = 1,
            Status = PlanStatus.Draft,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var sut = CreateService(db);

        var result = await sut.RecalculateCurrentPlansAsync(CancellationToken.None);

        result.ActivePlanId.Should().Be(10);
        result.DraftPlanId.Should().Be(20);
        await _evaluation.Received(1).RecalculatePlanAsync(10, Arg.Any<CancellationToken>());
        await _evaluation.Received(1).RecalculatePlanAsync(20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecalculateCurrentPlansAsync_WhenNoPlansExist_ReturnsNullIds()
    {
        using var db = CreateContext();
        var sut = CreateService(db);

        var result = await sut.RecalculateCurrentPlansAsync(CancellationToken.None);

        result.ActivePlanId.Should().BeNull();
        result.DraftPlanId.Should().BeNull();
        await _evaluation.DidNotReceive().RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureBacklogDraftAsync_ExtendsExistingDraftToRequestedRangeWithoutShrinkingToSeededTasks()
    {
        using var db = CreateContext();
        db.Plans.Add(new PlanEntity
        {
            Id = 23,
            PeriodStart = new DateTime(2026, 4, 8),
            PeriodEnd = new DateTime(2026, 4, 11),
            Weeks = 1,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.Plans.Add(new PlanEntity
        {
            Id = 24,
            PeriodStart = new DateTime(2026, 4, 12),
            PeriodEnd = new DateTime(2026, 4, 18),
            Weeks = 1,
            Status = PlanStatus.Draft,
            CreatedAt = DateTime.UtcNow
        });
        db.Tasks.Add(new TaskEntity
        {
            Id = 1001,
            PlanId = 23,
            TechnicianId = 5,
            ServiceObjectId = 1,
            Title = "Active carry-over",
            StartAt = new DateTime(2026, 4, 11, 17, 30, 0, DateTimeKind.Utc),
            EndAt = new DateTime(2026, 4, 11, 18, 0, 0, DateTimeKind.Utc),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var sut = CreateService(db);

        var draft = await sut.EnsureBacklogDraftAsync(new DateTime(2026, 4, 11), new DateTime(2026, 4, 11), cancellationToken: CancellationToken.None);

        draft.Id.Should().Be(24);
        draft.PeriodStart.Date.Should().Be(new DateTime(2026, 4, 11));
        draft.PeriodEnd.Date.Should().BeOnOrAfter(new DateTime(2026, 4, 18));
        draft.Status.Should().Be(PlanStatus.Draft);
        db.Tasks.Should().ContainSingle(t => t.PlanId == 23 && t.Id == 1001);
        db.Tasks.Should().Contain(t => t.PlanId == 24 && t.SourceTaskId == 1001);
    }

    [Fact]
    public async Task EnsureBacklogDraftAsync_UsesBacklogBoundaryInsteadOfGenericFutureDraftBoundary()
    {
        using var db = CreateContext();
        db.Plans.Add(new PlanEntity
        {
            Id = 23,
            PeriodStart = new DateTime(2026, 4, 8),
            PeriodEnd = new DateTime(2026, 4, 11),
            Weeks = 1,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        _planningCalendar.BuildWindowAsync(
                Arg.Any<DateTime?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<int?>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<PlanningWindow>>(_ => throw new InvalidOperationException("Drafts may start from 2026-04-12."));

        var sut = CreateService(db);

        var draft = await sut.EnsureBacklogDraftAsync(new DateTime(2026, 4, 11), new DateTime(2026, 4, 11), cancellationToken: CancellationToken.None);

        draft.PeriodStart.Date.Should().Be(new DateTime(2026, 4, 11));
        draft.PeriodEnd.Date.Should().Be(new DateTime(2026, 4, 11));
        draft.Status.Should().Be(PlanStatus.Draft);
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, _repairService, _travelResolver, Substitute.For<ITaskHistoryWriter>(), _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }
}
