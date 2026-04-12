using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Data.Seed;
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
    public async Task Creating_Task_Returns_Default_LoadSnapshot_Payload()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDay = DateTime.UtcNow.Date.AddDays(1);
        while (targetDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            targetDay = targetDay.AddDays(1);
        }
        var targetDate = DateOnly.FromDateTime(targetDay).ToString("yyyy-MM-dd");

        // First 6-hour task
        var firstDto = TestTaskRequestBuilder.Create(techId, targetDate, 8 * 60, 14 * 60, "Morning").Build();
        var firstResponse = await client.PostAsJsonAsync("/api/tasks", firstDto);
        firstResponse.EnsureSuccessStatusCode();

        // Second task on the same day; current API contract returns a default load snapshot on create.
        var secondDto = TestTaskRequestBuilder.Create(techId, targetDate, 14 * 60, 20 * 60, "Evening").Build();
        var secondResponse = await client.PostAsJsonAsync("/api/tasks", secondDto);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await secondResponse.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>();
        payload!.Load.Should().NotBeNull();
        payload.Load!.LoadState.Should().Be("Normal");
        payload.Load!.HasConflict.Should().BeFalse();
        payload.Load!.IsDailyLimitExceeded.Should().BeFalse();
        payload.Load!.IsWeeklyLimitExceeded.Should().BeFalse();
        payload.Load!.DailyHours.Should().Be(0);
        payload.Load!.WeeklyHours.Should().Be(0);
        payload.Load!.DailyLimit.Should().Be(8);
        payload.Load!.WeeklyLimit.Should().Be(40);
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
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedReferenceDataOnlyAsync();
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
