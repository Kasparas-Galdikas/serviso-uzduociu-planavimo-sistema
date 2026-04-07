using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Orchestrator.Shared;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Unavailability;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/unavailability")]
    [Authorize(Policy = "RequireManager")]
    public class UnavailabilityController : ControllerBase
    {
        private readonly IPlanService _planService;
        private readonly IUnavailabilityService _unavailabilityService;
        private readonly IScheduleTimeService _timeService;
        private readonly IUserContextResolver _userContextResolver;
        private readonly ILogger<UnavailabilityController> _logger;

        public UnavailabilityController(
            IPlanService planService,
            IUnavailabilityService unavailabilityService,
            IScheduleTimeService timeService,
            IUserContextResolver userContextResolver,
            ILogger<UnavailabilityController> logger)
        {
            _planService = planService;
            _unavailabilityService = unavailabilityService;
            _timeService = timeService;
            _userContextResolver = userContextResolver;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetPeriods(
            [FromQuery] long technicianId,
            CancellationToken cancellationToken)
        {
            var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);
            var periods = await _unavailabilityService.GetPeriodsAsync(technicianId, cancellationToken);

            var result = periods.Select(p =>
            {
                var startLocal = _timeService.ConvertUtcToLocal(p.PeriodStart, timezoneOffsetMinutes);
                var endLocal = _timeService.ConvertUtcToLocal(p.PeriodEnd, timezoneOffsetMinutes);

                return new
                {
                    id = p.Id,
                    technicianId = p.TechnicianId,
                    periodStart = startLocal.ToString("yyyy-MM-ddTHH:mm"),
                    periodEnd = endLocal.ToString("yyyy-MM-ddTHH:mm"),
                    createdAt = p.CreatedAt
                };
            });

            return Ok(result);
        }

        [HttpGet("defaults")]
        public IActionResult GetDefaults([FromQuery] string? targetDate = null)
        {
            var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            var effectiveDate = TryParseDate(targetDate ?? string.Empty, out var parsedDate)
                ? (parsedDate.Date < userLocalToday ? userLocalToday : parsedDate.Date)
                : userLocalToday;

            var placement = _timeService.GetEarliestCreatablePlacement(
                effectiveDate,
                timezoneOffsetMinutes,
                ScheduleGrid.SlotSizeMinutes,
                ScheduleGrid.DayStartMinutes,
                ScheduleGrid.DayEndMinutes);

            var startLocal = placement.TargetDate.Date.AddMinutes(placement.StartMinutes);
            var endLocal = startLocal.AddMinutes(ScheduleGrid.SlotSizeMinutes);

            return Ok(new
            {
                periodStart = startLocal.ToString("yyyy-MM-ddTHH:mm"),
                periodEnd = endLocal.ToString("yyyy-MM-ddTHH:mm")
            });
        }

        [HttpPost("preview")]
        public async Task<IActionResult> Preview(
            [FromBody] CreateUnavailabilityRequestDto dto,
            CancellationToken cancellationToken)
        {
            var parsed = ParseAndValidate(dto, out var validationResult);
            if (validationResult != null)
            {
                return validationResult;
            }

            var user = _userContextResolver.GetCurrentUser();
            var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);
            var preview = await _planService.PreviewDisplacementAsync(
                dto.TechnicianId, parsed.StartUtc, parsed.EndUtc, user, timezoneOffsetMinutes, cancellationToken);

            var affectedTasks = preview.AffectedTasks.Select(t => new
            {
                taskId = t.TaskId,
                title = t.Title,
                startAt = t.StartAt,
                endAt = t.EndAt,
                priority = t.Priority.ToString().ToLowerInvariant(),
                objectName = t.ObjectName,
                objectAddress = t.ObjectAddress,
                resolution = t.Resolution.ToString().ToLowerInvariant()
            });

            return Ok(new { affectedTasks });
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            [FromBody] CreateUnavailabilityRequestDto dto,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation(
                    "api.unavailability.create.request technicianId={TechnicianId} periodStart={PeriodStart} periodEnd={PeriodEnd}",
                    dto.TechnicianId,
                    dto.PeriodStart,
                    dto.PeriodEnd);

                var parsed = ParseAndValidate(dto, out var validationResult);
                if (validationResult != null)
                {
                    return validationResult;
                }

                var user = _userContextResolver.GetCurrentUser();
                var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);
                var result = await _planService.DisplaceTasksToBacklogAsync(
                    dto.TechnicianId, parsed.StartUtc, parsed.EndUtc, user, timezoneOffsetMinutes, cancellationToken);

                if (!result.Ok)
                {
                    _logger.LogWarning(
                        "api.unavailability.create.rejected technicianId={TechnicianId} error={Error} message={Message}",
                        dto.TechnicianId,
                        result.Error,
                        result.Message ?? string.Empty);

                    return result.Error switch
                    {
                        DisplaceTasksError.TechnicianNotFound => NotFound(new { error = result.Message }),
                        DisplaceTasksError.Unauthorized => Forbid(),
                        DisplaceTasksError.InvalidPeriod => BadRequest(new { error = result.Message }),
                        DisplaceTasksError.NotFound => NotFound(new { error = result.Message }),
                        DisplaceTasksError.Stale => Conflict(new { error = result.Message }),
                        DisplaceTasksError.RestoreFailed => BadRequest(new { error = result.Message }),
                        _ => BadRequest(new { error = result.Message })
                    };
                }

                _logger.LogInformation(
                    "api.unavailability.create.ok technicianId={TechnicianId} displacedCount={DisplacedCount}",
                    dto.TechnicianId,
                    result.DisplacedCount);

                return Ok(new { displacedCount = result.DisplacedCount, repairedCount = result.RepairedCount });
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "api.unavailability.create.concurrency technicianId={TechnicianId}", dto.TechnicianId);
                return StatusCode(500, new { error = "Nepavyko issaugoti neaktyvumo periodo." });
            }
        }

        [HttpPut("{id:long}")]
        public async Task<IActionResult> Update(
            long id,
            [FromBody] CreateUnavailabilityRequestDto dto,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation(
                    "api.unavailability.update.request id={Id} technicianId={TechnicianId} periodStart={PeriodStart} periodEnd={PeriodEnd}",
                    id,
                    dto.TechnicianId,
                    dto.PeriodStart,
                    dto.PeriodEnd);

                var parsed = ParseAndValidate(dto, out var validationResult);
                if (validationResult != null)
                {
                    return validationResult;
                }

                var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);
                var user = _userContextResolver.GetCurrentUser();
                var result = await _planService.UpdateUnavailabilityAsync(
                    id,
                    parsed.StartUtc,
                    parsed.EndUtc,
                    user,
                    timezoneOffsetMinutes,
                    cancellationToken);

                if (!result.Ok)
                {
                    _logger.LogWarning(
                        "api.unavailability.update.rejected id={Id} technicianId={TechnicianId} error={Error} message={Message}",
                        id,
                        dto.TechnicianId,
                        result.Error,
                        result.Message ?? string.Empty);

                    return result.Error switch
                    {
                        DisplaceTasksError.TechnicianNotFound => NotFound(new { error = result.Message }),
                        DisplaceTasksError.Unauthorized => Forbid(),
                        DisplaceTasksError.InvalidPeriod => BadRequest(new { error = result.Message }),
                        DisplaceTasksError.NotFound => NotFound(new { error = result.Message }),
                        DisplaceTasksError.Stale => Conflict(new { error = result.Message }),
                        DisplaceTasksError.RestoreFailed => BadRequest(new { error = result.Message }),
                        _ => BadRequest(new { error = result.Message })
                    };
                }

                _logger.LogInformation(
                    "api.unavailability.update.ok id={Id} technicianId={TechnicianId} displacedCount={DisplacedCount} repairedCount={RepairedCount} restoredCount={RestoredCount}",
                    id,
                    dto.TechnicianId,
                    result.DisplacedCount,
                    result.RepairedCount,
                    result.RestoredCount);

                return Ok(new
                {
                    updated = true,
                    displacedCount = result.DisplacedCount,
                    repairedCount = result.RepairedCount,
                    restoredCount = result.RestoredCount,
                    backlogRemainingCount = result.BacklogRemainingCount
                });
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "api.unavailability.update.concurrency id={Id} technicianId={TechnicianId}", id, dto.TechnicianId);
                return StatusCode(500, new { error = "Nepavyko issaugoti neaktyvumo periodo." });
            }
        }

        [HttpDelete("{id:long}")]
        public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("api.unavailability.delete.request id={Id}", id);

                var user = _userContextResolver.GetCurrentUser();
                var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);
                var result = await _planService.DeleteUnavailabilityAsync(id, user, timezoneOffsetMinutes, cancellationToken);

                if (!result.Ok)
                {
                    _logger.LogWarning(
                        "api.unavailability.delete.rejected id={Id} error={Error} message={Message}",
                        id,
                        result.Error,
                        result.Message ?? string.Empty);

                    return result.Error switch
                    {
                        DisplaceTasksError.NotFound => NotFound(new { error = UnavailabilityMessages.PeriodNotFound }),
                        DisplaceTasksError.Unauthorized => Forbid(),
                        DisplaceTasksError.Stale => Conflict(new { error = result.Message }),
                        DisplaceTasksError.RestoreFailed => BadRequest(new { error = result.Message }),
                        _ => BadRequest(new { error = result.Message })
                    };
                }

                _logger.LogInformation(
                    "api.unavailability.delete.ok id={Id} restoredCount={RestoredCount} backlogRemainingCount={BacklogRemainingCount}",
                    id,
                    result.RestoredCount,
                    result.BacklogRemainingCount);

                return Ok(new
                {
                    deleted = true,
                    restoredCount = result.RestoredCount,
                    backlogRemainingCount = result.BacklogRemainingCount
                });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                return StatusCode(500, new { error = UnavailabilityMessages.MigrationRequired });
            }
        }

        private (DateTime StartUtc, DateTime EndUtc) ParseAndValidate(
            CreateUnavailabilityRequestDto dto,
            out IActionResult? validationError)
        {
            validationError = null;
            var timezoneOffsetMinutes = _timeService.GetBrowserTimezoneOffset(Request);

            if (!TryParseLocalDateTime(dto.PeriodStart, timezoneOffsetMinutes, out var periodStart))
            {
                validationError = BadRequest(new { error = UnavailabilityMessages.InvalidStartFormat });
                return default;
            }

            if (!TryParseLocalDateTime(dto.PeriodEnd, timezoneOffsetMinutes, out var periodEnd))
            {
                validationError = BadRequest(new { error = UnavailabilityMessages.InvalidEndFormat });
                return default;
            }

            if (periodEnd <= periodStart)
            {
                validationError = BadRequest(new { error = UnavailabilityMessages.EndNotAfterStart });
                return default;
            }

            if (!IsSlotAligned(periodStart, timezoneOffsetMinutes) || !IsSlotAligned(periodEnd, timezoneOffsetMinutes))
            {
                validationError = BadRequest(new { error = UnavailabilityMessages.SlotMisaligned });
                return default;
            }

            if (!IsCreatablePeriodStart(periodStart, timezoneOffsetMinutes))
            {
                validationError = BadRequest(new { error = UnavailabilityMessages.PeriodInPast });
                return default;
            }

            if (!IsWithinWorkingWindow(dto.TechnicianId, periodStart, periodEnd, timezoneOffsetMinutes))
            {
                var windowStart = $"{ScheduleGrid.DayStartMinutes / 60:00}:{ScheduleGrid.DayStartMinutes % 60:00}";
                var windowEnd   = $"{ScheduleGrid.DayEndMinutes / 60:00}:{ScheduleGrid.DayEndMinutes % 60:00}";
                validationError = BadRequest(new { error = UnavailabilityMessages.OutsideWorkingWindow(windowStart, windowEnd) });
                return default;
            }

            return (periodStart, periodEnd);
        }

        private static bool TryParseDate(string value, out DateTime parsed)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var dt))
            {
                parsed = dt.Date;
                return true;
            }

            parsed = default;
            return false;
        }

        private static bool TryParseLocalDateTime(string value, int timezoneOffsetMinutes, out DateTime parsedUtc)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(
                    value,
                    "yyyy-MM-ddTHH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localDateTime))
            {
                var localUnspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
                parsedUtc = DateTime.SpecifyKind(localUnspecified.AddMinutes(-timezoneOffsetMinutes), DateTimeKind.Utc);
                return true;
            }

            parsedUtc = default;
            return false;
        }

        private bool IsSlotAligned(DateTime utcDateTime, int timezoneOffsetMinutes)
        {
            var localDateTime = _timeService.ConvertUtcToLocal(utcDateTime, timezoneOffsetMinutes);
            return localDateTime.Second == 0 &&
                   localDateTime.TimeOfDay.TotalMinutes % ScheduleGrid.SlotSizeMinutes == 0;
        }

        private bool IsCreatablePeriodStart(DateTime utcDateTime, int timezoneOffsetMinutes)
        {
            var localDateTime = _timeService.ConvertUtcToLocal(utcDateTime, timezoneOffsetMinutes);
            var userLocalToday = _timeService.GetUserLocalToday(timezoneOffsetMinutes);
            if (localDateTime.Date < userLocalToday)
            {
                return false;
            }

            var earliestPlacement = _timeService.GetEarliestCreatablePlacement(
                localDateTime.Date,
                timezoneOffsetMinutes,
                ScheduleGrid.SlotSizeMinutes,
                ScheduleGrid.DayStartMinutes,
                ScheduleGrid.DayEndMinutes);
            var earliestLocalDateTime = earliestPlacement.TargetDate.Date.AddMinutes(earliestPlacement.StartMinutes);
            return localDateTime >= earliestLocalDateTime;
        }

        private bool IsWithinWorkingWindow(long technicianId, DateTime periodStartUtc, DateTime periodEndUtc, int timezoneOffsetMinutes)
        {
            var startLocal = _timeService.ConvertUtcToLocal(periodStartUtc, timezoneOffsetMinutes);
            var endLocal = _timeService.ConvertUtcToLocal(periodEndUtc, timezoneOffsetMinutes);
            if (startLocal.Date != endLocal.Date)
            {
                return false;
            }

            var window = SchedulePlacementValidator.GetWorkingWindow(technicianId, startLocal.Date);
            var startMinutes = (int)startLocal.TimeOfDay.TotalMinutes;
            var endMinutes = (int)endLocal.TimeOfDay.TotalMinutes;
            return startMinutes >= window.StartMinutes && endMinutes <= window.EndMinutes;
        }
    }
}
