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
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using Xunit;

namespace Tests.Unit.PlanManagement;

public class PlanServiceTechnicianUpdateTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
    private readonly IScheduleRepairService _repairService = Substitute.For<IScheduleRepairService>();
    private readonly ITravelTimeResolver _travelResolver = Substitute.For<ITravelTimeResolver>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();

    private static readonly DateTime TaskStart = new(2026, 4, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TaskEnd   = new(2026, 4, 16, 10, 0, 0, DateTimeKind.Utc);

    public PlanServiceTechnicianUpdateTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
    }

    // ── 1. Blocked before task starts ────────────────────────────────────────

    [Fact]
    public async Task StatusChange_BeforeStartAt_ReturnsTooEarly()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);

        // now is 30 minutes before StartAt
        var now = TaskStart.AddMinutes(-30);
        _time.GetCurrentUtcTime().Returns(now);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Error.Should().Be(TechnicianUpdateError.TooEarly);

        // Ensure ActualEndAt was NOT written to DB
        var reloaded = await db.Tasks.FindAsync(task.Id);
        reloaded!.ActualEndAt.Should().BeNull();
    }

    // ── 2. Allowed exactly at StartAt (boundary) ─────────────────────────────

    [Fact]
    public async Task StatusChange_AtStartAt_Succeeds()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);
        SetVersion(db, task, 0);

        // now == StartAt exactly
        _time.GetCurrentUtcTime().Returns(TaskStart);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Ok.Should().BeTrue();
    }

    // ── 3. ActualEndAt is set during task window ──────────────────────────────

    [Fact]
    public async Task StatusChange_DuringTask_SetsActualEndAt()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);
        SetVersion(db, task, 0);

        var now = TaskStart.AddMinutes(20); // 20 min into a 60-min task
        _time.GetCurrentUtcTime().Returns(now);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        var reloaded = await db.Tasks.FindAsync(task.Id);
        reloaded!.ActualEndAt.Should().Be(now);
    }

    // ── 4. ActualEndAt equals server time, not something else ─────────────────

    [Fact]
    public async Task StatusChange_ActualEndAtSetToServerTime()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);
        SetVersion(db, task, 0);

        var serverNow = new DateTime(2026, 4, 16, 9, 37, 42, 123, DateTimeKind.Utc);
        _time.GetCurrentUtcTime().Returns(serverNow);
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        var reloaded = await db.Tasks.FindAsync(task.Id);
        reloaded!.ActualEndAt.Should().Be(serverNow);
        reloaded.ActualEndAt.Should().NotBe(task.EndAt);
    }

    // ── 5. Completed status is persisted ──────────────────────────────────────

    [Fact]
    public async Task StatusChange_Completed_SetsStatus()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);
        SetVersion(db, task, 0);

        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(10));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Ok.Should().BeTrue();
        var reloaded = await db.Tasks.FindAsync(task.Id);
        reloaded!.Status.Should().Be(ScheduleTaskStatus.Completed);
    }

    // ── 6. Conflict status is persisted ───────────────────────────────────────

    [Fact]
    public async Task StatusChange_Conflict_SetsStatus()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);
        SetVersion(db, task, 0);

        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(10));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Conflict,
            "problem", expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Ok.Should().BeTrue();
        var reloaded = await db.Tasks.FindAsync(task.Id);
        reloaded!.Status.Should().Be(ScheduleTaskStatus.Conflict);
    }

    // ── 7. Invalid status (InProgress / Canceled) → InvalidStatus ────────────

    [Theory]
    [InlineData(ScheduleTaskStatus.InProgress)]
    [InlineData(ScheduleTaskStatus.Canceled)]
    public async Task StatusChange_InvalidStatus_ReturnsInvalidStatus(ScheduleTaskStatus badStatus)
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);

        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(10));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, badStatus,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Error.Should().Be(TechnicianUpdateError.InvalidStatus);
    }

    // ── 8. Unknown task ID → TaskNotFound ─────────────────────────────────────

    [Fact]
    public async Task StatusChange_TaskNotFound_ReturnsTaskNotFound()
    {
        using var db = CreateContext();
        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(10));

        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(99999L, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Error.Should().Be(TechnicianUpdateError.TaskNotFound);
    }

    // ── 9. Technician updating another technician's task → NotOwner ───────────

    [Fact]
    public async Task StatusChange_NotOwner_ReturnsNotOwner()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5); // task belongs to tech 5

        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(10));
        // Permission service denies (because ownership mismatch, for example)
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));

        // User is a different technician (technicianId=7, not 5)
        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 7), 0, CancellationToken.None);

        result.Error.Should().Be(TechnicianUpdateError.NotOwner);
    }

    // ── 10. After EndAt → permission service returns false → NotEditable ───────

    [Fact]
    public async Task StatusChange_AfterTaskEnded_ReturnsNotEditable()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);

        _time.GetCurrentUtcTime().Returns(TaskEnd.AddHours(1)); // way past end
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(false));

        // Use a manager to avoid triggering NotOwner branch (only technician triggers it)
        var sut = CreateService(db);
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Manager(), 0, CancellationToken.None);

        result.Error.Should().Be(TechnicianUpdateError.NotEditable);
    }

    // ── 11. Version mismatch → Stale ──────────────────────────────────────────

    [Fact]
    public async Task StatusChange_StaleVersion_ReturnsStale()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);
        SetVersion(db, task, 42); // DB has version 42

        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(10));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        // Client thinks version is 99, but DB has 42 → stale
        var result = await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 99, Technician(technicianId: 5), 0, CancellationToken.None);

        result.Error.Should().Be(TechnicianUpdateError.Stale);
        result.CurrentVersion.Should().Be(42);
    }

    // ── 12. TooEarly: task is not persisted ───────────────────────────────────

    [Fact]
    public async Task StatusChange_TooEarly_DoesNotPersistChanges()
    {
        using var db = CreateContext();
        var (_, task) = Seed(db, technicianId: 5);

        _time.GetCurrentUtcTime().Returns(TaskStart.AddMinutes(-1)); // 1 min before start
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));

        var sut = CreateService(db);
        await sut.TechnicianUpdateTaskAsync(task.Id, ScheduleTaskStatus.Completed,
            null, expectedVersion: 0, Technician(technicianId: 5), 0, CancellationToken.None);

        // Re-load from DB — status must still be InProgress, ActualEndAt must be null
        var reloaded = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);
        reloaded.Status.Should().Be(ScheduleTaskStatus.InProgress);
        reloaded.ActualEndAt.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation,
            _travelFeasibility, _repairService, _travelResolver,
            Substitute.For<ITaskHistoryWriter>(), _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static (PlanEntity Plan, TaskEntity Task) Seed(ScheduleDbContext db, long technicianId)
    {
        var plan = new PlanEntity
        {
            PeriodStart = DateTime.Today,
            PeriodEnd = DateTime.Today.AddDays(6),
            Status = PlanStatus.Active,
            Weeks = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Plans.Add(plan);
        db.SaveChanges();

        if (!db.Addresses.Any())
            db.Addresses.Add(new AddressEntity { Id = 1, City = "City", Street = "Main", FullAddress = "Main 1, City" });
        if (!db.ServiceObjects.Any())
            db.ServiceObjects.Add(new ServiceObjectEntity { Id = 1, Name = "Obj", AddressId = 1 });

        var task = new TaskEntity
        {
            PlanId = plan.Id,
            TechnicianId = technicianId,
            ServiceObjectId = 1,
            StartAt = TaskStart,
            EndAt = TaskEnd,
            Title = "t",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(task);
        db.SaveChanges();

        return (plan, task);
    }

    private static void SetVersion(ScheduleDbContext db, TaskEntity task, uint version)
    {
        db.Entry(task).Property<uint>("xmin").CurrentValue = version;
    }

    private static UserContext Technician(long technicianId) => new()
    {
        Role = UserRole.Technician,
        UserId = technicianId + 100,
        TechnicianId = technicianId
    };

    private static UserContext Manager() => new()
    {
        Role = UserRole.Manager,
        UserId = 1
    };
}
