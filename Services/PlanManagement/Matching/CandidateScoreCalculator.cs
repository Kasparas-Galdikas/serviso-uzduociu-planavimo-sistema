using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed class CandidateScoreCalculator
{
    internal const double BatchBalancePenaltyPerAssignment = 20.0;
    internal const double TravelPenaltyPerHour = 15.0;

    public RankedTechnicianCandidate BuildRankedCandidate(
        TechnicianEntity technician,
        DateTime dayStartUtc,
        IReadOnlyList<TaskEntity> allTasks,
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        int batchAssignmentCount,
        int startMinutes,
        int endMinutes)
    {
        var dayEnd = dayStartUtc.AddDays(1);
        var weekStart = StartOfWeek(dayStartUtc);
        var weekEnd = weekStart.AddDays(7);

        var dayTasks = allTasks
            .Where(t => t.TechnicianId == technician.Id &&
                        t.StartAt >= dayStartUtc &&
                        t.StartAt < dayEnd)
            .OrderBy(t => t.StartAt)
            .ToList();

        var weekTasks = allTasks
            .Where(t => t.TechnicianId == technician.Id &&
                        t.StartAt >= weekStart &&
                        t.StartAt < weekEnd)
            .OrderBy(t => t.StartAt)
            .ToList();

        var travelMinutesByTaskId = BuildTravelMinutesByTaskIdFromLookup(
            technician,
            weekTasks,
            routeLookup,
            strictRouteCoverage: false) ?? new Dictionary<long, int>();

        var snapshot = WorkloadCalculator.Evaluate(technician, dayTasks, weekTasks, travelMinutesByTaskId);
        var loadScore = LoadScoreCalculator.ComputeLoadScore(snapshot.Context, LoadScoreWeights.CreateDefault());
        var travelPenalty = snapshot.Context.TravelTimeHours * TravelPenaltyPerHour;
        var batchPenalty = batchAssignmentCount * BatchBalancePenaltyPerAssignment;

        return new RankedTechnicianCandidate(
            technician,
            startMinutes,
            endMinutes,
            new CandidateScoreBreakdown(loadScore, travelPenalty, batchPenalty));
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

    private static DateTime StartOfWeek(DateTime date)
    {
        var current = date.Date;
        while (current.DayOfWeek != DayOfWeek.Monday)
        {
            current = current.AddDays(-1);
        }

        return current;
    }
}
