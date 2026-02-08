using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.Views;

public class ScheduleProviderTests
{
    [Fact]
    public async Task Filters_Tasks_By_Week_And_Location()
    {
        using var db = CreateContext();
        SeedAddress(db, 1, regionId: 1);
        var north = SeedTechnician(db, 1, "North");
        var south = SeedTechnician(db, 2, "South");
        var plan = SeedPlan(db, 1);

        var inWeek = new DateTime(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);
        var outOfWeek = new DateTime(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

        SeedTask(db, id: 1, techId: north.Id, planId: plan.Id, startUtc: inWeek, endUtc: inWeek.AddHours(1), evalStatus: LoadState.Normal);
        SeedTask(db, id: 2, techId: south.Id, planId: plan.Id, startUtc: inWeek, endUtc: inWeek.AddHours(1), evalStatus: LoadState.Normal);
        SeedTask(db, id: 3, techId: north.Id, planId: plan.Id, startUtc: outOfWeek, endUtc: outOfWeek.AddHours(1), evalStatus: LoadState.Normal);

        db.SaveChanges();

        var provider = CreateProvider(db);
        var user = new UserContext { Role = UserRole.Manager, UserId = 99 };

        var vm = await provider.GetScheduleAsync(
            view: "week",
            date: "2026-01-19",
            location: "North",
            workload: null,
            status: null,
            informationType: null,
            technicianId: null,
            activePlan: plan,
            draftPlan: null,
            user: user,
            timezoneOffsetMinutes: 0);

        vm.Tasks.Should().HaveCount(1);
        vm.Tasks.Single().Id.Should().Be(1);
        vm.Technicians.Should().HaveCount(1);
        vm.Technicians.Single().Name.Should().Be(north.User!.Name);
    }

    [Fact]
    public async Task Maps_Task_Flags_And_Object_Address()
    {
        using var db = CreateContext();
        SeedAddress(db, 1, regionId: 1);
        var tech = SeedTechnician(db, 1, "North");
        var plan = SeedPlan(db, 1);
        var startUtc = new DateTime(2026, 1, 20, 8, 0, 0, DateTimeKind.Utc);

        var task = new TaskEntity
        {
            Id = 10,
            PlanId = plan.Id,
            TechnicianId = tech.Id,
            StartAt = startUtc,
            EndAt = startUtc.AddHours(2),
            Title = "Conflict task",
            Status = ScheduleTaskStatus.InProgress,
            Priority = TaskPriority.High,
            ServiceObjectId = 100,
            RegionId = 1,
            ServiceObject = new ServiceObjectEntity
            {
                Id = 100,
                Name = "Obj",
                AddressId = 2,
                Address = new AddressEntity { Id = 2, RegionId = 1, Street = "Obj", FullAddress = "Obj address" }
            },
            Evaluation = new TaskEvaluationEntity
            {
                Status = LoadState.Conflict,
                OverloadHours = 2
            }
        };
        db.Tasks.Add(task);
        db.SaveChanges();

        var provider = CreateProvider(db);
        var user = new UserContext { Role = UserRole.Manager, UserId = 99 };

        var vm = await provider.GetScheduleAsync(
            view: "day",
            date: "2026-01-20",
            location: null,
            workload: null,
            status: null,
            informationType: null,
            technicianId: null,
            activePlan: plan,
            draftPlan: null,
            user: user,
            timezoneOffsetMinutes: 120);

        var mapped = vm.Tasks.Single(t => t.Id == 10);
        mapped.Location.Should().Be("Obj address");
        mapped.IsConflict.Should().BeTrue();
        mapped.IsLoad.Should().BeFalse();
        mapped.OverloadHours.Should().Be(2);
        mapped.LoadStatus.Should().Be("conflict");
        mapped.Start.Should().Be(startUtc.AddMinutes(120));
    }

    private static ScheduleProvider CreateProvider(ScheduleDbContext db)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var calendar = Substitute.For<IPlanningCalendar>();
        calendar.GetBoundaryAsync(Arg.Any<CancellationToken>())
            .Returns(new PlanningBoundary(DateTime.Today, DateTime.Today));
        var permission = Substitute.For<ServiceTaskPlanningSystem.Services.Authorization.ITaskPermissionService>();
        permission.CanEditTaskAsync(Arg.Any<TaskEntity>(), Arg.Any<UserContext>(), Arg.Any<int>())
            .Returns(Task.FromResult(true));
        var time = new ScheduleTimeService();
        var logger = NullLogger<ScheduleProvider>.Instance;

        return new ScheduleProvider(db, scopeFactory, calendar, permission, time, logger);
    }

    private static ScheduleDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ScheduleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ScheduleDbContext(options);
    }

    private static void SeedAddress(ScheduleDbContext db, long id, long regionId)
    {
        db.Addresses.Add(new AddressEntity { Id = id, RegionId = regionId, Street = "Addr", FullAddress = "Addr" });
    }

    private static TechnicianEntity SeedTechnician(ScheduleDbContext db, long id, string location)
    {
        var user = new UserEntity
        {
            Id = id,
            Email = $"tech{id}@test.com",
            Name = $"Tech {id}",
            Role = UserRole.Technician,
            IsActive = true
        };
        var region = new RegionEntity { Id = id, Name = location, Country = "LT", CreatedAt = DateTime.UtcNow };

        var tech = new TechnicianEntity
        {
            Id = id,
            UserId = user.Id,
            User = user,
            RegionId = region.Id,
            Region = region,
            WorkplaceAddressId = 1,
            DailyLimit = 8,
            WeeklyLimit = 40,
            TravelSwitchLimit = 1
        };

        db.Users.Add(user);
        db.Regions.Add(region);
        db.Technicians.Add(tech);
        return tech;
    }

    private static PlanEntity SeedPlan(ScheduleDbContext db, long id)
    {
        var plan = new PlanEntity
        {
            Id = id,
            PeriodStart = new DateTime(2026, 1, 19),
            PeriodEnd = new DateTime(2026, 1, 25),
            Weeks = 1,
            Status = PlanStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Plans.Add(plan);
        return plan;
    }

    private static void SeedTask(ScheduleDbContext db, long id, long techId, long planId, DateTime startUtc, DateTime endUtc, LoadState evalStatus)
    {
        if (!db.Addresses.Any(a => a.Id == 1))
        {
            db.Addresses.Add(new AddressEntity { Id = 1, RegionId = 1, Street = "Addr", FullAddress = "Addr" });
        }
        if (!db.ServiceObjects.Any(s => s.Id == 1))
        {
            db.ServiceObjects.Add(new ServiceObjectEntity { Id = 1, Name = "Obj", AddressId = 1 });
        }

        var task = new TaskEntity
        {
            Id = id,
            PlanId = planId,
            TechnicianId = techId,
            ServiceObjectId = 1,
            RegionId = 1,
            StartAt = startUtc,
            EndAt = endUtc,
            Title = $"Task {id}",
            Status = ScheduleTaskStatus.InProgress,
            Priority = TaskPriority.Medium,
            Evaluation = new TaskEvaluationEntity { Status = evalStatus }
        };
        db.Tasks.Add(task);
    }
}
