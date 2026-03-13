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
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.PlanManagement;

public class PlanServiceDeleteTests
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

    public PlanServiceDeleteTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                return DateTime.SpecifyKind(date.AddMinutes(minutes), DateTimeKind.Utc);
            });
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
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(ScheduleRepairPlan.Fail("no compact"));
        _repairService.ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairApplyResult(true, null, Array.Empty<long>()));
    }

    [Fact]
    public async Task DeleteTask_PastEditableWindow_Rejected()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1, endAtUtc: DateTime.UtcNow.AddHours(-1));
        SetVersion(db, task, 2);

        _permission.CanDeleteTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));
        _time.IsTaskStillEditable(task.EndAt).Returns(false);

        var service = CreateService(db);
        var result = await service.DeleteTaskAsync(task.Id, expectedVersion: 2, Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskDeleteError.NotEditable);
        await _repairService.DidNotReceive().BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTask_WhenSuccessful_BuildsDeleteCompactionPlan()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        SetVersion(db, task, 3);

        _permission.CanDeleteTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairPlan(true, null, ScheduleRepairKind.Compacted, Array.Empty<DayScheduleFitAdjustment>(), null));

        var service = CreateService(db);
        var result = await service.DeleteTaskAsync(task.Id, expectedVersion: 3, Manager(), 0, CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.Received(1).BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x =>
                x.ScenarioKind == ScheduleRepairScenarioKind.Delete &&
                x.TechnicianId == task.TechnicianId &&
                x.DayUtc == task.StartAt.Date &&
                x.PlanId == plan.Id &&
                x.FilterByPlanId),
            Arg.Any<CancellationToken>());
        await _repairService.Received(1).ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), null, Arg.Any<CancellationToken>());
        await _routeEvaluation.Received(1).EvaluateTechnicianDayAsync(task.TechnicianId, task.StartAt.Date, Arg.Any<CancellationToken>());
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, _repairService, _travelResolver, _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 10 };

    private static PlanEntity SeedPlan(ScheduleDbContext db)
    {
        var plan = new PlanEntity
        {
            PeriodStart = DateTime.Today,
            PeriodEnd = DateTime.Today.AddDays(7),
            Status = PlanStatus.Active,
            Weeks = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Plans.Add(plan);
        db.SaveChanges();
        return plan;
    }

    private static TaskEntity SeedTask(ScheduleDbContext db, long planId, long technicianId, DateTime? endAtUtc = null)
    {
        var start = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(10), DateTimeKind.Utc);
        if (!db.Addresses.AnyAsync().GetAwaiter().GetResult())
        {
            db.Addresses.Add(new AddressEntity { Id = 1, City = "City", Street = "Main", FullAddress = "Main 1, City" });
        }
        if (!db.ServiceObjects.AnyAsync().GetAwaiter().GetResult())
        {
            db.ServiceObjects.Add(new ServiceObjectEntity { Id = 1, Name = "Obj", AddressId = 1 });
        }

        var task = new TaskEntity
        {
            PlanId = planId,
            TechnicianId = technicianId,
            ServiceObjectId = 1,
            StartAt = start,
            EndAt = endAtUtc ?? start.AddHours(1),
            Title = "t",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(task);
        db.SaveChanges();
        return task;
    }

    private static void SetVersion(ScheduleDbContext db, TaskEntity task, uint version)
    {
        var entry = db.Entry(task).Property<uint>("xmin");
        entry.CurrentValue = version;
    }
}
