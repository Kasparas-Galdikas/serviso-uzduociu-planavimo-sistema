using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed record CandidateScoreBreakdown(
    double LoadScore,
    double TravelPenalty,
    double BatchPenalty,
    double SkillPenalty = 0d,
    double ContinuityPenalty = 0d)
{
    public double TotalScore => LoadScore + TravelPenalty + BatchPenalty + SkillPenalty + ContinuityPenalty;
}

internal sealed record RankedTechnicianCandidate(
    TechnicianEntity Technician,
    int StartMinutes,
    int EndMinutes,
    CandidateScoreBreakdown ScoreBreakdown);
