using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using ServiceTaskPlanningSystem.Models.Planning;
using ServiceTaskPlanningSystem.Services.PlanManagement.Matching;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator;

internal sealed class PlanServiceAssignmentMatching
{
    private const int SearchHorizonDays = 5;

    private readonly ScheduleDbContext _db;
    private readonly IScheduleTimeService _timeService;
    private readonly SchedulePlacementValidator _placementValidator;
    private readonly ITravelTimeResolver _travelResolver;
    private readonly CandidateEligibilityEvaluator _eligibilityEvaluator;
    private readonly CandidateLocalitySelector _localitySelector;
    private readonly CandidateDateWindowGenerator _dateWindowGenerator;
    private readonly CandidateSlotGenerator _slotGenerator;
    private readonly CandidateScoreCalculator _scoreCalculator;
    private readonly CandidateRankingService _rankingService;
    private readonly ILogger<PlanServiceAssignmentMatching> _logger;

    public PlanServiceAssignmentMatching(
        ScheduleDbContext db,
        IScheduleTimeService timeService,
        SchedulePlacementValidator placementValidator,
        ITravelTimeResolver travelResolver,
        CandidateEligibilityEvaluator eligibilityEvaluator,
        CandidateLocalitySelector localitySelector,
        CandidateDateWindowGenerator dateWindowGenerator,
        CandidateSlotGenerator slotGenerator,
        CandidateScoreCalculator scoreCalculator,
        CandidateRankingService rankingService,
        ILogger<PlanServiceAssignmentMatching> logger)
    {
        _db = db;
        _timeService = timeService;
        _placementValidator = placementValidator;
        _travelResolver = travelResolver;
        _eligibilityEvaluator = eligibilityEvaluator;
        _localitySelector = localitySelector;
        _dateWindowGenerator = dateWindowGenerator;
        _slotGenerator = slotGenerator;
        _scoreCalculator = scoreCalculator;
        _rankingService = rankingService;
        _logger = logger;
    }

    public async Task ApplyAutomaticAssignmentsAsync(
        List<CreateTaskRequestDto> items,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("svc.plan.auto-assign.start count={Count}", items.Count);
        var itemsProcessed = 0;
        var itemsSkipped = 0;
        var sortedIndexes = Enumerable.Range(0, items.Count)
            .OrderByDescending(idx => (int)items[idx].Priority)
            .ToList();
        var batchAssignmentCounts = new Dictionary<long, int>();
        var accumulatedSimulatedAssignments = new List<SimulatedTaskAssignmentDto>();

        for (var si = 0; si < sortedIndexes.Count; si++)
        {
            var i = sortedIndexes[si];
            var item = items[i];

            if (item.TechnicianId > 0)
            {
                itemsSkipped++;
                continue;
            }

            if (!DateTime.TryParseExact(
                    item.TargetDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var targetDate))
            {
                _logger.LogError("svc.plan.auto-assign.date-parse-failed index={Index} date={Date}", i, item.TargetDate);
                itemsSkipped++;
                continue;
            }

            _logger.LogInformation(
                "svc.plan.auto-assign.item.start index={Index} requestedDate={RequestedDate} startMinutes={StartMinutes} endMinutes={EndMinutes} durationMinutes={DurationMinutes} objectName={ObjectName} priority={Priority}",
                i,
                item.TargetDate,
                item.StartMinutes,
                item.EndMinutes,
                item.EndMinutes - item.StartMinutes,
                item.ObjectName,
                item.Priority);

            try
            {
                var requestSimulatedAssignments = item.SimulatedAssignments ?? new List<SimulatedTaskAssignmentDto>();
                var combinedSimulatedAssignments = requestSimulatedAssignments
                    .Concat(accumulatedSimulatedAssignments)
                    .ToList();

                var suggestion = await SuggestAutoAssignmentAsync(
                    targetDate,
                    item.EndMinutes - item.StartMinutes,
                    item.ObjectName,
                    item.Priority,
                    item.TimezoneOffsetMinutes,
                    combinedSimulatedAssignments,
                    batchAssignmentCounts,
                    cancellationToken);

                if (suggestion == null)
                {
                    _logger.LogWarning("svc.plan.auto-assign.no-suggestion index={Index}", i);
                    itemsSkipped++;
                    continue;
                }

                _logger.LogInformation(
                    "svc.plan.auto-assign.item.selected index={Index} technicianId={TechnicianId} targetDate={TargetDate} startMinutes={StartMinutes} endMinutes={EndMinutes}",
                    i,
                    suggestion.TechnicianId,
                    suggestion.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    suggestion.StartMinutes,
                    suggestion.EndMinutes);

                item.TechnicianId = suggestion.TechnicianId;
                item.TargetDate = suggestion.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                item.StartMinutes = suggestion.StartMinutes;
                item.EndMinutes = suggestion.EndMinutes;

                batchAssignmentCounts[suggestion.TechnicianId] =
                    batchAssignmentCounts.GetValueOrDefault(suggestion.TechnicianId) + 1;

                var resolvedServiceObject = await _db.ServiceObjects
                    .AsNoTracking()
                    .FirstOrDefaultAsync(so => so.Name == item.ObjectName, cancellationToken);

                if (resolvedServiceObject != null)
                {
                    accumulatedSimulatedAssignments.Add(new SimulatedTaskAssignmentDto
                    {
                        TechnicianId = suggestion.TechnicianId,
                        StartAt = suggestion.TargetDate.Date.AddMinutes(suggestion.StartMinutes).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                        EndAt = suggestion.TargetDate.Date.AddMinutes(suggestion.EndMinutes).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                        AddressId = resolvedServiceObject.AddressId,
                        ServiceObjectId = resolvedServiceObject.Id,
                        SourceBacklogItemId = item.SourceBacklogItemId
                    });
                }
                itemsProcessed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "svc.plan.auto-assign.exception index={Index}", i);
                itemsSkipped++;
            }
        }

        _logger.LogInformation("svc.plan.auto-assign.done processed={Processed} skipped={Skipped}", itemsProcessed, itemsSkipped);
    }

    public async Task<AutoAssignmentSuggestionDto?> SuggestAutoAssignmentAsync(
        DateTime targetDate,
        int durationMinutes,
        string objectName,
        TaskPriority priority,
        int timezoneOffsetMinutes,
        List<SimulatedTaskAssignmentDto>? simulatedAssignments,
        Dictionary<long, int>? batchAssignmentCounts,
        CancellationToken cancellationToken = default)
    {
        if (durationMinutes <= 0 || string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        var earliestPlacement = _timeService.GetEarliestCreatablePlacement(
            targetDate,
            timezoneOffsetMinutes,
            ScheduleGrid.SlotSizeMinutes,
            ScheduleGrid.DayStartMinutes,
            ScheduleGrid.DayEndMinutes);
        var requestedDate = earliestPlacement.TargetDate;

        _logger.LogInformation(
            "svc.plan.auto-assign.request objectName={ObjectName} inputDate={InputDate} normalizedDate={NormalizedDate} normalizedStartMinutes={NormalizedStartMinutes} durationMinutes={DurationMinutes} priority={Priority}",
            objectName,
            targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            earliestPlacement.StartMinutes,
            durationMinutes,
            priority);

        var technicians = await _db.Technicians
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.WorkplaceAddress)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var eligibleTechnicians = _eligibilityEvaluator.GetEligibleTechnicians(technicians, requiredSkillLevel: string.Empty);
        if (eligibleTechnicians.Count == 0)
        {
            return null;
        }

        var serviceObject = await _db.ServiceObjects
            .AsNoTracking()
            .Include(so => so.Address)
            .FirstOrDefaultAsync(so => so.Name == objectName, cancellationToken);

        if (serviceObject == null)
        {
            return null;
        }

        var localityPool = _localitySelector.SelectPool(eligibleTechnicians, serviceObject);
        _logger.LogInformation(
            "svc.plan.auto-assign.pool objectName={ObjectName} locality={Locality} localityTier={LocalityTier} technicianCount={TechnicianCount}",
            objectName,
            localityPool.Label,
            localityPool.Tier,
            localityPool.Technicians.Count);
        var candidateDates = _dateWindowGenerator.GenerateDates(
            requestedDate,
            requestedDate.AddDays(SearchHorizonDays - 1),
            SearchHorizonDays);
        if (candidateDates.Count == 0)
        {
            return null;
        }

        var firstCandidateDate = candidateDates[0];
        var lastCandidateDate = candidateDates[^1];
        var loadRangeStart = _timeService.GetWeekStart(firstCandidateDate);
        var loadRangeEndExclusive = _timeService.GetWeekEnd(lastCandidateDate).AddDays(1);

        var persistedWeekTasks = await _db.Tasks
            .AsNoTracking()
            .Include(t => t.ServiceObject)
            .Include(t => t.Technician)
                .ThenInclude(t => t.User)
            .Where(t => t.StartAt >= loadRangeStart &&
                        t.StartAt < loadRangeEndExclusive)
            .ToListAsync(cancellationToken);

        var simulatedTasks = await BuildSimulatedTasks(simulatedAssignments, timezoneOffsetMinutes, cancellationToken);
        var allWeekTasks = persistedWeekTasks
            .Concat(simulatedTasks)
            .ToList();

        var addressIds = allWeekTasks
            .Where(t => t.ServiceObject != null)
            .Select(t => t.ServiceObject.AddressId)
            .Concat(localityPool.Technicians.Select(t => t.WorkplaceAddressId))
            .Append(serviceObject.AddressId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken)
            ?? new Dictionary<(long From, long To), int>();
        var nextTempTaskId = NextTemporaryTaskId(allWeekTasks);

        var sameDayFeasibleCount = 0;

        foreach (var candidateDate in candidateDates)
        {
            var dayStart = candidateDate.Date;
            var rankedCandidates = new List<RankedTechnicianCandidate>();

            foreach (var technician in localityPool.Technicians)
            {
                var technicianDayTasks = allWeekTasks
                    .Where(t => t.TechnicianId == technician.Id &&
                                t.StartAt >= dayStart &&
                                t.StartAt < dayStart.AddDays(1) &&
                                WorkloadCalculator.CountsTowardsWorkload(t.Status))
                    .OrderBy(t => t.StartAt)
                    .ToList();

                var candidateStarts = _slotGenerator.GenerateCandidateStartMinutes(
                    technician,
                    technicianDayTasks,
                    dayStart,
                    timezoneOffsetMinutes,
                    durationMinutes,
                    serviceObject,
                    routeLookup);

                var simulatedStops = BuildSimulatedStops(simulatedTasks, technician.Id, dayStart);

                foreach (var candidateStartMinutes in candidateStarts)
                {
                    var candidateStartAt = _timeService.CreateUtcFromDateAndMinutes(
                        dayStart,
                        candidateStartMinutes,
                        timezoneOffsetMinutes);
                    var candidateEndAt = candidateStartAt.AddMinutes(durationMinutes);

                    var validation = await _placementValidator.ValidateTimingAndTravelAsync(
                        taskDateLocal: dayStart,
                        startMinutesLocal: candidateStartMinutes,
                        timezoneOffsetMinutes: timezoneOffsetMinutes,
                        technicianId: technician.Id,
                        startAtUtc: candidateStartAt,
                        endAtUtc: candidateEndAt,
                        serviceAddressId: serviceObject.AddressId,
                        excludeTaskId: null,
                        cancellationToken: cancellationToken,
                        simulatedStops: simulatedStops,
                        checkOverlap: true);

                    if (!validation.IsValid)
                    {
                        continue;
                    }

                    var candidateTasks = allWeekTasks.ToList();
                    candidateTasks.Add(new TaskEntity
                    {
                        Id = nextTempTaskId--,
                        PlanId = 0,
                        TechnicianId = technician.Id,
                        ServiceObjectId = serviceObject.Id,
                        ServiceObject = serviceObject,
                        Technician = technician,
                        Title = string.Empty,
                        StartAt = candidateStartAt,
                        EndAt = candidateEndAt,
                        Priority = priority,
                        Status = ScheduleTaskStatus.InProgress,
                        CreatedAt = DateTime.UtcNow
                    });

                    rankedCandidates.Add(_scoreCalculator.BuildRankedCandidate(
                        technician,
                        requestedDate,
                        dayStart,
                        dayStart,
                        candidateTasks,
                        routeLookup,
                        batchAssignmentCounts?.GetValueOrDefault(technician.Id) ?? 0,
                        localityPool.Tier,
                        candidateStartMinutes,
                        candidateStartMinutes + durationMinutes));
                }
            }

            if (dayStart == requestedDate.Date)
            {
                sameDayFeasibleCount = rankedCandidates.Count;
            }

            var bestForDay = _rankingService.SelectBest(rankedCandidates);
            if (bestForDay != null)
            {
                _logger.LogInformation(
                    "svc.plan.auto-assign.decision object={ObjectName} requestedDate={RequestedDate} selectedDate={SelectedDate} technicianId={TechnicianId} technicianName={TechnicianName} locality={Locality} startMinutes={StartMinutes} endMinutes={EndMinutes} totalScore={TotalScore:F2} loadScore={LoadScore:F2} travelPenalty={TravelPenalty:F2} batchPenalty={BatchPenalty:F2} delayPenalty={DelayPenalty:F2} localityPenalty={LocalityPenalty:F2} spreadPenalty={SpreadPenalty:F2} candidateCountForSelectedDate={CandidateCountForSelectedDate} sameDayFeasibleCount={SameDayFeasibleCount}",
                    objectName,
                    requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    bestForDay.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    bestForDay.Technician.Id,
                    bestForDay.Technician.User?.Name ?? $"Technician #{bestForDay.Technician.Id}",
                    localityPool.Label,
                    bestForDay.StartMinutes,
                    bestForDay.EndMinutes,
                    bestForDay.ScoreBreakdown.TotalScore,
                    bestForDay.ScoreBreakdown.LoadScore,
                    bestForDay.ScoreBreakdown.TravelPenalty,
                    bestForDay.ScoreBreakdown.BatchPenalty,
                    bestForDay.ScoreBreakdown.DelayPenalty,
                    bestForDay.ScoreBreakdown.LocalityPenalty,
                    bestForDay.ScoreBreakdown.SpreadPenalty,
                    rankedCandidates.Count,
                    sameDayFeasibleCount);

                return new AutoAssignmentSuggestionDto
                {
                    TechnicianId = bestForDay.Technician.Id,
                    TechnicianName = bestForDay.Technician.User?.Name ?? $"Technician #{bestForDay.Technician.Id}",
                    TargetDate = bestForDay.TargetDate,
                    StartMinutes = bestForDay.StartMinutes,
                    EndMinutes = bestForDay.EndMinutes
                };
            }

            _logger.LogInformation(
                "svc.plan.auto-assign.day-exhausted object={ObjectName} requestedDate={RequestedDate} searchedDate={SearchedDate} locality={Locality} feasibleCandidateCount={FeasibleCandidateCount} reason=no-feasible-candidates",
                objectName,
                requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dayStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                localityPool.Label,
                rankedCandidates.Count);
        }

        _logger.LogWarning(
            "svc.plan.auto-assign.no-match object={ObjectName} requestedDate={RequestedDate} horizonDays={HorizonDays} locality={Locality}",
            objectName,
            requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SearchHorizonDays,
            localityPool.Label);

        return null;
    }

    private async Task<List<TaskEntity>> BuildSimulatedTasks(
        IEnumerable<SimulatedTaskAssignmentDto>? simulatedAssignments,
        int timezoneOffsetMinutes,
        CancellationToken cancellationToken)
    {
        var tasks = new List<TaskEntity>();
        if (simulatedAssignments == null)
        {
            return tasks;
        }

        var nextTaskId = -1L;
        long nextServiceObjectId = -1;

        foreach (var assignment in simulatedAssignments)
        {
            var addressId = assignment.AddressId;
            if (addressId <= 0 && assignment.ServiceObjectId.HasValue && assignment.ServiceObjectId.Value > 0)
            {
                addressId = await _db.ServiceObjects
                    .AsNoTracking()
                    .Where(o => o.Id == assignment.ServiceObjectId.Value)
                    .Select(o => o.AddressId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (assignment.TechnicianId <= 0 ||
                addressId <= 0 ||
                !TryParseSimulatedDateTime(assignment.StartAt, timezoneOffsetMinutes, out var startAtUtc) ||
                !TryParseSimulatedDateTime(assignment.EndAt, timezoneOffsetMinutes, out var endAtUtc) ||
                endAtUtc <= startAtUtc)
            {
                continue;
            }

            var serviceObjectId = assignment.ServiceObjectId ?? nextServiceObjectId--;
            tasks.Add(new TaskEntity
            {
                Id = nextTaskId--,
                TechnicianId = assignment.TechnicianId,
                ServiceObjectId = serviceObjectId,
                ServiceObject = new ServiceObjectEntity
                {
                    Id = serviceObjectId,
                    AddressId = addressId,
                    Name = string.Empty
                },
                Title = string.Empty,
                StartAt = startAtUtc,
                EndAt = endAtUtc,
                Priority = TaskPriority.Medium,
                Status = ScheduleTaskStatus.InProgress,
                CreatedAt = DateTime.UtcNow,
                SourceBacklogItemId = assignment.SourceBacklogItemId
            });
        }

        return tasks;
    }

    private static IReadOnlyList<ProposedRouteStop> BuildSimulatedStops(
        IEnumerable<TaskEntity> simulatedTasks,
        long technicianId,
        DateTime dayStartUtc)
    {
        var dayEndUtc = dayStartUtc.AddDays(1);

        return simulatedTasks
            .Where(t => t.TechnicianId == technicianId &&
                        t.StartAt >= dayStartUtc &&
                        t.StartAt < dayEndUtc &&
                        t.ServiceObject != null &&
                        WorkloadCalculator.CountsTowardsWorkload(t.Status))
            .OrderBy(t => t.StartAt)
            .Select(t => new ProposedRouteStop
            {
                StartAt = t.StartAt,
                EndAt = t.EndAt,
                AddressId = t.ServiceObject!.AddressId
            })
            .ToList();
    }

    private static long NextTemporaryTaskId(IEnumerable<TaskEntity> tasks)
    {
        var minExistingId = tasks.Select(t => t.Id).DefaultIfEmpty(0).Min();
        return Math.Min(-1, minExistingId - 1);
    }

    private bool TryParseSimulatedDateTime(string value, int timezoneOffsetMinutes, out DateTime parsedUtc)
    {
        var formats = new[]
        {
            "yyyy-MM-dd'T'HH:mm",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.FFF",
            "O"
        };

        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            parsedUtc = parsed.Kind == DateTimeKind.Utc
                ? parsed
                : _timeService.ConvertLocalToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), timezoneOffsetMinutes);
            return true;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out parsed))
        {
            parsedUtc = parsed.Kind == DateTimeKind.Utc
                ? parsed
                : _timeService.ConvertLocalToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), timezoneOffsetMinutes);
            return true;
        }

        parsedUtc = default;
        return false;
    }
}
