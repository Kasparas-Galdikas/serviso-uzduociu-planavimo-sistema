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

public sealed class PlanServiceScheduleRepairTests
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

    public PlanServiceScheduleRepairTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _routeEvaluation.EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _time.GetUserLocalToday(Arg.Any<int>()).Returns(DateTime.Today);
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                return DateTime.SpecifyKind(date.AddMinutes(minutes), DateTimeKind.Utc);
            });
        _time.ConvertUtcToLocal(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(call => ((DateTime)call[0]).AddMinutes((int)call[1]));

        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(ScheduleRepairPlan.Fail("no repair"));
        _repairService.ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairApplyResult(true, null, Array.Empty<long>()));
        _travelResolver.GetTravelMinutesAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<(long From, long To), int>()));
    }

    [Fact]
    public async Task CreateTask_WhenTravelIsFeasible_DoesNotInvokeRepairService()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true });

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            9 * 60,
            10 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.DidNotReceive().BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>());
        await _repairService.DidNotReceive().ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenTravelIsInfeasibleAndRepairExists_AppliesRepairAndReturnsSuccess()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult { IsFeasible = false, Message = "travel fail" });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairPlan(
                true,
                null,
                ScheduleRepairKind.Shifted,
                Array.Empty<DayScheduleFitAdjustment>(),
                new ScheduleRepairPlacement(-1, 2, DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(10), DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(11), DateTimeKind.Utc))));
        _repairService.ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairApplyResult(true, null, Array.Empty<long>()));

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            9 * 60,
            10 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Message.Should().Contain("travel fail");
        await _repairService.Received(1).BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x => x.TechnicianId == 1 && x.ScenarioKind == ScheduleRepairScenarioKind.Move),
            Arg.Any<CancellationToken>());
        await _repairService.Received(1).ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenTaskStartsAtUnavailabilityEnd_AllowsCreation()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(14.5));
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true });

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            14 * 60 + 30,
            15 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        (result.Message ?? string.Empty).Should().NotContain("techniko neaktyvumas");
        await _repairService.DidNotReceive().BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenTaskOverlapsUnavailabilityAndRepairExists_CreatesAtBlockEnd()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(14.5));
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairPlan(
                true,
                null,
                ScheduleRepairKind.Shifted,
                Array.Empty<DayScheduleFitAdjustment>(),
                new ScheduleRepairPlacement(
                    -1,
                    2,
                    DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(14.5), DateTimeKind.Utc),
                    DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(15), DateTimeKind.Utc))));

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            14 * 60,
            14 * 60 + 30,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Task!.StartAt.Should().Be(DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(14.5), DateTimeKind.Utc));
        await _repairService.Received(1).BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x =>
                x.TechnicianId == 1 &&
                x.ScenarioKind == ScheduleRepairScenarioKind.Move &&
                x.AllowBlockedIntervalShift),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenTravelStartsAfterUnavailability_UsesBlockedBoundaryRepair()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(14.5));
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = false,
                InboundTravelMinutes = 82,
                Message = "travel fail"
            });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<ScheduleRepairRequest>(0);
                return new ScheduleRepairPlan(
                    true,
                    null,
                    ScheduleRepairKind.Shifted,
                    Array.Empty<DayScheduleFitAdjustment>(),
                    new ScheduleRepairPlacement(
                        -1,
                        request.ProposedPlacement!.ServiceAddressId,
                        request.ProposedPlacement.StartAt,
                        request.ProposedPlacement.EndAt));
            });

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            14 * 60 + 30,
            15 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            cancellationToken: CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.Received().BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x =>
                x.AllowBlockedIntervalShift &&
                x.SimulatedBlockedIntervalStart == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(11.5), DateTimeKind.Utc) &&
                x.SimulatedBlockedIntervalEnd == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(14.5), DateTimeKind.Utc) &&
                x.ProposedPlacement != null &&
                x.ProposedPlacement.StartAt == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(16), DateTimeKind.Utc) &&
                x.ProposedPlacement.EndAt == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(16.5), DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTask_WhenBlockedTravelRepairCannotFit_ReturnsTravelInfeasible()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(18.5));
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = false,
                InboundTravelMinutes = 82,
                Message = "travel fail"
            });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(ScheduleRepairPlan.Fail("no repair"));

        var sut = CreateService(db);

        var result = await sut.CreateTaskAsync(
            1,
            DateTime.Today.AddDays(1),
            18 * 60 + 30,
            19 * 60,
            "Task",
            TaskPriority.Medium,
            ScheduleTaskStatus.InProgress,
            "A",
            "Obj",
            "Main St 1, City",
            string.Empty,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            cancellationToken: CancellationToken.None);

        result.Error.Should().Be(TaskCreateError.TravelInfeasible);
    }

    [Fact]
    public async Task UpdateTask_WhenTravelStartsAfterUnavailability_UsesBlockedBoundaryRepair()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(14.5));
        SeedTask(db, 100, 1, 2, DateTime.Today.AddDays(1).AddHours(14.5), DateTime.Today.AddDays(1).AddHours(15));
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = false,
                InboundTravelMinutes = 82,
                Message = "travel fail"
            });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<ScheduleRepairRequest>(0);
                return new ScheduleRepairPlan(
                    true,
                    null,
                    ScheduleRepairKind.Shifted,
                    Array.Empty<DayScheduleFitAdjustment>(),
                    new ScheduleRepairPlacement(
                        -1,
                        request.ProposedPlacement!.ServiceAddressId,
                        request.ProposedPlacement.StartAt,
                        request.ProposedPlacement.EndAt));
            });

        var sut = CreateService(db);

        var result = await sut.UpdateTaskAsync(
            100,
            _ => { },
            1,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            null,
            null,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.Received().BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x =>
                x.AllowBlockedIntervalShift &&
                x.ProposedPlacement != null &&
                x.ProposedPlacement.StartAt == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(16), DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveTask_WhenTravelStartsAfterUnavailability_UsesBlockedBoundaryRepair()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(14.5));
        SeedTask(db, 100, 1, 2, DateTime.Today.AddDays(1).AddHours(9), DateTime.Today.AddDays(1).AddHours(9.5));
        SetVersion(db, 100, 1u);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = false,
                InboundTravelMinutes = 82,
                Message = "travel fail"
            });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<ScheduleRepairRequest>(0);
                return new ScheduleRepairPlan(
                    true,
                    null,
                    ScheduleRepairKind.Shifted,
                    Array.Empty<DayScheduleFitAdjustment>(),
                    new ScheduleRepairPlacement(
                        -1,
                        request.ProposedPlacement!.ServiceAddressId,
                        request.ProposedPlacement.StartAt,
                        request.ProposedPlacement.EndAt));
            });

        var sut = CreateService(db);

        var result = await sut.MoveTaskAsync(
            100,
            1,
            DateTime.Today.AddDays(1),
            14 * 60 + 30,
            1u,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.Received().BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x =>
                x.AllowBlockedIntervalShift &&
                x.ProposedPlacement != null &&
                x.ProposedPlacement.StartAt == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(16), DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTaskTime_WhenTravelStartsAfterUnavailability_UsesBlockedBoundaryRepair()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedUnavailability(db, 1, DateTime.Today.AddDays(1).AddHours(11.5), DateTime.Today.AddDays(1).AddHours(14.5));
        SeedTask(db, 100, 1, 2, DateTime.Today.AddDays(1).AddHours(9), DateTime.Today.AddDays(1).AddHours(9.5));
        SetVersion(db, 100, 1u);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Collections.Generic.IReadOnlyList<ServiceTaskPlanningSystem.Services.Travel.RouteChains.ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult
            {
                IsFeasible = false,
                InboundTravelMinutes = 82,
                Message = "travel fail"
            });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<ScheduleRepairRequest>(0);
                return new ScheduleRepairPlan(
                    true,
                    null,
                    ScheduleRepairKind.Shifted,
                    Array.Empty<DayScheduleFitAdjustment>(),
                    new ScheduleRepairPlacement(
                        -1,
                        request.ProposedPlacement!.ServiceAddressId,
                        request.ProposedPlacement.StartAt,
                        request.ProposedPlacement.EndAt));
            });

        var sut = CreateService(db);

        var result = await sut.UpdateTaskTimeAsync(
            100,
            DateTime.Today.AddDays(1),
            14 * 60 + 30,
            15 * 60,
            1u,
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        await _repairService.Received().BuildPlanAsync(
            Arg.Is<ScheduleRepairRequest>(x =>
                x.AllowBlockedIntervalShift &&
                x.ProposedPlacement != null &&
                x.ProposedPlacement.StartAt == DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(16), DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewDisplacement_WhenTaskStartsAtUnavailabilityEnd_DoesNotMarkTaskAffected()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedTask(db, 100, 1, 2, DateTime.Today.AddDays(1).AddHours(14.5), DateTime.Today.AddDays(1).AddHours(15));

        var sut = CreateService(db);

        var result = await sut.PreviewDisplacementAsync(
            1,
            DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(11.5), DateTimeKind.Utc),
            DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(14.5), DateTimeKind.Utc),
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            CancellationToken.None);

        result.AffectedTasks.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewDisplacement_WhenTaskOverlapsUnavailability_MarksTaskAffected()
    {
        using var db = CreateContext();
        SeedTechnicianAndPlan(db);
        SeedTask(db, 100, 1, 2, DateTime.Today.AddDays(1).AddHours(14), DateTime.Today.AddDays(1).AddHours(14.5));

        var sut = CreateService(db);

        var result = await sut.PreviewDisplacementAsync(
            1,
            DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(11.5), DateTimeKind.Utc),
            DateTime.SpecifyKind(DateTime.Today.AddDays(1).AddHours(14.5), DateTimeKind.Utc),
            new UserContext { Role = UserRole.Manager, UserId = 1 },
            0,
            CancellationToken.None);

        result.AffectedTasks.Should().ContainSingle(x => x.TaskId == 100);
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, _repairService, _travelResolver, Substitute.For<ITaskHistoryWriter>(), _logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static void SeedTechnicianAndPlan(ScheduleDbContext db)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = 1,
            City = "City",
            Street = "HQ",
            FullAddress = "HQ 1, City",
            CreatedAt = DateTime.UtcNow
        });
        db.Addresses.Add(new AddressEntity
        {
            Id = 2,
            City = "City",
            Street = "Main",
            FullAddress = "Main St 1, City",
            CreatedAt = DateTime.UtcNow
        });
        db.Technicians.Add(new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        });
        db.ServiceObjects.Add(new ServiceObjectEntity
        {
            Id = 2,
            Name = "Obj",
            AddressId = 2,
            CreatedAt = DateTime.UtcNow
        });
        db.Plans.Add(new PlanEntity
        {
            Id = 50,
            PeriodStart = DateTime.Today.AddDays(-1),
            PeriodEnd = DateTime.Today.AddDays(30),
            Weeks = 4,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SeedTask(ScheduleDbContext db, long taskId, long technicianId, long serviceObjectId, DateTime startAt, DateTime endAt)
    {
        db.Tasks.Add(new TaskEntity
        {
            Id = taskId,
            TechnicianId = technicianId,
            ServiceObjectId = serviceObjectId,
            Title = $"Task {taskId}",
            StartAt = DateTime.SpecifyKind(startAt, DateTimeKind.Utc),
            EndAt = DateTime.SpecifyKind(endAt, DateTimeKind.Utc),
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            PlanId = 50,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static void SetVersion(ScheduleDbContext db, long taskId, uint version)
    {
        var task = db.Tasks.Single(x => x.Id == taskId);
        db.Entry(task).Property<uint>("xmin").CurrentValue = version;
    }

    private static void SeedUnavailability(ScheduleDbContext db, long technicianId, DateTime startAt, DateTime endAt)
    {
        db.TechnicianUnavailabilities.Add(new TechnicianUnavailabilityEntity
        {
            TechnicianId = technicianId,
            PeriodStart = DateTime.SpecifyKind(startAt, DateTimeKind.Utc),
            PeriodEnd = DateTime.SpecifyKind(endAt, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}
