using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Boots the API for integration tests, overriding DB and auth for test purposes.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly PostgresContainerFixture _postgres;
    private readonly bool _useTestAuth;

    public TestWebApplicationFactory(PostgresContainerFixture postgres, bool useTestAuth = true)
    {
        _postgres = postgres;
        _useTestAuth = useTestAuth;

        // Set environment variable BEFORE host builds - ConfigurationLoader reads this
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.ConnectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (!_useTestAuth)
        {
            return;
        }

        builder.ConfigureServices(services =>
        {
            // Replace authentication with test scheme
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                options.DefaultChallengeScheme = TestAuthHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        });
    }
}
