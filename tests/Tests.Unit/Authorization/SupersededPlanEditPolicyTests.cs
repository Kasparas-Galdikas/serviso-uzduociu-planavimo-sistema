using System;
using FluentAssertions;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Authorization.Policies;
using Xunit;

namespace Tests.Unit.Authorization;

public class SupersededPlanEditPolicyTests
{
    private readonly SupersededPlanEditPolicy _policy = new();

    [Fact]
    public void PastLocalDate_Denied()
    {
        var result = _policy.CanEdit(TaskOwnedBy(1), Manager(), DateTime.Today.AddDays(-1), DateTime.Today);

        result.Should().BeFalse();
    }

    [Fact]
    public void Future_ManagerAllowed()
    {
        var result = _policy.CanEdit(TaskOwnedBy(1), Manager(), DateTime.Today.AddDays(1), DateTime.Today);

        result.Should().BeTrue();
    }

    [Fact]
    public void Future_TechnicianOwnTask_Allowed()
    {
        var user = Technician(7);
        var task = TaskOwnedBy(7);

        var result = _policy.CanEdit(task, user, DateTime.Today.AddDays(1), DateTime.Today);

        result.Should().BeTrue();
    }

    [Fact]
    public void Future_TechnicianOtherTask_Denied()
    {
        var user = Technician(7);
        var task = TaskOwnedBy(9);

        var result = _policy.CanEdit(task, user, DateTime.Today.AddDays(1), DateTime.Today);

        result.Should().BeFalse();
    }

    [Fact]
    public void TimezoneBoundary_LocalYesterday_Denied()
    {
        var result = _policy.CanEdit(TaskOwnedBy(1), Manager(), DateTime.Today.AddDays(-1), DateTime.Today);

        result.Should().BeFalse();
    }

    [Fact]
    public void TimezoneBoundary_LocalToday_Allowed()
    {
        var result = _policy.CanEdit(TaskOwnedBy(1), Manager(), DateTime.Today, DateTime.Today.AddDays(-1));

        result.Should().BeTrue();
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager };

    private static UserContext Technician(long? techId) => new() { Role = UserRole.Technician, TechnicianId = techId };

    private static TaskEntity TaskOwnedBy(long techId) => new() { TechnicianId = techId };
}
