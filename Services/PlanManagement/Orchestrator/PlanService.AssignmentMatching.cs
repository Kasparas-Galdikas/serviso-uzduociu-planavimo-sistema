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
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;

namespace ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator;

internal sealed class PlanServiceAssignmentMatching
{
    private readonly ScheduleDbContext _db;
    private readonly IScheduleTimeService _timeService;
    private readonly SchedulePlacementValidator _placementValidator;
    private readonly ITravelTimeResolver _travelResolver;
    private readonly CandidateEligibilityEvaluator _eligibilityEvaluator;
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

                item.TechnicianId = suggestion.TechnicianId;
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
                        StartAt = targetDate.Date.AddMinutes(suggestion.StartMinutes).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                        EndAt = targetDate.Date.AddMinutes(suggestion.EndMinutes).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
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

        var targetPlan = await _db.Plans
            .AsNoTracking()
            .OrderBy(p => p.Status == PlanStatus.Draft ? 0 : 1)
            .FirstOrDefaultAsync(
                p => p.Status == PlanStatus.Draft || p.Status == PlanStatus.Active,
                cancellationToken);

        if (targetPlan == null)
        {
            return null;
        }

        var technicians = await _db.Technicians
            .AsNoTracking()
            .Include(t => t.User)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var eligibleTechnicians = _eligibilityEvaluator.GetEligibleTechnicians(technicians, requiredSkillLevel: string.Empty);
        if (eligibleTechnicians.Count == 0)
        {
            return null;
        }

        var serviceObject = await _db.ServiceObjects
            .AsNoTracking()
            .FirstOrDefaultAsync(so => so.Name == objectName, cancellationToken);

        if (serviceObject == null)
        {
            return null;
        }

        var dayStart = targetDate.Date;
        var weekStart = _timeService.GetWeekStart(dayStart);
        var weekEnd = weekStart.AddDays(7);

        var persistedWeekTasks = await _db.Tasks
            .AsNoTracking()
            .Include(t => t.ServiceObject)
            .Include(t => t.Technician)
                .ThenInclude(t => t.User)
            .Where(t => t.PlanId == targetPlan.Id &&
                        t.StartAt >= weekStart &&
                        t.StartAt < weekEnd)
            .ToListAsync(cancellationToken);

        var simulatedTasks = BuildSimulatedTasks(simulatedAssignments);
        var allWeekTasks = persistedWeekTasks
            .Concat(simulatedTasks)
            .ToList();

        var addressIds = allWeekTasks
            .Where(t => t.ServiceObject != null)
            .Select(t => t.ServiceObject.AddressId)
            .Concat(eligibleTechnicians.Select(t => t.WorkplaceAddressId))
            .Append(serviceObject.AddressId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var routeLookup = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken)
            ?? new Dictionary<(long From, long To), int>();
        var nextTempTaskId = NextTemporaryTaskId(allWeekTasks);
        var rankedCandidates = new List<RankedTechnicianCandidate>();

        foreach (var technician in eligibleTechnicians)
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
                    PlanId = targetPlan.Id,
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
                    dayStart,
                    candidateTasks,
                    routeLookup,
                    batchAssignmentCounts?.GetValueOrDefault(technician.Id) ?? 0,
                    candidateStartMinutes,
                    candidateStartMinutes + durationMinutes));
            }
        }

        var best = _rankingService.SelectBest(rankedCandidates);
        if (best == null)
        {
            return null;
        }

        return new AutoAssignmentSuggestionDto
        {
            TechnicianId = best.Technician.Id,
            TechnicianName = best.Technician.User?.Name ?? $"Technician #{best.Technician.Id}",
            StartMinutes = best.StartMinutes,
            EndMinutes = best.EndMinutes
        };
    }

    private static List<TaskEntity> BuildSimulatedTasks(IEnumerable<SimulatedTaskAssignmentDto>? simulatedAssignments)
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
            if (assignment.TechnicianId <= 0 ||
                assignment.AddressId <= 0 ||
                !TryParseSimulatedDateTime(assignment.StartAt, out var startAt) ||
                !TryParseSimulatedDateTime(assignment.EndAt, out var endAt) ||
                endAt <= startAt)
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
                    AddressId = assignment.AddressId,
                    Name = string.Empty
                },
                Title = string.Empty,
                StartAt = startAt,
                EndAt = endAt,
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

    private static bool TryParseSimulatedDateTime(string value, out DateTime parsed)
    {
        var formats = new[]
        {
            "yyyy-MM-dd'T'HH:mm",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.FFF",
            "O"
        };

        return DateTime.TryParseExact(
                   value,
                   formats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out parsed)
               || DateTime.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal,
                   out parsed);
    }
}
