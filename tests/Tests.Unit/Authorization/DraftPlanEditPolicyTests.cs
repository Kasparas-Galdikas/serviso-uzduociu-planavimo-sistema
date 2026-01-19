using System;
using FluentAssertions;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Authorization.Policies;
using Xunit;

namespace Tests.Unit.Authorization;

public class DraftPlanEditPolicyTests
{
    private readonly DraftPlanEditPolicy _policy = new();

    [Fact]
    public void Manager_IsAllowed()
    {
        var result = _policy.CanEdit(new TaskEntity(), Manager(), DateTime.Today, DateTime.Today);

        result.Should().BeTrue();
    }

    [Fact]
    public void Technician_IsDenied()
    {
        var result = _policy.CanEdit(new TaskEntity(), Technician(1), DateTime.Today, DateTime.Today);

        result.Should().BeFalse();
    }

    private static UserContext Manager() => new() { Role = UserRole.Manager };

    private static UserContext Technician(long? techId) => new() { Role = UserRole.Technician, TechnicianId = techId };
}
