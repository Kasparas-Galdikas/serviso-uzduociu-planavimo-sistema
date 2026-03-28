using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using ServiceTaskPlanningSystem.Services.Scheduling;

namespace ServiceTaskPlanningSystem.Services.Travel
{
    public sealed class TaskTravelFeasibilityService : ITaskTravelFeasibilityService
    {
        private readonly IRouteChainBuilder _routeChainBuilder;
        private readonly ITravelTimeResolver _travelResolver;
        private readonly IRouteEvaluationService _routeEvalService;
        private readonly ILogger<TaskTravelFeasibilityService> _logger;

        private static string FormatDurationLabel(int totalMinutes)
        {
            var minutes = Math.Max(0, totalMinutes);
            var hours = minutes / 60;
            var remainder = minutes % 60;
            return $"{hours:00}:{remainder:00}";
        }
        public TaskTravelFeasibilityService(
            IRouteChainBuilder routeChainBuilder,
            ITravelTimeResolver travelResolver,
            IRouteEvaluationService routeEvalService,
            ILogger<TaskTravelFeasibilityService> logger)
        {
            _routeChainBuilder = routeChainBuilder;
            _travelResolver = travelResolver;
            _routeEvalService = routeEvalService;
            _logger = logger;
        }

        public async Task<TravelFeasibilityResult> EvaluatePlacementAsync(
            long technicianId,
            DateTime proposedStartAt,
            DateTime proposedEndAt,
            long serviceAddressId,
            int proposedStartMinutesLocal,
            long? excludeTaskId,
            bool checkOverlap,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ProposedRouteStop>? simulatedStops = null)
        {
            var localOffsetMinutes = proposedStartMinutesLocal - (int)proposedStartAt.TimeOfDay.TotalMinutes;
            var proposedStartLocal = proposedStartAt.AddMinutes(localOffsetMinutes);
            var proposedEndLocal = proposedEndAt.AddMinutes(localOffsetMinutes);

            var chain = await _routeChainBuilder.BuildDayChainAsync(
                new RouteChainRequest
                {
                    TechnicianId = technicianId,
                    DayUtc = proposedStartAt.Date,
                    IncludeCanceled = false,
                    FilterByPlanId = false,
                    ExcludeTaskId = excludeTaskId,
                    ProposedStop = new ProposedRouteStop
                    {
                        StartAt = proposedStartAt,
                        EndAt = proposedEndAt,
                        AddressId = serviceAddressId
                    },
                    SimulatedStops = simulatedStops
                },
                cancellationToken);

            if (chain == null || chain.WorkplaceAddressId <= 0)
            {
                return new TravelFeasibilityResult { IsFeasible = false, Message = "Techniko darbo vietos adresas nerastas." };
            }

            var daySlots = chain.Stops
                .Where(s => !s.IsProposed)
                .ToList();

            if (checkOverlap && daySlots.Any(t => t.StartAt < proposedEndAt && t.EndAt > proposedStartAt))
            {
                LogSummary(
                    technicianId,
                    excludeTaskId,
                    serviceAddressId,
                    proposedStartLocal,
                    proposedEndLocal,
                    chain.Stops,
                    localOffsetMinutes,
                    null,
                    null,
                    null,
                    "OVERLAP",
                    "Laiko langas uzimtas (persidengia uzduotys).");

                return new TravelFeasibilityResult
                {
                    IsFeasible = false,
                    HasOverlap = true,
                    Message = "Laiko langas uzimtas (persidengia uzduotys)."
                };
            }

            var sequence = chain.Stops;
            var idx = sequence.FindIndex(t => t.IsProposed);
            if (idx < 0)
            {
                return new TravelFeasibilityResult { IsFeasible = false, Message = "Nepavyko ivertinti keliones." };
            }

            var pairsToCheck = new List<(long From, long To, int Available, bool IsInbound, bool IsOutbound)>();
            if (idx == 0)
            {
                var available = proposedStartMinutesLocal - ScheduleGrid.DayStartMinutes;
                pairsToCheck.Add((chain.WorkplaceAddressId, serviceAddressId, available, true, false));
            }
            else
            {
                var prev = sequence[idx - 1];
                var available = (int)(proposedStartAt - prev.EndAt).TotalMinutes;
                pairsToCheck.Add((prev.AddressId, serviceAddressId, available, true, false));
            }

            if (idx < sequence.Count - 1)
            {
                var next = sequence[idx + 1];
                var available = (int)(next.StartAt - proposedEndAt).TotalMinutes;
                pairsToCheck.Add((serviceAddressId, next.AddressId, available, false, true));
            }

            var addressIds = pairsToCheck
                .SelectMany(p => new[] { p.From, p.To })
                .Distinct()
                .ToList();

            await _routeEvalService.EnsureAllPairwiseRoutesAsync(addressIds, cancellationToken);
            var routes = await _travelResolver.GetTravelMinutesAsync(addressIds, cancellationToken);

            int? inbound = null;
            int? outbound = null;
            foreach (var (from, to, available, isInbound, isOutbound) in pairsToCheck)
            {
                if (!routes.TryGetValue((from, to), out var required))
                {
                    if (from == to)
                    {
                        required = 0;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (isInbound) inbound = required;
                if (isOutbound) outbound = required;

                if (from != to && required <= 0)
                {
                    LogSummary(
                        technicianId,
                        excludeTaskId,
                        serviceAddressId,
                        proposedStartLocal,
                        proposedEndLocal,
                        sequence,
                        localOffsetMinutes,
                        BuildCheck(from, to, available, required),
                        inbound,
                        outbound,
                        "FAIL",
                        "Nepavyko ivertinti keliones (netinkama marsruto trukme).");

                    return new TravelFeasibilityResult
                    {
                        IsFeasible = false,
                        HasOverlap = false,
                        InboundTravelMinutes = inbound,
                        OutboundTravelMinutes = outbound,
                        Message = "Nepavyko ivertinti keliones (netinkama marsruto trukme)."
                    };
                }

                if (required > 0 && required > available)
                {
                    var shownAvailable = Math.Max(0, available);
                    var message = $"Nepakanka laiko kelionei: kelionei reikia {FormatDurationLabel(required)}, laisvo laiko yra {FormatDurationLabel(shownAvailable)}.";

                    LogSummary(
                        technicianId,
                        excludeTaskId,
                        serviceAddressId,
                        proposedStartLocal,
                        proposedEndLocal,
                        sequence,
                        localOffsetMinutes,
                        BuildCheck(from, to, shownAvailable, required),
                        inbound,
                        outbound,
                        "FAIL",
                        message);

                    return new TravelFeasibilityResult
                    {
                        IsFeasible = false,
                        HasOverlap = false,
                        InboundTravelMinutes = inbound,
                        OutboundTravelMinutes = outbound,
                        Message = message
                    };
                }
            }

            LogSummary(
                technicianId,
                excludeTaskId,
                serviceAddressId,
                proposedStartLocal,
                proposedEndLocal,
                sequence,
                localOffsetMinutes,
                BuildPrimaryCheck(pairsToCheck, inbound, outbound),
                inbound,
                outbound,
                "OK",
                null);

            return new TravelFeasibilityResult
            {
                IsFeasible = true,
                HasOverlap = false,
                InboundTravelMinutes = inbound,
                OutboundTravelMinutes = outbound
            };
        }

        private static string BuildPrimaryCheck(
            List<(long From, long To, int Available, bool IsInbound, bool IsOutbound)> pairsToCheck,
            int? inbound,
            int? outbound)
        {
            var pair = pairsToCheck.FirstOrDefault(p => p.IsOutbound) != default
                ? pairsToCheck.First(p => p.IsOutbound)
                : pairsToCheck.FirstOrDefault();

            if (pair == default)
            {
                return "none";
            }

            var required = pair.IsOutbound ? outbound : inbound;
            return BuildCheck(pair.From, pair.To, pair.Available, required);
        }

        private static string BuildCheck(long from, long to, int gap, int? required)
        {
            return $"{from}->{to} gap={Math.Max(0, gap)} required={(required.HasValue ? required.Value : -1)}";
        }

        private void LogSummary(
            long technicianId,
            long? excludeTaskId,
            long serviceAddressId,
            DateTime proposedStartLocal,
            DateTime proposedEndLocal,
            List<RouteStop> sequence,
            int localOffsetMinutes,
            string? check,
            int? inbound,
            int? outbound,
            string result,
            string? reason)
        {
            var chainDesc = string.Join(" -> ", sequence.Select(s =>
            {
                var startLocal = s.StartAt.AddMinutes(localOffsetMinutes);
                var endLocal = s.EndAt.AddMinutes(localOffsetMinutes);
                return $"{(s.IsProposed ? "P" : "T")}@{s.AddressId} {startLocal:HH:mm}-{endLocal:HH:mm}";
            }));

            _logger.LogInformation(
                "[TAV] tech={TechnicianId} task={TaskId} move=addr{AddressId} {Start}-{End} chain=[{Chain}] check={Check} inbound={Inbound} outbound={Outbound} result={Result} reason={Reason}",
                technicianId,
                excludeTaskId,
                serviceAddressId,
                proposedStartLocal.ToString("HH:mm"),
                proposedEndLocal.ToString("HH:mm"),
                chainDesc,
                check ?? "none",
                inbound,
                outbound,
                result,
                reason ?? string.Empty);
        }
    }
}

