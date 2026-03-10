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
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.Authorization;

public class PlanServiceAuthorizationTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
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
        _time.ConvertUtcToLocal(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(call =>
            {
                var utc = DateTime.SpecifyKind((DateTime)call[0], DateTimeKind.Utc);
                var offsetMinutes = (int)call[1];
                return utc.AddMinutes(offsetMinutes);
            });
        _time.GetEarliestCreatableStartMinutes(
                Arg.Any<DateTime>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true });
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
    public async Task SwapTasks_PermissionDenied_ReturnsNotEditable()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        SeedTechnician(db, 1);
        var first = SeedTask(db, plan.Id, technicianId: 1);
        var second = SeedTask(db, plan.Id, technicianId: 1, startOffsetHours: 2);
        SetVersion(db, first, 1);
        SetVersion(db, second, 2);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));

        var service = CreateService(db);
        var result = await service.SwapTasksAsync(first.Id, second.Id, 1, 2, Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskSwapError.NotEditable);
    }

    [Fact]
    public async Task SwapTasks_DifferentTechnicians_ReturnsInvalidSwap()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        SeedTechnician(db, 1);
        SeedTechnician(db, 2);
        var first = SeedTask(db, plan.Id, technicianId: 1);
        var second = SeedTask(db, plan.Id, technicianId: 2, startOffsetHours: 2);
        SetVersion(db, first, 1);
        SetVersion(db, second, 2);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var service = CreateService(db);
        var result = await service.SwapTasksAsync(first.Id, second.Id, 1, 2, Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TaskSwapError.InvalidSwap);
    }

    [Fact]
    public async Task UpdateTask_PermissionDenied_ReturnsNotEditable()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));

        var service = CreateService(db);
        var result = await service.UpdateTaskAsync(task.Id, _ => { }, task.TechnicianId, Manager(), 0, string.Empty, string.Empty, CancellationToken.None);
        result.Error.Should().Be(TaskUpdateError.NotEditable);
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

    [Fact]
    public async Task CreateTask_TodayBeforeEarliestSlot_Rejected()
    {
        using var db = CreateContext();
        var today = DateTime.Today;
        _time.GetUserLocalToday(0).Returns(today);
        _time.GetEarliestCreatableStartMinutes(today, 0, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes)
            .Returns(11 * 60 + 30);

        var service = CreateService(db);
        var result = await service.CreateTaskAsync(
            technicianId: 1,
            targetDate: today,
            startMinutes: 11 * 60,
            endMinutes: 12 * 60,
            title: "t",
            priority: TaskPriority.Medium,
            status: ScheduleTaskStatus.InProgress,
            skillLevel: string.Empty,
            objectName: string.Empty,
            objectAddress: string.Empty,
            instructions: string.Empty,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskCreateError.InvalidWindow);
    }

    [Fact]
    public async Task CreateTask_InboundTravelStartsInPast_ReturnsTravelInfeasible()
    {
        using var db = CreateContext();
        SeedTechnician(db, 1);
        SeedPlan(db);

        var today = DateTime.Today;
        _time.GetUserLocalToday(0).Returns(today);
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(false);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = true,
                InboundTravelMinutes = 45
            });

        var service = CreateService(db);
        var result = await service.CreateTaskAsync(
            technicianId: 1,
            targetDate: today,
            startMinutes: 12 * 60,
            endMinutes: 13 * 60,
            title: "t",
            priority: TaskPriority.Medium,
            status: ScheduleTaskStatus.InProgress,
            skillLevel: string.Empty,
            objectName: string.Empty,
            objectAddress: string.Empty,
            instructions: string.Empty,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskCreateError.TravelInfeasible);
    }

    [Fact]
    public async Task UpdateTask_TodayBeforeEarliestSlot_ReturnsInvalidWindow()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var today = DateTime.Today;
        _time.GetUserLocalToday(0).Returns(today);
        _time.GetEarliestCreatableStartMinutes(today, 0, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes)
            .Returns(11 * 60 + 30);

        var service = CreateService(db);
        var result = await service.UpdateTaskAsync(
            task.Id,
            t =>
            {
                t.StartAt = DateTime.SpecifyKind(today.AddHours(11), DateTimeKind.Utc);
                t.EndAt = DateTime.SpecifyKind(today.AddHours(12), DateTimeKind.Utc);
            },
            task.TechnicianId,
            Manager(),
            timezoneOffsetMinutes: 0,
            objectName: null,
            objectAddress: null,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskUpdateError.InvalidWindow);
        result.Message.Should().Be("Negalima planuoti ankstesniame nei artimiausias galimas 30 min. laiko langas.");
    }

    [Fact]
    public async Task UpdateTask_InboundTravelStartsInPast_ReturnsTravelInfeasible()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _time.GetUserLocalToday(0).Returns(DateTime.Today);
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(false);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = true,
                InboundTravelMinutes = 45
            });

        var service = CreateService(db);
        var result = await service.UpdateTaskAsync(
            task.Id,
            _ => { },
            task.TechnicianId,
            Manager(),
            timezoneOffsetMinutes: 0,
            objectName: null,
            objectAddress: null,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskUpdateError.TravelInfeasible);
        result.Message.Should().Be("Nepakanka laiko kelionei nuo dabartinio laiko.");
    }

    [Fact]
    public async Task MoveTask_TodayBeforeEarliestSlot_ReturnsInvalidWindow()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        SeedTechnician(db, 1);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        SetVersion(db, task, 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var today = DateTime.Today;
        _time.GetUserLocalToday(0).Returns(today);
        _time.GetEarliestCreatableStartMinutes(today, 0, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes)
            .Returns(11 * 60 + 30);

        var service = CreateService(db);
        var result = await service.MoveTaskAsync(
            task.Id,
            targetTechnicianId: 1,
            targetDate: today,
            targetSlotStartMinutes: 11 * 60,
            expectedVersion: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskMoveError.InvalidWindow);
        result.Message.Should().Be("Negalima planuoti ankstesniame nei artimiausias galimas 30 min. laiko langas.");
    }

    [Fact]
    public async Task MoveTask_InboundTravelStartsInPast_ReturnsTravelInfeasible()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        SeedTechnician(db, 1);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        SetVersion(db, task, 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _time.GetUserLocalToday(0).Returns(DateTime.Today);
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(false);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = true,
                InboundTravelMinutes = 45
            });

        var service = CreateService(db);
        var result = await service.MoveTaskAsync(
            task.Id,
            targetTechnicianId: 1,
            targetDate: DateTime.Today,
            targetSlotStartMinutes: 12 * 60,
            expectedVersion: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskMoveError.TravelInfeasible);
        result.Message.Should().Be("Nepakanka laiko kelionei nuo dabartinio laiko.");
    }

    [Fact]
    public async Task UpdateTaskTime_TodayBeforeEarliestSlot_ReturnsInvalidWindow()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        SetVersion(db, task, 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var today = DateTime.Today;
        _time.GetUserLocalToday(0).Returns(today);
        _time.GetEarliestCreatableStartMinutes(today, 0, ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes)
            .Returns(11 * 60 + 30);

        var service = CreateService(db);
        var result = await service.UpdateTaskTimeAsync(
            taskId: task.Id,
            targetDate: today,
            targetSlotStartMinutes: 11 * 60,
            targetSlotEndMinutes: 12 * 60,
            expectedVersion: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskTimeUpdateError.InvalidWindow);
        result.Message.Should().Be("Negalima planuoti ankstesniame nei artimiausias galimas 30 min. laiko langas.");
    }

    [Fact]
    public async Task UpdateTaskTime_InboundTravelStartsInPast_ReturnsTravelInfeasible()
    {
        using var db = CreateContext();
        var plan = SeedPlan(db);
        var task = SeedTask(db, plan.Id, technicianId: 1);
        SetVersion(db, task, 1);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _time.GetUserLocalToday(0).Returns(DateTime.Today);
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(false);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = true,
                InboundTravelMinutes = 45
            });

        var service = CreateService(db);
        var result = await service.UpdateTaskTimeAsync(
            taskId: task.Id,
            targetDate: DateTime.Today,
            targetSlotStartMinutes: 12 * 60,
            targetSlotEndMinutes: 13 * 60,
            expectedVersion: 1,
            user: Manager(),
            timezoneOffsetMinutes: 0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskTimeUpdateError.TravelInfeasible);
        result.Message.Should().Be("Nepakanka laiko kelionei nuo dabartinio laiko.");
    }
    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, _logger);

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
        if (!db.Addresses.AnyAsync().GetAwaiter().GetResult())
        {
            db.Addresses.Add(new AddressEntity { Id = 1, City = "City", Street = "Main", FullAddress = "Main 1, City" });
        }
        db.Technicians.Add(new TechnicianEntity
        {
            Id = id,
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

    private static TaskEntity SeedTask(ScheduleDbContext db, long planId, long technicianId, int startOffsetHours = 0)
    {
        var start = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddHours(10 + startOffsetHours), DateTimeKind.Utc);
        if (!db.Addresses.AnyAsync().GetAwaiter().GetResult())
        {
            db.Addresses.Add(new AddressEntity { Id = 1, City = "City", Street = "Main", FullAddress = "Main 1, City" });
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

