using System;
using System.Collections.Generic;
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

public class DistanceApiClientTests
{
    private readonly IExternalCallPolicy _policy = Substitute.For<IExternalCallPolicy>();
    private readonly ILogger<DistanceApiClient> _logger = Substitute.For<ILogger<DistanceApiClient>>();

    [Fact]
    public async Task GetMatrixAsync_UsesHeigitMatrixEndpointAndConfiguredPerMinuteLimit()
    {
        var keyedLock = new ExternalCallKeyedLock();
        var options = new HeigitApiOptions();
        options.Endpoints[HeigitEndpoints.Matrix].PerMinute = 29;
        var expected = new MatrixResult(
            new List<IReadOnlyList<double>>
            {
                new List<double> { 0d, 60d },
                new List<double> { 60d, 0d }
            },
            new List<IReadOnlyList<double>>
            {
                new List<double> { 0d, 1000d },
                new List<double> { 1000d, 0d }
            });
        _policy.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Func<CancellationToken, Task<MatrixResult>>>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);

        var sut = new DistanceApiClient(
            new HttpClient(new FailingHandler()),
            BuildConfig(),
            options,
            _policy,
            keyedLock,
            _logger);

        var actual = await sut.GetMatrixAsync(
            new List<GeoPoint> { new(54.7m, 25.3m), new(54.8m, 25.4m) },
            CancellationToken.None);

        actual.Should().BeSameAs(expected);
        await _policy.Received(1).ExecuteAsync(
            HeigitEndpoints.Matrix,
            29,
            Arg.Any<Func<CancellationToken, Task<MatrixResult>>>(),
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
