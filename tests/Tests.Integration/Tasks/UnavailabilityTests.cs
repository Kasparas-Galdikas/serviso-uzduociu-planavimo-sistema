using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;

namespace Tests.Integration.Tasks;

[Collection(IntegrationTestCollection.Name)]
public sealed class UnavailabilityTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public UnavailabilityTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Preview_And_Create_Repair_Task_When_Same_Day_Space_Exists_And_Block_New_Task()
    {
        await ResetAndReseedAsync();
        var technicianId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = DateTime.UtcNow.Date.AddDays(15).ToString("yyyy-MM-dd");

        var createTaskDto = TestTaskRequestBuilder.Create(technicianId, targetDate, 10 * 60, 11 * 60, "Overlap Task").Build();
        (await client.PostAsJsonAsync("/api/tasks", createTaskDto)).EnsureSuccessStatusCode();

        var previewResponse = await client.PostAsJsonAsync("/api/unavailability/preview", new
        {
            technicianId,
            periodStart = $"{targetDate}T09:30",
            periodEnd = $"{targetDate}T10:30"
        });

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewPayload = await previewResponse.Content.ReadFromJsonAsync<PreviewPayload>();
        previewPayload!.AffectedTasks.Should().ContainSingle();
        previewPayload.AffectedTasks[0].Title.Should().Be("Overlap Task");
        previewPayload.AffectedTasks[0].Resolution.Should().Be("backlog");

        var createResponse = await client.PostAsJsonAsync("/api/unavailability", new
        {
            technicianId,
            periodStart = $"{targetDate}T09:30",
            periodEnd = $"{targetDate}T10:30"
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createPayload = await createResponse.Content.ReadFromJsonAsync<CreatePayload>();
        createPayload!.DisplacedCount.Should().Be(1);
        createPayload.RepairedCount.Should().Be(0);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var savedUnavailability = await db.TechnicianUnavailabilities.SingleAsync();
        savedUnavailability.PeriodStart.Hour.Should().Be(9);
        savedUnavailability.PeriodStart.Minute.Should().Be(30);
        savedUnavailability.PeriodEnd.Hour.Should().Be(10);
        savedUnavailability.PeriodEnd.Minute.Should().Be(30);
        (await db.BacklogItems.CountAsync()).Should().Be(1);
        var backlogTask = await db.BacklogItems.SingleAsync();
        backlogTask.Title.Should().Be("Overlap Task");
        (await db.Tasks.CountAsync()).Should().Be(0);

        var blockedTaskDto = TestTaskRequestBuilder.Create(technicianId, targetDate, 10 * 60, 10 * 60 + 30, "Blocked Task").Build();
        var blockedTaskResponse = await client.PostAsJsonAsync("/api/tasks", blockedTaskDto);
        blockedTaskResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var blockedMessage = await blockedTaskResponse.Content.ReadAsStringAsync();
        blockedMessage.Should().Contain("neaktyvumas");
    }

    [Fact]
    public async Task Preview_Is_ReadOnly()
    {
        await ResetAndReseedAsync();
        var technicianId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = DateTime.UtcNow.Date.AddDays(16).ToString("yyyy-MM-dd");

        var createTaskDto = TestTaskRequestBuilder.Create(technicianId, targetDate, 10 * 60, 11 * 60, "Preview Only").Build();
        (await client.PostAsJsonAsync("/api/tasks", createTaskDto)).EnsureSuccessStatusCode();

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            (await db.TechnicianUnavailabilities.CountAsync()).Should().Be(0);
            (await db.BacklogItems.CountAsync()).Should().Be(0);
            (await db.Tasks.CountAsync()).Should().Be(1);
        }

        var previewResponse = await client.PostAsJsonAsync("/api/unavailability/preview", new
        {
            technicianId,
            periodStart = $"{targetDate}T09:30",
            periodEnd = $"{targetDate}T10:30"
        });

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            (await db.TechnicianUnavailabilities.CountAsync()).Should().Be(0);
            (await db.BacklogItems.CountAsync()).Should().Be(0);
            (await db.Tasks.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task Delete_Restores_Task_When_Original_Slot_Is_Free()
    {
        await ResetAndReseedAsync();
        var technicianId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = DateTime.UtcNow.Date.AddDays(18).ToString("yyyy-MM-dd");

        var createTaskDto = TestTaskRequestBuilder.Create(technicianId, targetDate, 19 * 60, 20 * 60, "Restore Me").Build();
        (await client.PostAsJsonAsync("/api/tasks", createTaskDto)).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync("/api/unavailability", new
        {
            technicianId,
            periodStart = $"{targetDate}T19:00",
            periodEnd = $"{targetDate}T20:00"
        })).EnsureSuccessStatusCode();

        long unavailabilityId;
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            unavailabilityId = await db.TechnicianUnavailabilities.Select(u => u.Id).SingleAsync();
            (await db.Tasks.CountAsync()).Should().Be(0);
            (await db.BacklogItems.CountAsync()).Should().Be(1);
        }

        var deleteResponse = await client.DeleteAsync($"/api/unavailability/{unavailabilityId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var deletePayload = await deleteResponse.Content.ReadFromJsonAsync<DeletePayload>();
        deletePayload!.RestoredCount.Should().Be(1);
        deletePayload.BacklogRemainingCount.Should().Be(0);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            (await db.TechnicianUnavailabilities.CountAsync()).Should().Be(0);
            (await db.BacklogItems.CountAsync()).Should().Be(0);

            var restoredTask = await db.Tasks.SingleAsync();
            restoredTask.Title.Should().Be("Restore Me");
            restoredTask.TechnicianId.Should().Be(technicianId);
            restoredTask.StartAt.Hour.Should().Be(19);
            restoredTask.EndAt.Hour.Should().Be(20);
        }
    }

    [Fact]
    public async Task Update_Atomically_Restores_Old_Task_And_Displaces_New_Task()
    {
        await ResetAndReseedAsync();
        var technicianId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = DateTime.UtcNow.Date.AddDays(19).ToString("yyyy-MM-dd");

        var taskA = TestTaskRequestBuilder.Create(technicianId, targetDate, 12 * 60, 13 * 60, "Task A").Build();
        var taskB = TestTaskRequestBuilder.Create(technicianId, targetDate, 19 * 60, 20 * 60, "Task B").Build();
        (await client.PostAsJsonAsync("/api/tasks", taskA)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/tasks", taskB)).EnsureSuccessStatusCode();

        (await client.PostAsJsonAsync("/api/unavailability", new
        {
            technicianId,
            periodStart = $"{targetDate}T19:00",
            periodEnd = $"{targetDate}T20:00"
        })).EnsureSuccessStatusCode();

        long unavailabilityId;
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            unavailabilityId = await db.TechnicianUnavailabilities.Select(u => u.Id).SingleAsync();
            (await db.Tasks.Select(t => t.Title).ToListAsync()).Should().ContainSingle().Which.Should().Be("Task A");
            (await db.BacklogItems.Select(b => b.Title).ToListAsync()).Should().ContainSingle().Which.Should().Be("Task B");
        }

        var updateResponse = await client.PutAsJsonAsync($"/api/unavailability/{unavailabilityId}", new
        {
            technicianId,
            periodStart = $"{targetDate}T12:00",
            periodEnd = $"{targetDate}T13:00"
        });

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
            var tasks = await db.Tasks.OrderBy(t => t.StartAt).Select(t => t.Title).ToListAsync();
            var backlog = await db.BacklogItems.OrderBy(b => b.CreatedAt).Select(b => b.Title).ToListAsync();
            var period = await db.TechnicianUnavailabilities.SingleAsync();

            tasks.Should().ContainSingle().Which.Should().Be("Task B");
            backlog.Should().ContainSingle().Which.Should().Be("Task A");
            period.PeriodStart.Hour.Should().Be(12);
            period.PeriodEnd.Hour.Should().Be(13);
        }
    }

    private HttpClient CreateClient(string email, string role = "manager")
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        client.DefaultRequestHeaders.Add("X-Timezone-Offset", "0");
        return client;
    }

    private async Task ResetAndReseedAsync()
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = _fixture.Factory.Services.CreateScope();
        await MinimalSeedData.EnsureSeedDataAsync(scope);
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

    private sealed class PreviewPayload
    {
        public PreviewTask[] AffectedTasks { get; set; } = Array.Empty<PreviewTask>();
    }

    private sealed class PreviewTask
    {
        public string Title { get; set; } = string.Empty;
        public string Resolution { get; set; } = string.Empty;
    }

    private sealed class CreatePayload
    {
        public int DisplacedCount { get; set; }
        public int RepairedCount { get; set; }
    }

    private sealed class DeletePayload
    {
        public int RestoredCount { get; set; }
        public int BacklogRemainingCount { get; set; }
    }
}
