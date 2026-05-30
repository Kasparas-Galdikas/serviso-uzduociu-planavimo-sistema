using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Data.Enums;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

// SCORING DESIGN
// OneDayUnit (100) is the cost of delaying a task by one scheduling day.
// Every soft penalty below is expressed as a multiple of OneDayUnit so that
// relative importance can be reasoned about in "day-equivalent" terms.
//
// Bands by magnitude:
//   0–50     Assignment quality  (load balance, spread, start-time tiebreaker)
//   50–150   Logistics           (travel, nearby-city admission)
//   150–300  Location mismatch   (global fallback)
//   < 0      Hard violations     (daily/weekly overload — always disqualify)

internal sealed class CandidateScoreCalculator
{
    // === Scoring anchor ===
    internal const double OneDayUnit = 100.0;

    // === Time fit ===
    internal const double DelayPenaltyPerDay          = OneDayUnit;           // 100 — the anchor
    internal const double LateStartPenaltyPerMinute   = OneDayUnit / 2000.0; //  0.05 — tiebreaker only

    // === Logistics ===
    internal const double TravelPenaltyPerHour        = OneDayUnit * 0.15;   //  15
    internal const double NearbyCityPenalty           = OneDayUnit * 0.75;   //  75
    internal const double GlobalFallbackPenalty       = OneDayUnit * 2.5;    // 250

    // === Assignment quality ===
    internal const double DayTaskCountPenaltyPerTask       = OneDayUnit * 0.25; //  25
    internal const double BatchBalancePenaltyPerAssignment = OneDayUnit * 0.20; //  20 (applied as n²)

    // === Skill / experience (soft — never blocks, only nudges) ===
    internal const double ExperiencePenaltyHighJunior = OneDayUnit * 1.2; // 120
    internal const double ExperiencePenaltyHighMid    = OneDayUnit * 0.6; //  60
    internal const double ExperiencePenaltyMedJunior  = OneDayUnit * 0.3; //  30

    private static readonly LoadScoreWeights AssignmentWeights = new()
    {
        ConflictPenalty = 0.0,
        DailyOverPenalty = 500.0,
        WeeklyOverPenalty = 900.0,
        DailyOverHoursPenalty = 75.0,
        WeeklyOverHoursPenalty = 120.0,
        LowUtilizationPenalty = 0.0,
        MediumUtilizationPenalty = 10.0,
        HighUtilizationPenalty = 25.0,
        VeryHighUtilizationPenalty = 50.0,
        DailyLoadWeight = 1.5,
        WeeklyLoadWeight = 2.0
    };

    public RankedTechnicianCandidate BuildRankedCandidate(
        TechnicianEntity technician,
        DateTime requestedDate,
        DateTime candidateDate,
        IReadOnlyList<TaskEntity> dayTasks,
        IReadOnlyList<TaskEntity> weekTasks,
        TaskEntity extraTask,
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        int batchAssignmentCount,
        CandidateLocalityTier localityTier,
        int startMinutes,
        int endMinutes,
        string candidateSource = "generated",
        bool wasRepaired = false,
        int? originalStartMinutes = null)
    {
        var dayTasksWithCandidate = dayTasks.ToList();
        var weekTasksWithCandidate = weekTasks.ToList();

        if (extraTask.TechnicianId == technician.Id)
        {
            dayTasksWithCandidate.Add(extraTask);
            weekTasksWithCandidate.Add(extraTask);
        }

        var travelMinutesByTaskId = BuildTravelMinutesByTaskIdFromLookup(
            technician,
            weekTasksWithCandidate,
            routeLookup,
            strictRouteCoverage: false) ?? new Dictionary<long, int>();

        var snapshot = WorkloadCalculator.Evaluate(technician, dayTasksWithCandidate, weekTasksWithCandidate, travelMinutesByTaskId);
        var loadScore = LoadScoreCalculator.ComputeLoadScore(snapshot.Context, AssignmentWeights);
        var travelPenalty = snapshot.Context.TravelTimeHours * TravelPenaltyPerHour;
        var batchPenalty = batchAssignmentCount <= 0
            ? 0
            : batchAssignmentCount * batchAssignmentCount * BatchBalancePenaltyPerAssignment;
        var delayPenalty = Math.Max(0, (candidateDate.Date - requestedDate.Date).Days) * DelayPenaltyPerDay;
        var localityPenalty = localityTier switch
        {
            CandidateLocalityTier.SameCity       => 0.0,
            CandidateLocalityTier.NearestCity    => NearbyCityPenalty,
            CandidateLocalityTier.GlobalFallback => GlobalFallbackPenalty,
            _                                    => GlobalFallbackPenalty
        };
        var existingDayTaskCount = dayTasks.Count;
        var spreadPenalty = existingDayTaskCount * DayTaskCountPenaltyPerTask;
        var startTimePenalty = startMinutes * LateStartPenaltyPerMinute;
        var experiencePenalty = (extraTask.Priority, technician.ExperienceLevel) switch
        {
            (TaskPriority.High,   ExperienceLevel.Junior) => ExperiencePenaltyHighJunior,
            (TaskPriority.High,   ExperienceLevel.Mid)    => ExperiencePenaltyHighMid,
            (TaskPriority.Medium, ExperienceLevel.Junior) => ExperiencePenaltyMedJunior,
            _                                              => 0.0
        };

        return new RankedTechnicianCandidate(
            technician,
            candidateDate.Date,
            startMinutes,
            endMinutes,
            localityTier,
            new CandidateScoreBreakdown(loadScore, travelPenalty, batchPenalty, delayPenalty, localityPenalty, SkillPenalty: experiencePenalty, SpreadPenalty: spreadPenalty, StartTimePenalty: startTimePenalty),
            candidateSource,
            wasRepaired,
            originalStartMinutes);
    }

    internal static Dictionary<long, int>? BuildTravelMinutesByTaskIdFromLookup(
        TechnicianEntity technician,
        List<TaskEntity> weekTasks,
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        bool strictRouteCoverage)
    {
        var result = new Dictionary<long, int>();

        var tasksByDay = weekTasks
            .OrderBy(t => t.StartAt)
            .GroupBy(t => t.StartAt.Date)
            .ToList();

        foreach (var dayGroup in tasksByDay)
        {
            TaskEntity? prev = null;
            foreach (var task in dayGroup.OrderBy(t => t.StartAt))
            {
                var fromAddressId = prev?.ServiceObject?.AddressId ?? technician.WorkplaceAddressId;
                var toAddressId = task.ServiceObject?.AddressId ?? 0;

                if (fromAddressId <= 0 || toAddressId <= 0)
                {
                    if (strictRouteCoverage)
                    {
                        return null;
                    }

                    result[task.Id] = 0;
                    prev = task;
                    continue;
                }

                if (routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes))
                {
                    result[task.Id] = minutes;
                }
                else
                {
                    if (strictRouteCoverage)
                    {
                        return null;
                    }

                    result[task.Id] = 0;
                }

                prev = task;
            }
        }

        return result;
    }
}
