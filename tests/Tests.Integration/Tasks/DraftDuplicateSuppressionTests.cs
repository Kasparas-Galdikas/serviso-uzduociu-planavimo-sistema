using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Xunit;

namespace Tests.Integration.Tasks;

[Collection(IntegrationTestCollection.Name)]
public sealed class DraftDuplicateSuppressionTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public DraftDuplicateSuppressionTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateDraftTask_ConcurrentExactDuplicates_CreateSinglePersistedTask()
    {
        await EnsureDraftAsync();
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var targetDay = DateTime.UtcNow.Date.AddDays(1);
        var targetDate = DateOnly.FromDateTime(targetDay).ToString("yyyy-MM-dd");
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 17 * 60 + 30, 18 * 60, "dup")
            .WithObjectName("Obj")
            .WithObjectAddress("Main 1, City")
            .Build();

        var clientA = CreateClient("manager@test.com");
        var clientB = CreateClient("manager@test.com");

        var responses = await Task.WhenAll(
            clientA.PostAsJsonAsync("/api/tasks/draft", dto),
            clientB.PostAsJsonAsync("/api/tasks/draft", dto));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var payloads = await Task.WhenAll(
            responses[0].Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>(),
            responses[1].Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>());

        payloads[0]!.Id.Should().Be(payloads[1]!.Id);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var draftPlanId = await db.Plans.Where(p => p.Status == PlanStatus.Draft).Select(p => p.Id).SingleAsync();
        var startAtUtc = DateTime.SpecifyKind(targetDay.AddHours(17.5), DateTimeKind.Utc);
        var endAtUtc = DateTime.SpecifyKind(targetDay.AddHours(18), DateTimeKind.Utc);

        var count = await db.Tasks
            .Where(t => t.PlanId == draftPlanId)
            .Where(t => t.TechnicianId == techId)
            .Where(t => t.StartAt == startAtUtc && t.EndAt == endAtUtc)
            .CountAsync();

        count.Should().Be(1);
    }

    private HttpClient CreateClient(string email, string role = "manager")
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private async Task EnsureDraftAsync()
    {
        var client = CreateClient("manager@test.com");
        var response = await client.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task ResetAndReseedAsync()
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = _fixture.Factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync();
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
}
