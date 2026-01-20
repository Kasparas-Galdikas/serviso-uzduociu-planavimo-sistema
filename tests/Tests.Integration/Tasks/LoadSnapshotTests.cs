using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using Microsoft.EntityFrameworkCore;

namespace Tests.Integration.Tasks;

/// <summary>
/// Integration tests for load snapshots returned after task creation.
/// Ensures overload flags are surfaced when limits are exceeded.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class LoadSnapshotTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public LoadSnapshotTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_Task_That_Exceeds_Daily_Limit_Returns_Overload_LoadSnapshot()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(15)).ToString("yyyy-MM-dd");

        // First 6-hour task
        var firstResponse = await client.PostAsJsonAsync("/api/tasks", new CreateTaskRequestDto
        {
            TechnicianId = (int)techId,
            TargetDate = targetDate,
            StartMinutes = 8 * 60,
            EndMinutes = 14 * 60,
            Title = "Morning",
            Location = "HQ",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.Planned,
            TimezoneOffsetMinutes = 0
        });
        firstResponse.EnsureSuccessStatusCode();

        // Second 6-hour task (total 12h > 8h limit) same day, non-overlapping
        var secondResponse = await client.PostAsJsonAsync("/api/tasks", new CreateTaskRequestDto
        {
            TechnicianId = (int)techId,
            TargetDate = targetDate,
            StartMinutes = 14 * 60,
            EndMinutes = 20 * 60,
            Title = "Evening",
            Location = "HQ",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.Planned,
            TimezoneOffsetMinutes = 0
        });

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await secondResponse.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>();
        payload!.Load.Should().NotBeNull();
        payload.Load!.IsDailyLimitExceeded.Should().BeTrue();
        payload.Load!.LoadState.Should().Be("Conflict");
        payload.Load!.DailyHours.Should().BeGreaterThan(payload.Load!.DailyLimit);
    }

    private HttpClient CreateClient(string email, string role = "manager")
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
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
        var db = scope.ServiceProvider.GetRequiredService<ServiceTaskPlanningSystem.Data.ScheduleDbContext>();
        return await db.Technicians
            .Include(t => t.User)
            .Where(t => t.User != null && t.User.Email == email)
            .Select(t => t.Id)
            .FirstAsync();
    }
}
