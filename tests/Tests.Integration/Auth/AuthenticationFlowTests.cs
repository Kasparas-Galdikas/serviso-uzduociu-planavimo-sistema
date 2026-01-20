using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;

namespace Tests.Integration.Auth;

/// <summary>
/// Integration coverage for authentication/authorization failure modes and real cookie login flow.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class AuthenticationFlowTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public AuthenticationFlowTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MissingAuthHeaders_ReturnsUnauthorized()
    {
        await ResetAndReseedAsync();
        var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidRoleHeader_ReturnsUnauthorized()
    {
        await ResetAndReseedAsync();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", "manager@test.com");
        client.DefaultRequestHeaders.Add("X-Test-Role", "invalid");

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CookieLogin_Allows_ApiAccess()
    {
        await ResetAndReseedAsync();

        using var factory = new TestWebApplicationFactory(_fixture.Postgres, useTestAuth: false);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var antiForgeryToken = await GetAntiForgeryTokenAsync(client);

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("email", "manager@test.com"),
            new KeyValuePair<string, string>("password", string.Empty),
            new KeyValuePair<string, string>("view", "week"),
            new KeyValuePair<string, string>("__RequestVerificationToken", antiForgeryToken)
        });

        var loginResponse = await client.PostAsync("/Authentication/Login", form);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var meResponse = await client.GetAsync("/api/auth/me");

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        payload!.GetProperty("role").GetString().Should().Be("manager");
        payload.GetProperty("userId").ValueKind.Should().Be(JsonValueKind.Number);
    }

    private async Task ResetAndReseedAsync()
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = _fixture.Factory.Services.CreateScope();
        await SeedData.EnsureSeedDataAsync(scope);
    }

    private static async Task<string> GetAntiForgeryTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Authentication/Index");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("anti-forgery token should be present on login page");
        return match.Groups[1].Value;
    }
}
