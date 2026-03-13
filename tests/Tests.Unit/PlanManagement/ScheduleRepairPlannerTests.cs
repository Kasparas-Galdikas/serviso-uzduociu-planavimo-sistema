using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class ScheduleRepairServiceTests
{
    [Fact]
    public async Task BuildPlanAsync_ForSwap_ReusesSharedFitCore()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        SeedTask(db, 100, 1, 10, day.AddHours(8), day.AddHours(9));
        SeedTask(db, 101, 1, 11, day.AddHours(10), day.AddHours(11));
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 1, 3, 0);
        SeedRoute(db, 2, 3, 30);
        SeedRoute(db, 3, 2, 30);

        var routeChainBuilder = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder.BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteChain
            {
                TechnicianId = 1,
                WorkplaceAddressId = 1,
                Stops =
                {
                    new RouteStop { TaskId = 100, StartAt = day.AddHours(8), EndAt = day.AddHours(9), AddressId = 2 },
                    new RouteStop { TaskId = 101, StartAt = day.AddHours(10), EndAt = day.AddHours(11), AddressId = 3 }
                }
            });

        var service = new ScheduleRepairService(db, CreateTimeService(), routeChainBuilder, new DayScheduleFitCore());

        var result = await service.BuildPlanAsync(new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Swap,
            SourceTaskId = 100,
            TargetTaskId = 101,
            TimezoneOffsetMinutes = 0
        });

        result.Success.Should().BeTrue();
        result.RepairKind.Should().Be(ScheduleRepairKind.Swapped);
        result.Adjustments.Should().Contain(x => x.TaskId == 100 && x.StartAt == day.AddHours(10));
        result.Adjustments.Should().Contain(x => x.TaskId == 101 && x.StartAt == day.AddHours(8));
    }

    [Fact]
    public async Task BuildPlanAsync_ForPlacement_ReturnsPrimaryPlacement()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        SeedTask(db, 100, 1, 10, day.AddHours(8), day.AddHours(9));
        SeedTask(db, 101, 1, 11, day.AddHours(9), day.AddHours(10));
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 2, 4, 30);
        SeedRoute(db, 4, 3, 30);
        SeedRoute(db, 1, 4, 0);
        SeedAddress(db, 4, "New, City");

        var service = new ScheduleRepairService(db, CreateTimeService(), Substitute.For<IRouteChainBuilder>(), new DayScheduleFitCore());

        var result = await service.BuildPlanAsync(new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Move,
            TechnicianId = 1,
            DayUtc = day,
            TimezoneOffsetMinutes = 0,
            ProposedPlacement = new ScheduleRepairPlacement(0, 4, day.AddHours(8.5), day.AddHours(9.5))
        });

        result.Success.Should().BeTrue();
        result.PrimaryPlacement.Should().NotBeNull();
        result.Adjustments.Should().Contain(x => x.TaskId == -1);
    }

    [Fact]
    public async Task ApplyPlanAsync_ForPlacement_UpdatesPrimaryTaskAndNeighbors()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        SeedTask(db, 100, 1, 10, day.AddHours(8), day.AddHours(9));
        SeedTask(db, 101, 1, 11, day.AddHours(9), day.AddHours(10));

        var service = new ScheduleRepairService(db, CreateTimeService(), Substitute.For<IRouteChainBuilder>(), new DayScheduleFitCore());
        var plan = new ScheduleRepairPlan(
            true,
            null,
            ScheduleRepairKind.Shifted,
            new[] { new DayScheduleFitAdjustment(101, day.AddHours(10), day.AddHours(11)) },
            new ScheduleRepairPlacement(-1, 2, day.AddHours(8.5), day.AddHours(9.5)));

        var result = await service.ApplyPlanAsync(plan, 100, CancellationToken.None);

        result.Success.Should().BeTrue();
        db.Tasks.Single(x => x.Id == 100).StartAt.Should().Be(day.AddHours(8.5));
        db.Tasks.Single(x => x.Id == 101).StartAt.Should().Be(day.AddHours(10));
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static IScheduleTimeService CreateTimeService()
    {
        var timeService = Substitute.For<IScheduleTimeService>();
        timeService.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                var offset = (int)call[2];
                return DateTime.SpecifyKind(date.AddMinutes(minutes - offset), DateTimeKind.Utc);
            });
        return timeService;
    }

    private static void SeedTechnician(ScheduleDbContext db, long technicianId, long workplaceAddressId)
    {
        SeedAddress(db, workplaceAddressId, "HQ, City");
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

    private static void SeedObject(ScheduleDbContext db, long objectId, long addressId)
    {
        SeedAddress(db, addressId, $"Address {addressId}, City");
        db.ServiceObjects.Add(new ServiceObjectEntity
        {
            Id = objectId,
            Name = $"Obj {objectId}",
            AddressId = addressId,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedTask(ScheduleDbContext db, long taskId, long technicianId, long serviceObjectId, DateTime startAt, DateTime endAt)
    {
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            TechnicianId = technicianId,
            ServiceObjectId = serviceObjectId,
            Title = $"Task {taskId}",
            StartAt = DateTime.SpecifyKind(startAt, DateTimeKind.Utc),
            EndAt = DateTime.SpecifyKind(endAt, DateTimeKind.Utc),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
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

    private static void SeedAddress(ScheduleDbContext db, long addressId, string fullAddress)
    {
        if (db.Addresses.Any(x => x.Id == addressId))
        {
            return;
        }

        db.Addresses.Add(new AddressEntity
        {
            Id = addressId,
            City = "City",
            Street = fullAddress.Split(',')[0],
            FullAddress = fullAddress,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}
