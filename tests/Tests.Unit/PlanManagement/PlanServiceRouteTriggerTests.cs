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
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.PlanManagement;

public class PlanServiceRouteTriggerTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();

    public PlanServiceRouteTriggerTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _routeEvaluation.EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _time.GetUserLocalToday(Arg.Any<int>()).Returns(DateTime.Today);
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                return DateTime.SpecifyKind(date.AddMinutes(minutes), DateTimeKind.Utc);
            });
        _time.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        _time.ConvertUtcToLocal(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(call =>
            {
                var utc = DateTime.SpecifyKind((DateTime)call[0], DateTimeKind.Utc);
                var offsetMinutes = (int)call[1];
                return utc.AddMinutes(offsetMinutes);
            });
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);
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
    }

    [Fact]
    public async Task CreateTask_CallsRouteEvaluation_Once()
    {
        using var db = CreateContext();
        SeedRegionAddressAndTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedActivePlan(db, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(30));
        var service = CreateService(db);

        var result = await service.CreateTaskAsync(
            technicianId: 1,
            targetDate: DateTime.Today.AddDays(1),
            startMinutes: 9 * 60,
            endMinutes: 10 * 60,
            title: "Task",
            priority: TaskPriority.Medium,
            status: ScheduleTaskStatus.InProgress,
            skillLevel: "A",
            objectName: "Obj",
            objectAddress: "Main St 5, City",
            instructions: string.Empty,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _routeEvaluation.Received(1).EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _travelFeasibility.Received(1).EvaluatePlacementAsync(
            Arg.Any<long>(),
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Is<long?>(x => x.HasValue && x.Value > 0),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenRouteEvaluationThrows_DoesNotFailTaskCreation()
    {
        using var db = CreateContext();
        SeedRegionAddressAndTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedActivePlan(db, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(30));
        _routeEvaluation.EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var service = CreateService(db);

        var result = await service.CreateTaskAsync(
            technicianId: 1,
            targetDate: DateTime.Today.AddDays(1),
            startMinutes: 10 * 60,
            endMinutes: 11 * 60,
            title: "Task",
            priority: TaskPriority.Medium,
            status: ScheduleTaskStatus.InProgress,
            skillLevel: "A",
            objectName: "Obj",
            objectAddress: "Main St 6, City",
            instructions: string.Empty,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _routeEvaluation.Received(1).EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_AddressChanged_CallsRouteEvaluation()
    {
        using var db = CreateContext();
        SeedRegionAddressAndTechnician(db, technicianId: 1, workplaceAddressId: 1);
        var existing = SeedTaskWithObject(db, technicianId: 1, objectAddressId: 10, objectName: "Obj A");

        var service = CreateService(db);

        var updated = await service.UpdateTaskAsync(
            existing.Id,
            task => task.Title = "Updated",
            technicianId: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            objectName: "Obj B",
            objectAddress: "New Street 99, City",
            cancellationToken: CancellationToken.None);

        updated.Should().NotBeNull();
        await _routeEvaluation.Received(1).EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_AddressUnchanged_CallsRouteEvaluation()
    {
        using var db = CreateContext();
        SeedRegionAddressAndTechnician(db, technicianId: 1, workplaceAddressId: 1);
        var existing = SeedTaskWithObject(db, technicianId: 1, objectAddressId: 10, objectName: "Obj A");

        var service = CreateService(db);

        var updated = await service.UpdateTaskAsync(
            existing.Id,
            task => task.Title = "Updated",
            technicianId: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            objectName: "Obj A",
            objectAddress: "Obj St, City",
            cancellationToken: CancellationToken.None);

        updated.Should().NotBeNull();
        await _routeEvaluation.Received(1).EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTask_WhenOnlyTitleTimeChanges_CallsRouteEvaluation()
    {
        using var db = CreateContext();
        SeedRegionAddressAndTechnician(db, technicianId: 1, workplaceAddressId: 1);
        var existing = SeedTaskWithObject(db, technicianId: 1, objectAddressId: 10, objectName: "Obj A");

        var service = CreateService(db);

        var updated = await service.UpdateTaskAsync(
            existing.Id,
            task =>
            {
                task.Title = "Changed only fields";
                task.StartAt = task.StartAt.AddHours(1);
                task.EndAt = task.EndAt.AddHours(1);
            },
            technicianId: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            objectName: null,
            objectAddress: null,
            cancellationToken: CancellationToken.None);

        updated.Should().NotBeNull();
        await _routeEvaluation.Received(1).EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 100 };

    private static void SeedRegionAddressAndTechnician(ScheduleDbContext db, long technicianId, long workplaceAddressId)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = workplaceAddressId,
            City = "City",
            Street = "HQ",
            FullAddress = "HQ 1, City",
            Latitude = 54.6872m,
            Longitude = 25.2797m
        });
        db.Technicians.Add(new TechnicianEntity
        {
            Id = technicianId,
            WorkplaceAddressId = workplaceAddressId,
            DailyLimit = 8,
            WeeklyLimit = 40,
        });
        db.SaveChanges();
    }

    private static void SeedActivePlan(ScheduleDbContext db, DateTime start, DateTime end)
    {
        db.Plans.Add(new PlanEntity
        {
            Id = 50,
            PeriodStart = start.Date,
            PeriodEnd = end.Date,
            Weeks = 4,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static TaskEntity SeedTaskWithObject(ScheduleDbContext db, long technicianId, long objectAddressId, string objectName)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = objectAddressId,
            City = "City",
            Street = "Obj St",
            FullAddress = "Obj St, City",
            Latitude = 54.7m,
            Longitude = 25.3m
        });

        var serviceObject = new ServiceObjectEntity
        {
            Id = 100,
            Name = objectName,
            AddressId = objectAddressId,
            CreatedAt = DateTime.UtcNow
        };
        db.ServiceObjects.Add(serviceObject);

        var now = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(10), DateTimeKind.Utc);
        var task = new TaskEntity
        {
            Id = 200,
            TechnicianId = technicianId,
            ServiceObjectId = serviceObject.Id,
            StartAt = now,
            EndAt = now.AddHours(1),
            Title = "Task",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(task);
        db.SaveChanges();
        return task;
    }
}



