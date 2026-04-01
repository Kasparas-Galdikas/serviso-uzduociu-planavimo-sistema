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
    public void CandidateSlotGenerator_ForFirstTask_RoundsTravelAdjustedStartToGrid()
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
            30,
            serviceObject,
            new Dictionary<(long From, long To), int>
            {
                [(100, 200)] = 4
            });

        starts.Should().ContainSingle();
        starts[0].Should().Be(510);
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

        var candidateTask = new TaskEntity
        {
            Id = -1,
            TechnicianId = 1,
            ServiceObjectId = 10,
            ServiceObject = serviceObject,
            StartAt = day.AddHours(9),
            EndAt = day.AddHours(10),
            Status = ScheduleTaskStatus.InProgress
        };

        var withoutPenalties = calculator.BuildRankedCandidate(
            technician,
            day,
            day,
            tasks,
            tasks,
            candidateTask,
            new Dictionary<(long From, long To), int>(),
            0,
            CandidateLocalityTier.SameCity,
            480,
            540);
        var withPenalties = calculator.BuildRankedCandidate(
            technician,
            day,
            day,
            tasks,
            tasks,
            candidateTask,
            new Dictionary<(long From, long To), int>
            {
                [(100, 200)] = 120
            },
            2,
            CandidateLocalityTier.SameCity,
            480,
            540);

        withPenalties.ScoreBreakdown.TravelPenalty.Should().BeGreaterThan(0);
        withPenalties.ScoreBreakdown.BatchPenalty.Should().Be(4 * CandidateScoreCalculator.BatchBalancePenaltyPerAssignment);
        withPenalties.ScoreBreakdown.TotalScore.Should().BeGreaterThan(withoutPenalties.ScoreBreakdown.TotalScore);
    }

    [Fact]
    public void CandidateScoreCalculator_AddsDelayPenaltyForFutureDates()
    {
        var calculator = new CandidateScoreCalculator();
        var technician = new TechnicianEntity
        {
            Id = 1,
            WorkplaceAddressId = 100,
            DailyLimit = 8,
            WeeklyLimit = 40
        };
        var day = DateTime.UtcNow.Date.AddDays(1);

        var todayExtra = new TaskEntity
        {
            Id = -1,
            TechnicianId = 1,
            StartAt = day.AddHours(8),
            EndAt = day.AddHours(9),
            Status = ScheduleTaskStatus.InProgress
        };
        var tomorrowExtra = new TaskEntity
        {
            Id = -2,
            TechnicianId = 1,
            StartAt = day.AddDays(1).AddHours(8),
            EndAt = day.AddDays(1).AddHours(9),
            Status = ScheduleTaskStatus.InProgress
        };

        var todayCandidate = calculator.BuildRankedCandidate(
            technician,
            day,
            day,
            new List<TaskEntity>(),
            new List<TaskEntity>(),
            todayExtra,
            new Dictionary<(long From, long To), int>(),
            0,
            CandidateLocalityTier.SameCity,
            480,
            540);
        var tomorrowCandidate = calculator.BuildRankedCandidate(
            technician,
            day,
            day.AddDays(1),
            new List<TaskEntity>(),
            new List<TaskEntity>(),
            tomorrowExtra,
            new Dictionary<(long From, long To), int>(),
            0,
            CandidateLocalityTier.SameCity,
            480,
            540);

        tomorrowCandidate.ScoreBreakdown.DelayPenalty.Should().Be(CandidateScoreCalculator.DelayPenaltyPerDay);
        tomorrowCandidate.ScoreBreakdown.TotalScore.Should().BeGreaterThan(todayCandidate.ScoreBreakdown.TotalScore);
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
                new RankedTechnicianCandidate(tech2, DateTime.Today, 540, 600, CandidateLocalityTier.SameCity, new CandidateScoreBreakdown(10, 0, 0, 0, 0)),
                new RankedTechnicianCandidate(tech1, DateTime.Today, 480, 540, CandidateLocalityTier.SameCity, new CandidateScoreBreakdown(10, 0, 0, 0, 0)),
                new RankedTechnicianCandidate(tech1, DateTime.Today, 600, 660, CandidateLocalityTier.SameCity, new CandidateScoreBreakdown(15, 0, 0, 0, 0))
            });

        best.Should().NotBeNull();
        best!.Technician.Id.Should().Be(1);
        best.StartMinutes.Should().Be(480);
    }

    [Fact]
    public void CandidateDateWindowGenerator_GeneratesBoundedFutureDates()
    {
        var generator = new CandidateDateWindowGenerator();
        var start = new DateTime(2026, 3, 10);
        var dates = generator.GenerateDates(start, start.AddDays(2), 5);

        dates.Should().Equal(
            start,
            start.AddDays(1),
            start.AddDays(2));
    }

    [Fact]
    public void CandidateLocalitySelector_PrefersSameCityTechnicians()
    {
        var selector = new CandidateLocalitySelector();
        var pool = selector.SelectPool(
            new[]
            {
                new TechnicianEntity { Id = 1, WorkplaceAddress = new AddressEntity { City = "Vilnius" } },
                new TechnicianEntity { Id = 2, WorkplaceAddress = new AddressEntity { City = "Kaunas" } }
            },
            new ServiceObjectEntity { Address = new AddressEntity { City = "Vilnius" } });

        pool.Tier.Should().Be(CandidateLocalityTier.SameCity);
        pool.Technicians.Should().ContainSingle();
        pool.Technicians[0].Id.Should().Be(1);
    }
}
