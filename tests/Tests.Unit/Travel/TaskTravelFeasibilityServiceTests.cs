using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using Xunit;

namespace Tests.Unit.Travel;

public sealed class TaskTravelFeasibilityServiceTests
{
    [Fact]
    public async Task EvaluatePlacement_FirstSlot_InboundTravelTooLong_ReturnsInfeasible()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddress(db, 2, "Target 1, City");
        SeedRoute(db, from: 1, to: 2, minutes: 30);

        var sut = new TaskTravelFeasibilityService(db, new RouteChainBuilder(db));
        var day = DateTime.UtcNow.Date.AddDays(1);

        var result = await sut.EvaluatePlacementAsync(
            technicianId: 1,
            proposedStartAt: day.AddHours(8),
            proposedEndAt: day.AddHours(9),
            serviceAddressId: 2,
            proposedStartMinutesLocal: 8 * 60,
            excludeTaskId: null,
            checkOverlap: true,
            cancellationToken: CancellationToken.None);

        result.IsFeasible.Should().BeFalse();
        result.HasOverlap.Should().BeFalse();
        result.InboundTravelMinutes.Should().Be(30);
        result.Message.Should().Contain("Nepakanka laiko kelionei");
    }

    [Fact]
    public async Task EvaluatePlacement_WithOverlapCheck_ReturnsOverlap()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddress(db, 2, "Obj A, City");
        SeedServiceObject(db, objectId: 100, addressId: 2);
        SeedTask(db, taskId: 200, technicianId: 1, serviceObjectId: 100, startAt: DateTime.UtcNow.Date.AddDays(1).AddHours(9), endAt: DateTime.UtcNow.Date.AddDays(1).AddHours(10));

        var sut = new TaskTravelFeasibilityService(db, new RouteChainBuilder(db));
        var day = DateTime.UtcNow.Date.AddDays(1);

        var result = await sut.EvaluatePlacementAsync(
            technicianId: 1,
            proposedStartAt: day.AddHours(9).AddMinutes(30),
            proposedEndAt: day.AddHours(10).AddMinutes(30),
            serviceAddressId: 2,
            proposedStartMinutesLocal: 9 * 60 + 30,
            excludeTaskId: null,
            checkOverlap: true,
            cancellationToken: CancellationToken.None);

        result.IsFeasible.Should().BeFalse();
        result.HasOverlap.Should().BeTrue();
        result.Message.Should().Contain("persidengia");
    }

    [Fact]
    public async Task EvaluatePlacement_MiddleSlot_OutboundTravelTooLong_ReturnsInfeasible()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddress(db, 10, "Prev, City");
        SeedAddress(db, 20, "Proposed, City");
        SeedAddress(db, 30, "Next, City");
        SeedServiceObject(db, objectId: 101, addressId: 10);
        SeedServiceObject(db, objectId: 102, addressId: 30);
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTask(db, taskId: 201, technicianId: 1, serviceObjectId: 101, startAt: day.AddHours(9), endAt: day.AddHours(10));
        SeedTask(db, taskId: 202, technicianId: 1, serviceObjectId: 102, startAt: day.AddHours(12), endAt: day.AddHours(13));
        SeedRoute(db, from: 10, to: 20, minutes: 10);
        SeedRoute(db, from: 20, to: 30, minutes: 90);

        var sut = new TaskTravelFeasibilityService(db, new RouteChainBuilder(db));

        var result = await sut.EvaluatePlacementAsync(
            technicianId: 1,
            proposedStartAt: day.AddHours(10).AddMinutes(30),
            proposedEndAt: day.AddHours(11).AddMinutes(30),
            serviceAddressId: 20,
            proposedStartMinutesLocal: 10 * 60 + 30,
            excludeTaskId: null,
            checkOverlap: true,
            cancellationToken: CancellationToken.None);

        result.IsFeasible.Should().BeFalse();
        result.InboundTravelMinutes.Should().Be(10);
        result.OutboundTravelMinutes.Should().Be(90);
        result.Message.Should().Contain("Nepakanka laiko kelionei");
    }

    [Fact]
    public async Task EvaluatePlacement_WhenRoutesMissing_ReturnsInfeasible()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddress(db, 2, "Target 2, City");

        var sut = new TaskTravelFeasibilityService(db, new RouteChainBuilder(db));
        var day = DateTime.UtcNow.Date.AddDays(1);

        var result = await sut.EvaluatePlacementAsync(
            technicianId: 1,
            proposedStartAt: day.AddHours(10),
            proposedEndAt: day.AddHours(11),
            serviceAddressId: 2,
            proposedStartMinutesLocal: 10 * 60,
            excludeTaskId: null,
            checkOverlap: true,
            cancellationToken: CancellationToken.None);

        result.IsFeasible.Should().BeFalse();
        result.InboundTravelMinutes.Should().BeNull();
        result.OutboundTravelMinutes.Should().BeNull();
        result.Message.Should().Contain("truksta marsruto");
    }

    [Fact]
    public async Task EvaluatePlacement_WhenFromAndToSameAddress_MissingRoute_DoesNotBlockPlacement()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);

        var sut = new TaskTravelFeasibilityService(db, new RouteChainBuilder(db));
        var day = DateTime.UtcNow.Date.AddDays(1);

        var result = await sut.EvaluatePlacementAsync(
            technicianId: 1,
            proposedStartAt: day.AddHours(10),
            proposedEndAt: day.AddHours(11),
            serviceAddressId: 1,
            proposedStartMinutesLocal: 10 * 60,
            excludeTaskId: null,
            checkOverlap: true,
            cancellationToken: CancellationToken.None);

        result.IsFeasible.Should().BeTrue();
        result.InboundTravelMinutes.Should().Be(0);
        result.OutboundTravelMinutes.Should().BeNull();
    }

    [Fact]
    public async Task EvaluatePlacement_WhenRouteDurationIsZero_ForDifferentAddresses_ReturnsInfeasible()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedAddress(db, 2, "Target Zero, City");
        SeedRoute(db, from: 1, to: 2, minutes: 0);

        var sut = new TaskTravelFeasibilityService(db, new RouteChainBuilder(db));
        var day = DateTime.UtcNow.Date.AddDays(1);

        var result = await sut.EvaluatePlacementAsync(
            technicianId: 1,
            proposedStartAt: day.AddHours(8),
            proposedEndAt: day.AddHours(9),
            serviceAddressId: 2,
            proposedStartMinutesLocal: 8 * 60,
            excludeTaskId: null,
            checkOverlap: true,
            cancellationToken: CancellationToken.None);

        result.IsFeasible.Should().BeFalse();
        result.Message.Should().Contain("netinkama marsruto trukme");
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static void SeedTechnician(ScheduleDbContext db, long technicianId, long workplaceAddressId)
    {
        if (!db.Addresses.AnyAsync(a => a.Id == workplaceAddressId).GetAwaiter().GetResult())
        {
            SeedAddress(db, workplaceAddressId, $"HQ {workplaceAddressId}, City");
        }

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

    private static void SeedAddress(ScheduleDbContext db, long id, string fullAddress)
    {
        var street = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        db.Addresses.Add(new AddressEntity
        {
            Id = id,
            City = "City",
            Street = street,
            FullAddress = fullAddress,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedServiceObject(ScheduleDbContext db, long objectId, long addressId)
    {
        db.ServiceObjects.Add(new ServiceObjectEntity
        {
            Id = objectId,
            Name = $"Obj-{objectId}",
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
            Title = $"Task-{taskId}",
            StartAt = DateTime.SpecifyKind(startAt, DateTimeKind.Utc),
            EndAt = DateTime.SpecifyKind(endAt, DateTimeKind.Utc),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedRoute(ScheduleDbContext db, long from, long to, int minutes)
    {
        db.TravelRoutes.Add(new TravelRouteEntity
        {
            FromAddressId = from,
            ToAddressId = to,
            DistanceKm = 1.0m,
            DurationMinutes = minutes,
            CalculatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}
