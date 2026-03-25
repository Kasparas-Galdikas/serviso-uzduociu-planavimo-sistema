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
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class PlanServiceScheduleRepairTests
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

    public PlanServiceScheduleRepairTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _routeEvaluation.EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _time.GetUserLocalToday(Arg.Any<int>()).Returns(DateTime.Today);
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                return DateTime.SpecifyKind(date.AddMinutes(minutes), DateTimeKind.Utc);
            });
        _time.ConvertUtcToLocal(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(call => ((DateTime)call[0]).AddMinutes((int)call[1]));

        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(ScheduleRepairPlan.Fail("no repair"));
        _repairService.ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairApplyResult(true, null, Array.Empty<long>()));
    }

    [Fact]
    public async Task CreateTask_WhenTravelIsFeasible_DoesNotInvokeRepairService()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true });

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            9 * 60,
            10 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.DidNotReceive().BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>());
        await _repairService.DidNotReceive().ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenTravelIsInfeasibleAndRepairExists_AppliesRepairAndReturnsSuccess()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult { IsFeasible = false, Message = "travel fail" });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairPlan(
                true,
                null,
                ScheduleRepairKind.Shifted,
                Array.Empty<DayScheduleFitAdjustment>(),
                new ScheduleRepairPlacement(-1, 2, DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(10), DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(11), DateTimeKind.Utc))));
        _repairService.ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairApplyResult(true, null, Array.Empty<long>()));

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            9 * 60,
            10 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("travel fail");
        await _repairService.Received(1).BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x => x.TechnicianId == 1 && x.ScenarioKind == ScheduleRepairScenarioKind.Move),
            Arg.Any<CancellationToken>());
        await _repairService.Received(1).ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
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

    private static void SeedTechnicianAndPlan(ScheduleDbContext db)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = 1,
            City = "City",
            Street = "HQ",
            FullAddress = "HQ 1, City",
            CreatedAt = DateTime.UtcNow
        });
        db.Addresses.Add(new AddressEntity
        {
            Id = 2,
            City = "City",
            Street = "Main",
            FullAddress = "Main St 1, City",
            CreatedAt = DateTime.UtcNow
        });
        db.Technicians.Add(new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        });
        db.Plans.Add(new PlanEntity
        {
            Id = 50,
            PeriodStart = DateTime.Today.AddDays(-1),
            PeriodEnd = DateTime.Today.AddDays(30),
            Weeks = 4,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}
