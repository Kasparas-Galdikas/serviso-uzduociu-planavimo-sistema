using System.Net;
using System.Net.Http;
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
/// Integration tests for plan lifecycle rules:
/// - Draft creation
/// - Publishing
/// - Superseding
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

        var response = await client.PostAsync("/api/plans/draft", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertSinglePlanWithStatus(PlanStatus.Draft);
    }

    [Fact]
    public async Task Technician_Cannot_Create_Draft_Plan()
    {
        var client = CreateClient("technician@test.com", "technician");

        var response = await client.PostAsync("/api/plans/draft", null);

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
        var draftResponse = await manager.PostAsync("/api/plans/draft", null);
        draftResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Publish
        var publishResponse = await manager.PostAsync("/api/plans/publish", null);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertSinglePlanWithStatus(PlanStatus.Active);
    }

    [Fact]
    public async Task Technician_Cannot_Publish_Plan()
    {
        var manager = CreateClient("manager@test.com", "manager");
        await manager.PostAsync("/api/plans/draft", null);

        var technician = CreateClient("technician@test.com", "technician");

        var response = await technician.PostAsync("/api/plans/publish", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Publishing_Without_Draft_Fails()
    {
        var manager = CreateClient("manager@test.com", "manager");

        var response = await manager.PostAsync("/api/plans/publish", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -------------------------
    // SUPERSEDING
    // -------------------------

    [Fact]
    public async Task Publishing_New_Plan_Supersedes_Previous_Active_Plan()
    {
        var manager = CreateClient("manager@test.com", "manager");

        // First plan
        await manager.PostAsync("/api/plans/draft", null);
        await manager.PostAsync("/api/plans/publish", null);

        // Second plan
        await manager.PostAsync("/api/plans/draft", null);
        await manager.PostAsync("/api/plans/publish", null);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var activePlans = await db.Plans
            .Where(p => p.Status == PlanStatus.Active)
            .ToListAsync();

        var supersededPlans = await db.Plans
            .Where(p => p.Status == PlanStatus.Superseded)
            .ToListAsync();

        activePlans.Should().HaveCount(1);

        supersededPlans.Should().NotBeEmpty();
        supersededPlans.Should().OnlyContain(p => p.SupersededByPlanId != null);

        supersededPlans.Select(p => p.SupersededByPlanId)
            .Should().Contain(activePlans.Single().Id);
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
