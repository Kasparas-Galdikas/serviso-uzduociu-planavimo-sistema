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

    /// <param name="blockedWindows">
    /// Technician unavailability windows for the target day expressed in
    /// local-time minutes (e.g. 660–780 for 11:00–13:00).  Pass an empty
    /// list when there are no blocked periods.
    /// </param>
    public IReadOnlyList<int> GenerateCandidateStartMinutes(
        TechnicianEntity technician,
        List<TaskEntity> dayTasks,
        DateTime targetDate,
        int timezoneOffsetMinutes,
        int durationMinutes,
        ServiceObjectEntity serviceObject,
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        IReadOnlyList<(int StartMinutes, int EndMinutes)> blockedWindows)
    {
        var slotSize = ScheduleGrid.SlotSizeMinutes;
        var window = SchedulePlacementValidator.GetWorkingWindow(technician.Id, targetDate);
        var earliestAllowed = _timeService.GetEarliestCreatablePlacement(
            targetDate,
            timezoneOffsetMinutes,
            slotSize,
            ScheduleGrid.DayStartMinutes,
            window.EndMinutes).StartMinutes;
        var userLocalNow = _timeService.ConvertUtcToLocal(_timeService.GetCurrentUtcTime(), timezoneOffsetMinutes);
        var isTargetToday = targetDate.Date == userLocalNow.Date;
        var nowLocalMinutes = (int)userLocalNow.TimeOfDay.TotalMinutes;
        var candidates = new HashSet<int>();
        var ordered = dayTasks
            .Where(t => WorkloadCalculator.CountsTowardsWorkload(t.Status))
            .OrderBy(t => t.StartAt)
            .ToList();

        if (ordered.Count == 0)
        {
            var workplaceTravel = GetTravelMinutes(routeLookup, technician.WorkplaceAddressId, serviceObject.AddressId);
            var firstStart = Math.Max(
                earliestAllowed,
                _timeService.CeilingToSlot(ScheduleGrid.DayStartMinutes + workplaceTravel, slotSize));
            if (isTargetToday)
            {
                var travelFloor = _timeService.CeilingToSlot(nowLocalMinutes + workplaceTravel, slotSize);
                firstStart = Math.Max(firstStart, travelFloor);
            }

            if (FitsWithinWindow(firstStart, durationMinutes, window.EndMinutes) &&
                !OverlapsAnyBlock(firstStart, durationMinutes, blockedWindows))
            {
                candidates.Add(firstStart);
            }

            AddAfterBlockCandidates(
                candidates, blockedWindows, ordered, technician, serviceObject,
                routeLookup, earliestAllowed, isTargetToday, nowLocalMinutes,
                timezoneOffsetMinutes, durationMinutes, window.EndMinutes, slotSize);

            return candidates.OrderBy(x => x).ToList();
        }

        var first = ordered[0];
        var firstTaskInboundTravel = GetTravelMinutes(routeLookup, technician.WorkplaceAddressId, serviceObject.AddressId);
        var beforeFirstCandidate = earliestAllowed;
        if (isTargetToday)
        {
            var travelFloor = _timeService.CeilingToSlot(nowLocalMinutes + firstTaskInboundTravel, slotSize);
            beforeFirstCandidate = Math.Max(beforeFirstCandidate, travelFloor);
        }

        if (IsSlotFeasible(
                candidateStartMinutes: beforeFirstCandidate,
                durationMinutes: durationMinutes,
                previousTask: null,
                nextTask: first,
                technician: technician,
                serviceObject: serviceObject,
                routeLookup: routeLookup,
                timezoneOffsetMinutes: timezoneOffsetMinutes,
                windowEndMinutes: window.EndMinutes,
                blockedWindows: blockedWindows))
        {
            candidates.Add(beforeFirstCandidate);
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

            if (isTargetToday)
            {
                var travelFloor = _timeService.CeilingToSlot(nowLocalMinutes + previousTravelMinutes, slotSize);
                if (candidateStartMinutes < travelFloor)
                {
                    candidateStartMinutes = travelFloor;
                }
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
                    window.EndMinutes,
                    blockedWindows))
            {
                candidates.Add(candidateStartMinutes);
            }
        }

        AddAfterBlockCandidates(
            candidates, blockedWindows, ordered, technician, serviceObject,
            routeLookup, earliestAllowed, isTargetToday, nowLocalMinutes,
            timezoneOffsetMinutes, durationMinutes, window.EndMinutes, slotSize);

        return candidates.OrderBy(x => x).ToList();
    }

    /// <summary>
    /// For each blocked window, generates a candidate slot starting immediately
    /// after the block ends (accounting for inbound travel).  This ensures the
    /// algorithm considers the time right after unavailability as a placement option,
    /// even when no existing task borders that gap.
    /// </summary>
    private void AddAfterBlockCandidates(
        HashSet<int> candidates,
        IReadOnlyList<(int StartMinutes, int EndMinutes)> blockedWindows,
        List<TaskEntity> ordered,
        TechnicianEntity technician,
        ServiceObjectEntity serviceObject,
        IReadOnlyDictionary<(long From, long To), int> routeLookup,
        int earliestAllowed,
        bool isTargetToday,
        int nowLocalMinutes,
        int timezoneOffsetMinutes,
        int durationMinutes,
        int windowEndMinutes,
        int slotSize)
    {
        foreach (var block in blockedWindows)
        {
            // Last task that ends at or before the block starts
            var prevTask = ordered.LastOrDefault(
                t => LocalMinutes(t.EndAt, timezoneOffsetMinutes) <= block.StartMinutes);

            // First task that starts at or after the block ends
            var nextTask = ordered.FirstOrDefault(
                t => LocalMinutes(t.StartAt, timezoneOffsetMinutes) >= block.EndMinutes);

            var inboundFromAddressId = prevTask?.ServiceObject?.AddressId ?? technician.WorkplaceAddressId;
            var travel = GetTravelMinutes(routeLookup, inboundFromAddressId, serviceObject.AddressId);

            var afterBlock = _timeService.CeilingToSlot(block.EndMinutes, slotSize);
            var afterBlockWithTravel = _timeService.CeilingToSlot(block.EndMinutes + travel, slotSize);
            var candidateStart = Math.Max(afterBlock, afterBlockWithTravel);

            if (candidateStart < earliestAllowed)
            {
                candidateStart = earliestAllowed;
            }

            if (isTargetToday)
            {
                var travelFloor = _timeService.CeilingToSlot(nowLocalMinutes + travel, slotSize);
                if (candidateStart < travelFloor)
                {
                    candidateStart = travelFloor;
                }
            }

            if (IsSlotFeasible(
                    candidateStart,
                    durationMinutes,
                    prevTask,
                    nextTask,
                    technician,
                    serviceObject,
                    routeLookup,
                    timezoneOffsetMinutes,
                    windowEndMinutes,
                    blockedWindows))
            {
                candidates.Add(candidateStart);
            }
        }
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
        int windowEndMinutes,
        IReadOnlyList<(int StartMinutes, int EndMinutes)> blockedWindows)
    {
        if (!FitsWithinWindow(candidateStartMinutes, durationMinutes, windowEndMinutes))
        {
            return false;
        }

        if (OverlapsAnyBlock(candidateStartMinutes, durationMinutes, blockedWindows))
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

    private static bool OverlapsAnyBlock(
        int candidateStartMinutes,
        int durationMinutes,
        IReadOnlyList<(int StartMinutes, int EndMinutes)> blockedWindows)
    {
        var candidateEnd = candidateStartMinutes + durationMinutes;
        foreach (var block in blockedWindows)
        {
            if (candidateStartMinutes < block.EndMinutes && candidateEnd > block.StartMinutes)
            {
                return true;
            }
        }

        return false;
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
