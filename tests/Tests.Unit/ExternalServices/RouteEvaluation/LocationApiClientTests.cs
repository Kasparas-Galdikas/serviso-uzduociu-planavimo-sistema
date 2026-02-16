using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Services.ExternalServices.Common;
using ServiceTaskPlanningSystem.Services.ExternalServices.ExternalApis;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using Xunit;

namespace Tests.Unit.ExternalServices.RouteEvaluation;

public class LocationApiClientTests
{
    private readonly IExternalCallPolicy _policy = Substitute.For<IExternalCallPolicy>();
    private readonly ILogger<LocationApiClient> _logger = Substitute.For<ILogger<LocationApiClient>>();

    [Fact]
    public async Task GeocodeAsync_UsesHeigitGeocodeEndpointAndConfiguredPerMinuteLimit()
    {
        var keyedLock = new ExternalCallKeyedLock();
        var options = new HeigitApiOptions();
        options.Endpoints[HeigitEndpoints.Geocode].PerMinute = 77;
        var expected = new GeoPoint(55.1m, 24.9m);
        _policy.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Func<CancellationToken, Task<GeoPoint>>>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new LocationApiClient(
            new HttpClient(new FailingHandler()),
            BuildConfig(),
            options,
            _policy,
            keyedLock,
            _logger);

        var actual = await sut.GeocodeAsync("Kaunas Pipių 8", CancellationToken.None);

        actual.Should().Be(expected);
        await _policy.Received(1).ExecuteAsync(
            HeigitEndpoints.Geocode,
            77,
            Arg.Any<Func<CancellationToken, Task<GeoPoint>>>(),
            Arg.Any<CancellationToken>());
    }

    private static IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HEIGIT_API_KEY"] = "test-key",
                ["HEIGIT_BASE_URL"] = "https://example.test"
            })
            .Build();
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("HTTP should not be called in this test.");
        }
    }
}
