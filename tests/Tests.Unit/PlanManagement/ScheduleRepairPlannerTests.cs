using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Unavailability;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;
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

        var service = new ScheduleRepairService(db, CreateTimeService(), routeChainBuilder, new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));

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
    public async Task BuildPlanAsync_ForSwap_SlidesSwappedTaskForwardWhenExactSlotMissesTravelGap()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        SeedTask(db, 100, 1, 10, day.AddHours(5.5), day.AddHours(8));
        SeedTask(db, 101, 1, 11, day.AddHours(8.5), day.AddHours(10.5));
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 2, 1, 0);
        SeedRoute(db, 1, 3, 0);
        SeedRoute(db, 3, 1, 0);
        SeedRoute(db, 2, 3, 90);
        SeedRoute(db, 3, 2, 90);

        var routeChainBuilder = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder.BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteChain
            {
                TechnicianId = 1,
                WorkplaceAddressId = 1,
                Stops =
                {
                    new RouteStop { TaskId = 100, StartAt = day.AddHours(5.5), EndAt = day.AddHours(8), AddressId = 2 },
                    new RouteStop { TaskId = 101, StartAt = day.AddHours(8.5), EndAt = day.AddHours(10.5), AddressId = 3 }
                }
            });

        var timeService = CreateTimeService();
        timeService.GetCurrentUtcTime().Returns(DateTime.SpecifyKind(day.AddHours(4), DateTimeKind.Utc));
        var service = new ScheduleRepairService(db, timeService, routeChainBuilder, new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));

        var result = await service.BuildPlanAsync(new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Swap,
            SourceTaskId = 100,
            TargetTaskId = 101,
            TimezoneOffsetMinutes = 0
        });

        result.Success.Should().BeTrue();
        result.Adjustments.Should().Contain(x => x.TaskId == 101 && x.StartAt == day.AddHours(8) && x.EndAt == day.AddHours(10));
        result.Adjustments.Should().Contain(x => x.TaskId == 100 && x.StartAt == day.AddHours(11.5) && x.EndAt == day.AddHours(14));
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

        var service = new ScheduleRepairService(db, CreateTimeService(), Substitute.For<IRouteChainBuilder>(), new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));

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
    public async Task BuildPlanAsync_ForDelete_ReusesCompactPlanner()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 2, 3, 30);
        SeedTask(db, 100, 1, 10, day.AddHours(8), day.AddHours(9));
        SeedTask(db, 101, 1, 11, day.AddHours(11), day.AddHours(12));

        var service = new ScheduleRepairService(db, CreateTimeService(), Substitute.For<IRouteChainBuilder>(), new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));
        var compactRequest = new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Compact,
            TechnicianId = 1,
            DayUtc = day,
            TimezoneOffsetMinutes = 0
        };
        var deleteRequest = new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Delete,
            TechnicianId = compactRequest.TechnicianId,
            DayUtc = compactRequest.DayUtc,
            TimezoneOffsetMinutes = compactRequest.TimezoneOffsetMinutes
        };

        var compactPlan = await service.BuildPlanAsync(compactRequest);
        var deletePlan = await service.BuildPlanAsync(deleteRequest);

        deletePlan.Success.Should().Be(compactPlan.Success);
        deletePlan.RepairKind.Should().Be(compactPlan.RepairKind);
        deletePlan.Adjustments.Should().BeEquivalentTo(compactPlan.Adjustments);
    }

    [Fact]
    public async Task BuildPlanAsync_ForSwap_PullsBackSwappedTaskWhenItOverflowsDayEnd()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        // T1 is 1.5h (source, early); T2 is 30min near day-end (target)
        // After swap T1 wants T2's slot (19:00) but 19:00+1.5h=20:30 overflows DayEnd (20:00)
        SeedTask(db, 100, 1, 10, day.AddHours(8), day.AddHours(9.5));
        SeedTask(db, 101, 1, 11, day.AddHours(19), day.AddHours(19.5));
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 1, 3, 0);
        SeedRoute(db, 2, 3, 0);
        SeedRoute(db, 3, 2, 0);

        var routeChainBuilder = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder.BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteChain
            {
                TechnicianId = 1,
                WorkplaceAddressId = 1,
                Stops =
                {
                    new RouteStop { TaskId = 100, StartAt = day.AddHours(8), EndAt = day.AddHours(9.5), AddressId = 2 },
                    new RouteStop { TaskId = 101, StartAt = day.AddHours(19), EndAt = day.AddHours(19.5), AddressId = 3 }
                }
            });

        var service = new ScheduleRepairService(db, CreateTimeService(), routeChainBuilder, new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));

        var result = await service.BuildPlanAsync(new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Swap,
            SourceTaskId = 100,
            TargetTaskId = 101,
            TimezoneOffsetMinutes = 0
        });

        result.Success.Should().BeTrue();
        // T2 (30min) goes to T1's early slot with no constraint
        result.Adjustments.Should().Contain(x => x.TaskId == 101 && x.StartAt == day.AddHours(8) && x.EndAt == day.AddHours(8.5));
        // T1 (1.5h) is pulled back from 19:00 to 18:30 so it ends exactly at DayEnd (20:00)
        result.Adjustments.Should().Contain(x => x.TaskId == 100 && x.StartAt == day.AddHours(18.5) && x.EndAt == day.AddHours(20));
    }

    [Fact]
    public async Task BuildPlanAsync_ForSwap_PullsBackSwappedTaskWhenBystanderWouldOverflow()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);
        SeedObject(db, 11, 3);
        SeedObject(db, 12, 2);
        // T1 (1h, source) + T2 (30min, target near end) + T3 (30min, bystander right after T2)
        // After swap T1 wants T2's slot (19:00); T1 ends at 20:00 leaving no room for bystander T3
        SeedTask(db, 100, 1, 10, day.AddHours(8), day.AddHours(9));
        SeedTask(db, 101, 1, 11, day.AddHours(19), day.AddHours(19.5));
        SeedTask(db, 102, 1, 12, day.AddHours(19.5), day.AddHours(20));
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 1, 3, 0);
        SeedRoute(db, 2, 3, 0);
        SeedRoute(db, 3, 2, 0);

        var routeChainBuilder = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder.BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteChain
            {
                TechnicianId = 1,
                WorkplaceAddressId = 1,
                Stops =
                {
                    new RouteStop { TaskId = 100, StartAt = day.AddHours(8), EndAt = day.AddHours(9), AddressId = 2 },
                    new RouteStop { TaskId = 101, StartAt = day.AddHours(19), EndAt = day.AddHours(19.5), AddressId = 3 },
                    new RouteStop { TaskId = 102, StartAt = day.AddHours(19.5), EndAt = day.AddHours(20), AddressId = 2 }
                }
            });

        var service = new ScheduleRepairService(db, CreateTimeService(), routeChainBuilder, new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));

        var result = await service.BuildPlanAsync(new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Swap,
            SourceTaskId = 100,
            TargetTaskId = 101,
            TimezoneOffsetMinutes = 0
        });

        result.Success.Should().BeTrue();
        // T2 (30min) swaps to T1's early slot
        result.Adjustments.Should().Contain(x => x.TaskId == 101 && x.StartAt == day.AddHours(8) && x.EndAt == day.AddHours(8.5));
        // T1 (1h) pulled back from 19:00 to 18:30 — backward pass reserves room for bystander T3
        result.Adjustments.Should().Contain(x => x.TaskId == 100 && x.StartAt == day.AddHours(18.5) && x.EndAt == day.AddHours(19.5));
        // Bystander T3 is unchanged
        result.Adjustments.Should().NotContain(x => x.TaskId == 102);
    }

    [Fact]
    public async Task BuildPlanAsync_ForSwap_SlidesPreSegmentTaskBackwardWhenGapAllowsIt()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 1);
        SeedObject(db, 10, 2);  // T2 pre-segment at addr2
        SeedObject(db, 11, 2);  // T3 swap source at addr2
        SeedObject(db, 12, 3);  // T4 swap target at addr3
        // T2 (30min, pre-segment) has a large free gap before it (08:00–17:00)
        // T3 (30min, source) starts right after T2 at the same address (0 travel)
        // T4 (30min, target) is 60min travel away; return trip addr3→addr2 is only 5min
        SeedTask(db, 101, 1, 10, day.AddHours(17.5), day.AddHours(18));
        SeedTask(db, 102, 1, 11, day.AddHours(18), day.AddHours(18.5));
        SeedTask(db, 103, 1, 12, day.AddHours(19.5), day.AddHours(20));
        SeedRoute(db, 1, 2, 0);
        SeedRoute(db, 1, 3, 0);
        SeedRoute(db, 2, 3, 60);  // addr2 → addr3: 60 min
        SeedRoute(db, 3, 2, 5);   // addr3 → addr2: 5 min (asymmetric)

        var routeChainBuilder = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder.BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteChain
            {
                TechnicianId = 1,
                WorkplaceAddressId = 1,
                Stops =
                {
                    new RouteStop { TaskId = 101, StartAt = day.AddHours(17.5), EndAt = day.AddHours(18), AddressId = 2 },
                    new RouteStop { TaskId = 102, StartAt = day.AddHours(18), EndAt = day.AddHours(18.5), AddressId = 2 },
                    new RouteStop { TaskId = 103, StartAt = day.AddHours(19.5), EndAt = day.AddHours(20), AddressId = 3 }
                }
            });

        var service = new ScheduleRepairService(db, CreateTimeService(), routeChainBuilder, new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));

        var result = await service.BuildPlanAsync(new ScheduleRepairRequest
        {
            ScenarioKind = ScheduleRepairScenarioKind.Swap,
            SourceTaskId = 102,
            TargetTaskId = 103,
            TimezoneOffsetMinutes = 0
        });

        result.Success.Should().BeTrue();
        // Pre-segment T2 slides backward from 17:30 to 17:00 to fit the swap chain
        result.Adjustments.Should().Contain(x => x.TaskId == 101 && x.StartAt == day.AddHours(17) && x.EndAt == day.AddHours(17.5));
        // T4 (swap target) placed at 18:30 — earliest after T2 ends at 17:30 + 60min travel
        result.Adjustments.Should().Contain(x => x.TaskId == 103 && x.StartAt == day.AddHours(18.5) && x.EndAt == day.AddHours(19));
        // T3 (swap source) placed at 19:30 — after T4 ends at 19:00 + 5min travel, aligned to slot
        result.Adjustments.Should().Contain(x => x.TaskId == 102 && x.StartAt == day.AddHours(19.5) && x.EndAt == day.AddHours(20));
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

        var service = new ScheduleRepairService(db, CreateTimeService(), Substitute.For<IRouteChainBuilder>(), new DayScheduleFitCore(), CreateTravelResolver(db), Substitute.For<IRouteEvaluationService>(), new TechnicianUnavailabilityReader(db));
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
        timeService.GetCurrentUtcTime().Returns(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));
        return timeService;
    }

    private static ITravelTimeResolver CreateTravelResolver(ScheduleDbContext db) =>
        new TravelTimeResolver(db, Substitute.For<ILogger<TravelTimeResolver>>());

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
