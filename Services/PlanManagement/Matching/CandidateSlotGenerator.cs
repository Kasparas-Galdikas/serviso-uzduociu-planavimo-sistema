using System;
using System.Collections.Generic;
using System.Linq;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Matching;

internal sealed class CandidateSlotGenerator
{
    private readonly IScheduleTimeService _timeService;

    public CandidateSlotGenerator(IScheduleTimeService timeService)
    {
        _timeService = timeService;
    }

    public IReadOnlyList<int> GenerateCandidateStartMinutes(
        TechnicianEntity technician,
        List<TaskEntity> dayTasks,
        DateTime targetDate,
        int timezoneOffsetMinutes,
        int durationMinutes,
        ServiceObjectEntity serviceObject,
        IReadOnlyDictionary<(long From, long To), int> routeLookup)
    {
        var slotSize = ScheduleGrid.SlotSizeMinutes;
        var window = SchedulePlacementValidator.GetWorkingWindow(technician.Id, targetDate);
        var earliestAllowed = _timeService.GetEarliestCreatableStartMinutes(
            targetDate,
            timezoneOffsetMinutes,
            slotSize,
            ScheduleGrid.DayStartMinutes);
        var candidates = new HashSet<int>();
        var ordered = dayTasks
            .Where(t => WorkloadCalculator.CountsTowardsWorkload(t.Status))
            .OrderBy(t => t.StartAt)
            .ToList();

        if (ordered.Count == 0)
        {
            var firstStart = Math.Max(
                earliestAllowed,
                ScheduleGrid.DayStartMinutes + GetTravelMinutes(
                    routeLookup,
                    technician.WorkplaceAddressId,
                    serviceObject.AddressId));

            if (FitsWithinWindow(firstStart, durationMinutes, window.EndMinutes))
            {
                candidates.Add(firstStart);
            }

            return candidates.OrderBy(x => x).ToList();
        }

        var first = ordered[0];
        if (IsSlotFeasible(
                candidateStartMinutes: earliestAllowed,
                durationMinutes: durationMinutes,
                previousTask: null,
                nextTask: first,
                technician: technician,
                serviceObject: serviceObject,
                routeLookup: routeLookup,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                windowEndMinutes: window.EndMinutes))
        {
            candidates.Add(earliestAllowed);
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var previous = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1] : null;
            var previousEndMinutes = LocalMinutes(previous.EndAt, timezoneOffsetMinutes);
            var previousTravelMinutes = GetTravelMinutes(
                routeLookup,
                previous.ServiceObject?.AddressId ?? 0,
                serviceObject.AddressId);
            var candidateStartMinutes = _timeService.CeilingToSlot(
                previousEndMinutes + previousTravelMinutes,
                slotSize);

            if (candidateStartMinutes < earliestAllowed)
            {
                candidateStartMinutes = earliestAllowed;
            }

            if (IsSlotFeasible(
                    candidateStartMinutes,
                    durationMinutes,
                    previous,
                    next,
                    technician,
                    serviceObject,
                    routeLookup,
                    timezoneOffsetMinutes,
                    window.EndMinutes))
            {
                candidates.Add(candidateStartMinutes);
            }
        }

        return candidates.OrderBy(x => x).ToList();
    }

    private bool IsSlotFeasible(
        int candidateStartMinutes,
        int durationMinutes,
        TaskEntity? previousTask,
        TaskEntity? nextTask,
        TechnicianEntity technician,
        ServiceObjectEntity serviceObject,
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        int timezoneOffsetMinutes,
        int windowEndMinutes)
    {
        if (!FitsWithinWindow(candidateStartMinutes, durationMinutes, windowEndMinutes))
        {
            return false;
        }

        var inboundFromAddressId = previousTask?.ServiceObject?.AddressId ?? technician.WorkplaceAddressId;
        var requiredInboundTravel = GetTravelMinutes(
            routeLookup,
            inboundFromAddressId,
            serviceObject.AddressId);

        if (previousTask != null)
        {
            var previousEndMinutes = LocalMinutes(previousTask.EndAt, timezoneOffsetMinutes);
            if (candidateStartMinutes < previousEndMinutes + requiredInboundTravel)
            {
                return false;
            }
        }
        else if (candidateStartMinutes < ScheduleGrid.DayStartMinutes + requiredInboundTravel)
        {
            return false;
        }

        if (nextTask == null)
        {
            return true;
        }

        var nextStartMinutes = LocalMinutes(nextTask.StartAt, timezoneOffsetMinutes);
        var requiredOutboundTravel = GetTravelMinutes(
            routeLookup,
            serviceObject.AddressId,
            nextTask.ServiceObject?.AddressId ?? 0);

        return candidateStartMinutes + durationMinutes + requiredOutboundTravel <= nextStartMinutes;
    }

    private static bool FitsWithinWindow(int candidateStartMinutes, int durationMinutes, int windowEndMinutes) =>
        candidateStartMinutes >= ScheduleGrid.DayStartMinutes &&
        candidateStartMinutes + durationMinutes <= windowEndMinutes;

    private int LocalMinutes(DateTime utcDateTime, int timezoneOffsetMinutes) =>
        (int)_timeService.ConvertUtcToLocal(utcDateTime, timezoneOffsetMinutes).TimeOfDay.TotalMinutes;

    private static int GetTravelMinutes(
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        long fromAddressId,
        long toAddressId)
    {
        if (fromAddressId <= 0 || toAddressId <= 0 || fromAddressId == toAddressId)
        {
            return 0;
        }

        return routeLookup.TryGetValue((fromAddressId, toAddressId), out var minutes)
            ? minutes
            : 0;
    }
}
