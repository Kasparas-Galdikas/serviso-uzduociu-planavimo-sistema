using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.Time;

public class PlanningCalendarTests
{
    [Fact]
    public async Task Builds_Window_From_Boundary_With_Fallback_Weeks()
    {
        using var db = CreateContext();
        var end = DateTime.Today.AddDays(5);
        db.Plans.Add(new PlanEntity
        {
            Id = 1,
            PeriodStart = end.AddDays(-7),
            PeriodEnd = end,
            Weeks = 1,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var calendar = new PlanningCalendar(db, NullLogger<PlanningCalendar>.Instance);

        var boundary = await calendar.GetBoundaryAsync();
        var window = await calendar.BuildWindowAsync(null, null, null, fallbackWeeks: 4);

        window.Start.Should().Be(boundary.FirstFreeDate);
        window.End.Should().Be(window.Start.AddDays(27));
        window.Weeks.Should().Be(4);
        window.Days.Should().Be(28);
        window.FirstFreeDate.Should().Be(boundary.FirstFreeDate);
    }

    [Fact]
    public async Task Throws_When_Requested_Start_Before_FirstFree()
    {
        using var db = CreateContext();
        var end = DateTime.Today.AddDays(3);
        db.Plans.Add(new PlanEntity
        {
            Id = 2,
            PeriodStart = end.AddDays(-7),
            PeriodEnd = end,
            Weeks = 1,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var calendar = new PlanningCalendar(db, NullLogger<PlanningCalendar>.Instance);
        var boundary = await calendar.GetBoundaryAsync();
        var beforeBoundary = boundary.FirstFreeDate.AddDays(-1);

        var act = async () => await calendar.BuildWindowAsync(beforeBoundary, beforeBoundary.AddDays(1), null, fallbackWeeks: 2);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Drafts may start from*");
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }
}
