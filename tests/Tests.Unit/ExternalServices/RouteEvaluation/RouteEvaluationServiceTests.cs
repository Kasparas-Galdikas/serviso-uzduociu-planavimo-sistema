using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using Xunit;

namespace Tests.Unit.ExternalServices.RouteEvaluation;

public class RouteEvaluationServiceTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly ILogger<RouteEvaluationService> _logger = Substitute.For<ILogger<RouteEvaluationService>>();
    private readonly ILocationApiClient _location = Substitute.For<ILocationApiClient>();
    private readonly IDistanceApiClient _distance = Substitute.For<IDistanceApiClient>();

    public RouteEvaluationServiceTests()
    {
        _evaluation.RecalculateTasksAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _location.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GeoPoint(54.6872m, 25.2797m)));
        _distance.GetMatrixAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MatrixResult(
                new List<IReadOnlyList<double>>
                {
                    new List<double> { 0d, 600d },
                    new List<double> { 600d, 0d }
                },
                new List<IReadOnlyList<double>>
                {
                    new List<double> { 0d, 1500d },
                    new List<double> { 1500d, 0d }
                })));
    }

    [Fact]
    public async Task EvaluateTaskRouteImpact_WhenApiKeyMissing_SkipsAllExternalCalls()
    {
        using var db = CreateContext();
        var taskId = SeedSingleTaskDay(db, hasCoordinates: false, withRoute: false);
        var service = CreateService(db, apiKey: string.Empty);

        await service.EvaluateTaskRouteImpactAsync(taskId, CancellationToken.None);

        await _location.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _distance.DidNotReceive().GetMatrixAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>());
        await _evaluation.DidNotReceive().RecalculateTasksAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateTaskRouteImpact_WhenCoordinatesExist_DoesNotGeocode()
    {
        using var db = CreateContext();
        var taskId = SeedSingleTaskDay(db, hasCoordinates: true, withRoute: true);
        var service = CreateService(db);

        await service.EvaluateTaskRouteImpactAsync(taskId, CancellationToken.None);

        await _location.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _distance.DidNotReceive().GetMatrixAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>());
        await _evaluation.Received(1).RecalculateTasksAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateTaskRouteImpact_WhenCoordinatesMissing_GeocodesAndSavesCoordinates()
    {
        using var db = CreateContext();
        var taskId = SeedSingleTaskDay(db, hasCoordinates: false, withRoute: true);
        _location.GeocodeAsync("HQ 1, City", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GeoPoint(54.70m, 25.20m)));
        _location.GeocodeAsync("Obj 1, City", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GeoPoint(54.71m, 25.21m)));

        var service = CreateService(db);

        await service.EvaluateTaskRouteImpactAsync(taskId, CancellationToken.None);

        await _location.Received(1).GeocodeAsync("HQ 1, City", Arg.Any<CancellationToken>());
        await _location.Received(1).GeocodeAsync("Obj 1, City", Arg.Any<CancellationToken>());

        var addresses = await db.Addresses.OrderBy(a => a.Id).ToListAsync();
        addresses[0].Latitude.Should().Be(54.70m);
        addresses[0].Longitude.Should().Be(25.20m);
        addresses[1].Latitude.Should().Be(54.71m);
        addresses[1].Longitude.Should().Be(25.21m);
    }

    [Fact]
    public async Task EvaluateTaskRouteImpact_WhenRoutesAlreadyExist_DoesNotCallMatrix()
    {
        using var db = CreateContext();
        var taskId = SeedSingleTaskDay(db, hasCoordinates: true, withRoute: true);
        var service = CreateService(db);

        await service.EvaluateTaskRouteImpactAsync(taskId, CancellationToken.None);

        await _distance.DidNotReceive().GetMatrixAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>());
        (await db.TravelRoutes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EvaluateTaskRouteImpact_WhenRouteMissing_CallsMatrixAndInsertsRoute()
    {
        using var db = CreateContext();
        var taskId = SeedSingleTaskDay(db, hasCoordinates: true, withRoute: false);
        var service = CreateService(db);

        await service.EvaluateTaskRouteImpactAsync(taskId, CancellationToken.None);

        await _distance.Received(1).GetMatrixAsync(Arg.Any<IReadOnlyList<GeoPoint>>(), Arg.Any<CancellationToken>());
        var routes = await db.TravelRoutes.ToListAsync();
        routes.Should().HaveCount(1);
        routes[0].DistanceKm.Should().Be(1.5m);
        routes[0].DurationMinutes.Should().Be(10);
    }

    private RouteEvaluationService CreateService(ScheduleDbContext db, string apiKey = "test-key")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HEIGIT_API_KEY"] = apiKey,
                ["HEIGIT_BASE_URL"] = "https://example.test"
            })
            .Build();

        return new RouteEvaluationService(db, _evaluation, _logger, config, _location, _distance);
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static long SeedSingleTaskDay(ScheduleDbContext db, bool hasCoordinates, bool withRoute)
    {
        var baseDay = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1).AddHours(9), DateTimeKind.Utc);

        db.Regions.Add(new RegionEntity { Id = 1, Name = "Region", Country = "LT" });

        db.Addresses.Add(new AddressEntity
        {
            Id = 1,
            RegionId = 1,
            Street = "HQ",
            FullAddress = "HQ 1, City",
            Latitude = hasCoordinates ? 54.6872m : null,
            Longitude = hasCoordinates ? 25.2797m : null
        });
        db.Addresses.Add(new AddressEntity
        {
            Id = 2,
            RegionId = 1,
            Street = "Obj",
            FullAddress = "Obj 1, City",
            Latitude = hasCoordinates ? 54.6900m : null,
            Longitude = hasCoordinates ? 25.3000m : null
        });

        db.Technicians.Add(new TechnicianEntity
        {
            Id = 10,
            RegionId = 1,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
        });

        db.ServiceObjects.Add(new ServiceObjectEntity
        {
            Id = 20,
            Name = "Obj",
            AddressId = 2,
            CreatedAt = DateTime.UtcNow
        });

        db.Tasks.Add(new TaskEntity
        {
            Id = 30,
            TechnicianId = 10,
            ServiceObjectId = 20,
            RegionId = 1,
            Title = "Task",
            StartAt = baseDay,
            EndAt = baseDay.AddHours(1),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        if (withRoute)
        {
            db.TravelRoutes.Add(new TravelRouteEntity
            {
                Id = 100,
                FromAddressId = 1,
                ToAddressId = 2,
                DistanceKm = 2.0m,
                DurationMinutes = 12,
                CalculatedAt = DateTime.UtcNow
            });
        }

        db.SaveChanges();
        return 30;
    }
}

