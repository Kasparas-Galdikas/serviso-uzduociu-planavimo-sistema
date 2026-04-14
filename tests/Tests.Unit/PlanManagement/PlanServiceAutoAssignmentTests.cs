using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class PlanServiceAutoAssignmentTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
    private readonly IScheduleRepairService _repairService = Substitute.For<IScheduleRepairService>();
    private readonly ITravelTimeResolver _travelResolver = Substitute.For<ITravelTimeResolver>();
    private readonly ILogger<PlanService> _logger = Substitute.For<ILogger<PlanService>>();
    private readonly IScheduleTimeService _timeService = new ScheduleTimeService();

    public PlanServiceAutoAssignmentTests()
    {
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
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = true,
                InboundTravelMinutes = 0,
                OutboundTravelMinutes = 0
            });
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_WithoutActiveOrDraftPlan_CanStillSuggest()
    {
        using var db = CreateContext();
        SeedTechnician(db, 1, 100, "Tech One");
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        await db.SaveChangesAsync();
        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger: logger);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            DateTime.UtcNow.Date.AddDays(1),
            60,
            serviceObject.Id,
            "Obj-1",
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().NotBeNull();
        suggestion!.TechnicianId.Should().Be(1);
        suggestion.TargetDate.Should().Be(DateTime.UtcNow.Date.AddDays(1));
        suggestion.StartMinutes.Should().BeGreaterThanOrEqualTo(8 * 60);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_WhenObjectMissing_ReturnsNull()
    {
        using var db = CreateContext();
        SeedPlan(db);
        SeedTechnician(db, 1, 100);
        await db.SaveChangesAsync();

        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger: logger);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            DateTime.UtcNow.Date.AddDays(1),
            60,
            null,
            "Missing",
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().BeNull();
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_PrefersLessLoadedTechnician_AndAppliesBatchPenalty()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        var plan = SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");
        SeedTechnician(db, 2, 101, "Tech Two");

        db.Tasks.Add(new TaskEntity
        {
            Id = 1000,
            PlanId = plan.Id,
            TechnicianId = 1,
            ServiceObjectId = serviceObject.Id,
            StartAt = day.AddHours(8),
            EndAt = day.AddHours(14),
            Title = "Loaded Day",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        db.Tasks.Add(new TaskEntity
        {
            Id = 1001,
            PlanId = plan.Id,
            TechnicianId = 1,
            ServiceObjectId = serviceObject.Id,
            StartAt = day.AddDays(-1).AddHours(8),
            EndAt = day.AddDays(-1).AddHours(17),
            Title = "Loaded Week A",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        db.Tasks.Add(new TaskEntity
        {
            Id = 1002,
            PlanId = plan.Id,
            TechnicianId = 1,
            ServiceObjectId = serviceObject.Id,
            StartAt = day.AddDays(2).AddHours(8),
            EndAt = day.AddDays(2).AddHours(17),
            Title = "Loaded Week B",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var service = CreateService(db);

        var firstSuggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        firstSuggestion.Should().NotBeNull();
        firstSuggestion!.TechnicianId.Should().Be(2);
        firstSuggestion.StartMinutes.Should().BeGreaterThanOrEqualTo(0);
        firstSuggestion.EndMinutes.Should().Be(firstSuggestion.StartMinutes + 60);

        var secondSuggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            new List<SimulatedTaskAssignmentDto>
            {
                new()
                {
                    TechnicianId = 2,
                    StartAt = day.AddHours(8).ToString("yyyy-MM-dd'T'HH:mm"),
                    EndAt = day.AddHours(9).ToString("yyyy-MM-dd'T'HH:mm"),
                    AddressId = serviceObject.AddressId,
                    ServiceObjectId = serviceObject.Id
                }
            },
            new Dictionary<long, int> { [2] = 2 },
            CancellationToken.None);

        secondSuggestion.Should().NotBeNull();
        secondSuggestion!.TechnicianId.Should().Be(1);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_ForFirstTask_AccountsForWorkplaceTravel()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>
            {
                [(100, 200)] = 120
            }));

        var service = CreateService(db);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().NotBeNull();
        suggestion!.StartMinutes.Should().BeGreaterThanOrEqualTo(600);
        suggestion.EndMinutes.Should().Be(suggestion.StartMinutes + 60);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_UsesSharedTravelValidation()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedPlan(db);
        SeedTechnician(db, 1, 100, "Tech One");
        SeedServiceObject(db, 10, "Obj-1", 200);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        await service.SuggestAutoAssignmentAsync(
            day,
            60,
            10,
            "Obj-1",
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        await _travelFeasibility.Received().EvaluatePlacementAsync(
            Arg.Any<long>(),
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            200,
            Arg.Any<int>(),
            Arg.Is<long?>(value => value == null),
            true,
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
            Arg.Is<long?>(value => value == 1));
    }

    [Fact]
    public async Task GetSuggestedNextSlotAsync_ForFirstTask_AccountsForWorkplaceTravel()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 100, "Tech One");
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>
            {
                [(100, 200)] = 120
            }));

        var service = CreateService(db);

        var suggestion = await service.GetSuggestedNextSlotAsync(
            1,
            day,
            0,
            30,
            8 * 60,
            null,
            null,
            serviceObject.Id,
            false,
            null,
            CancellationToken.None);

        suggestion.StartMinutes.Should().BeGreaterThanOrEqualTo(600);
        suggestion.EndMinutes.Should().Be(suggestion.StartMinutes + 30);
    }

    [Fact]
    public async Task GetSuggestedNextSlotAsync_UsesSharedTravelValidation()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedTechnician(db, 1, 100, "Tech One");
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        await service.GetSuggestedNextSlotAsync(
            1,
            day,
            0,
            30,
            8 * 60,
            null,
            null,
            serviceObject.Id,
            false,
            null,
            CancellationToken.None);

        await _travelFeasibility.Received().EvaluatePlacementAsync(
            1,
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            200,
            Arg.Any<int>(),
            Arg.Is<long?>(value => value == null),
            true,
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
            Arg.Is<long?>(value => value == null));
    }

    [Fact]
    public async Task GetSuggestedNextSlotAsync_AfterPlanningWindow_RollsToTomorrow()
    {
        using var db = CreateContext();
        var today = new DateTime(2026, 3, 10);
        SeedTechnician(db, 1, 100, "Tech One");
        await db.SaveChangesAsync();

        var service = CreateService(db, new FixedTimeService(new DateTime(2026, 3, 10, 20, 1, 0, DateTimeKind.Utc)));

        var suggestion = await service.GetSuggestedNextSlotAsync(
            1,
            today,
            0,
            30,
            8 * 60,
            null,
            null,
            null,
            false,
            null,
            CancellationToken.None);

        suggestion.TargetDate.Should().Be(today.AddDays(1));
        suggestion.StartMinutes.Should().Be(8 * 60);
        suggestion.EndMinutes.Should().Be(8 * 60 + 30);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_AfterPlanningWindow_RollsToTomorrow()
    {
        using var db = CreateContext();
        var today = new DateTime(2026, 3, 10);
        SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");
        await db.SaveChangesAsync();

        var service = CreateService(db, new FixedTimeService(new DateTime(2026, 3, 10, 20, 1, 0, DateTimeKind.Utc)));

        var suggestion = await service.SuggestAutoAssignmentAsync(
            today,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().NotBeNull();
        suggestion!.TargetDate.Should().Be(today.AddDays(1));
        suggestion.StartMinutes.Should().BeGreaterThanOrEqualTo(8 * 60);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_WhenRequestedDayIsFull_ChoosesFutureDate()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        var plan = SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");

        db.Tasks.Add(new TaskEntity
        {
            Id = 2000,
            PlanId = plan.Id,
            TechnicianId = 1,
            ServiceObjectId = serviceObject.Id,
            StartAt = day.AddHours(8),
            EndAt = day.AddHours(20),
            Title = "Full Day",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var service = CreateService(db);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().NotBeNull();
        suggestion!.TargetDate.Should().Be(day.AddDays(1));
        suggestion.StartMinutes.Should().Be(8 * 60);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_PrefersSameDayWhenFeasibleDespiteFutureAvailability()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        var plan = SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");

        db.Tasks.Add(new TaskEntity
        {
            Id = 2100,
            PlanId = plan.Id,
            TechnicianId = 1,
            ServiceObjectId = serviceObject.Id,
            StartAt = day.AddDays(1).AddHours(8),
            EndAt = day.AddDays(1).AddHours(17),
            Title = "Heavier Tomorrow",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var service = CreateService(db);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().NotBeNull();
        suggestion!.TargetDate.Should().Be(day);
    }

    [Fact]
    public async Task BulkValidateTasksAsync_AutomatinisMode_KeepsClientAssignmentsForValidation()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedPlan(db);
        SeedTechnician(db, 1, 100, "Tech One");
        SeedServiceObject(db, 10, "Obj-1", 200);
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var service = CreateService(db);
        var items = new List<CreateTaskRequestDto>
        {
            new()
            {
                TechnicianId = 999,
                TargetDate = day.ToString("yyyy-MM-dd"),
                StartMinutes = 8 * 60,
                EndMinutes = 9 * 60,
                Title = "Task",
                Priority = TaskPriority.Medium,
                Status = ScheduleTaskStatus.InProgress,
                SkillLevel = "A",
                ObjectName = "Obj-1",
                ObjectAddress = "Object 10, City",
                TimezoneOffsetMinutes = 0
            }
        };

        var result = await service.BulkValidateTasksAsync(
            items,
            "automatinis",
            new UserContext { UserId = 1, Role = UserRole.Manager },
            null,
            CancellationToken.None);

        result.AllSucceeded.Should().BeFalse();
        items[0].TechnicianId.Should().Be(999);
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_RepairsInvalidAnchorAndScoresRepairedCandidate()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(call =>
            {
                var startAt = (DateTime)call[1];
                var startMinutes = (int)call[4];
                return Task.FromResult(startMinutes == 8 * 60
                    ? new TravelFeasibilityResult
                    {
                        IsFeasible = false,
                        InboundTravelMinutes = 30,
                        Message = "Nepakanka laiko kelionei."
                    }
                    : new TravelFeasibilityResult
                    {
                        IsFeasible = true,
                        InboundTravelMinutes = 0,
                        OutboundTravelMinutes = 0
                    });
            });

        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ScheduleRepairRequest>();
                var repairedStart = request.ProposedPlacement!.StartAt.AddMinutes(30);
                return Task.FromResult(new ScheduleRepairPlan(
                    true,
                    null,
                    ScheduleRepairKind.Shifted,
                    Array.Empty<DayScheduleFitAdjustment>(),
                    new ScheduleRepairPlacement(0, request.ProposedPlacement.ServiceAddressId, repairedStart, repairedStart.AddMinutes(60))));
            });

        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger: logger);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().NotBeNull();
        suggestion!.StartMinutes.Should().Be(8 * 60 + 30);
        logger.Entries.Should().Contain(entry => entry.Message.Contains("svc.plan.auto-assign.candidate-repaired", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry => entry.Message.Contains("svc.plan.auto-assign.decision", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_ExcludesSupersededTasksFromDecisionScope()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        var activePlan = SeedPlan(db);
        var supersededPlan = new PlanEntity
        {
            Id = 2,
            PeriodStart = activePlan.PeriodStart,
            PeriodEnd = activePlan.PeriodEnd,
            Status = PlanStatus.Superseded,
            Weeks = 1,
            CreatedAt = DateTime.UtcNow,
            SupersededByPlanId = activePlan.Id
        };
        db.Plans.Add(supersededPlan);

        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");

        db.Tasks.Add(new TaskEntity
        {
            Id = 3000,
            PlanId = supersededPlan.Id,
            TechnicianId = 1,
            ServiceObjectId = serviceObject.Id,
            StartAt = day.AddHours(8),
            EndAt = day.AddHours(20),
            Title = "Old superseded day",
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger: logger);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            day,
            60,
            serviceObject.Id,
            serviceObject.Name,
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        var diagnosticLog = string.Join(" | ", logger.Entries.Select(entry => entry.Message));
        suggestion.Should().NotBeNull(diagnosticLog);
        suggestion!.TargetDate.Should().Be(day, diagnosticLog);
        suggestion.StartMinutes.Should().Be(8 * 60);
        logger.Entries.Should().Contain(entry =>
            entry.Message.Contains("svc.plan.auto-assign.scope", StringComparison.Ordinal) &&
            entry.Message.Contains("assignment-active+simulated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuggestBulkAutoAssignmentsAsync_MergesClientAndServerSimulatedAssignments()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        var plan = SeedPlan(db);
        plan.PeriodStart = day.AddDays(-1);
        plan.PeriodEnd = day.AddDays(6);

        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger: logger);
        var suggestions = await service.SuggestBulkAutoAssignmentsAsync(
            new List<CreateTaskRequestDto>
            {
                new()
                {
                    TechnicianId = 0,
                    TargetDate = day.ToString("yyyy-MM-dd"),
                    StartMinutes = 8 * 60,
                    EndMinutes = 8 * 60 + 30,
                    Title = "Task 1",
                    Priority = TaskPriority.Medium,
                    Status = ScheduleTaskStatus.InProgress,
                    SkillLevel = "A",
                    ObjectId = serviceObject.Id,
                    ObjectName = serviceObject.Name,
                    ObjectAddress = "Object 200, City",
                    TimezoneOffsetMinutes = 0
                },
                new()
                {
                    TechnicianId = 0,
                    TargetDate = day.ToString("yyyy-MM-dd"),
                    StartMinutes = 8 * 60,
                    EndMinutes = 8 * 60 + 30,
                    Title = "Task 2",
                    Priority = TaskPriority.Medium,
                    Status = ScheduleTaskStatus.InProgress,
                    SkillLevel = "A",
                    ObjectId = serviceObject.Id,
                    ObjectName = serviceObject.Name,
                    ObjectAddress = "Object 200, City",
                    TimezoneOffsetMinutes = 0,
                    SimulatedAssignments = new List<SimulatedTaskAssignmentDto>
                    {
                        new()
                        {
                            TechnicianId = 1,
                            StartAt = $"{day:yyyy-MM-dd}T12:00",
                            EndAt = $"{day:yyyy-MM-dd}T12:30",
                            AddressId = serviceObject.AddressId,
                            ServiceObjectId = serviceObject.Id
                        }
                    }
                }
            },
            CancellationToken.None);

        var diagnosticLog = string.Join(" | ", logger.Entries.Select(entry => entry.Message));
        suggestions[0].Should().NotBeNull(diagnosticLog);
        suggestions[1].Should().NotBeNull(diagnosticLog);
        suggestions[0]!.TechnicianId.Should().Be(1);
        suggestions[0]!.StartMinutes.Should().Be(8 * 60);
        suggestions[1]!.TechnicianId.Should().Be(1);
        suggestions[1]!.StartMinutes.Should().Be(8 * 60 + 30);
    }

    [Fact]
    public async Task BulkValidateTasksAsync_AutomatinisMode_LogsReservedOverlapDiagnosis()
    {
        using var db = CreateContext();
        var day = DateTime.UtcNow.Date.AddDays(1);
        SeedPlan(db);
        var serviceObject = SeedServiceObject(db, 10, "Obj-1", 200);
        SeedTechnician(db, 1, 100, "Tech One");
        await db.SaveChangesAsync();

        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));

        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger: logger);
        var items = new List<CreateTaskRequestDto>
        {
            new()
            {
                TechnicianId = 1,
                TargetDate = day.ToString("yyyy-MM-dd"),
                StartMinutes = 8 * 60,
                EndMinutes = 8 * 60 + 30,
                Title = "Task 1",
                Priority = TaskPriority.Medium,
                Status = ScheduleTaskStatus.InProgress,
                SkillLevel = "A",
                ObjectId = serviceObject.Id,
                ObjectName = serviceObject.Name,
                ObjectAddress = "Object 200, City",
                TimezoneOffsetMinutes = 0
            },
            new()
            {
                TechnicianId = 1,
                TargetDate = day.ToString("yyyy-MM-dd"),
                StartMinutes = 8 * 60,
                EndMinutes = 8 * 60 + 30,
                Title = "Task 2",
                Priority = TaskPriority.Medium,
                Status = ScheduleTaskStatus.InProgress,
                SkillLevel = "A",
                ObjectId = serviceObject.Id,
                ObjectName = serviceObject.Name,
                ObjectAddress = "Object 200, City",
                TimezoneOffsetMinutes = 0
            }
        };

        var result = await service.BulkValidateTasksAsync(
            items,
            "automatinis",
            new UserContext { UserId = 1, Role = UserRole.Manager },
            null,
            CancellationToken.None);

        result.AllSucceeded.Should().BeFalse();
        logger.Entries.Should().Contain(entry =>
            entry.Message.Contains("svc.plan.bulk.validate.diagnostic", StringComparison.Ordinal) &&
            entry.Message.Contains("server-batch-conflict-missing-from-client-snapshot", StringComparison.Ordinal));
    }

    private PlanService CreateService(ScheduleDbContext db, IScheduleTimeService? timeService = null, ILogger<PlanService>? logger = null) =>
        new(
            db,
            _evaluation,
            _planningCalendar,
            _permission,
            timeService ?? _timeService,
            _routeEvaluation,
            _travelFeasibility,
            _repairService,
            _travelResolver,
            Substitute.For<ITaskHistoryWriter>(),
            logger ?? _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static PlanEntity SeedPlan(ScheduleDbContext db)
    {
        var plan = new PlanEntity
        {
            Id = 1,
            PeriodStart = DateTime.UtcNow.Date,
            PeriodEnd = DateTime.UtcNow.Date.AddDays(7),
            Status = PlanStatus.Active,
            Weeks = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Plans.Add(plan);
        return plan;
    }

    private static void SeedTechnician(ScheduleDbContext db, long technicianId, long workplaceAddressId, string userName = "")
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = workplaceAddressId,
            City = "City",
            Street = $"Street {workplaceAddressId}",
            FullAddress = $"Street {workplaceAddressId}, City",
            CreatedAt = DateTime.UtcNow
        });

        UserEntity? user = null;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            user = new UserEntity
            {
                Id = technicianId + 1000,
                Name = userName,
                Email = $"tech{technicianId}@example.com",
                Role = UserRole.Technician,
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
        }

        db.Technicians.Add(new TechnicianEntity
        {
            Id = technicianId,
            UserId = user?.Id,
            User = user,
            WorkplaceAddressId = workplaceAddressId,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        });
    }

    private static ServiceObjectEntity SeedServiceObject(ScheduleDbContext db, long id, string name, long addressId)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = addressId,
            City = "City",
            Street = $"Object {addressId}",
            FullAddress = $"Object {addressId}, City",
            CreatedAt = DateTime.UtcNow
        });

        var serviceObject = new ServiceObjectEntity
        {
            Id = id,
            Name = name,
            AddressId = addressId,
            CreatedAt = DateTime.UtcNow
        };
        db.ServiceObjects.Add(serviceObject);
        return serviceObject;
    }

    private sealed class FixedTimeService : ScheduleTimeService
    {
        private readonly DateTime _nowUtc;

        public FixedTimeService(DateTime nowUtc)
        {
            _nowUtc = nowUtc;
        }

        public override DateTime GetCurrentUtcTime() => _nowUtc;
    }

    private sealed record CapturedLogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class TestLogger<T> : ILogger<T>, ILogger
    {
        private readonly object _lock = new();
        private readonly List<CapturedLogEntry> _entries = new();
        private readonly AsyncLocal<Stack<Dictionary<string, object?>>> _scopes = new();

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var stack = _scopes.Value ??= new Stack<Dictionary<string, object?>>();
            stack.Push(ToDictionary(state));
            return new Scope(stack);
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = ToDictionary(state);
            properties["EventId"] = eventId.Id;
            if (_scopes.Value is { Count: > 0 })
            {
                foreach (var scope in _scopes.Value.Reverse())
                {
                    foreach (var pair in scope)
                    {
                        properties.TryAdd(pair.Key, pair.Value);
                    }
                }
            }

            var message = formatter(state, exception);
            if (exception != null)
            {
                message = $"{message} :: {exception}";
            }
            lock (_lock)
            {
                _entries.Add(new CapturedLogEntry(logLevel, message, properties));
            }
        }

        private static Dictionary<string, object?> ToDictionary<TState>(TState state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                return kvps.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            return new Dictionary<string, object?>();
        }

        private sealed class Scope : IDisposable
        {
            private readonly Stack<Dictionary<string, object?>> _stack;

            public Scope(Stack<Dictionary<string, object?>> stack)
            {
                _stack = stack;
            }

            public void Dispose()
            {
                if (_stack.Count > 0)
                {
                    _stack.Pop();
                }
            }
        }
    }
}
