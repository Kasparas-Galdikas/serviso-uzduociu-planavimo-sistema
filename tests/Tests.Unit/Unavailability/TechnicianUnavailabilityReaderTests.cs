using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Unavailability;
using Xunit;

namespace Tests.Unit.Unavailability;

public sealed class TechnicianUnavailabilityReaderTests
{
    [Fact]
    public void HasBlockedOverlap_WhenTaskStartsAtBlockEnd_ReturnsFalse()
    {
        var reader = CreateReader();
        var block = new TechnicianBlockedInterval(
            new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc));

        var result = reader.HasBlockedOverlap(
            new[] { block },
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 15, 0, 0, DateTimeKind.Utc));

        result.Should().BeFalse();
    }

    [Fact]
    public void HasBlockedOverlap_WhenTaskEndsAtBlockStart_ReturnsFalse()
    {
        var reader = CreateReader();
        var block = new TechnicianBlockedInterval(
            new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc));

        var result = reader.HasBlockedOverlap(
            new[] { block },
            new DateTime(2026, 4, 7, 11, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc));

        result.Should().BeFalse();
    }

    [Fact]
    public void HasBlockedOverlap_WhenTaskStartsInsideBlock_ReturnsTrue()
    {
        var reader = CreateReader();
        var block = new TechnicianBlockedInterval(
            new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc));

        var result = reader.HasBlockedOverlap(
            new[] { block },
            new DateTime(2026, 4, 7, 14, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc));

        result.Should().BeTrue();
    }

    [Fact]
    public void HasBlockedOverlap_WhenTaskFullyCoversBlock_ReturnsTrue()
    {
        var reader = CreateReader();
        var block = new TechnicianBlockedInterval(
            new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc));

        var result = reader.HasBlockedOverlap(
            new[] { block },
            new DateTime(2026, 4, 7, 11, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 15, 0, 0, DateTimeKind.Utc));

        result.Should().BeTrue();
    }

    [Fact]
    public void TouchesOrOverlaps_WhenTaskStartsAtBlockEnd_ReturnsTrue()
    {
        var reader = CreateReader();
        var block = new TechnicianBlockedInterval(
            new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc));

        var result = reader.TouchesOrOverlaps(
            block,
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 15, 0, 0, DateTimeKind.Utc));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetBlockedIntervalsAsync_WhenWindowStartsAtBlockEnd_ReturnsEmpty()
    {
        await using var db = CreateContext();
        db.TechnicianUnavailabilities.Add(new TechnicianUnavailabilityEntity
        {
            TechnicianId = 1,
            PeriodStart = new DateTime(2026, 4, 7, 11, 30, 0, DateTimeKind.Utc),
            PeriodEnd = new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var reader = new TechnicianUnavailabilityReader(db);

        var blocked = await reader.GetBlockedIntervalsAsync(
            1,
            new DateTime(2026, 4, 7, 14, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 7, 15, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        blocked.Should().BeEmpty();
    }

    private static TechnicianUnavailabilityReader CreateReader()
    {
        var db = CreateContext();
        return new TechnicianUnavailabilityReader(db);
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }
}
