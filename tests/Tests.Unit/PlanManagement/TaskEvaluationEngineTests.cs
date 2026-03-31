using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class TaskEvaluationEngineTests
{
    [Fact]
    public async Task RecalculateTasks_SetsStatusAndOverloadHours()
    {
        await using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);

        SeedAddress(db, 1, "HQ 1, City");
        SeedAddress(db, 2, "Obj A, City");
        SeedAddress(db, 3, "Obj B, City");

        db.Technicians.Add(new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        });

        db.ServiceObjects.AddRange(
            new ServiceObjectEntity { Id = 10, Name = "ObjA", AddressId = 2, CreatedAt = DateTime.UtcNow },
            new ServiceObjectEntity { Id = 11, Name = "ObjB", AddressId = 3, CreatedAt = DateTime.UtcNow });

        var t1 = new TaskEntity
        {
            Id = 100,
            TechnicianId = 1,
            ServiceObjectId = 10,
            Title = "A",
            StartAt = day.AddHours(9),
            EndAt = day.AddHours(10),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        var t2 = new TaskEntity
        {
            Id = 101,
            TechnicianId = 1,
            ServiceObjectId = 11,
            Title = "B",
            StartAt = day.AddHours(10).AddMinutes(30),
            EndAt = day.AddHours(11).AddMinutes(30),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        db.Tasks.AddRange(t1, t2);

        db.TravelRoutes.AddRange(
            new TravelRouteEntity
            {
                FromAddressId = 1,
                ToAddressId = 2,
                DistanceKm = 1,
                DurationMinutes = 10,
                CalculatedAt = DateTime.UtcNow
            },
            new TravelRouteEntity
            {
                FromAddressId = 2,
                ToAddressId = 3,
                DistanceKm = 8,
                DurationMinutes = 60,
                CalculatedAt = DateTime.UtcNow
            });

        await db.SaveChangesAsync();

        var timeService = new ScheduleTimeService();
        var travelResolver = Substitute.For<ITravelTimeResolver>();
        travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>
            {
                [(1, 2)] = 10,
                [(2, 3)] = 60
            }));
        var logger = Substitute.For<ILogger<TaskEvaluationEngine>>();
        var sut = new TaskEvaluationEngine(db, timeService, travelResolver, logger);

        await sut.RecalculateTasksAsync(new[] { 100L, 101L }, CancellationToken.None);

        var evals = await db.TaskEvaluations
            .AsNoTracking()
            .OrderBy(e => e.TaskId)
            .ToListAsync();

        evals.Should().HaveCount(2);
        var first = evals.Single(e => e.TaskId == 100);
        var second = evals.Single(e => e.TaskId == 101);

        first.Status.Should().Be(LoadState.Normal);
        second.Status.Should().Be(LoadState.Normal);
        first.OverloadHours.Should().BeNull();
        second.OverloadHours.Should().BeNull();
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ScheduleDbContext(options);
    }

    private static void SeedAddress(ScheduleDbContext db, long id, string fullAddress)
    {
        var street = fullAddress.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        db.Addresses.Add(new AddressEntity
        {
            Id = id,
            City = "City",
            Street = street,
            FullAddress = fullAddress,
            CreatedAt = DateTime.UtcNow
        });
    }
}
