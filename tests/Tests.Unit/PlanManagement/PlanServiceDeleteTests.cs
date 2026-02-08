using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.PlanManagement;

public class PlanServiceDeleteTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();

    public PlanServiceDeleteTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                return DateTime.SpecifyKind(date.AddMinutes(minutes), DateTimeKind.Utc);
            });
    }

    [Fact]
    public async Task DeleteTask_PastEditableWindow_Rejected()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1, endAtUtc: DateTime.UtcNow.AddHours(-1));
        SetVersion(db, task, 2);

        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));
        _time.IsTaskStillEditable(task.EndAt).Returns(false);

        var service = CreateService(db);
        var result = await service.DeleteTaskAsync(task.Id, expectedVersion: 2, Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskDeleteError.NotEditable);
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 10 };

    private static PlanEntity SeedPlan(ScheduleDbContext db)
    {
        var plan = new PlanEntity
        {
            PeriodStart = DateTime.Today,
            PeriodEnd = DateTime.Today.AddDays(7),
            Status = PlanStatus.Active,
            Weeks = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Plans.Add(plan);
        db.SaveChanges();
        return plan;
    }

    private static TaskEntity SeedTask(ScheduleDbContext db, long planId, long technicianId, DateTime? endAtUtc = null)
    {
        var start = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(10), DateTimeKind.Utc);
        if (!db.Regions.AnyAsync().GetAwaiter().GetResult())
        {
            db.Regions.Add(new RegionEntity { Id = 1, Name = "Region", Country = "LT" });
        }
        if (!db.Addresses.AnyAsync().GetAwaiter().GetResult())
        {
            db.Addresses.Add(new AddressEntity { Id = 1, RegionId = 1, Street = "Main", FullAddress = "Main 1" });
        }
        if (!db.ServiceObjects.AnyAsync().GetAwaiter().GetResult())
        {
            db.ServiceObjects.Add(new ServiceObjectEntity { Id = 1, Name = "Obj", AddressId = 1 });
        }

        var task = new TaskEntity
        {
            PlanId = planId,
            TechnicianId = technicianId,
            ServiceObjectId = 1,
            RegionId = 1,
            StartAt = start,
            EndAt = endAtUtc ?? start.AddHours(1),
            Title = "t",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(task);
        db.SaveChanges();
        return task;
    }

    private static void SetVersion(ScheduleDbContext db, TaskEntity task, uint version)
    {
        var entry = db.Entry(task).Property<uint>("xmin");
        entry.CurrentValue = version;
    }
}
