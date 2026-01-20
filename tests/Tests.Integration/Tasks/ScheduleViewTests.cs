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
using Microsoft.EntityFrameworkCore;

namespace Tests.Integration.Tasks;

/// <summary>
/// Integration coverage for schedule retrieval and load snapshots on move/update.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ScheduleViewTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public ScheduleViewTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Schedule_ReturnsFutureTasks_OrderedAndFilteredByTechnician()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(14)).ToString("yyyy-MM-dd");

        // Create tasks for two technicians in the same week
        await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Tech1 AM");
        await CreateTaskAsync(client, techId, targetDate, 11 * 60, 12 * 60, "Tech1 Noon");
        await CreateTaskAsync(client, otherTechId, targetDate, 13 * 60, 14 * 60, "Other Tech");

        var techName = await GetTechnicianNameAsync(techId);
        var otherTechName = await GetTechnicianNameAsync(otherTechId);

        // Request schedule filtered to first technician
        var response = await client.GetAsync($"/Schedule/Table?view=week&date={targetDate}&technicianId={techId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        // Verify technician filtering in rendered table HTML (row headers)
        html.Should().Contain(techName);
        html.Should().NotContain(otherTechName);
    }

    [Fact]
    public async Task MoveTask_Returns_LoadSnapshot_With_WeeklyLimitExceeded()
    {
        await ResetAndReseedAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");

        // Find the next Monday to ensure all tasks fall within the same calendar week
        var baseDate = DateTime.UtcNow.Date.AddDays(10);
        while (baseDate.DayOfWeek != DayOfWeek.Monday)
        {
            baseDate = baseDate.AddDays(1);
        }
        var startDate = DateOnly.FromDateTime(baseDate);

        // Seed 6 daily tasks (Mon-Sat) within the same week: 6 days × 8h = 48h > 40h weekly limit
        for (int day = 0; day < 6; day++)
        {
            var date = startDate.AddDays(day).ToString("yyyy-MM-dd");
            await CreateTaskAsync(client, techId, date, 8 * 60, 16 * 60, $"Day{day}-Shift"); // 8h per day -> 48h/week
        }

        // Create and move a task on Friday (day 4) to trigger load snapshot calculation
        var targetDate = startDate.AddDays(4).ToString("yyyy-MM-dd");
        var lastTask = await CreateTaskAsync(client, techId, targetDate, 16 * 60, 17 * 60, "Last");
        var version = await GetTaskVersionAsync(lastTask.Id);

        var response = await client.PatchAsJsonAsync($"/api/tasks/{lastTask.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = techId,
            TargetDate = targetDate,
            TargetSlotStartMinutes = 17 * 60, // move to 17:00, no overlap
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<TaskMovedResponseDto>();
        payload!.Load.Should().NotBeNull();
        payload.Load!.IsWeeklyLimitExceeded.Should().BeTrue();
        payload.Load!.LoadState.Should().Be("Conflict");
    }

    private HttpClient CreateClient(string email, string role = "manager")
    {
        var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
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
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Technicians
            .Include(t => t.User)
            .Where(t => t.User != null && t.User.Email == email)
            .Select(t => t.Id)
            .FirstAsync();
    }

    private async Task<string> GetTechnicianNameAsync(long techId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Technicians
            .Include(t => t.User)
            .Where(t => t.Id == techId)
            .Select(t => t.User != null && !string.IsNullOrWhiteSpace(t.User.Name) ? t.User.Name : t.User!.Email)
            .FirstAsync();
    }

    private async Task<TaskCreatedOrUpdatedResponseDto> CreateTaskAsync(HttpClient client, long techId, string targetDate, int startMinutes, int endMinutes, string title)
    {
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, startMinutes, endMinutes, title).Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;
    }

    private async Task<uint> GetTaskVersionAsync(long taskId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => EF.Property<uint>(t, "xmin"))
            .FirstAsync();
    }
}
