using System;
using System.Collections.Generic;
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

    [Fact]
    public async Task SuggestAutoAssignmentAsync_WithoutActiveOrDraftPlan_ReturnsNull()
    {
        using var db = CreateContext();
        var service = CreateService(db);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            DateTime.UtcNow.Date.AddDays(1),
            60,
            "Obj-1",
            TaskPriority.Medium,
            0,
            null,
            null,
            CancellationToken.None);

        suggestion.Should().BeNull();
    }

    [Fact]
    public async Task SuggestAutoAssignmentAsync_WhenObjectMissing_ReturnsNull()
    {
        using var db = CreateContext();
        SeedPlan(db);
        SeedTechnician(db, 1, 100);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var suggestion = await service.SuggestAutoAssignmentAsync(
            DateTime.UtcNow.Date.AddDays(1),
            60,
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
            CancellationToken.None);

        suggestion.StartMinutes.Should().BeGreaterThanOrEqualTo(600);
        suggestion.EndMinutes.Should().Be(suggestion.StartMinutes + 30);
    }

    private PlanService CreateService(ScheduleDbContext db) =>
        new(
            db,
            _evaluation,
            _planningCalendar,
            _permission,
            _timeService,
            _routeEvaluation,
            _travelFeasibility,
            _repairService,
            _travelResolver,
            Substitute.For<ITaskHistoryWriter>(),
            _logger);

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
}
