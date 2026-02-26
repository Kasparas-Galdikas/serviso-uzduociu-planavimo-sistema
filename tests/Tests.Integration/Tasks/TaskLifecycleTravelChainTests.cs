using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;

namespace Tests.Integration.Tasks;

[Collection(IntegrationTestCollection.Name)]
public sealed class TaskLifecycleTravelChainTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public TaskLifecycleTravelChainTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateTask_FirstSlot_WithRequiredInboundTravel_Returns422()
    {
        await ResetAndReseedAsync();
        var client = CreateClient();
        var setup = await EnsureIsolatedTechnicianAsync();

        const string address = "First Slot A 1, City";
        const string objectName = "First Slot Object A";
        var targetAddressId = await EnsureAddressAndObjectAsync(address, objectName);
        await EnsureRouteAsync(setup.WorkplaceAddressId, targetAddressId, km: 4.5m, minutes: 30);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2)).ToString("yyyy-MM-dd");
        var response = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 8 * 60, 9 * 60, "First infeasible")
                .WithObjectName(objectName)
                .WithObjectAddress(address)
                .Build());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<MessageOnly>();
        payload.Should().NotBeNull();
        payload!.Message.Should().Contain("Nepakanka laiko kelionei");
    }

    [Fact]
    public async Task CreateTask_MiddleSlot_WithInfeasibleOutbound_Returns422()
    {
        await ResetAndReseedAsync();
        var client = CreateClient();
        var setup = await EnsureIsolatedTechnicianAsync();
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2)).ToString("yyyy-MM-dd");

        const string aAddress = "Chain A 1, City";
        const string bAddress = "Chain B 1, City";
        const string cAddress = "Chain C 1, City";
        var aId = await EnsureAddressAndObjectAsync(aAddress, "Obj A");
        var bId = await EnsureAddressAndObjectAsync(bAddress, "Obj B");
        var cId = await EnsureAddressAndObjectAsync(cAddress, "Obj C");

        await EnsureRouteAsync(setup.WorkplaceAddressId, aId, 2m, 10);
        await EnsureRouteAsync(aId, cId, 5m, 15);
        await EnsureRouteAsync(aId, bId, 3m, 20);
        await EnsureRouteAsync(bId, cId, 12m, 90);

        var createA = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 9 * 60, 10 * 60, "A")
                .WithObjectName("Obj A")
                .WithObjectAddress(aAddress)
                .Build());
        createA.StatusCode.Should().Be(HttpStatusCode.OK);

        var createC = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 12 * 60, 13 * 60, "C")
                .WithObjectName("Obj C")
                .WithObjectAddress(cAddress)
                .Build());
        createC.StatusCode.Should().Be(HttpStatusCode.OK);

        var createB = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 10 * 60 + 30, 11 * 60 + 30, "B middle infeasible")
                .WithObjectName("Obj B")
                .WithObjectAddress(bAddress)
                .Build());

        createB.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await createB.Content.ReadFromJsonAsync<MessageOnly>();
        payload!.Message.Should().Contain("Nepakanka laiko kelionei");
    }

    [Fact]
    public async Task UpdateTask_WhenTravelWouldBecomeInfeasible_Returns422()
    {
        await ResetAndReseedAsync();
        var client = CreateClient();
        var setup = await EnsureIsolatedTechnicianAsync();
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(3)).ToString("yyyy-MM-dd");

        const string bAddress = "Upd B 1, City";
        const string cAddress = "Upd C 1, City";
        var bId = await EnsureAddressAndObjectAsync(bAddress, "Upd Obj B");
        var cId = await EnsureAddressAndObjectAsync(cAddress, "Upd Obj C");

        await EnsureRouteAsync(setup.WorkplaceAddressId, bId, 2m, 10);
        await EnsureRouteAsync(bId, cId, 6m, 100);
        await EnsureRouteAsync(cId, bId, 6m, 100);

        var createB = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 9 * 60, 10 * 60, "B")
                .WithObjectName("Upd Obj B")
                .WithObjectAddress(bAddress)
                .Build());
        createB.EnsureSuccessStatusCode();
        var taskB = (await createB.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;

        var createC = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 10 * 60 + 30, 11 * 60 + 30, "C")
                .WithObjectName("Upd Obj C")
                .WithObjectAddress(cAddress)
                .Build());
        createC.EnsureSuccessStatusCode();

        var detail = await client.GetFromJsonAsync<TaskDetailResponseDto>($"/api/tasks/{taskB.Id}");
        detail.Should().NotBeNull();

        var update = await client.PatchAsJsonAsync($"/api/tasks/{taskB.Id}",
            TestUpdateTaskRequestBuilder
                .Create(detail!.TechnicianId, detail.StartAt, detail.EndAt, "B Updated", detail.Version)
                .WithSkillLevel(detail.SkillLevel)
                .WithObjectName(detail.ObjectName)
                .WithObjectAddress(detail.ObjectAddress)
                .WithInstructions(detail.Instructions)
                .Build());

        update.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await update.Content.ReadFromJsonAsync<MessageOnly>();
        payload!.Message.Should().Contain("Nepakanka laiko kelionei");
    }

    [Fact]
    public async Task MoveTask_WhenTravelWouldBecomeInfeasible_Returns422()
    {
        await ResetAndReseedAsync();
        var client = CreateClient();
        var setup = await EnsureIsolatedTechnicianAsync();
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(4)).ToString("yyyy-MM-dd");

        const string aAddress = "Move A 1, City";
        const string bAddress = "Move B 1, City";
        const string cAddress = "Move C 1, City";
        var aId = await EnsureAddressAndObjectAsync(aAddress, "Move Obj A");
        var bId = await EnsureAddressAndObjectAsync(bAddress, "Move Obj B");
        var cId = await EnsureAddressAndObjectAsync(cAddress, "Move Obj C");

        await EnsureRouteAsync(setup.WorkplaceAddressId, aId, 2m, 10);
        await EnsureRouteAsync(aId, bId, 10m, 70);
        await EnsureRouteAsync(bId, cId, 2m, 10);
        await EnsureRouteAsync(aId, cId, 3m, 15);

        var createA = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 9 * 60, 10 * 60, "A")
                .WithObjectName("Move Obj A")
                .WithObjectAddress(aAddress)
                .Build());
        createA.EnsureSuccessStatusCode();

        var createC = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 12 * 60, 13 * 60, "C")
                .WithObjectName("Move Obj C")
                .WithObjectAddress(cAddress)
                .Build());
        createC.EnsureSuccessStatusCode();

        var createB = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 15 * 60, 16 * 60, "B")
                .WithObjectName("Move Obj B")
                .WithObjectAddress(bAddress)
                .Build());
        createB.EnsureSuccessStatusCode();
        var movedCandidate = (await createB.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;

        var move = await client.PatchAsJsonAsync($"/api/tasks/{movedCandidate.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = setup.TechnicianId,
            TargetDate = targetDate,
            TargetSlotStartMinutes = 10 * 60 + 30,
            Version = movedCandidate.Version,
            TimezoneOffsetMinutes = 0
        });

        move.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await move.Content.ReadFromJsonAsync<MessageOnly>();
        payload!.Message.Should().Contain("Nepakanka laiko kelionei");
    }

    [Fact]
    public async Task DeleteTask_Success_And_StaleFailurePath_AreCovered()
    {
        await ResetAndReseedAsync();
        var client = CreateClient();
        var setup = await EnsureIsolatedTechnicianAsync();
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(5)).ToString("yyyy-MM-dd");

        var create = await client.PostAsJsonAsync("/api/tasks",
            TestTaskRequestBuilder.Create(setup.TechnicianId, targetDate, 10 * 60, 11 * 60, "Delete me").Build());
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;
        var staleVersion = created.Version - 1;

        var staleDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/tasks/{created.Id}")
        {
            Content = JsonContent.Create(new DeleteTaskRequestDto
            {
                Version = staleVersion,
                TimezoneOffsetMinutes = 0
            })
        };
        var staleDelete = await client.SendAsync(staleDeleteRequest);
        staleDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/tasks/{created.Id}")
        {
            Content = JsonContent.Create(new DeleteTaskRequestDto
            {
                Version = created.Version,
                TimezoneOffsetMinutes = 0
            })
        };
        var delete = await client.SendAsync(deleteRequest);
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await delete.Content.ReadFromJsonAsync<TaskDeletedResponseDto>();
        payload.Should().NotBeNull();
        payload!.DeletedTaskId.Should().Be(created.Id);
        payload.Load.Should().NotBeNull();
    }

    private HttpClient CreateClient()
    {
        var client = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-Email", "manager@test.com");
        client.DefaultRequestHeaders.Add("X-Test-Role", "manager");
        return client;
    }

    private async Task ResetAndReseedAsync()
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = _fixture.Factory.Services.CreateScope();
        await SeedData.EnsureSeedDataAsync(scope);
    }

    private async Task<(long TechnicianId, long WorkplaceAddressId)> EnsureIsolatedTechnicianAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var workplaceAddress = new AddressEntity
        {
            City = "City",
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
            WorkplaceAddressId = workplaceAddress.Id,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        };
        db.Technicians.Add(technician);
        await db.SaveChangesAsync();
        return (technician.Id, workplaceAddress.Id);
    }

    private async Task<long> EnsureAddressAndObjectAsync(string fullAddress, string objectName)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var street = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0].Trim();
        var address = await db.Addresses.FirstOrDefaultAsync(a => a.City == "City" && a.Street == street);
        if (address == null)
        {
            address = new AddressEntity
            {
                City = "City",
                Street = street,
                FullAddress = fullAddress,
                Latitude = 54.61m,
                Longitude = 25.11m,
                CreatedAt = DateTime.UtcNow
            };
            db.Addresses.Add(address);
            await db.SaveChangesAsync();
        }

        var existingObject = await db.ServiceObjects.FirstOrDefaultAsync(o => o.Name == objectName && o.AddressId == address.Id);
        if (existingObject == null)
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

    private async Task EnsureRouteAsync(long fromAddressId, long toAddressId, decimal km, int minutes)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
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

    private sealed class MessageOnly
    {
        public string Message { get; set; } = string.Empty;
    }
}
