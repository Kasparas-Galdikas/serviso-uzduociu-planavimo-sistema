using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed class CandidateScoreCalculator
{
    internal const double BatchBalancePenaltyPerAssignment = 20.0;
    internal const double TravelPenaltyPerHour = 15.0;
    internal const double DelayPenaltyPerDay = 100.0;
    internal const double GlobalFallbackPenalty = 250.0;
    internal const double DayTaskCountPenaltyPerTask = 25.0;
    internal const double LateStartPenaltyPerMinute = 0.05;
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
        var localityPenalty = localityTier == CandidateLocalityTier.GlobalFallback ? GlobalFallbackPenalty : 0;
        var existingDayTaskCount = dayTasks.Count;
        var spreadPenalty = existingDayTaskCount * DayTaskCountPenaltyPerTask;
        var startTimePenalty = startMinutes * LateStartPenaltyPerMinute;

        return new RankedTechnicianCandidate(
            technician,
            candidateDate.Date,
            startMinutes,
            endMinutes,
            localityTier,
            new CandidateScoreBreakdown(loadScore, travelPenalty, batchPenalty, delayPenalty, localityPenalty, SpreadPenalty: spreadPenalty, StartTimePenalty: startTimePenalty),
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
