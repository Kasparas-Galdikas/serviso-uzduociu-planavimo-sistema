using System;
using System.Collections.Generic;
using FluentAssertions;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement.Matching;
using ServiceTaskPlanningSystem.Services.Time;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class AssignmentMatchingServicesTests
{
    private readonly ScheduleTimeService _timeService = new();

    [Fact]
    public void CandidateEligibilityEvaluator_FiltersOutTechniciansWithoutWorkplaceAddress()
    {
        var evaluator = new CandidateEligibilityEvaluator();
        var eligible = evaluator.GetEligibleTechnicians(
            new[]
            {
                new TechnicianEntity { Id = 2, WorkplaceAddressId = 0 },
                new TechnicianEntity { Id = 1, WorkplaceAddressId = 100 }
            },
            requiredSkillLevel: "expert");

        eligible.Should().ContainSingle();
        eligible[0].Id.Should().Be(1);
    }

    [Fact]
    public void CandidateSlotGenerator_ForFirstTask_AccountsForWorkplaceTravel()
    {
        var generator = new CandidateSlotGenerator(_timeService);
        var technician = new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = 100,
            DailyLimit = 8,
            WeeklyLimit = 40
        };
        var serviceObject = new ServiceObjectEntity
        {
            Id = 10,
            AddressId = 200,
            Name = "Obj-1"
        };
        var day = DateTime.UtcNow.Date.AddDays(1);

        var starts = generator.GenerateCandidateStartMinutes(
            technician,
            new List<TaskEntity>(),
            day,
            0,
            60,
            serviceObject,
            new Dictionary<(long From, long To), int>
            {
                [(100, 200)] = 120
            });

        starts.Should().ContainSingle();
        starts[0].Should().BeGreaterThanOrEqualTo(600);
    }

    [Fact]
    public void CandidateScoreCalculator_AddsTravelAndBatchPenalties()
    {
        var calculator = new CandidateScoreCalculator();
        var technician = new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = 100,
            DailyLimit = 8,
            WeeklyLimit = 40
        };
        var serviceObject = new ServiceObjectEntity
        {
            Id = 10,
            AddressId = 200,
            Name = "Obj-1"
        };
        var day = DateTime.UtcNow.Date.AddDays(1);
        var tasks = new List<TaskEntity>
        {
            new()
            {
                Id = 1,
                TechnicianId = 1,
                ServiceObjectId = 10,
                ServiceObject = serviceObject,
                StartAt = day.AddHours(8),
                EndAt = day.AddHours(9),
                Status = ScheduleTaskStatus.InProgress
            }
        };

        var withoutPenalties = calculator.BuildRankedCandidate(
            technician,
            day,
            tasks,
            new Dictionary<(long From, long To), int>(),
            0,
            480,
            540);
        var withPenalties = calculator.BuildRankedCandidate(
            technician,
            day,
            tasks,
            new Dictionary<(long From, long To), int>
            {
                [(100, 200)] = 120
            },
            2,
            480,
            540);

        withPenalties.ScoreBreakdown.TravelPenalty.Should().BeGreaterThan(0);
        withPenalties.ScoreBreakdown.BatchPenalty.Should().Be(2 * CandidateScoreCalculator.BatchBalancePenaltyPerAssignment);
        withPenalties.ScoreBreakdown.TotalScore.Should().BeGreaterThan(withoutPenalties.ScoreBreakdown.TotalScore);
    }

    [Fact]
    public void CandidateRankingService_SelectsLowestScoreThenEarliestStartThenTechnicianId()
    {
        var ranking = new CandidateRankingService();
        var tech1 = new TechnicianEntity { Id = 1 };
        var tech2 = new TechnicianEntity { Id = 2 };

        var best = ranking.SelectBest(
            new[]
            {
                new RankedTechnicianCandidate(tech2, 540, 600, new CandidateScoreBreakdown(10, 0, 0)),
                new RankedTechnicianCandidate(tech1, 480, 540, new CandidateScoreBreakdown(10, 0, 0)),
                new RankedTechnicianCandidate(tech1, 600, 660, new CandidateScoreBreakdown(15, 0, 0))
            });

        best.Should().NotBeNull();
        best!.Technician.Id.Should().Be(1);
        best.StartMinutes.Should().Be(480);
    }
}
