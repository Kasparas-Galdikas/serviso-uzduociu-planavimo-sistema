using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ServiceTaskPlanningSystem.Controllers.Api;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.History;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using Xunit;

namespace Tests.Unit.Controllers;

public sealed class PlansControllerTests
{
    [Fact]
    public async Task EnsureBacklogDraft_ReturnsCanonicalDraftContext()
    {
        var planService = Substitute.For<IPlanService>();
        var historyReader = Substitute.For<ITaskHistoryReader>();
        var userContextResolver = Substitute.For<IUserContextResolver>();
        var logger = Substitute.For<ILogger<PlansController>>();

        planService.GetDraftPlanAsync(Arg.Any<CancellationToken>())
            .Returns((PlanEntity?)null);
        planService.EnsureBacklogDraftAsync(
                Arg.Any<DateTime?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanEntity
            {
                Id = 24,
                PeriodStart = new DateTime(2026, 4, 11),
                PeriodEnd = new DateTime(2026, 4, 18),
                Weeks = 2,
                Status = PlanStatus.Draft,
                CreatedAt = DateTime.UtcNow
            });

        var controller = new PlansController(planService, logger, historyReader, userContextResolver);

        var result = await controller.EnsureBacklogDraft(
            new PlansController.EnsureBacklogDraftRequest
            {
                StartDate = new DateTime(2026, 4, 11),
                EndDate = new DateTime(2026, 4, 11)
            },
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        dynamic payload = ok.Value!;

        ((long)payload.draftPlanId).Should().Be(24);
        ((bool)payload.created).Should().BeTrue();
        ((bool)payload.refreshed).Should().BeFalse();
        ((string)payload.periodStart).Should().Be("2026-04-11");
        ((string)payload.periodEnd).Should().Be("2026-04-18");
    }
}
