using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;

namespace Tests.Integration.Auth;

/// <summary>
/// Integration coverage for authorization/identity behavior on task APIs and /api/auth/me.
/// Uses TestAuthHandler headers to impersonate users seeded in the DB.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TasksAuthorizationTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public TasksAuthorizationTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AuthMe_Returns_ManagerIdentity()
    {
        await ResetAndReseedAsync();
        var client = CreateClient("manager@test.com");

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload!.GetProperty("role").GetString().Should().Be("manager");
        payload.GetProperty("userId").ValueKind.Should().Be(JsonValueKind.Number);
        payload.GetProperty("technicianId").ValueKind.Should().BeOneOf(JsonValueKind.Null, JsonValueKind.Number);
    }

    [Fact]
    public async Task AuthMe_UnknownUser_FallsBackToAnonymousContext()
    {
        await ResetAndReseedAsync();
        var client = CreateClient("missing-user@test.com");

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload!.GetProperty("role").GetString().Should().Be("anonymous");
        payload.GetProperty("userId").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("technicianId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CreateTask_Manager_Succeeds()
    {
        await ResetAndReseedAsync();
        var technicianId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com", timezoneOffsetMinutes: -120);

        // Use 18:00-19:00 and AddDays(10) to avoid overlap with seeded tasks
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10)).ToString("yyyy-MM-dd");
        var dto = TestTaskRequestBuilder.Create(technicianId, targetDate, 18 * 60, 19 * 60, "Manager created")
            .WithTimezone(-120)
            .Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>();
        payload!.Id.Should().BeGreaterThan(0);
        payload.Version.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateTask_Technician_ForAnotherTech_IsForbidden()
    {
        await ResetAndReseedAsync();
        var ownTechId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("technician@test.com", timezoneOffsetMinutes: -120, technicianId: ownTechId);

        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10)).ToString("yyyy-MM-dd");
        var dto = TestTaskRequestBuilder.Create(otherTechId, targetDate, 18 * 60, 19 * 60, "Tech for others")
            .WithTimezone(-120)
            .Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateTask_Technician_CannotEdit_OtherTechnicianTask()
    {
        await ResetAndReseedAsync();
        var ownerTechId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var managerClient = CreateClient("manager@test.com", timezoneOffsetMinutes: -120);
        var task = await CreateTaskAsync(managerClient, ownerTechId, DateTime.UtcNow.Date.AddDays(10));
        var version = await GetTaskVersionAsync(task.Id);

        var attackerClient = CreateClient("austeja.j@test.com", timezoneOffsetMinutes: -120, technicianId: otherTechId);
        var updateDto = TestUpdateTaskRequestBuilder.Create(ownerTechId, task.LocalStart, task.LocalEnd, "Should fail", version)
            .WithLocation("Anywhere")
            .WithTimezone(-120)
            .Build();
        var response = await attackerClient.PatchAsJsonAsync($"/api/tasks/{task.Id}", updateDto);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateTask_PastTask_IsRejected()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var managerClient = CreateClient("manager@test.com", timezoneOffsetMinutes: -120);

        // Create a task in the future first (API rejects past dates)
        var task = await CreateTaskAsync(managerClient, techId, DateTime.UtcNow.Date.AddDays(10));

        // Force task into the past via direct DB update (end already elapsed)
        await UpdateTaskTimestampsAsync(task.Id, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        var version = await GetTaskVersionAsync(task.Id);

        var updateDto = TestUpdateTaskRequestBuilder.Create(techId, task.LocalStart, task.LocalEnd, "Past update", version)
            .WithLocation("Past")
            .WithTimezone(-120)
            .Build();
        var response = await managerClient.PatchAsJsonAsync($"/api/tasks/{task.Id}", updateDto);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task MoveTask_ManagerAcrossTechnicians_Succeeds()
    {
        await ResetAndReseedAsync();
        var sourceTechId = await GetTechnicianIdAsync("technician@test.com");
        var targetTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("manager@test.com", timezoneOffsetMinutes: 120);
        // Use AddDays(10) to avoid overlap with seeded tasks (seed creates 5 days of tasks)
        var task = await CreateTaskAsync(client, sourceTechId, DateTime.UtcNow.Date.AddDays(10));
        var version = await GetTaskVersionAsync(task.Id);

        // Move to same day different slot to avoid any seeded task overlap
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = targetTechId,
            TargetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10)).ToString("yyyy-MM-dd"),
            TargetSlotStartMinutes = 19 * 60,
            Version = version,
            TimezoneOffsetMinutes = 120
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await response.Content.ReadFromJsonAsync<TaskMovedResponseDto>();
        moved!.TechnicianId.Should().Be(targetTechId);
    }

    [Fact]
    public async Task MoveTask_TechnicianToOtherTechnician_IsForbidden()
    {
        await ResetAndReseedAsync();
        var ownerTechId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var managerClient = CreateClient("manager@test.com", timezoneOffsetMinutes: 120);
        var task = await CreateTaskAsync(managerClient, ownerTechId, DateTime.UtcNow.Date.AddDays(10));
        var version = await GetTaskVersionAsync(task.Id);

        // Try to move to 19:00 slot (fits in working window 08:00-20:00) - should fail due to permissions, not overlap
        var techClient = CreateClient("technician@test.com", timezoneOffsetMinutes: 120, technicianId: ownerTechId);
        var response = await techClient.PatchAsJsonAsync($"/api/tasks/{task.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = otherTechId,
            TargetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(11)).ToString("yyyy-MM-dd"),
            TargetSlotStartMinutes = 19 * 60,
            Version = version,
            TimezoneOffsetMinutes = 120
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MoveTask_StaleVersion_ReturnsConflictWithCurrentVersion()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com", timezoneOffsetMinutes: 120);
        var task = await CreateTaskAsync(client, techId, DateTime.UtcNow.Date.AddDays(10));
        var actualVersion = await GetTaskVersionAsync(task.Id);

        // Move to 19:00 to avoid overlap with seeded tasks
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = techId,
            TargetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10)).ToString("yyyy-MM-dd"),
            TargetSlotStartMinutes = 19 * 60,
            Version = actualVersion + 5,
            TimezoneOffsetMinutes = 120
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload!.GetProperty("version").GetUInt32().Should().Be(actualVersion);
    }

    [Fact]
    public async Task TechnicianUpdate_OwnTask_Succeeds()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var managerClient = CreateClient("manager@test.com", timezoneOffsetMinutes: 0);
        var task = await CreateTaskAsync(managerClient, techId, DateTime.UtcNow.Date.AddDays(10));
        var version = await GetTaskVersionAsync(task.Id);

        var techClient = CreateClient("technician@test.com", timezoneOffsetMinutes: 0, technicianId: techId);
        var response = await techClient.PatchAsJsonAsync($"/api/tasks/{task.Id}/technician-update", new TechnicianUpdateTaskRequestDto
        {
            Status = ScheduleTaskStatus.Completed,
            Comment = "Starting now",
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>();
        updated!.Version.Should().BeGreaterThan(version);
    }

    [Fact]
    public async Task TechnicianUpdate_OtherTechnicianTask_IsForbidden()
    {
        await ResetAndReseedAsync();
        var ownerTechId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var managerClient = CreateClient("manager@test.com", timezoneOffsetMinutes: 0);
        var task = await CreateTaskAsync(managerClient, ownerTechId, DateTime.UtcNow.Date.AddDays(10));
        var version = await GetTaskVersionAsync(task.Id);

        var techClient = CreateClient("austeja.j@test.com", timezoneOffsetMinutes: 0, technicianId: otherTechId);
        var response = await techClient.PatchAsJsonAsync($"/api/tasks/{task.Id}/technician-update", new TechnicianUpdateTaskRequestDto
        {
            Status = ScheduleTaskStatus.Completed,
            Comment = "Should fail",
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_UsingTechnicianUpdateEndpoint_IsForbidden()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var managerClient = CreateClient("manager@test.com", timezoneOffsetMinutes: 0);
        var task = await CreateTaskAsync(managerClient, techId, DateTime.UtcNow.Date.AddDays(10));
        var version = await GetTaskVersionAsync(task.Id);

        var response = await managerClient.PatchAsJsonAsync($"/api/tasks/{task.Id}/technician-update", new TechnicianUpdateTaskRequestDto
        {
            Status = ScheduleTaskStatus.InProgress,
            Comment = "Managers not allowed here",
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private HttpClient CreateClient(string email, string role = "manager", int? timezoneOffsetMinutes = null, long? technicianId = null)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        if (timezoneOffsetMinutes.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Timezone-Offset", timezoneOffsetMinutes.Value.ToString());
        }

        if (technicianId.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Test-TechnicianId", technicianId.Value.ToString());
        }

        return client;
    }

    private async Task ResetAndReseedAsync()
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = _fixture.Factory.Services.CreateScope();
        await SeedData.EnsureSeedDataAsync(scope);
    }

    private async Task<long> GetTechnicianIdAsync(string email)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Technicians
            .Include(t => t.User)
            .Where(t => t.User != null && t.User.Email == email)
            .Select(t => t.Id)
            .FirstAsync();
    }

    private async Task<(long Id, DateTime LocalStart, DateTime LocalEnd)> CreateTaskAsync(HttpClient client, long technicianId, DateTime date)
    {
        // Use 18:00-19:00 to avoid overlap with seeded tasks (which end by 18:00)
        var dto = TestTaskRequestBuilder.Create(technicianId, date, 18 * 60, 19 * 60, "Test task")
            .WithTimezone(-120)
            .Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>();
        created!.Id.Should().BeGreaterThan(0);

        var localStart = DateTime.SpecifyKind(date.Date.AddHours(18), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(date.Date.AddHours(19), DateTimeKind.Unspecified);
        return (created.Id, localStart, localEnd);
    }

    private async Task<uint> GetTaskVersionAsync(long taskId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        // Query xmin directly - EF shadow property access after AsNoTracking doesn't work reliably
        return await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => EF.Property<uint>(t, "xmin"))
            .FirstAsync();
    }

    private async Task UpdateTaskTimestampsAsync(long taskId, DateTime startUtc, DateTime endUtc)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var task = await db.Tasks.FirstAsync(t => t.Id == taskId);
        task.StartAt = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        task.EndAt = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc);
        await db.SaveChangesAsync();
    }
}
