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
using ServiceTaskPlanningSystem.Services.Unavailability;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator;

internal sealed class PlanServiceAssignmentMatching
{
    private const int SearchHorizonDays = 5;

    private sealed record AssignmentContextRequest(DateTime TargetDate, string ObjectName, int TimezoneOffsetMinutes);

    private sealed class AssignmentMatchingContext
    {
        public required List<TechnicianEntity> Technicians { get; init; }
        public required Dictionary<string, ServiceObjectEntity> ServiceObjectsByName { get; init; }
        public required List<TaskEntity> PersistedWeekTasks { get; init; }
        public required Dictionary<(long From, long To), int> RouteLookup { get; init; }
        public required HashSet<long> RouteLookupAddressIds { get; init; }
        public required DateTime LoadRangeStart { get; init; }
        public required DateTime LoadRangeEndExclusive { get; init; }
        public required Dictionary<long, List<TechnicianBlockedInterval>> BlockedIntervalsByTechnician { get; init; }
    }

    /// <summary>
    /// Holds incrementally-updated state shared across all items in a bulk assignment loop.
    /// Eliminates the O(n²) cost of rebuilding allWeekTasks, workload groupings, and
    /// simulated-stop groupings from scratch for every item in the batch.
    /// </summary>
    private sealed class BulkMatchingState
    {
        private long _nextTempTaskId;

        public List<TaskEntity> AllWeekTasks { get; }
        public Dictionary<(long TechnicianId, DateTime Day), List<TaskEntity>> WorkloadByTechnicianDay { get; }
        public Dictionary<(long TechnicianId, DateTime Day), List<ProposedRouteStop>> SimulatedStopsByTechnicianDay { get; }
        public HashSet<long> KnownAddressIds { get; }

        public BulkMatchingState(List<TaskEntity> persistedTasks, List<TechnicianEntity> technicians)
        {
            AllWeekTasks = new List<TaskEntity>(persistedTasks);
            WorkloadByTechnicianDay = persistedTasks
                .Where(t => WorkloadCalculator.CountsTowardsWorkload(t.Status))
                .GroupBy(t => (t.TechnicianId, Day: t.StartAt.Date))
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.StartAt).ToList());
            SimulatedStopsByTechnicianDay = new Dictionary<(long, DateTime), List<ProposedRouteStop>>();
            KnownAddressIds = persistedTasks
                .Where(t => t.ServiceObject != null)
                .Select(t => t.ServiceObject!.AddressId)
                .Concat(technicians.Select(t => t.WorkplaceAddressId))
                .Where(id => id > 0)
                .ToHashSet();
            var minId = persistedTasks.Count > 0 ? persistedTasks.Min(t => (long?)t.Id) ?? 0L : 0L;
            _nextTempTaskId = Math.Min(-1L, minId - 1L);
        }

        /// <summary>Returns the next available temp ID without consuming it.</summary>
        public long PeekNextTempId() => _nextTempTaskId;

        /// <summary>Returns and consumes the next available temp ID.</summary>
        public long AllocateTempId() => _nextTempTaskId--;

        /// <summary>
        /// Appends a newly-decided simulated task and updates all groupings in O(1).
        /// Call this after each successful suggestion in the bulk loop.
        /// </summary>
        public void AppendSimulatedTask(TaskEntity task)
        {
            AllWeekTasks.Add(task);

            if (!WorkloadCalculator.CountsTowardsWorkload(task.Status))
                return;

            var key = (task.TechnicianId, task.StartAt.Date);

            if (!WorkloadByTechnicianDay.TryGetValue(key, out var workloadList))
            {
                workloadList = new List<TaskEntity>();
                WorkloadByTechnicianDay[key] = workloadList;
            }

            workloadList.Add(task);
            // Keep sorted by StartAt; new tasks usually land at the end, but sort defensively
            if (workloadList.Count > 1 && task.StartAt < workloadList[^2].StartAt)
                workloadList.Sort((a, b) => a.StartAt.CompareTo(b.StartAt));

            if (task.ServiceObject == null)
                return;

            KnownAddressIds.Add(task.ServiceObject.AddressId);

            if (!SimulatedStopsByTechnicianDay.TryGetValue(key, out var stops))
            {
                stops = new List<ProposedRouteStop>();
                SimulatedStopsByTechnicianDay[key] = stops;
            }

            stops.Add(new ProposedRouteStop
            {
                StartAt = task.StartAt,
                EndAt = task.EndAt,
                AddressId = task.ServiceObject.AddressId
            });

            if (stops.Count > 1 && task.StartAt < stops[^2].StartAt)
                stops.Sort((a, b) => a.StartAt.CompareTo(b.StartAt));
        }
    }

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
    private readonly TechnicianUnavailabilityReader _unavailabilityReader;
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
        TechnicianUnavailabilityReader unavailabilityReader,
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
        _unavailabilityReader = unavailabilityReader;
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
        var sharedContext = await BuildAssignmentMatchingContextAsync(
            sortedIndexes
                .Select(index => TryBuildAssignmentContextRequest(items[index]))
                .Where(request => request != null)
                .Select(request => request!)
                .ToList(),
            cancellationToken);
        var bulkState = sharedContext != null
            ? new BulkMatchingState(sharedContext.PersistedWeekTasks, sharedContext.Technicians)
            : null;

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
                // When bulkState is active, accumulated assignments live there; only pass
                // item-specific simulated assignments to avoid rebuilding the accumulated list.
                var simulatedAssignmentsForCore = bulkState != null
                    ? item.SimulatedAssignments
                    : (item.SimulatedAssignments ?? new List<SimulatedTaskAssignmentDto>())
                        .Concat(accumulatedSimulatedAssignments)
                        .ToList();

                var suggestion = await SuggestAutoAssignmentCoreAsync(
                    targetDate,
                    item.EndMinutes - item.StartMinutes,
                    item.ObjectName,
                    item.Priority,
                    item.TimezoneOffsetMinutes,
                    simulatedAssignmentsForCore,
                    batchAssignmentCounts,
                    cancellationToken,
                    sharedContext,
                    bulkState);

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

                var resolvedServiceObject = TryResolveServiceObject(sharedContext, item.ObjectName);

                if (bulkState != null)
                {
                    var startAtUtc = _timeService.CreateUtcFromDateAndMinutes(
                        suggestion.TargetDate.Date,
                        suggestion.StartMinutes,
                        item.TimezoneOffsetMinutes);
                    var endAtUtc = _timeService.CreateUtcFromDateAndMinutes(
                        suggestion.TargetDate.Date,
                        suggestion.EndMinutes,
                        item.TimezoneOffsetMinutes);
                    bulkState.AppendSimulatedTask(new TaskEntity
                    {
                        Id = bulkState.AllocateTempId(),
                        TechnicianId = suggestion.TechnicianId,
                        ServiceObjectId = resolvedServiceObject?.Id ?? 0,
                        ServiceObject = resolvedServiceObject!,
                        Title = string.Empty,
                        StartAt = startAtUtc,
                        EndAt = endAtUtc,
                        Priority = TaskPriority.Medium,
                        Status = ScheduleTaskStatus.InProgress,
                        CreatedAt = DateTime.UtcNow,
                        SourceBacklogItemId = item.SourceBacklogItemId
                    });
                }
                else if (resolvedServiceObject != null)
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

    public async Task<IReadOnlyList<AutoAssignmentSuggestionDto?>> SuggestBulkAutoAssignmentsAsync(
        IReadOnlyList<CreateTaskRequestDto> items,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("svc.plan.auto-assign.bulk-suggest.start count={Count}", items.Count);
        var suggestions = new AutoAssignmentSuggestionDto?[items.Count];
        var sortedIndexes = Enumerable.Range(0, items.Count)
            .OrderByDescending(idx => (int)items[idx].Priority)
            .ToList();
        var batchAssignmentCounts = new Dictionary<long, int>();
        var accumulatedSimulatedAssignments = new List<SimulatedTaskAssignmentDto>();
        var sharedContext = await BuildAssignmentMatchingContextAsync(
            sortedIndexes
                .Select(index => TryBuildAssignmentContextRequest(items[index]))
                .Where(request => request != null)
                .Select(request => request!)
                .ToList(),
            cancellationToken);
        var bulkState = sharedContext != null
            ? new BulkMatchingState(sharedContext.PersistedWeekTasks, sharedContext.Technicians)
            : null;

        for (var si = 0; si < sortedIndexes.Count; si++)
        {
            var i = sortedIndexes[si];
            var item = items[i];

            if (!DateTime.TryParseExact(
                    item.TargetDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var targetDate))
            {
                _logger.LogWarning("svc.plan.auto-assign.bulk-suggest.date-parse-failed index={Index} date={Date}", i, item.TargetDate);
                continue;
            }

            try
            {
                var simulatedAssignmentsForCore = bulkState != null
                    ? item.SimulatedAssignments
                    : (item.SimulatedAssignments ?? new List<SimulatedTaskAssignmentDto>())
                        .Concat(accumulatedSimulatedAssignments)
                        .ToList();

                var suggestion = await SuggestAutoAssignmentCoreAsync(
                    targetDate,
                    item.EndMinutes - item.StartMinutes,
                    item.ObjectName,
                    item.Priority,
                    item.TimezoneOffsetMinutes,
                    simulatedAssignmentsForCore,
                    batchAssignmentCounts,
                    cancellationToken,
                    sharedContext,
                    bulkState);

                if (suggestion == null)
                {
                    _logger.LogWarning("svc.plan.auto-assign.bulk-suggest.no-suggestion index={Index}", i);
                    continue;
                }

                suggestions[i] = suggestion;

                batchAssignmentCounts[suggestion.TechnicianId] =
                    batchAssignmentCounts.GetValueOrDefault(suggestion.TechnicianId) + 1;

                var resolvedServiceObject = TryResolveServiceObject(sharedContext, item.ObjectName);

                if (bulkState != null)
                {
                    var startAtUtc = _timeService.CreateUtcFromDateAndMinutes(
                        suggestion.TargetDate.Date,
                        suggestion.StartMinutes,
                        item.TimezoneOffsetMinutes);
                    var endAtUtc = _timeService.CreateUtcFromDateAndMinutes(
                        suggestion.TargetDate.Date,
                        suggestion.EndMinutes,
                        item.TimezoneOffsetMinutes);
                    bulkState.AppendSimulatedTask(new TaskEntity
                    {
                        Id = bulkState.AllocateTempId(),
                        TechnicianId = suggestion.TechnicianId,
                        ServiceObjectId = resolvedServiceObject?.Id ?? 0,
                        ServiceObject = resolvedServiceObject!,
                        Title = string.Empty,
                        StartAt = startAtUtc,
                        EndAt = endAtUtc,
                        Priority = TaskPriority.Medium,
                        Status = ScheduleTaskStatus.InProgress,
                        CreatedAt = DateTime.UtcNow,
                        SourceBacklogItemId = item.SourceBacklogItemId
                    });
                }
                else if (resolvedServiceObject != null)
                {
                    accumulatedSimulatedAssignments.Add(new SimulatedTaskAssignmentDto
                    {
                        TechnicianId = suggestion.TechnicianId,
                        StartAt = suggestion.TargetDate.Date.AddMinutes(suggestion.StartMinutes)
                            .ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                        EndAt = suggestion.TargetDate.Date.AddMinutes(suggestion.EndMinutes)
                            .ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                        AddressId = resolvedServiceObject.AddressId,
                        ServiceObjectId = resolvedServiceObject.Id,
                        SourceBacklogItemId = item.SourceBacklogItemId
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "svc.plan.auto-assign.bulk-suggest.exception index={Index}", i);
            }
        }

        _logger.LogInformation("svc.plan.auto-assign.bulk-suggest.done found={Found} of={Total}",
            suggestions.Count(s => s != null), items.Count);
        return suggestions;
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
        => await SuggestAutoAssignmentCoreAsync(
            targetDate,
            durationMinutes,
            objectName,
            priority,
            timezoneOffsetMinutes,
            simulatedAssignments,
            batchAssignmentCounts,
            cancellationToken,
            sharedContext: null);

    private async Task<AutoAssignmentSuggestionDto?> SuggestAutoAssignmentCoreAsync(
        DateTime targetDate,
        int durationMinutes,
        string objectName,
        TaskPriority priority,
        int timezoneOffsetMinutes,
        List<SimulatedTaskAssignmentDto>? simulatedAssignments,
        Dictionary<long, int>? batchAssignmentCounts,
        CancellationToken cancellationToken,
        AssignmentMatchingContext? sharedContext,
        BulkMatchingState? bulkState = null)
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

        var technicians = sharedContext?.Technicians
            ?? await _db.Technicians
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

        var serviceObject = TryResolveServiceObject(sharedContext, objectName)
            ?? await _db.ServiceObjects
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

        // Fast path: when BulkMatchingState is available and the item has no item-specific
        // simulated assignments, skip all per-item rebuilds. The accumulated state is already
        // tracked incrementally in bulkState. Falls back to the original path for single-item
        // calls (bulkState == null) and the rare case of item-specific SimulatedAssignments.
        var useBulkState = bulkState != null && (simulatedAssignments == null || simulatedAssignments.Count == 0);

        IReadOnlyList<TaskEntity> allWeekTasks;
        IReadOnlyDictionary<(long From, long To), int> routeLookup;
        long nextTempTaskId;
        Func<long, DateTime, List<TaskEntity>> getWorkloadTasks;
        Func<long, DateTime, IReadOnlyList<ProposedRouteStop>> getSimulatedStops;

        if (useBulkState)
        {
            allWeekTasks = bulkState!.AllWeekTasks;
            var routeAddressIds = bulkState.KnownAddressIds
                .Append(serviceObject.AddressId)
                .Where(id => id > 0)
                .ToList();
            routeLookup = await BuildRouteLookupAsync(sharedContext, routeAddressIds, cancellationToken);
            nextTempTaskId = bulkState.PeekNextTempId();
            getWorkloadTasks = (techId, day) =>
                bulkState.WorkloadByTechnicianDay.GetValueOrDefault((techId, day)) ?? new List<TaskEntity>();
            getSimulatedStops = (techId, day) =>
            {
                return bulkState.SimulatedStopsByTechnicianDay.TryGetValue((techId, day), out var stops)
                    ? (IReadOnlyList<ProposedRouteStop>)stops
                    : Array.Empty<ProposedRouteStop>();
            };
        }
        else
        {
            var persistedWeekTasks = sharedContext != null &&
                                     sharedContext.LoadRangeStart <= loadRangeStart &&
                                     sharedContext.LoadRangeEndExclusive >= loadRangeEndExclusive
                ? sharedContext.PersistedWeekTasks
                    .Where(t => t.StartAt >= loadRangeStart && t.StartAt < loadRangeEndExclusive)
                    .ToList()
                : await _db.Tasks
                    .AsNoTracking()
                    .Include(t => t.ServiceObject)
                    .Include(t => t.Technician)
                        .ThenInclude(t => t.User)
                    .Where(t => t.StartAt >= loadRangeStart &&
                                t.StartAt < loadRangeEndExclusive)
                    .ToListAsync(cancellationToken);

            var simulatedTasks = await BuildSimulatedTasks(
                simulatedAssignments,
                timezoneOffsetMinutes,
                cancellationToken,
                sharedContext?.ServiceObjectsByName);
            var builtAllWeekTasks = persistedWeekTasks.Concat(simulatedTasks).ToList();
            allWeekTasks = builtAllWeekTasks;

            var addressIds = builtAllWeekTasks
                .Where(t => t.ServiceObject != null)
                .Select(t => t.ServiceObject.AddressId)
                .Concat(localityPool.Technicians.Select(t => t.WorkplaceAddressId))
                .Append(serviceObject.AddressId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            routeLookup = await BuildRouteLookupAsync(sharedContext, addressIds, cancellationToken);
            nextTempTaskId = NextTemporaryTaskId(builtAllWeekTasks);

            var workloadTasksByTechnicianDay = builtAllWeekTasks
                .Where(task => WorkloadCalculator.CountsTowardsWorkload(task.Status))
                .GroupBy(task => (task.TechnicianId, Day: task.StartAt.Date))
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(task => task.StartAt).ToList());
            var simulatedStopsByTechnicianDay = simulatedTasks
                .Where(task => task.ServiceObject != null && WorkloadCalculator.CountsTowardsWorkload(task.Status))
                .GroupBy(task => (task.TechnicianId, Day: task.StartAt.Date))
                .ToDictionary(
                    group => group.Key,
                    group => BuildSimulatedStops(group));

            getWorkloadTasks = (techId, day) =>
                workloadTasksByTechnicianDay.GetValueOrDefault((techId, day)) ?? new List<TaskEntity>();
            getSimulatedStops = (techId, day) =>
                simulatedStopsByTechnicianDay.GetValueOrDefault((techId, day)) ?? Array.Empty<ProposedRouteStop>();
        }

        var sameDayFeasibleCount = 0;
        var weekTasksByTechnician = allWeekTasks
            .Where(task => WorkloadCalculator.CountsTowardsWorkload(task.Status))
            .GroupBy(task => task.TechnicianId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(task => task.StartAt).ToList());

        foreach (var candidateDate in candidateDates)
        {
            var dayStart = candidateDate.Date;
            var rankedCandidates = new List<RankedTechnicianCandidate>();

            foreach (var technician in localityPool.Technicians)
            {
                var technicianDayTasks = getWorkloadTasks(technician.Id, dayStart);
                var technicianWeekTasks = weekTasksByTechnician.GetValueOrDefault(technician.Id)
                    ?? new List<TaskEntity>();

                var dayBlockedWindows = BuildDayBlockedWindows(
                    sharedContext?.BlockedIntervalsByTechnician ?? new Dictionary<long, List<TechnicianBlockedInterval>>(),
                    technician.Id,
                    dayStart,
                    timezoneOffsetMinutes);

                var candidateStarts = _slotGenerator.GenerateCandidateStartMinutes(
                    technician,
                    technicianDayTasks,
                    dayStart,
                    timezoneOffsetMinutes,
                    durationMinutes,
                    serviceObject,
                    routeLookup,
                    dayBlockedWindows);

                var simulatedStops = getSimulatedStops(technician.Id, dayStart);

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

                    // Pass the candidate task directly instead of copying allWeekTasks
                    var candidateTask = new TaskEntity
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
                    };

                    rankedCandidates.Add(_scoreCalculator.BuildRankedCandidate(
                        technician,
                        requestedDate,
                        dayStart,
                        technicianDayTasks,
                        technicianWeekTasks,
                        candidateTask,
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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ServiceObjectEntity>? serviceObjectsByName = null)
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
                var cachedServiceObject = serviceObjectsByName?.Values
                    .FirstOrDefault(o => o.Id == assignment.ServiceObjectId.Value);

                addressId = cachedServiceObject?.AddressId
                    ?? await _db.ServiceObjects
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

    private async Task<AssignmentMatchingContext?> BuildAssignmentMatchingContextAsync(
        IReadOnlyList<AssignmentContextRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return null;
        }

        var technicians = await _db.Technicians
            .AsNoTracking()
            .Include(t => t.User)
            .Include(t => t.WorkplaceAddress)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var objectNames = requests
            .Select(r => r.ObjectName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var serviceObjects = await _db.ServiceObjects
            .AsNoTracking()
            .Include(so => so.Address)
            .Where(so => objectNames.Contains(so.Name))
            .ToListAsync(cancellationToken);

        var candidateDates = requests
            .SelectMany(r =>
            {
                var earliestPlacement = _timeService.GetEarliestCreatablePlacement(
                    r.TargetDate,
                    r.TimezoneOffsetMinutes,
                    ScheduleGrid.SlotSizeMinutes,
                    ScheduleGrid.DayStartMinutes,
                    ScheduleGrid.DayEndMinutes);

                return _dateWindowGenerator.GenerateDates(
                    earliestPlacement.TargetDate,
                    earliestPlacement.TargetDate.AddDays(SearchHorizonDays - 1),
                    SearchHorizonDays);
            })
            .Select(d => d.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (candidateDates.Count == 0)
        {
            return new AssignmentMatchingContext
            {
                Technicians = technicians,
                ServiceObjectsByName = serviceObjects
                    .GroupBy(so => so.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                PersistedWeekTasks = new List<TaskEntity>(),
                RouteLookup = new Dictionary<(long From, long To), int>(),
                RouteLookupAddressIds = new HashSet<long>(),
                LoadRangeStart = DateTime.MaxValue,
                LoadRangeEndExclusive = DateTime.MinValue,
                BlockedIntervalsByTechnician = new Dictionary<long, List<TechnicianBlockedInterval>>()
            };
        }

        var loadRangeStart = _timeService.GetWeekStart(candidateDates[0]);
        var loadRangeEndExclusive = _timeService.GetWeekEnd(candidateDates[^1]).AddDays(1);

        var persistedWeekTasks = await _db.Tasks
            .AsNoTracking()
            .Include(t => t.ServiceObject)
            .Include(t => t.Technician)
                .ThenInclude(t => t.User)
            .Where(t => t.StartAt >= loadRangeStart && t.StartAt < loadRangeEndExclusive)
            .ToListAsync(cancellationToken);

        var addressIds = persistedWeekTasks
            .Where(t => t.ServiceObject != null)
            .Select(t => t.ServiceObject!.AddressId)
            .Concat(technicians.Select(t => t.WorkplaceAddressId))
            .Concat(serviceObjects.Select(so => so.AddressId))
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken)
            ?? new Dictionary<(long From, long To), int>();

        var allTechnicianIds = technicians.Select(t => t.Id).ToList();
        var blockedIntervalsByTechnician = await _unavailabilityReader.GetBlockedIntervalsByTechnicianAsync(
            allTechnicianIds,
            loadRangeStart,
            loadRangeEndExclusive,
            cancellationToken);

        return new AssignmentMatchingContext
        {
            Technicians = technicians,
            ServiceObjectsByName = serviceObjects
                .GroupBy(so => so.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            PersistedWeekTasks = persistedWeekTasks,
            RouteLookup = routeLookup,
            RouteLookupAddressIds = addressIds.ToHashSet(),
            LoadRangeStart = loadRangeStart,
            LoadRangeEndExclusive = loadRangeEndExclusive,
            BlockedIntervalsByTechnician = blockedIntervalsByTechnician
        };
    }

    /// <summary>
    /// Converts technician unavailability intervals that overlap <paramref name="localDay"/>
    /// into (StartMinutes, EndMinutes) tuples in local time, ready for the slot generator.
    /// </summary>
    private IReadOnlyList<(int StartMinutes, int EndMinutes)> BuildDayBlockedWindows(
        Dictionary<long, List<TechnicianBlockedInterval>> blockedByTechnician,
        long technicianId,
        DateTime localDay,
        int timezoneOffsetMinutes)
    {
        if (!blockedByTechnician.TryGetValue(technicianId, out var intervals) || intervals.Count == 0)
        {
            return Array.Empty<(int, int)>();
        }

        // Convert the local day boundaries to UTC for overlap filtering
        var localDayStartUtc = localDay.AddMinutes(-timezoneOffsetMinutes);
        var localDayEndUtc   = localDayStartUtc.AddDays(1);

        var result = new List<(int StartMinutes, int EndMinutes)>();
        foreach (var interval in intervals)
        {
            if (interval.StartAtUtc >= localDayEndUtc || interval.EndAtUtc <= localDayStartUtc)
            {
                continue;
            }

            var startLocal = _timeService.ConvertUtcToLocal(interval.StartAtUtc, timezoneOffsetMinutes);
            var endLocal   = _timeService.ConvertUtcToLocal(interval.EndAtUtc,   timezoneOffsetMinutes);

            // Express as minutes from midnight of the local day so values align
            // with the slot-generator's working-window minute arithmetic.
            var startMinutes = (int)(startLocal - localDay).TotalMinutes;
            var endMinutes   = (int)(endLocal   - localDay).TotalMinutes;

            // Clamp to the representable range (cross-midnight edge cases)
            startMinutes = Math.Max(0, startMinutes);
            endMinutes   = Math.Min(1440, endMinutes);

            if (startMinutes < endMinutes)
            {
                result.Add((startMinutes, endMinutes));
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<(long From, long To), int>> BuildRouteLookupAsync(
        AssignmentMatchingContext? sharedContext,
        IReadOnlyCollection<long> addressIds,
        CancellationToken cancellationToken)
    {
        var distinctAddressIds = addressIds.Where(id => id > 0).Distinct().ToList();
        if (distinctAddressIds.Count == 0)
        {
            return new Dictionary<(long From, long To), int>();
        }

        if (sharedContext == null)
        {
            return await _travelResolver.GetTravelMinutesAsync(distinctAddressIds, cancellationToken)
                ?? new Dictionary<(long From, long To), int>();
        }

        var missingAddressIds = distinctAddressIds
            .Where(addressId => !sharedContext.RouteLookupAddressIds.Contains(addressId))
            .ToList();

        if (missingAddressIds.Count == 0)
        {
            return sharedContext.RouteLookup;
        }

        var routeLookup = new Dictionary<(long From, long To), int>(sharedContext.RouteLookup);
        var additionalAddressIds = distinctAddressIds.Distinct().ToList();
        var additionalRoutes = await _travelResolver.GetTravelMinutesAsync(additionalAddressIds, cancellationToken)
            ?? new Dictionary<(long From, long To), int>();

        foreach (var route in additionalRoutes)
        {
            routeLookup[route.Key] = route.Value;
        }

        return routeLookup;
    }

    private AssignmentContextRequest? TryBuildAssignmentContextRequest(CreateTaskRequestDto item)
    {
        if (string.IsNullOrWhiteSpace(item.ObjectName) ||
            !DateTime.TryParseExact(item.TargetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var targetDate))
        {
            return null;
        }

        return new AssignmentContextRequest(targetDate, item.ObjectName, item.TimezoneOffsetMinutes);
    }

    private ServiceObjectEntity? TryResolveServiceObject(AssignmentMatchingContext? context, string? objectName)
    {
        if (context == null || string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        context.ServiceObjectsByName.TryGetValue(objectName.Trim(), out var serviceObject);
        return serviceObject;
    }

    private static IReadOnlyList<ProposedRouteStop> BuildSimulatedStops(
        IEnumerable<TaskEntity> simulatedTasks)
    {
        return simulatedTasks
            .Where(t => t.ServiceObject != null)
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
