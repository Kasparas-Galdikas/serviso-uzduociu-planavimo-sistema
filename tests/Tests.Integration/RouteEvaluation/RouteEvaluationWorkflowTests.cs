using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;

namespace Tests.Integration.RouteEvaluation;

[Collection(IntegrationTestCollection.Name)]
public sealed class RouteEvaluationWorkflowTests
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public RouteEvaluationWorkflowTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateTask_NewAddress_GeocodesAndInsertsTravelRoute()
    {
        var fakeLocation = new FakeLocationApiClient();
        var fakeDistance = new FakeDistanceApiClient();
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "test-key");
        await ResetAndReseedAsync(factory);

        var techId = await GetTechnicianIdAsync(factory, "technician@test.com");
        var workplaceAddressId = await GetWorkplaceAddressIdAsync(factory, techId);
        await EnsureAddressCoordinatesAsync(factory, workplaceAddressId, 54.70m, 25.30m);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(40)).ToString("yyyy-MM-dd");
        var address = "Route Eval New 101, City";
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Route task")
            .WithObjectName("Route Obj 101")
            .WithObjectAddress(address)
            .Build();

        var client = CreateClient(factory);
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var createdAddress = await FindAddressByFullAddressAsync(factory, address);
        createdAddress.Should().NotBeNull();
        createdAddress!.Latitude.Should().NotBeNull();
        createdAddress.Longitude.Should().NotBeNull();

        var route = await FindRouteAsync(factory, workplaceAddressId, createdAddress.Id);
        route.Should().NotBeNull();
        fakeLocation.CallCount.Should().BeGreaterThan(0);
        fakeDistance.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateTask_AddressChanged_CreatesRouteForNewAddress()
    {
        var fakeLocation = new FakeLocationApiClient();
        var fakeDistance = new FakeDistanceApiClient();
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "test-key");
        await ResetAndReseedAsync(factory);

        var techId = await GetTechnicianIdAsync(factory, "technician@test.com");
        var workplaceAddressId = await GetWorkplaceAddressIdAsync(factory, techId);
        await EnsureAddressCoordinatesAsync(factory, workplaceAddressId, 54.70m, 25.30m);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(41)).ToString("yyyy-MM-dd");
        var createDto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Task before update")
            .WithObjectName("Obj Before")
            .WithObjectAddress("Route Eval Old 201, City")
            .Build();

        var client = CreateClient(factory);
        var createResponse = await client.PostAsJsonAsync("/api/tasks", createDto);
        createResponse.EnsureSuccessStatusCode();
        var created = (await createResponse.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;

        fakeLocation.Reset();
        fakeDistance.Reset();

        var detailResponse = await client.GetAsync($"/api/tasks/{created.Id}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = (await detailResponse.Content.ReadFromJsonAsync<TaskDetailResponseDto>())!;

        var updatedAddress = "Route Eval New 202, City";
        var updateDto = TestUpdateTaskRequestBuilder
            .Create(detail.TechnicianId, detail.StartAt, detail.EndAt, "Task updated", detail.Version)
            .WithSkillLevel(detail.SkillLevel)
            .WithObjectName("Obj After")
            .WithObjectAddress(updatedAddress)
            .WithInstructions(detail.Instructions)
            .Build();

        var updateResponse = await client.PatchAsJsonAsync($"/api/tasks/{created.Id}", updateDto);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var newAddress = await FindAddressByFullAddressAsync(factory, updatedAddress);
        newAddress.Should().NotBeNull();
        var newRoute = await FindRouteAsync(factory, workplaceAddressId, newAddress!.Id);
        newRoute.Should().NotBeNull();
        fakeDistance.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateTask_WhenCoordsAndRouteExist_ReusesCacheWithoutMatrixCall()
    {
        var fakeLocation = new FakeLocationApiClient();
        var fakeDistance = new FakeDistanceApiClient();
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "test-key");
        await ResetAndReseedAsync(factory);

        var isolated = await EnsureIsolatedTechnicianAsync(factory);
        var techId = isolated.TechnicianId;
        var workplaceAddressId = isolated.WorkplaceAddressId;

        const string cachedAddressText = "Cached Route 301, City";
        var cachedAddressId = await EnsureAddressAndObjectAsync(factory, cachedAddressText, "Cached Obj 301", 54.66m, 25.22m);
        await EnsureAddressCoordinatesAsync(factory, workplaceAddressId, 54.70m, 25.30m);
        await EnsureRouteAsync(factory, workplaceAddressId, cachedAddressId, 3.2m, 14);

        var baselinePairCount = await CountRoutesForPairAsync(factory, workplaceAddressId, cachedAddressId);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(42)).ToString("yyyy-MM-dd");
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Use cache")
            .WithObjectName("Cached Obj 301")
            .WithObjectAddress(cachedAddressText)
            .Build();

        var client = CreateClient(factory);
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var finalPairCount = await CountRoutesForPairAsync(factory, workplaceAddressId, cachedAddressId);
        finalPairCount.Should().Be(baselinePairCount);
        fakeDistance.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateTask_WithoutAddressChange_DoesNotCallRouteApis()
    {
        var fakeLocation = new FakeLocationApiClient();
        var fakeDistance = new FakeDistanceApiClient();
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "test-key");
        await ResetAndReseedAsync(factory);

        var techId = await GetTechnicianIdAsync(factory, "technician@test.com");
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(43)).ToString("yyyy-MM-dd");
        var originalAddress = "No Change 401, City";

        var client = CreateClient(factory);
        var createDto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Original")
            .WithObjectName("No Change Obj")
            .WithObjectAddress(originalAddress)
            .Build();
        var createResponse = await client.PostAsJsonAsync("/api/tasks", createDto);
        createResponse.EnsureSuccessStatusCode();
        var created = (await createResponse.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;

        fakeLocation.Reset();
        fakeDistance.Reset();
        var baselineRoutes = await CountAllRoutesAsync(factory);

        var detailResponse = await client.GetAsync($"/api/tasks/{created.Id}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = (await detailResponse.Content.ReadFromJsonAsync<TaskDetailResponseDto>())!;

        var updateDto = TestUpdateTaskRequestBuilder
            .Create(detail.TechnicianId, detail.StartAt, detail.EndAt, "Renamed only", detail.Version)
            .WithSkillLevel(detail.SkillLevel)
            .WithObjectName(detail.ObjectName)
            .WithObjectAddress(detail.ObjectAddress)
            .WithInstructions(detail.Instructions)
            .Build();

        var updateResponse = await client.PatchAsJsonAsync($"/api/tasks/{created.Id}", updateDto);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        (await CountAllRoutesAsync(factory)).Should().Be(baselineRoutes);
        fakeLocation.CallCount.Should().Be(0);
        fakeDistance.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateTask_WithMissingApiKey_SucceedsAndSkipsExternalWorkflow()
    {
        var fakeLocation = new FakeLocationApiClient();
        var fakeDistance = new FakeDistanceApiClient();
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "__PLACEHOLDER__");
        await ResetAndReseedAsync(factory);

        var techId = await GetTechnicianIdAsync(factory, "technician@test.com");
        var workplaceAddressId = await GetWorkplaceAddressIdAsync(factory, techId);
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(44)).ToString("yyyy-MM-dd");
        var newAddress = "No Key 501, City";
        var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "No key")
                .WithObjectName("No Key Obj")
                .WithObjectAddress(newAddress)
                .Build());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var address = await FindAddressByFullAddressAsync(factory, newAddress);
        address.Should().NotBeNull();
        address!.Latitude.Should().BeNull();
        address.Longitude.Should().BeNull();
        (await FindRouteAsync(factory, workplaceAddressId, address.Id)).Should().BeNull();
        fakeLocation.CallCount.Should().Be(0);
        fakeDistance.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateTask_WhenGeocodeFails_SucceedsWithoutRouteInsertion()
    {
        var fakeLocation = new FakeLocationApiClient { ThrowOnCall = true };
        var fakeDistance = new FakeDistanceApiClient();
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "test-key");
        await ResetAndReseedAsync(factory);

        var techId = await GetTechnicianIdAsync(factory, "technician@test.com");
        var workplaceAddressId = await GetWorkplaceAddressIdAsync(factory, techId);
        await EnsureAddressCoordinatesAsync(factory, workplaceAddressId, 54.70m, 25.30m);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(45)).ToString("yyyy-MM-dd");
        var addressText = "Geo Fail 601, City";
        var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Geo fail")
                .WithObjectName("Geo Fail Obj")
                .WithObjectAddress(addressText)
                .Build());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var address = await FindAddressByFullAddressAsync(factory, addressText);
        address.Should().NotBeNull();
        address!.Latitude.Should().BeNull();
        address.Longitude.Should().BeNull();
        (await FindRouteAsync(factory, workplaceAddressId, address.Id)).Should().BeNull();
        fakeDistance.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateTask_WhenMatrixFails_SucceedsWithoutRouteInsertion()
    {
        var fakeLocation = new FakeLocationApiClient();
        var fakeDistance = new FakeDistanceApiClient { ThrowOnCall = true };
        using var factory = CreateFactory(fakeLocation, fakeDistance, apiKey: "test-key");
        await ResetAndReseedAsync(factory);

        var techId = await GetTechnicianIdAsync(factory, "technician@test.com");
        var workplaceAddressId = await GetWorkplaceAddressIdAsync(factory, techId);
        await EnsureAddressCoordinatesAsync(factory, workplaceAddressId, 54.70m, 25.30m);

        const string matrixFailAddress = "Matrix Fail 701, City";
        var objectAddressId = await EnsureAddressAndObjectAsync(factory, matrixFailAddress, "Matrix Fail Obj", 54.61m, 25.11m);
        var initialCount = await CountRoutesForPairAsync(factory, workplaceAddressId, objectAddressId);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(46)).ToString("yyyy-MM-dd");
        var client = CreateClient(factory);
        var response = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Matrix fail")
                .WithObjectName("Matrix Fail Obj")
                .WithObjectAddress(matrixFailAddress)
                .Build());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountRoutesForPairAsync(factory, workplaceAddressId, objectAddressId)).Should().Be(initialCount);
        fakeDistance.CallCount.Should().BeGreaterThan(0);
    }

    private TestWebApplicationFactory CreateFactory(FakeLocationApiClient location, FakeDistanceApiClient distance, string apiKey)
    {
        var factory = new TestWebApplicationFactory(
            _fixture.Postgres,
            useTestAuth: true,
            configureTestServices: services =>
            {
                services.RemoveAll<ILocationApiClient>();
                services.RemoveAll<IDistanceApiClient>();
                services.AddSingleton<ILocationApiClient>(location);
                services.AddSingleton<IDistanceApiClient>(distance);
            },
            configOverrides: new Dictionary<string, string?>
            {
                ["HEIGIT_API_KEY"] = apiKey,
                ["HEIGIT_BASE_URL"] = "https://stubbed.example"
            });

        _ = factory.Services;
        return factory;
    }

    private HttpClient CreateClient(TestWebApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-Email", "manager@test.com");
        client.DefaultRequestHeaders.Add("X-Test-Role", "manager");
        return client;
    }

    private async Task ResetAndReseedAsync(TestWebApplicationFactory factory)
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = factory.Services.CreateScope();
        await SeedData.EnsureSeedDataAsync(scope);
    }

    private static async Task<long> GetTechnicianIdAsync(TestWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Technicians
            .Include(t => t.User)
            .Where(t => t.User != null && t.User.Email == email)
            .Select(t => t.Id)
            .FirstAsync();
    }

    private static async Task<long> GetWorkplaceAddressIdAsync(TestWebApplicationFactory factory, long technicianId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Technicians
            .Where(t => t.Id == technicianId)
            .Select(t => t.WorkplaceAddressId)
            .FirstAsync();
    }

    private static async Task EnsureAddressCoordinatesAsync(TestWebApplicationFactory factory, long addressId, decimal lat, decimal lon)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var address = await db.Addresses.FirstAsync(a => a.Id == addressId);
        address.Latitude = lat;
        address.Longitude = lon;
        await db.SaveChangesAsync();
    }

    private static async Task<AddressEntity?> FindAddressByFullAddressAsync(TestWebApplicationFactory factory, string fullAddress)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Addresses.FirstOrDefaultAsync(a => a.FullAddress == fullAddress);
    }

    private static async Task<TravelRouteEntity?> FindRouteAsync(TestWebApplicationFactory factory, long fromAddressId, long toAddressId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.TravelRoutes.FirstOrDefaultAsync(r => r.FromAddressId == fromAddressId && r.ToAddressId == toAddressId);
    }

    private static async Task<int> CountRoutesForPairAsync(TestWebApplicationFactory factory, long fromAddressId, long toAddressId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.TravelRoutes.CountAsync(r => r.FromAddressId == fromAddressId && r.ToAddressId == toAddressId);
    }

    private static async Task<int> CountAllRoutesAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.TravelRoutes.CountAsync();
    }

    private static async Task<long> EnsureAddressAndObjectAsync(
        TestWebApplicationFactory factory,
        string fullAddress,
        string objectName,
        decimal lat,
        decimal lon)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var regionId = await db.Regions.Select(r => r.Id).FirstAsync();
        var street = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim();

        var address = await db.Addresses.FirstOrDefaultAsync(a => a.RegionId == regionId && a.Street == street);
        if (address == null)
        {
            address = new AddressEntity
            {
                RegionId = regionId,
                Street = street,
                FullAddress = fullAddress,
                Latitude = lat,
                Longitude = lon,
                CreatedAt = DateTime.UtcNow
            };
            db.Addresses.Add(address);
            await db.SaveChangesAsync();
        }
        else
        {
            address.FullAddress = fullAddress;
            address.Latitude = lat;
            address.Longitude = lon;
            await db.SaveChangesAsync();
        }

        var serviceObject = await db.ServiceObjects.FirstOrDefaultAsync(o => o.Name == objectName && o.AddressId == address.Id);
        if (serviceObject == null)
        {
            db.ServiceObjects.Add(new ServiceObjectEntity
            {
                Name = objectName,
                AddressId = address.Id,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        return address.Id;
    }

    private static async Task EnsureRouteAsync(TestWebApplicationFactory factory, long fromAddressId, long toAddressId, decimal km, int minutes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var existing = await db.TravelRoutes.FirstOrDefaultAsync(r => r.FromAddressId == fromAddressId && r.ToAddressId == toAddressId);
        if (existing != null)
        {
            existing.DistanceKm = km;
            existing.DurationMinutes = minutes;
            existing.CalculatedAt = DateTime.UtcNow;
        }
        else
        {
            db.TravelRoutes.Add(new TravelRouteEntity
            {
                FromAddressId = fromAddressId,
                ToAddressId = toAddressId,
                DistanceKm = km,
                DurationMinutes = minutes,
                CalculatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<(long TechnicianId, long WorkplaceAddressId)> EnsureIsolatedTechnicianAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var regionId = await db.Regions.Select(r => r.Id).FirstAsync();

        var workplaceAddress = new AddressEntity
        {
            RegionId = regionId,
            Street = "Isolated HQ",
            FullAddress = $"Isolated HQ {Guid.NewGuid():N}, City",
            Latitude = 54.70m,
            Longitude = 25.30m,
            CreatedAt = DateTime.UtcNow
        };
        db.Addresses.Add(workplaceAddress);
        await db.SaveChangesAsync();

        var technician = new TechnicianEntity
        {
            RegionId = regionId,
            WorkplaceAddressId = workplaceAddress.Id,
            DailyLimit = 8,
            WeeklyLimit = 40,
            TravelSwitchLimit = 0,
            CreatedAt = DateTime.UtcNow
        };
        db.Technicians.Add(technician);

        await db.SaveChangesAsync();
        return (technician.Id, workplaceAddress.Id);
    }

    private sealed class FakeLocationApiClient : ILocationApiClient
    {
        private int _callCount;
        public bool ThrowOnCall { get; set; }
        public int CallCount => _callCount;

        public Task<GeoPoint> GeocodeAsync(string address, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (ThrowOnCall)
            {
                throw new InvalidOperationException("geocode failure");
            }

            // Deterministic but unique-ish response per input.
            var hash = Math.Abs(address.GetHashCode());
            var lat = 54.5m + (hash % 1000) / 10000m;
            var lon = 25.0m + (hash % 1000) / 10000m;
            return Task.FromResult(new GeoPoint(lat, lon));
        }

        public void Reset() => Interlocked.Exchange(ref _callCount, 0);
    }

    private sealed class FakeDistanceApiClient : IDistanceApiClient
    {
        private int _callCount;
        public bool ThrowOnCall { get; set; }
        public int CallCount => _callCount;

        public Task<MatrixResult> GetMatrixAsync(IReadOnlyList<GeoPoint> coordinates, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (ThrowOnCall)
            {
                throw new InvalidOperationException("matrix failure");
            }

            var size = coordinates.Count;
            var durations = new List<IReadOnlyList<double>>(size);
            var distances = new List<IReadOnlyList<double>>(size);
            for (var i = 0; i < size; i++)
            {
                var durRow = new List<double>(size);
                var distRow = new List<double>(size);
                for (var j = 0; j < size; j++)
                {
                    if (i == j)
                    {
                        durRow.Add(0d);
                        distRow.Add(0d);
                    }
                    else
                    {
                        durRow.Add(600d);
                        distRow.Add(1500d);
                    }
                }
                durations.Add(durRow);
                distances.Add(distRow);
            }

            return Task.FromResult(new MatrixResult(durations, distances));
        }

        public void Reset() => Interlocked.Exchange(ref _callCount, 0);
    }
}
