using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Integration.Fixtures;

/// <summary>
/// Boots the API for integration tests, overriding DB and auth for test purposes.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IntegrationDatabaseFixture _database;
    private readonly bool _useTestAuth;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly IReadOnlyDictionary<string, string?>? _configOverrides;

    public TestWebApplicationFactory(
        IntegrationDatabaseFixture database,
        bool useTestAuth = true,
        Action<IServiceCollection>? configureTestServices = null,
        IReadOnlyDictionary<string, string?>? configOverrides = null)
    {
        _database = database;
        _useTestAuth = useTestAuth;
        _configureTestServices = configureTestServices;
        _configOverrides = configOverrides;

        // Set environment variable BEFORE host builds - ConfigurationLoader reads this
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _database.ConnectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_configOverrides != null && _configOverrides.Count > 0)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(_configOverrides);
            });
        }

        builder.ConfigureServices(services =>
        {
            if (_useTestAuth)
            {
                // Replace authentication with test scheme
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthHandler.Scheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            }

            _configureTestServices?.Invoke(services);
        });
    }
}
