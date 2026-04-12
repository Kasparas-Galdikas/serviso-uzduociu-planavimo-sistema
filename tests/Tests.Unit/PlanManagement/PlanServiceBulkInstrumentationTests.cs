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
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
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
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using Xunit;

namespace Tests.Unit.PlanManagement;

public class PlanServiceBulkInstrumentationTests
{
    private readonly IEvaluationEngine _evaluation = Substitute.For<IEvaluationEngine>();
    private readonly IPlanningCalendar _planningCalendar = Substitute.For<IPlanningCalendar>();
    private readonly ITaskPermissionService _permission = Substitute.For<ITaskPermissionService>();
    private readonly IScheduleTimeService _time = Substitute.For<IScheduleTimeService>();
    private readonly IRouteEvaluationService _routeEvaluation = Substitute.For<IRouteEvaluationService>();
    private readonly ITaskTravelFeasibilityService _travelFeasibility = Substitute.For<ITaskTravelFeasibilityService>();
    private readonly IScheduleRepairService _repairService = Substitute.For<IScheduleRepairService>();
    private readonly ITravelTimeResolver _travelResolver = Substitute.For<ITravelTimeResolver>();
    private readonly ITaskHistoryWriter _historyWriter = Substitute.For<ITaskHistoryWriter>();

    public PlanServiceBulkInstrumentationTests()
    {
        _evaluation.RecalculatePlanAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _planningCalendar.BuildWindowAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningWindow(DateTime.Today, DateTime.Today.AddDays(27), 28, 4, DateTime.Today));
        _permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        _routeEvaluation.EvaluateTechnicianDayAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _routeEvaluation.EvaluateTechnicianDayRoutesOnlyAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _travelFeasibility.EvaluatePlacementAsync(
                Arg.Any<long>(),
                Arg.Any<DateTime>(),
                Arg.Any<DateTime>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<ProposedRouteStop>?>(),
                Arg.Any<long?>())
            .Returns(new TravelFeasibilityResult { IsFeasible = true, InboundTravelMinutes = 15, OutboundTravelMinutes = 10 });
        _repairService.BuildPlanAsync(Arg.Any<ScheduleRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(ScheduleRepairPlan.Fail("no repair"));
        _repairService.ApplyPlanAsync(Arg.Any<ScheduleRepairPlan>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduleRepairApplyResult(true, null, Array.Empty<long>()));

        _time.GetUserLocalToday(Arg.Any<int>()).Returns(DateTime.Today);
        _time.CreateUtcFromDateAndMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(call =>
            {
                var date = ((DateTime)call[0]).Date;
                var minutes = (int)call[1];
                return DateTime.SpecifyKind(date.AddMinutes(minutes), DateTimeKind.Utc);
            });
        _time.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        _time.ConvertUtcToLocal(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(call =>
            {
                var utc = DateTime.SpecifyKind((DateTime)call[0], DateTimeKind.Utc);
                var offsetMinutes = (int)call[1];
                return utc.AddMinutes(offsetMinutes);
            });
        _time.GetEarliestCreatableStartMinutes(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ScheduleGrid.DayStartMinutes);
        _time.IsTravelStartFeasible(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(true);
    }

    [Fact]
    public async Task BulkCreate_LogsAllTimingPhasesAndSummary()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedActivePlan(db, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(7));
        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger);

        var result = await service.BulkCreateTasksAsync(
            new[]
            {
                CreateValidBulkItem(technicianId: 1, targetDate: DateTime.Today.AddDays(1))
            },
            planMode: null,
            user: Manager(),
            cancellationToken: CancellationToken.None);

        result.AllSucceeded.Should().BeTrue();

        var phaseEntries = logger.Entries
            .Where(e => Equals(e.Properties.GetValueOrDefault("EventName"), "svc.plan.bulk.create.phase"))
            .ToList();
        phaseEntries.Should().HaveCount(7);
        phaseEntries.Select(e => e.Properties["Phase"]).Should().BeEquivalentTo(
            ["prepare", "validate", "build", "save", "route-evaluation", "plan-recalculation", "history"]);
        phaseEntries.Should().OnlyContain(e => Equals(e.Properties.GetValueOrDefault("Success"), true));
        phaseEntries.Should().OnlyContain(e => Equals(e.Properties.GetValueOrDefault("ItemCount"), 1));
        phaseEntries.Should().OnlyContain(e => Equals(e.Properties.GetValueOrDefault("PlanMode"), "rankinis"));

        var summary = logger.Entries.Single(e => Equals(e.Properties.GetValueOrDefault("EventName"), "svc.plan.bulk.create.summary"));
        summary.Properties["Success"].Should().Be(true);
        summary.Properties["ItemCount"].Should().Be(1);
        summary.Properties["PlanMode"].Should().Be("rankinis");
        summary.Properties["OperationId"].Should().NotBeNull();

        var phaseDurations = summary.Properties["PhaseDurations"].Should().BeAssignableTo<IReadOnlyDictionary<string, long>>().Subject;
        phaseDurations.Keys.Should().BeEquivalentTo(
            ["prepare", "validate", "build", "save", "route-evaluation", "plan-recalculation", "history"]);

        var summaryMetrics = summary.Properties["SummaryMetrics"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        summaryMetrics["createdTaskCount"].Should().Be(1);
        summaryMetrics["errorCount"].Should().Be(0);
        summaryMetrics["affectedTechDateCount"].Should().Be(1);
        summaryMetrics["affectedPlanCount"].Should().Be(1);
        summaryMetrics["historyRecordCount"].Should().Be(1);

        phaseEntries.Should().OnlyContain(e => Equals(e.Properties.GetValueOrDefault("OperationId"), summary.Properties["OperationId"]));
        logger.Entries.Should().Contain(e => e.Message.StartsWith("svc.plan.bulk.create.start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkCreate_WhenValidationFails_LogsFailureSummary()
    {
        using var db = CreateContext();
        SeedTechnician(db, technicianId: 1, workplaceAddressId: 1);
        SeedActivePlan(db, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(7));
        var logger = new TestLogger<PlanService>();
        var service = CreateService(db, logger);

        var result = await service.BulkCreateTasksAsync(
            new[]
            {
                CreateValidBulkItem(technicianId: 0, targetDate: DateTime.Today.AddDays(1))
            },
            planMode: null,
            user: Manager(),
            cancellationToken: CancellationToken.None);

        result.AllSucceeded.Should().BeFalse();
        result.Errors.Should().ContainSingle();

        var phaseEntries = logger.Entries
            .Where(e => Equals(e.Properties.GetValueOrDefault("EventName"), "svc.plan.bulk.create.phase"))
            .ToList();
        phaseEntries.Should().HaveCount(2);
        phaseEntries.Select(e => e.Properties["Phase"]).Should().BeEquivalentTo(["prepare", "validate"]);

        var summary = logger.Entries.Single(e => Equals(e.Properties.GetValueOrDefault("EventName"), "svc.plan.bulk.create.summary"));
        summary.Properties["Success"].Should().Be(false);
        var phaseDurations = summary.Properties["PhaseDurations"].Should().BeAssignableTo<IReadOnlyDictionary<string, long>>().Subject;
        phaseDurations.Keys.Should().BeEquivalentTo(["prepare", "validate"]);

        var summaryMetrics = summary.Properties["SummaryMetrics"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        summaryMetrics["errorCount"].Should().Be(1);
        summaryMetrics["createdTaskCount"].Should().Be(0);
        summaryMetrics["affectedTechDateCount"].Should().Be(0);
    }

    private PlanService CreateService(ScheduleDbContext db, ILogger<PlanService> logger) =>
        new(db, _evaluation, _planningCalendar, _permission, _time, _routeEvaluation, _travelFeasibility, _repairService, _travelResolver, _historyWriter, logger);

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 25 };

    private static CreateTaskRequestDto CreateValidBulkItem(long technicianId, DateTime targetDate) =>
        new()
        {
            TechnicianId = technicianId,
            TargetDate = targetDate.ToString("yyyy-MM-dd"),
            StartMinutes = 9 * 60,
            EndMinutes = 10 * 60,
            Priority = TaskPriority.Medium,
            Status = ScheduleTaskStatus.InProgress,
            TimezoneOffsetMinutes = 0,
            Title = "Bulk task",
            SkillLevel = "A",
            ObjectName = "Object A",
            ObjectAddress = "Main St 1, City",
            Instructions = "Do work"
        };

    private static void SeedTechnician(ScheduleDbContext db, long technicianId, long workplaceAddressId)
    {
        db.Addresses.Add(new AddressEntity
        {
            Id = workplaceAddressId,
            City = "City",
            Street = "HQ",
            FullAddress = "HQ 1, City",
            Latitude = 54.6872m,
            Longitude = 25.2797m
        });
        db.Technicians.Add(new TechnicianEntity
        {
            Id = technicianId,
            WorkplaceAddressId = workplaceAddressId,
            DailyLimit = 8,
            WeeklyLimit = 40
        });
        db.SaveChanges();
    }

    private static void SeedActivePlan(ScheduleDbContext db, DateTime start, DateTime end)
    {
        db.Plans.Add(new PlanEntity
        {
            Id = 50,
            PeriodStart = start.Date,
            PeriodEnd = end.Date,
            Weeks = 2,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
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
            return new ScopeHandle(stack);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var scopeValues in (_scopes.Value ?? []).Reverse())
            {
                foreach (var pair in scopeValues)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in ToDictionary(state))
            {
                properties[pair.Key] = pair.Value;
            }

            lock (_lock)
            {
                _entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), properties));
            }
        }

        private static Dictionary<string, object?> ToDictionary<TState>(TState state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                return pairs
                    .Where(pair => !string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal) &&
                                   !string.Equals(pair.Key, "OriginalFormat", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, object?>();
        }

        private sealed class ScopeHandle : IDisposable
        {
            private readonly Stack<Dictionary<string, object?>> _stack;
            private bool _disposed;

            public ScopeHandle(Stack<Dictionary<string, object?>> stack)
            {
                _stack = stack;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_stack.Count > 0)
                {
                    _stack.Pop();
                }
            }
        }
    }
}
