using System;
using System.Linq;
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
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Unavailability;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using Xunit;

namespace Tests.Unit.PlanManagement;

public class PlanServiceSwapCompactionTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
    private readonly ITravelTimeResolver _travelResolver = Substitute.For<ITravelTimeResolver>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();

    public PlanServiceSwapCompactionTests()
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
        _time.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);
        _time.GetCurrentUtcTime().Returns(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                var offset = (int)call[2];
                var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified).AddMinutes(minutes);
                return DateTime.SpecifyKind(local.AddMinutes(-offset), DateTimeKind.Utc);
            });
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true });
    }

    [Fact]
    public async Task SwapTasks_WhenMiddleGapExists_SwapsTasksWithoutBreakingFollowingSchedule()
    {
        using var db = CreateContext();
        SeedTechnicianWithWorkplace(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddressAndObject(db, addressId: 2, objectId: 10);
        SeedObjectOnAddress(db, addressId: 1, objectId: 11);
        SeedAddressAndObject(db, addressId: 4, objectId: 12);
        SeedRoute(db, 1, 2, 1);
        SeedRoute(db, 2, 4, 1);

        var day = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(5), DateTimeKind.Utc);
        var source = SeedTask(db, 1, 10, day.AddHours(8), day.AddHours(9));
        var target = SeedTask(db, 1, 11, day.AddHours(10), day.AddHours(11));
        var tail = SeedTask(db, 1, 12, day.AddHours(14), day.AddHours(15));
        SetVersion(db, source, 100);
        SetVersion(db, target, 200);

        var service = CreateService(db);
        var result = await service.SwapTasksAsync(source.Id, target.Id, 100, 200, Manager(), 0, CancellationToken.None);

        result.Ok.Should().BeTrue();

        var reloaded = await db.Tasks.AsNoTracking().OrderBy(t => t.StartAt).ToListAsync();
        reloaded.Single(t => t.Id == source.Id).StartAt.Should().Be(day.AddHours(10));
        reloaded.Single(t => t.Id == target.Id).StartAt.Should().Be(day.AddHours(8));
        reloaded.Single(t => t.Id == tail.Id).StartAt.Should().Be(day.AddHours(14));
    }

    [Fact]
    public async Task SwapTasks_WhenRouteMissing_FailsAndLeavesScheduleUnchanged()
    {
        using var db = CreateContext();
        SeedTechnicianWithWorkplace(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddressAndObject(db, addressId: 2, objectId: 10);
        SeedObjectOnAddress(db, addressId: 1, objectId: 11);
        SeedAddressAndObject(db, addressId: 4, objectId: 12);
        SeedRoute(db, 1, 2, 1);

        var day = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(6), DateTimeKind.Utc);
        var source = SeedTask(db, 1, 10, day.AddHours(8), day.AddHours(9));
        var target = SeedTask(db, 1, 11, day.AddHours(10), day.AddHours(11));
        var tail = SeedTask(db, 1, 12, day.AddHours(14), day.AddHours(15));
        SetVersion(db, source, 300);
        SetVersion(db, target, 400);

        var service = CreateService(db);
        var result = await service.SwapTasksAsync(source.Id, target.Id, 300, 400, Manager(), 0, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(TaskSwapError.InvalidSwap);

        var reloaded = await db.Tasks.AsNoTracking().OrderBy(t => t.StartAt).ToListAsync();
        reloaded.Single(t => t.Id == source.Id).StartAt.Should().Be(day.AddHours(8));
        reloaded.Single(t => t.Id == target.Id).StartAt.Should().Be(day.AddHours(10));
        reloaded.Single(t => t.Id == tail.Id).StartAt.Should().Be(day.AddHours(14));
    }
    private PlanService CreateService(ScheduleDbContext db)
    {
        var repairService = new ScheduleRepairService(
            db,
            _time,
            new RouteChainBuilder(db),
            new DayScheduleFitCore(),
            new TravelTimeResolver(db, Substitute.For<ILogger<TravelTimeResolver>>()),
            Substitute.For<IRouteEvaluationService>(),
            new TechnicianUnavailabilityReader(db));

        return new PlanService(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, repairService, _travelResolver, new TechnicianUnavailabilityReader(db), Substitute.For<ITaskHistoryWriter>(), _logger);
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 7 };

    private static void SeedTechnicianWithWorkplace(ScheduleDbContext db, long technicianId, long workplaceAddressId)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = workplaceAddressId,
            City = "City",
            Street = "HQ",
            FullAddress = "HQ 1, City",
            CreatedAt = DateTime.UtcNow
        });

        db.Technicians.Add(new TechnicianEntity
        {
            Id = technicianId,
            WorkplaceAddressId = workplaceAddressId,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedAddressAndObject(ScheduleDbContext db, long addressId, long objectId)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = addressId,
            City = "City",
            Street = $"Street {addressId}",
            FullAddress = $"Street {addressId}, City",
            CreatedAt = DateTime.UtcNow
        });
        db.ServiceObjects.Add(new ServiceObjectEntity
        {
            Id = objectId,
            Name = $"Obj {objectId}",
            AddressId = addressId,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedObjectOnAddress(ScheduleDbContext db, long addressId, long objectId)
    {
        db.ServiceObjects.Add(new ServiceObjectEntity
        {
            Id = objectId,
            Name = $"Obj {objectId}",
            AddressId = addressId,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedRoute(ScheduleDbContext db, long fromAddressId, long toAddressId, int minutes)
    {
        db.TravelRoutes.Add(new TravelRouteEntity
        {
            FromAddressId = fromAddressId,
            ToAddressId = toAddressId,
            DistanceKm = 1m,
            DurationMinutes = minutes,
            CalculatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static TaskEntity SeedTask(ScheduleDbContext db, long technicianId, long serviceObjectId, DateTime startAt, DateTime endAt)
    {
        var task = new TaskEntity
        {
            TechnicianId = technicianId,
            ServiceObjectId = serviceObjectId,
            Title = "Task",
            StartAt = startAt,
            EndAt = endAt,
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
        db.Entry(task).Property<uint>("xmin").CurrentValue = version;
    }
}
