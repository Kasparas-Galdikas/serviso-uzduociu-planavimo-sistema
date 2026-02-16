using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ServiceTaskPlanningSystem.Services.ExternalServices.Common;
using Xunit;

namespace Tests.Unit.ExternalServices.Common;

public class ExternalCallKeyedLockTests
{
    [Fact]
    public async Task RunSingleFlightAsync_SameKeyConcurrent_ExecutesActionOnce()
    {
        var sut = new ExternalCallKeyedLock();
        var executionCount = 0;

        var calls = Enumerable.Range(0, 12)
            .Select(_ => sut.RunSingleFlightAsync(
                "geocode:ADDR-1",
                async _ =>
                {
                    Interlocked.Increment(ref executionCount);
                    await Task.Delay(50);
                    return 42;
                }))
            .ToList();

        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(v => v == 42);
        executionCount.Should().Be(1);
    }

    [Fact]
    public async Task RunSingleFlightAsync_DifferentKeys_ExecuteSeparately()
    {
        var sut = new ExternalCallKeyedLock();
        var executionCount = 0;

        var calls = new List<Task<int>>
        {
            sut.RunSingleFlightAsync("matrix:route-a", async _ =>
            {
                Interlocked.Increment(ref executionCount);
                await Task.Delay(30);
                return 1;
            }),
            sut.RunSingleFlightAsync("matrix:route-b", async _ =>
            {
                Interlocked.Increment(ref executionCount);
                await Task.Delay(30);
                return 2;
            }),
            sut.RunSingleFlightAsync("matrix:route-a", async _ =>
            {
                Interlocked.Increment(ref executionCount);
                await Task.Delay(30);
                return 1;
            })
        };

        var results = await Task.WhenAll(calls);

        results.Should().BeEquivalentTo(new[] { 1, 2, 1 });
        executionCount.Should().Be(2);
    }

    [Fact]
    public async Task RunSingleFlightAsync_AfterFailure_KeyIsReleasedAndCanRunAgain()
    {
        var sut = new ExternalCallKeyedLock();
        var attempts = 0;

        Func<CancellationToken, Task<int>> failingAction = _ =>
        {
            attempts++;
            throw new InvalidOperationException("boom");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RunSingleFlightAsync("geocode:ADDR-ERR", failingAction));

        var result = await sut.RunSingleFlightAsync(
            "geocode:ADDR-ERR",
            _ =>
            {
                attempts++;
                return Task.FromResult(7);
            });

        result.Should().Be(7);
        attempts.Should().Be(2);
    }
}
