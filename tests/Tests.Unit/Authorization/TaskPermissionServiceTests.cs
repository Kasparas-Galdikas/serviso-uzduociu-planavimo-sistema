using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Interfaces;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.Authorization;

public class TaskPermissionServiceTests
{
    private readonly IScheduleTimeService _timeService = Substitute.For<IScheduleTimeService>();
    private readonly ILogger<TaskPermissionService> _logger = Substitute.For<ILogger<TaskPermissionService>>();

    [Fact]
    public async Task TaskAlreadyEnded_ReturnsFalse()
    {
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(false);
        var service = CreateService(new[] { AllowPolicy() });

        var result = await service.CanEditTaskAsync(CreateTask(), Manager(), 0);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task UnsupportedRole_ReturnsFalse()
    {
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        var service = CreateService(new[] { AllowPolicy() });
        var user = new UserContext { Role = (UserRole)999 };

        var result = await service.CanEditTaskAsync(CreateTask(), user, 0);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TechnicianWithoutMapping_ReturnsFalse()
    {
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        var service = CreateService(new[] { AllowPolicy() });
        var user = new UserContext { Role = UserRole.Technician, TechnicianId = null };

        var result = await service.CanEditTaskAsync(CreateTask(), user, 0);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task MissingPolicy_ReturnsFalse()
    {
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        var service = CreateService(Array.Empty<IPlanStatusEditPolicy>());

        var result = await service.CanEditTaskAsync(CreateTask(), Manager(), 0);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PolicyDenies_ReturnsFalse()
    {
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        var service = CreateService(new[] { new StubPolicy(PlanStatus.Active, false, (_, _) => { }) });

        var result = await service.CanEditTaskAsync(CreateTask(), Manager(), 0);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PolicyAllows_ReturnsTrue()
    {
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        var service = CreateService(new[] { AllowPolicy() });

        var result = await service.CanEditTaskAsync(CreateTask(), Manager(), 0);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TimezoneValues_AreCalculatedBeforePolicy()
    {
        var localStart = new DateTime(2024, 1, 10, 0, 0, 0);
        var userToday = new DateTime(2024, 1, 9, 0, 0, 0);
        _timeService.IsTaskStillEditable(Arg.Any<DateTime>()).Returns(true);
        _timeService.ConvertUtcToLocal(Arg.Any<DateTime>(), 180).Returns(localStart);
        _timeService.GetUserLocalToday(180).Returns(userToday);

        DateTime capturedTaskLocalDate = default;
        DateTime capturedUserToday = default;
        var policy = new StubPolicy(PlanStatus.Active, true, (taskLocal, today) =>
        {
            capturedTaskLocalDate = taskLocal;
            capturedUserToday = today;
        });

        var service = CreateService(new[] { policy });

        var result = await service.CanEditTaskAsync(CreateTask(), Manager(), 180);

        result.Should().BeTrue();
        capturedTaskLocalDate.Date.Should().Be(localStart.Date);
        capturedUserToday.Date.Should().Be(userToday.Date);
    }

    private TaskPermissionService CreateService(IEnumerable<IPlanStatusEditPolicy> policies) =>
        new TaskPermissionService(_timeService, policies, _logger);

    private static UserContext Manager() => new() { Role = UserRole.Manager, UserId = 1 };

    private static TaskEntity CreateTask(PlanStatus status = PlanStatus.Active) =>
        new()
        {
            Id = 1,
            TechnicianId = 10,
            Plan = new PlanEntity { Status = status },
            EndAt = DateTime.UtcNow.AddHours(1),
            StartAt = DateTime.UtcNow
        };

    private static IPlanStatusEditPolicy AllowPolicy() => new StubPolicy(PlanStatus.Active, true, (_, _) => { });

    private sealed class StubPolicy : IPlanStatusEditPolicy
    {
        private readonly bool _result;
        private readonly Action<DateTime, DateTime> _onCall;

        public StubPolicy(PlanStatus appliesTo, bool result, Action<DateTime, DateTime> onCall)
        {
            AppliesTo = appliesTo;
            _result = result;
            _onCall = onCall;
        }

        public PlanStatus AppliesTo { get; }

        public bool CanEdit(TaskEntity task, ServiceTaskPlanningSystem.Services.Authorization.Models.UserContext user, DateTime taskLocalDate, DateTime userLocalToday)
        {
            _onCall(taskLocalDate, userLocalToday);
            return _result;
        }
    }
}
