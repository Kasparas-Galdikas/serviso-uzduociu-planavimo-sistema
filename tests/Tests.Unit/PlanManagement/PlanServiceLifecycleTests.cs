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
