using FluentAssertions;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using Xunit;

namespace Tests.Unit.PlanManagement;

public sealed class LoadScoreCalculatorTests
{
    [Fact]
    public void ComputeLoadScore_UsesPositiveSoftPenalties_ToDifferentiateUtilizationBands()
    {
        var optimal = new LoadScoreContext
        {
            DailyUtilization = 0.4,
            WeeklyUtilization = 0.4
        };
        var high = new LoadScoreContext
        {
            DailyUtilization = 0.8,
            WeeklyUtilization = 0.8
        };

        var optimalScore = LoadScoreCalculator.ComputeLoadScore(optimal);
        var highScore = LoadScoreCalculator.ComputeLoadScore(high);

        optimalScore.Should().Be(0);
        highScore.Should().BeGreaterThan(optimalScore);
    }

    [Fact]
    public void ComputeLoadScore_WhenHardConstraintViolated_ReturnsNegativeScore()
    {
        var conflict = new LoadScoreContext
        {
            HasConflict = true,
            DailyUtilization = 0.4,
            WeeklyUtilization = 0.4
        };

        var score = LoadScoreCalculator.ComputeLoadScore(conflict);

        score.Should().BeLessThan(0);
    }
}
