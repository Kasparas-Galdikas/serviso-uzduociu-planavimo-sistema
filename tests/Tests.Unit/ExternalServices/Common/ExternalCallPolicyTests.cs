using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Services.ExternalServices.Common;
using Xunit;

namespace Tests.Unit.ExternalServices.Common;

public class ExternalCallPolicyTests
{
    private readonly ILogger<ExternalCallPolicy> _logger = Substitute.For<ILogger<ExternalCallPolicy>>();

    [Fact]
    public async Task ExecuteAsync_InvalidPerMinute_Throws()
    {
        var sut = new ExternalCallPolicy(_logger);

        var act = async () => await sut.ExecuteAsync(
            "geocode",
            0,
            _ => Task.FromResult(1),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ExecuteAsync_TransientFailure_RetriesAndSucceeds()
    {
        var sut = new ExternalCallPolicy(_logger);
        var attempts = 0;

        var result = await sut.ExecuteAsync(
            "matrix",
            30,
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
                }

                return Task.FromResult(9);
            },
            CancellationToken.None);

        result.Should().Be(9);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_NonRetryableFailure_DoesNotRetry()
    {
        var sut = new ExternalCallPolicy(_logger);
        var attempts = 0;

        var act = async () => await sut.ExecuteAsync<int>(
            "geocode",
            80,
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("invalid");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }
}
