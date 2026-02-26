using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.Authorization;

public class PlanServiceAuthorizationTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();

    public PlanServiceAuthorizationTests()
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
    public async Task CreateTask_PermissionDenied_ReturnsNotEditable()
    {
        using var db = CreateContext();
        SeedTechnician(db, 1);
        _time.GetUserLocalToday(0).Returns(DateTime.Today);
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(DateTime.UtcNow);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));

        var service = CreateService(db);
        var result = await service.CreateTaskAsync(1, DateTime.Today.AddDays(1), 480, 540, "t",
            TaskPriority.Medium, ScheduleTaskStatus.InProgress, string.Empty, string.Empty, string.Empty, string.Empty,
            Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskCreateError.NotEditable);
    }

    [Fact]
    public async Task CreateTask_TechnicianNotFound_ReturnsError()
    {
        using var db = CreateContext();
        _time.GetUserLocalToday(0).Returns(DateTime.Today);
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(DateTime.UtcNow);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var service = CreateService(db);
        var result = await service.CreateTaskAsync(999, DateTime.Today.AddDays(1), 480, 540, "t",
            TaskPriority.Medium, ScheduleTaskStatus.InProgress, string.Empty, string.Empty, string.Empty, string.Empty,
            Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskCreateError.TechnicianNotFound);
    }

    [Fact]
    public async Task MoveTask_PermissionDenied_ReturnsNotEditable()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        SeedTechnician(db, 1);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        SetVersion(db, task, 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(DateTime.UtcNow);

        var service = CreateService(db);
        var result = await service.MoveTaskAsync(task.Id, 1, DateTime.Today.AddDays(1), 480, expectedVersion: 1,
            Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskMoveError.NotEditable);
    }

    [Fact]
    public async Task UpdateTask_PermissionDenied_Throws()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));

        var service = CreateService(db);
        var act = async () => await service.UpdateTaskAsync(task.Id, _ => { }, task.TechnicianId, Manager(), 0, string.Empty, string.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateTask_PastLocalDate_Rejected()
    {
        using var db = CreateContext();
        _time.GetUserLocalToday(0).Returns(DateTime.Today);

        var service = CreateService(db);
        var result = await service.CreateTaskAsync(1, DateTime.Today.AddDays(-1), 480, 540, "t",
            TaskPriority.Medium, ScheduleTaskStatus.InProgress, string.Empty, string.Empty, string.Empty, string.Empty,
            Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskCreateError.InvalidWindow);
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 10 };

    private static void SeedTechnician(ScheduleDbContext db, long id)
    {
        if (!db.Regions.AnyAsync().GetAwaiter().GetResult())
        {
            db.Regions.Add(new RegionEntity { Id = 1, Name = "Region", Country = "LT" });
        }
        if (!db.Addresses.AnyAsync().GetAwaiter().GetResult())
        {
            db.Addresses.Add(new AddressEntity { Id = 1, RegionId = 1, Street = "Main", FullAddress = "Main 1" });
        }
        db.Technicians.Add(new TechnicianEntity
        {
            Id = id,
            RegionId = 1,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
        });
        db.SaveChanges();
    }

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

    private static TaskEntity SeedTask(ScheduleDbContext db, long planId, long technicianId)
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
            EndAt = start.AddHours(1),
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

