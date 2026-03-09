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
using ServiceTaskPlanningSystem.Services.PlanManagement.SmartSwap;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using Xunit;

namespace Tests.Unit.PlanManagement;

/// <summary>
/// Unit tests for SmartSwapPlanner.BuildPlanAsync covering the backward-pull
/// repair path (longer task swapped into a slot near end-of-day) and general
/// feasibility / infeasibility scenarios.
/// </summary>
public class SmartSwapPlannerTests
{
    // Fixed UTC day used by all tests – avoids day-boundary surprises.
    private static readonly DateTime Day = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

    // Address IDs
    private const long WorkplaceAddressId = 1; // Kaunas – technician base
    private const long KaunasAddressId = 2;    // close to workplace
    private const long SiauliaiAddressId = 3;  // ~115 min from Kaunas

    // Service object IDs
    private const long KaunasObjectId = 10;
    private const long SiauliaiObjectId = 11;

    private const int TravelKaunasToSiauliai = 115;
    private const int TravelSiauliaiToKaunas = 115;
    private const int TravelWithinKaunas = 0;

    // -------------------------------------------------------------------------
    // Test 1 – backward-pull: longer task (3 h) at 13:00 <-> shorter task
    // (1.5 h) at 18:00.  After swap the 3 h task overflows the 20:00 window if
    // placed naively at 18:00.  The planner must pull it back to 16:30.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_LongerTaskSwappedNearEndOfDay_PullsBackToFitWindow()
    {
        using var db = CreateContext();
        SeedBase(db);

        var source = SeedTask(db, SiauliaiObjectId, Day.AddHours(13), Day.AddHours(16));    // 3 h, Šiauliai
        var target = SeedTask(db, KaunasObjectId,   Day.AddHours(18), Day.AddHours(19.5));  // 1.5 h, Kaunas

        var planner = CreatePlanner(db, ChainOf(source, target));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeTrue("smart planner must find a valid schedule");
        plan.Adjustments.Should().HaveCount(2, "both tasks must be repositioned");

        var srcAdj = plan.Adjustments.Single(a => a.TaskId == source.Id);
        var tgtAdj = plan.Adjustments.Single(a => a.TaskId == target.Id);

        // Target (1.5 h, Kaunas) takes the 13:00 slot – no travel from workplace.
        tgtAdj.StartAt.Should().Be(Day.AddHours(13), "short Kaunas task stays at 13:00");
        tgtAdj.EndAt.Should().Be(Day.AddHours(14.5));

        // Source (3 h, Šiauliai) must be pulled back to its earliest feasible slot.
        // prev ends 14:30 + 115 min travel = 16:25 → AlignToSlot(30) rounds up to 16:30.
        srcAdj.StartAt.Should().Be(Day.AddHours(16.5),
            "source is placed at slot-aligned minStart (16:25 rounds up to 16:30)");
        srcAdj.EndAt.Should().BeBefore(Day.AddHours(20), "must finish within work window");
    }

    // -------------------------------------------------------------------------
    // Test 2 – same-duration swap: both tasks have the same length so strict
    // swap always fits.  The planner should return success with both adjusted.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_SameDurationTasks_SwapsSuccessfully()
    {
        using var db = CreateContext();
        SeedBase(db);

        var source = SeedTask(db, SiauliaiObjectId, Day.AddHours(13), Day.AddHours(16));  // 3 h
        var target = SeedTask(db, KaunasObjectId,   Day.AddHours(16).AddMinutes(30), Day.AddHours(19).AddMinutes(30)); // 3 h

        var planner = CreatePlanner(db, ChainOf(source, target));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeTrue();
        // Chain pull-back may land the source back at its original time if that
        // happens to be the optimal slot; at least one task must be repositioned.
        plan.Adjustments.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // -------------------------------------------------------------------------
    // Test 3 – truly infeasible: source task is so long (5 h) that even at its
    // earliest feasible slot after travel it overflows the 20:00 boundary.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_TaskTooLongEvenAtEarliestSlot_ReturnsFail()
    {
        using var db = CreateContext();
        SeedBase(db);

        // 11 h task from Šiauliai.  Even at the absolute earliest slot
        // (08:00 + 120 min travel aligned = 10:00) it ends at 21:00 > 20:00.
        // Neither the per-task nor the chain pull-back can recover this.
        var source = SeedTask(db, SiauliaiObjectId, Day.AddHours(9), Day.AddHours(20));   // 11 h
        var target = SeedTask(db, KaunasObjectId,   Day.AddHours(18), Day.AddHours(19.5)); // 1.5 h

        var planner = CreatePlanner(db, ChainOf(source, target));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeFalse("11 h task cannot fit before 20:00 even at the earliest feasible slot");
        plan.Message.Should().NotBeNullOrEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 4 – three-task day: only source and target swap; the third task that
    // sits between them in time must be shifted forward to avoid overlap.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_ThreeTaskDay_IntermediateTaskShiftedForward()
    {
        using var db = CreateContext();
        SeedBase(db);

        // Source: Kaunas 08:30-10:30 (2 h)
        // Middle: Kaunas 11:00-12:00 (1 h) – sits between source and target
        // Target: Kaunas 14:00-16:00 (2 h)
        // Swap source <-> target → both 2 h, so strict swap would work but
        // middle task ends up between them and may need forward push.
        var source = SeedTask(db, KaunasObjectId, Day.AddHours(8.5), Day.AddHours(10.5));
        var middle = SeedTask(db, KaunasObjectId, Day.AddHours(11),  Day.AddHours(12));
        var target = SeedTask(db, KaunasObjectId, Day.AddHours(14),  Day.AddHours(16));

        var planner = CreatePlanner(db, ChainOf(source, middle, target));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeTrue();
        // All tasks must finish within the work window.
        foreach (var adj in plan.Adjustments)
            adj.EndAt.Should().BeBefore(Day.AddHours(20).AddMinutes(1));
    }

    // -------------------------------------------------------------------------
    // Test 5 – parity: preview (PreviewSwapSmartAsync) returns feasible=true
    // for the same 3 h <-> 1.5 h scenario that execute path should also accept.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task PreviewSwapSmartAsync_SameScenarioAsBackwardPull_ReturnsFeasible()
    {
        using var db = CreateContext();
        SeedBase(db);

        var source = SeedTask(db, SiauliaiObjectId, Day.AddHours(13), Day.AddHours(16));
        var target = SeedTask(db, KaunasObjectId,   Day.AddHours(18), Day.AddHours(19.5));

        // Build a PlanService just to test the preview path.
        var planService = CreatePlanService(db, ChainOf(source, target));

        var preview = await planService.PreviewSwapSmartAsync(
            source.Id, target.Id, timezoneOffsetMinutes: 0, CancellationToken.None);

        preview.Feasible.Should().BeTrue("the backward-pull planner must find a valid plan");
        preview.RequiresSmartAdjustment.Should().BeTrue("strict slot-swap overflows the window");
    }

    // -------------------------------------------------------------------------
    // Test 6 – parity: preview returns feasible=false for a genuinely
    // infeasible swap (5 h task near end of day).
    // -------------------------------------------------------------------------
    [Fact]
    public async Task PreviewSwapSmartAsync_InfeasibleScenario_ReturnsFalse()
    {
        using var db = CreateContext();
        SeedBase(db);

        var source = SeedTask(db, SiauliaiObjectId, Day.AddHours(9), Day.AddHours(20));   // 11 h – cannot fit even at earliest
        var target = SeedTask(db, KaunasObjectId,   Day.AddHours(18), Day.AddHours(19.5)); // 1.5 h

        var planService = CreatePlanService(db, ChainOf(source, target));

        var preview = await planService.PreviewSwapSmartAsync(
            source.Id, target.Id, timezoneOffsetMinutes: 0, CancellationToken.None);

        preview.Feasible.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 7 – chain pull-back: swapping the later task (source, Kaunas 3 h
    // @14:00) with the earlier task (target, Kaunas 3 h @09:30) anchors the
    // chain at 09:30.  A third task (Kaunas 3.5 h @17:00) then ends at 20:30,
    // overflowing by 30 min.  The per-task pull-back cannot help because the
    // third task is already at its minStart.  The chain pull-back shifts
    // everything 90 min earlier (to dayStart 08:00), and the third task ends at
    // 19:00 which is within the work window.
    //
    // This is the exact real-world scenario that caused the failure:
    //   j (near city, @14:00) ↔ d (near city, @09:30)
    //   third task "test" (near city, @17:00, 3.5 h) overflows after swap.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_ChainAnchoredLate_PullsBackEntireChainToFit()
    {
        using var db = CreateContext();
        SeedBase(db);

        // All in Kaunas – zero travel between tasks, so the only slack is at the
        // start of the day (source is anchored 90 min after dayStart).
        var source = SeedTask(db, KaunasObjectId, Day.AddHours(14),   Day.AddHours(17));    // 3 h  @14:00 (j)
        var target = SeedTask(db, KaunasObjectId, Day.AddHours(9.5),  Day.AddHours(12.5));  // 3 h  @09:30 (d)
        var third  = SeedTask(db, KaunasObjectId, Day.AddHours(17),   Day.AddHours(20.5));  // 3.5 h @17:00 (test)

        var planner = CreatePlanner(db, ChainOf(source, target, third));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeTrue("chain pull-back must recover the 30-min overflow");

        // After pull-back the whole chain starts 90 min earlier (08:00 instead of 09:30).
        // No task should start before the work-day boundary.
        foreach (var adj in plan.Adjustments)
        {
            adj.StartAt.Should().BeOnOrAfter(Day.AddHours(8), "no task may start before 08:00");
            adj.EndAt.Should().BeOnOrBefore(Day.AddHours(20).AddMinutes(1), "all tasks must finish within the work window");
        }

        // Source (the swapped task) must have been pulled to its earliest possible
        // slot – dayStart, since workplace travel to KaunasAddressId is 0.
        var srcAdj = plan.Adjustments.Single(a => a.TaskId == source.Id);
        srcAdj.StartAt.Should().Be(Day.AddHours(8), "source must start at dayStart after chain pull-back");
    }

    // -------------------------------------------------------------------------
    // Test 8 – chain pull-back has nothing to pull: the first task after the
    // swap is already anchored at the earliest feasible start (08:00).  The
    // third task still overflows, but there is no slack at the beginning of the
    // day so the planner must correctly declare the swap infeasible.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_ChainAlreadyAtEarliestStart_CannotPullBack_ReturnsFail()
    {
        using var db = CreateContext();
        SeedBase(db);

        // Target starts exactly at dayStart; after swap source is anchored at 08:00.
        var source = SeedTask(db, KaunasObjectId, Day.AddHours(14),  Day.AddHours(17));   // 3 h @14:00
        var target = SeedTask(db, KaunasObjectId, Day.AddHours(8),   Day.AddHours(11));   // 3 h @08:00 ← at dayStart
        var third  = SeedTask(db, KaunasObjectId, Day.AddHours(17),  Day.AddHours(20.5)); // 3.5 h – will overflow

        var planner = CreatePlanner(db, ChainOf(source, target, third));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeFalse("no slack at start of day means the chain cannot be compacted further");
        plan.Message.Should().NotBeNullOrEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 9 – chain pull-back runs but the total task+travel time still exceeds
    // the work window even after compaction.  Planner must return fail and must
    // NOT loop forever or modify tasks incorrectly.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task BuildPlanAsync_ChainPullBackInsufficientForTotalDuration_ReturnsFail()
    {
        using var db = CreateContext();
        SeedBase(db);

        // 3 h source + 3 h target + 7 h third = 13 h total, day window = 12 h → infeasible.
        var source = SeedTask(db, KaunasObjectId, Day.AddHours(14),  Day.AddHours(17));    // 3 h
        var target = SeedTask(db, KaunasObjectId, Day.AddHours(9.5), Day.AddHours(12.5)); // 3 h
        var third  = SeedTask(db, KaunasObjectId, Day.AddHours(17),  Day.AddHours(24));   // 7 h – guaranteed overflow

        var planner = CreatePlanner(db, ChainOf(source, target, third));

        var plan = await planner.BuildPlanAsync(source.Id, target.Id, 0, CancellationToken.None);

        plan.IsSuccess.Should().BeFalse("13 h of tasks cannot fit in a 12 h work window");
        plan.Message.Should().NotBeNullOrEmpty();
    }

    // =========================================================================
    // Infrastructure
    // =========================================================================

    private static ScheduleDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(opts);
    }

    private static void SeedBase(ScheduleDbContext db)
    {
        db.Addresses.AddRange(
            new AddressEntity { Id = WorkplaceAddressId, City = "Kaunas", Street = "HQ", FullAddress = "HQ, Kaunas", CreatedAt = DateTime.UtcNow },
            new AddressEntity { Id = KaunasAddressId,    City = "Kaunas", Street = "K. Donelaicio g. 73", FullAddress = "K. Donelaicio g. 73, Kaunas", CreatedAt = DateTime.UtcNow },
            new AddressEntity { Id = SiauliaiAddressId,  City = "Siauliai", Street = "Vytauto g. 23", FullAddress = "Vytauto g. 23, Siauliai", CreatedAt = DateTime.UtcNow }
        );

        db.Technicians.Add(new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = WorkplaceAddressId,
            DailyLimit = 8,
            WeeklyLimit = 40,
            CreatedAt = DateTime.UtcNow
        });

        db.ServiceObjects.AddRange(
            new ServiceObjectEntity { Id = KaunasObjectId,   Name = "Kaunas obj",   AddressId = KaunasAddressId,   CreatedAt = DateTime.UtcNow },
            new ServiceObjectEntity { Id = SiauliaiObjectId, Name = "Siauliai obj", AddressId = SiauliaiAddressId, CreatedAt = DateTime.UtcNow }
        );

        // Travel routes
        db.TravelRoutes.AddRange(
            new TravelRouteEntity { FromAddressId = WorkplaceAddressId, ToAddressId = KaunasAddressId,    DurationMinutes = TravelWithinKaunas,        DistanceKm = 1,   CalculatedAt = DateTime.UtcNow },
            new TravelRouteEntity { FromAddressId = KaunasAddressId,    ToAddressId = WorkplaceAddressId, DurationMinutes = TravelWithinKaunas,        DistanceKm = 1,   CalculatedAt = DateTime.UtcNow },
            new TravelRouteEntity { FromAddressId = WorkplaceAddressId, ToAddressId = SiauliaiAddressId,  DurationMinutes = TravelKaunasToSiauliai,    DistanceKm = 130, CalculatedAt = DateTime.UtcNow },
            new TravelRouteEntity { FromAddressId = SiauliaiAddressId,  ToAddressId = WorkplaceAddressId, DurationMinutes = TravelSiauliaiToKaunas,    DistanceKm = 130, CalculatedAt = DateTime.UtcNow },
            new TravelRouteEntity { FromAddressId = KaunasAddressId,    ToAddressId = SiauliaiAddressId,  DurationMinutes = TravelKaunasToSiauliai,    DistanceKm = 130, CalculatedAt = DateTime.UtcNow },
            new TravelRouteEntity { FromAddressId = SiauliaiAddressId,  ToAddressId = KaunasAddressId,    DurationMinutes = TravelSiauliaiToKaunas,    DistanceKm = 130, CalculatedAt = DateTime.UtcNow },
            new TravelRouteEntity { FromAddressId = KaunasAddressId,    ToAddressId = KaunasAddressId,    DurationMinutes = TravelWithinKaunas,        DistanceKm = 1,   CalculatedAt = DateTime.UtcNow }
        );

        db.SaveChanges();
    }

    private static TaskEntity SeedTask(
        ScheduleDbContext db,
        long serviceObjectId,
        DateTime startAt,
        DateTime endAt)
    {
        var task = new TaskEntity
        {
            TechnicianId = 1,
            ServiceObjectId = serviceObjectId,
            Title = "Task",
            StartAt = startAt,
            EndAt = endAt,
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            CreatedAt = DateTime.UtcNow
        };
        db.Tasks.Add(task);
        db.SaveChanges();
        return task;
    }

    private static RouteChain ChainOf(params TaskEntity[] tasks)
    {
        var chain = new RouteChain
        {
            TechnicianId = 1,
            WorkplaceAddressId = WorkplaceAddressId,
        };
        foreach (var t in tasks.OrderBy(x => x.StartAt))
        {
            chain.Stops.Add(new RouteStop
            {
                TaskId = t.Id,
                StartAt = t.StartAt,
                EndAt = t.EndAt,
                AddressId = t.ServiceObject?.AddressId ?? KaunasAddressId
            });
        }
        return chain;
    }

    private static SmartSwapPlanner CreatePlanner(ScheduleDbContext db, RouteChain chain)
    {
        var timeService = Substitute.For<IScheduleTimeService>();
        timeService
            .CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date   = ((DateTime)call[0]).Date;
                var mins   = (int)call[1];
                var offset = (int)call[2];
                return DateTime.SpecifyKind(date.AddMinutes(mins - offset), DateTimeKind.Utc);
            });

        var routeChainBuilder = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder
            .BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(chain);

        return new SmartSwapPlanner(
            db,
            timeService,
            routeChainBuilder,
            Substitute.For<ILogger<SmartSwapPlanner>>());
    }

    private static ServiceTaskPlanningSystem.Services.PlanManagement.PlanService CreatePlanService(
        ScheduleDbContext db, RouteChain chain)
    {
        var evaluation       = Substitute.For<ServiceTaskPlanningSystem.Services.PlanManagement.IEvaluationEngine>();
        var planningCalendar = Substitute.For<ServiceTaskPlanningSystem.Services.Time.IPlanningCalendar>();
        var permission       = Substitute.For<ServiceTaskPlanningSystem.Services.Authorization.ITaskPermissionService>();
        var time             = Substitute.For<IScheduleTimeService>();
        var routeEval        = Substitute.For<ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation.IRouteEvaluationService>();
        var travelFeasibility= Substitute.For<ServiceTaskPlanningSystem.Services.Travel.ITaskTravelFeasibilityService>();
        var logger           = Substitute.For<ILogger<ServiceTaskPlanningSystem.Services.PlanManagement.PlanService>>();

        time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date   = ((DateTime)call[0]).Date;
                var mins   = (int)call[1];
                var offset = (int)call[2];
                return DateTime.SpecifyKind(date.AddMinutes(mins - offset), DateTimeKind.Utc);
            });

        planningCalendar
            .BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceTaskPlanningSystem.Services.Time.PlanningWindow(
                DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));

        var timeService2 = Substitute.For<IScheduleTimeService>();
        timeService2
            .CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date   = ((DateTime)call[0]).Date;
                var mins   = (int)call[1];
                var offset = (int)call[2];
                return DateTime.SpecifyKind(date.AddMinutes(mins - offset), DateTimeKind.Utc);
            });

        var routeChainBuilder2 = Substitute.For<IRouteChainBuilder>();
        routeChainBuilder2
            .BuildDayChainAsync(Arg.Any<RouteChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(chain);

        var planner = new SmartSwapPlanner(
            db, timeService2, routeChainBuilder2,
            Substitute.For<ILogger<SmartSwapPlanner>>());

        return new ServiceTaskPlanningSystem.Services.PlanManagement.PlanService(
            db, evaluation, planningCalendar, permission, time,
            routeEval, travelFeasibility, planner, logger);
    }
}
