using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed record CandidateScoreBreakdown(
    double LoadScore,
    double TravelPenalty,
    double BatchPenalty,
    double DelayPenalty,
    double LocalityPenalty,
    double SkillPenalty = 0d,
    double ContinuityPenalty = 0d,
    double SpreadPenalty = 0d,
    double StartTimePenalty = 0d)
{
    public double TotalScore => LoadScore + TravelPenalty + BatchPenalty + DelayPenalty + LocalityPenalty + SkillPenalty + ContinuityPenalty + SpreadPenalty + StartTimePenalty;
}

internal sealed record RankedTechnicianCandidate(
    TechnicianEntity Technician,
    DateTime TargetDate,
    int StartMinutes,
    int EndMinutes,
    CandidateLocalityTier LocalityTier,
    CandidateScoreBreakdown ScoreBreakdown,
    string CandidateSource = "generated",
    bool WasRepaired = false,
    int? OriginalStartMinutes = null);
