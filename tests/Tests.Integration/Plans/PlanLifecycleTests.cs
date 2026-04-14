using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;

namespace Tests.Integration.Plans;

/// <summary>
/// Integration tests for the current backlog-draft lifecycle:
/// - Backlog draft creation/refresh
/// - Applying a backlog draft onto the active plan
/// - Preserving the active plan identity across publishes
/// - Role-based access (manager vs technician)
///
/// These tests verify PLAN STATE transitions,
/// NOT task-level permissions.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class PlanLifecycleTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public PlanLifecycleTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // -------------------------
    // DRAFT CREATION
    // -------------------------

    [Fact]
    public async Task Manager_Can_Create_Draft_Plan()
    {
        var client = CreateClient("manager@test.com");

        var response = await client.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertSinglePlanWithStatus(PlanStatus.Draft);
    }

    [Fact]
    public async Task Technician_Cannot_Create_Draft_Plan()
    {
        var client = CreateClient("technician@test.com", "technician");

        var response = await client.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -------------------------
    // PUBLISHING
    // -------------------------

    [Fact]
    public async Task Manager_Can_Publish_Draft_Plan()
    {
        var manager = CreateClient("manager@test.com", "manager");

        // Create draft
        var draftResponse = await manager.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });
        draftResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Apply the draft onto the active plan
        var publishResponse = await manager.PostAsync("/api/plans/backlog-draft/publish", null);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertSinglePlanWithStatus(PlanStatus.Active);
    }

    [Fact]
    public async Task Technician_Cannot_Publish_Plan()
    {
        var manager = CreateClient("manager@test.com", "manager");
        await manager.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });

        var technician = CreateClient("technician@test.com", "technician");

        var response = await technician.PostAsync("/api/plans/backlog-draft/publish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Publishing_Without_Draft_Fails()
    {
        var manager = CreateClient("manager@test.com", "manager");

        var response = await manager.PostAsync("/api/plans/backlog-draft/publish", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -------------------------
    // SUPERSEDING
    // -------------------------

    [Fact]
    public async Task Publishing_BacklogDraft_Reuses_ActivePlan_Without_Creating_Superseded_Copy()
    {
        var manager = CreateClient("manager@test.com", "manager");

        using var beforeScope = _fixture.Factory.Services.CreateScope();
        var beforeDb = beforeScope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var initialActivePlanId = await beforeDb.Plans
            .Where(p => p.Status == PlanStatus.Active)
            .Select(p => p.Id)
            .SingleAsync();

        // First plan
        await manager.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });
        await manager.PostAsync("/api/plans/backlog-draft/publish", null);

        // Second plan
        await manager.PostAsJsonAsync("/api/plans/backlog-draft/ensure", new { });
        await manager.PostAsync("/api/plans/backlog-draft/publish", null);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var activePlans = await db.Plans
            .Where(p => p.Status == PlanStatus.Active)
            .ToListAsync();

        var supersededPlans = await db.Plans
            .Where(p => p.Status == PlanStatus.Superseded)
            .ToListAsync();

        activePlans.Should().HaveCount(1);
        activePlans.Single().Id.Should().Be(initialActivePlanId);
        supersededPlans.Should().BeEmpty();
        await AssertSinglePlanWithStatus(PlanStatus.Draft);
    }

    // -------------------------
    // HELPERS
    // -------------------------

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

    private async Task AssertSinglePlanWithStatus(PlanStatus status)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var plans = await db.Plans
            .Where(p => p.Status == status)
            .ToListAsync();

        plans.Should().HaveCount(1);
    }
}
