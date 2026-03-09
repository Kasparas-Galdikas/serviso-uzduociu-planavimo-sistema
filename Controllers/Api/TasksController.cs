using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ServiceTaskPlanningSystem.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/tasks")]
    public class TasksController : ControllerBase
    {
        private sealed record PreviewTaskRow(long TechnicianId, DateTime StartAt, DateTime EndAt, long AddressId);

        private readonly IPlanService _planService;
        private readonly ILogger<TasksController> _logger;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly IUserContextResolver _userContextResolver;
        private readonly ScheduleDbContext _db;
        private readonly ILocationApiClient _locationApiClient;
        private readonly IDistanceApiClient _distanceApiClient;
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;

        public TasksController(
            IPlanService planService,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IUserContextResolver userContextResolver,
            ScheduleDbContext db,
            ILocationApiClient locationApiClient,
            IDistanceApiClient distanceApiClient,
            ITaskTravelFeasibilityService travelFeasibilityService,
            ILogger<TasksController> logger)
        {
            _planService = planService;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _userContextResolver = userContextResolver;
            _db = db;
            _locationApiClient = locationApiClient;
            _distanceApiClient = distanceApiClient;
            _travelFeasibilityService = travelFeasibilityService;
            _logger = logger;
        }

        [HttpPost]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            // System constraint: New tasks must always start with InProgress status
            if (dto.Status != ScheduleTaskStatus.InProgress)
            {
                return UnprocessableEntity(new { message = "Naujos uÅ¾duotys turi prasidÄ—ti su 'Vykdoma' statusu. Sistema nustato statusÄ… automatiÅ¡kai." });
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            if (dto.TechnicianId <= 0)
            {
                return UnprocessableEntity(new { message = "Priskirtas darbuotojas yra privalomas." });
            }

            if (string.IsNullOrWhiteSpace(dto.SkillLevel) ||
                string.IsNullOrWhiteSpace(dto.ObjectName) ||
                string.IsNullOrWhiteSpace(dto.ObjectAddress))
            {
                return UnprocessableEntity(new { message = "Privalomi laukai (kvalifikacija, objektas ir adresas) turi buti uzpildyti." });
            }

            var normalizedSkillLevel = dto.SkillLevel.Trim();
            var normalizedObjectName = dto.ObjectName.Trim();
            var normalizedObjectAddress = dto.ObjectAddress.Trim();

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.CreateTaskAsync(
                dto.TechnicianId,
                targetDate,
                dto.StartMinutes,
                dto.EndMinutes,
                dto.Title,
                dto.Priority,
                dto.Status,
                normalizedSkillLevel,
                normalizedObjectName,
                normalizedObjectAddress,
                dto.Instructions ?? string.Empty,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var response = new TaskCreatedOrUpdatedResponseDto
                {
                    Id = result.Task.Id,
                    Version = result.CurrentVersion ?? 0,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };

                _logger.LogInformation("api.task.create id={Id} planId={PlanId}", result.Task.Id, result.Task.PlanId);
                return Ok(response);
            }

            return result.Error switch
            {
                TaskCreateError.Overlap => Conflict(new { message = result.Message }),
                TaskCreateError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskCreateError.TechnicianNotFound => UnprocessableEntity(new { message = result.Message }),
                _ => UnprocessableEntity(new { message = result.Message ?? "Netinkamas laiko langas." })
            };
        }

        [HttpGet("{id:long}")]
        public async Task<IActionResult> GetTask(long id, CancellationToken cancellationToken)
        {
            var taskDetail = await _planService.GetTaskDetailAsync(id, cancellationToken);

            if (taskDetail == null)
            {
                return NotFound();
            }

            var task = taskDetail.Task;
            var version = taskDetail.Version;

            var timezoneOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var user = _userContextResolver.GetCurrentUser();
            var isEditable = await _taskPermissionService.CanEditTaskAsync(task, user, timezoneOffset);
            var isDeletable = await _taskPermissionService.CanDeleteTaskAsync(task, user, timezoneOffset);

            // Convert UTC times from database to local time for frontend
            var localStartAt = _timeService.ConvertUtcToLocal(task.StartAt, timezoneOffset);
            var localEndAt = _timeService.ConvertUtcToLocal(task.EndAt, timezoneOffset);

            var response = new TaskDetailResponseDto
            {
                Id = task.Id,
                Version = version,
                PlanId = task.PlanId,
                TechnicianId = task.TechnicianId,
                Title = task.Title,
                Location = task.ServiceObject?.Address?.FullAddress ?? string.Empty,
                StartAt = localStartAt,
                EndAt = localEndAt,
                Priority = task.Priority,
                Status = task.Status,
                TechnicianComment = task.TechnicianComment,
                SkillLevel = task.SkillLevel,
                ObjectId = task.ServiceObjectId,
                ObjectName = task.ServiceObject?.Name ?? string.Empty,
                ObjectAddress = task.ServiceObject?.Address?.FullAddress ?? string.Empty,
                Instructions = task.Instructions,
                PlanStatus = task.Plan?.Status.ToString() ?? string.Empty,
                IsEditable = isEditable,
                IsDeletable = isDeletable,
                TimezoneOffsetMinutes = timezoneOffset
            };

            _logger.LogInformation("api.task.get id={Id} editable={Editable} tz={Tz} utcStart={UtcStart} localStart={LocalStart}", id, isEditable, timezoneOffset, task.StartAt, localStartAt);
            return Ok(response);
        }

        [HttpDelete("{id:long}")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> DeleteTask(long id, [FromBody] DeleteTaskRequestDto dto, CancellationToken cancellationToken)
        {
            _logger.LogInformation("api.task.delete.requested id={Id} version={Version}", id, dto.Version);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("api.task.delete.validation-failed id={Id}", id);
                return ValidationProblem(ModelState);
            }

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.DeleteTaskAsync(id, dto.Version, user, dto.TimezoneOffsetMinutes, cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var response = new TaskDeletedResponseDto
                {
                    DeletedTaskId = result.Task.Id,
                    Version = result.CurrentVersion ?? dto.Version,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };
                _logger.LogInformation("api.task.delete.succeeded id={Id}", id);
                return Ok(response);
            }

            _logger.LogWarning("api.task.delete.rejected id={Id} reason={Reason}", id, result.Error);

            return result.Error switch
            {
                TaskDeleteError.NotFound => NotFound(new { message = result.Message }),
                TaskDeleteError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskDeleteError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => StatusCode(500, new { message = "Nepavyko istrinti uzduoties." })
            };
        }

        [HttpPatch("{id:long}/time")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> UpdateTaskTime(long id, [FromBody] UpdateTaskTimeRequestDto dto, CancellationToken cancellationToken)
        {
            _logger.LogInformation("api.task.time.requested id={Id} start={Start} end={End} version={Version}", id, dto.TargetSlotStartMinutes, dto.TargetSlotEndMinutes, dto.Version);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("api.task.time.validation-failed id={Id}", id);
                return ValidationProblem(ModelState);
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.UpdateTaskTimeAsync(
                id,
                targetDate,
                dto.TargetSlotStartMinutes,
                dto.TargetSlotEndMinutes,
                dto.Version,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var localStart = _timeService.ConvertUtcToLocal(result.Task.StartAt, dto.TimezoneOffsetMinutes);
                var localEnd = _timeService.ConvertUtcToLocal(result.Task.EndAt, dto.TimezoneOffsetMinutes);
                var response = new TaskMovedResponseDto
                {
                    Id = result.Task.Id,
                    Version = result.CurrentVersion ?? dto.Version,
                    TechnicianId = result.Task.TechnicianId,
                    StartAt = localStart,
                    EndAt = localEnd,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };
                _logger.LogInformation("api.task.time.succeeded id={Id}", id);
                return Ok(response);
            }

            _logger.LogWarning("api.task.time.rejected id={Id} reason={Reason}", id, result.Error);

            return result.Error switch
            {
                TaskTimeUpdateError.NotFound => NotFound(new { message = result.Message }),
                TaskTimeUpdateError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskTimeUpdateError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                TaskTimeUpdateError.Overlap => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => UnprocessableEntity(new { message = result.Message ?? "Netinkamas laiko langas." })
            };
        }

        [HttpPost("preview/drag")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> PreviewDrag([FromBody] DragPreviewRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (dto.TargetEndAt <= dto.TargetStartAt)
            {
                return UnprocessableEntity(new { message = "Pabaigos laikas turi būti vėlesnis už pradžia." });
            }

            var timezoneOffset = _timeService.GetBrowserTimezoneOffset(Request);
            var user = _userContextResolver.GetCurrentUser();

            var result = await _planService.PreviewDragAsync(
                dto.TaskId,
                dto.TargetTechnicianId,
                dto.TargetStartAt,
                dto.TargetEndAt,
                user,
                timezoneOffset,
                cancellationToken);

            if (result.Ok && result.Response != null)
            {
                _logger.LogInformation("api.task.preview.drag task={TaskId} tech={Tech} states={Count}",
                    dto.TaskId, dto.TargetTechnicianId, result.Response.PreviewStates.Count);
                return Ok(result.Response);
            }

            return result.Error switch
            {
                DragPreviewError.TaskNotFound => NotFound(),
                DragPreviewError.NotEditable => StatusCode(403),
                DragPreviewError.NoPlan => UnprocessableEntity(new { message = result.Message }),
                DragPreviewError.TechnicianNotFound => UnprocessableEntity(new { message = result.Message }),
                _ => StatusCode(500)
            };
        }

        [HttpPost("preview/assignee-travel")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> PreviewAssigneeTravel([FromBody] AssigneeTravelPreviewRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            if (dto.EndMinutes <= dto.StartMinutes)
            {
                return UnprocessableEntity(new { message = "Pabaigos laikas turi būti vėlesnis už pradžią." });
            }

            var normalizedAddress = dto.ObjectAddress.Trim();
            var targetAddressId = await _db.Addresses
                .AsNoTracking()
                .Where(a => a.FullAddress == normalizedAddress)
                .Select(a => (long?)a.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var result = new AssigneeTravelPreviewResponseDto();
            var technicians = await _db.Technicians
                .AsNoTracking()
                .Select(t => new { t.Id, t.WorkplaceAddressId })
                .ToListAsync(cancellationToken);

            if (!targetAddressId.HasValue || technicians.Count == 0)
            {
                result.Items.AddRange(technicians.Select(t => new AssigneeTravelPreviewItemDto
                {
                    TechnicianId = t.Id,
                    TravelMinutes = null,
                    IsSelectable = true
                }));
                return Ok(result);
            }

            var startAtUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate, dto.StartMinutes, dto.TimezoneOffsetMinutes);
            var endAtUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate, dto.EndMinutes, dto.TimezoneOffsetMinutes);
            var dayStartUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate, 0, dto.TimezoneOffsetMinutes);
            var dayEndUtc = _timeService.CreateUtcFromDateAndMinutes(targetDate.AddDays(1), 0, dto.TimezoneOffsetMinutes);
            long? scopedPlanId = null;
            if (dto.TaskId.HasValue)
            {
                scopedPlanId = await _db.Tasks
                    .AsNoTracking()
                    .Where(t => t.Id == dto.TaskId.Value)
                    .Select(t => t.PlanId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var dayTasks = await _db.Tasks
                .AsNoTracking()
                .Where(t => t.Status != ScheduleTaskStatus.Canceled)
                .Where(t => dto.TaskId == null || t.Id != dto.TaskId.Value)
                .Where(t => scopedPlanId == null || t.PlanId == scopedPlanId)
                .Where(t => t.StartAt < dayEndUtc && t.EndAt > dayStartUtc)
                .Select(t => new PreviewTaskRow(
                    t.TechnicianId,
                    t.StartAt,
                    t.EndAt,
                    t.ServiceObject.AddressId))
                .ToListAsync(cancellationToken);

            var tasksByTech = dayTasks
                .GroupBy(t => t.TechnicianId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartAt).ToList());

            var candidates = new List<(long TechnicianId, long FromAddressId)>();
            foreach (var tech in technicians)
            {
                var techTasks = tasksByTech.TryGetValue(tech.Id, out var list)
                    ? list
                    : new List<PreviewTaskRow>();

                var prevTask = techTasks
                    .Where(t => t.EndAt <= startAtUtc)
                    .OrderByDescending(t => t.EndAt)
                    .FirstOrDefault();

                var fromAddressId = prevTask != null ? (long)prevTask.AddressId : tech.WorkplaceAddressId;
                candidates.Add((tech.Id, fromAddressId));
            }

            var requiredPairs = candidates
                .Select(c => (FromAddressId: c.FromAddressId, ToAddressId: targetAddressId.Value))
                .Distinct()
                .ToList();

            await WarmupMissingRoutesAsync(requiredPairs, cancellationToken);

            foreach (var c in candidates)
            {
                var feasibility = await _travelFeasibilityService.EvaluatePlacementAsync(
                    c.TechnicianId,
                    startAtUtc,
                    endAtUtc,
                    targetAddressId.Value,
                    dto.StartMinutes,
                    dto.TaskId,
                    checkOverlap: true,
                    cancellationToken);

                result.Items.Add(new AssigneeTravelPreviewItemDto
                {
                    TechnicianId = c.TechnicianId,
                    TravelMinutes = feasibility.InboundTravelMinutes,
                    IsSelectable = feasibility.IsFeasible
                });
            }

            return Ok(result);
        }

        private async Task WarmupMissingRoutesAsync(
            IReadOnlyCollection<(long FromAddressId, long ToAddressId)> pairs,
            CancellationToken cancellationToken)
        {
            if (pairs.Count == 0)
            {
                return;
            }

            var fromIds = pairs.Select(p => p.FromAddressId).Distinct().ToList();
            var toIds = pairs.Select(p => p.ToAddressId).Distinct().ToList();
            var existingPairs = await _db.TravelRoutes
                .AsNoTracking()
                .Where(r => fromIds.Contains(r.FromAddressId) && toIds.Contains(r.ToAddressId))
                .Select(r => new { r.FromAddressId, r.ToAddressId })
                .ToListAsync(cancellationToken);
            var existingLookup = existingPairs
                .Select(p => (p.FromAddressId, p.ToAddressId))
                .ToHashSet();

            var missingPairs = pairs
                .Where(p => !existingLookup.Contains((p.FromAddressId, p.ToAddressId)))
                .Distinct()
                .ToList();
            if (missingPairs.Count == 0)
            {
                return;
            }

            try
            {
                var addressIds = missingPairs
                    .SelectMany(p => new[] { p.FromAddressId, p.ToAddressId })
                    .Distinct()
                    .ToList();
                var addresses = await _db.Addresses
                    .Where(a => addressIds.Contains(a.Id))
                    .ToListAsync(cancellationToken);
                var byId = addresses.ToDictionary(a => a.Id);

                var coordsUpdated = false;
                foreach (var address in addresses)
                {
                    if (address.Latitude.HasValue && address.Longitude.HasValue)
                    {
                        continue;
                    }

                    var coords = await _locationApiClient.GeocodeAsync(address.FullAddress, cancellationToken);
                    address.Latitude = coords.Latitude;
                    address.Longitude = coords.Longitude;
                    coordsUpdated = true;
                }

                if (coordsUpdated)
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }

                var matrixAddressIds = addresses
                    .Where(a => a.Latitude.HasValue && a.Longitude.HasValue)
                    .Select(a => a.Id)
                    .Distinct()
                    .ToList();
                if (matrixAddressIds.Count < 2)
                {
                    return;
                }

                var matrixAddresses = matrixAddressIds
                    .Select(id => byId[id])
                    .ToList();
                var indexByAddressId = matrixAddresses
                    .Select((address, idx) => new { address.Id, idx })
                    .ToDictionary(x => x.Id, x => x.idx);
                var points = matrixAddresses
                    .Select(a => new GeoPoint(a.Latitude!.Value, a.Longitude!.Value))
                    .ToList();
                var matrix = await _distanceApiClient.GetMatrixAsync(points, cancellationToken);

                foreach (var pair in missingPairs)
                {
                    if (!indexByAddressId.TryGetValue(pair.FromAddressId, out var fromIdx) ||
                        !indexByAddressId.TryGetValue(pair.ToAddressId, out var toIdx))
                    {
                        continue;
                    }

                    var distanceMeters = matrix.DistancesMeters[fromIdx][toIdx];
                    var durationSeconds = matrix.DurationsSeconds[fromIdx][toIdx];
                    var distanceKm = Math.Round((decimal)(distanceMeters / 1000d), 2);
                    var durationMinutes = (int)Math.Ceiling(durationSeconds / 60d);
                    var calculatedAt = DateTime.UtcNow;

                    // Warmup may run in parallel with other route evaluators.
                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $@"INSERT INTO travel_routes (from_address_id, to_address_id, distance_km, duration_minutes, calculated_at)
                           VALUES ({pair.FromAddressId}, {pair.ToAddressId}, {distanceKm}, {durationMinutes}, {calculatedAt})
                           ON CONFLICT (from_address_id, to_address_id)
                           DO UPDATE SET
                               distance_km = EXCLUDED.distance_km,
                               duration_minutes = EXCLUDED.duration_minutes,
                               calculated_at = EXCLUDED.calculated_at",
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "api.task.preview.assignee-travel.warmup.failed");
            }
        }

        [HttpPatch("{id:long}")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> UpdateTask(long id, [FromBody] UpdateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (dto.EndAt <= dto.StartAt)
            {
                return UnprocessableEntity(new { message = "Pabaigos laikas turi būti vėlenis už pradžia." });
            }

            if (string.IsNullOrWhiteSpace(dto.SkillLevel) ||
                string.IsNullOrWhiteSpace(dto.ObjectName) ||
                string.IsNullOrWhiteSpace(dto.ObjectAddress))
            {
                return UnprocessableEntity(new { message = "Privalomi laukai (kvalifikacija, objektas ir adresas) turi buti uzpildyti." });
            }

            var normalizedSkillLevel = dto.SkillLevel.Trim();
            var normalizedObjectName = dto.ObjectName.Trim();
            var normalizedObjectAddress = dto.ObjectAddress.Trim();
            var normalizedInstructions = (dto.Instructions ?? string.Empty).Trim();

            // Convert local times from frontend to UTC for storage
            var utcStartAt = _timeService.ConvertLocalToUtc(dto.StartAt, dto.TimezoneOffsetMinutes);
            var utcEndAt = _timeService.ConvertLocalToUtc(dto.EndAt, dto.TimezoneOffsetMinutes);

            _logger.LogInformation(
                "api.task.update.request id={Id} tz={Tz} localStart={LocalStart} utcStart={UtcStart}",
                id, dto.TimezoneOffsetMinutes, dto.StartAt, utcStartAt);

            var existingDetail = await _planService.GetTaskDetailAsync(id, cancellationToken);

            if (existingDetail == null)
            {
                return NotFound(new { message = "Uzduotis nerasta." });
            }

            var existing = existingDetail.Task;
            var currentVersion = existingDetail.Version;
            if (dto.Version == 0 || dto.Version != currentVersion)
            {
                return Conflict(new { message = "Uzduotis buvo pakeista kito vartotojo.", version = currentVersion });
            }

            var user = _userContextResolver.GetCurrentUser();

            // Single authorization check - TaskPermissionService is the sole authority
            var canEdit = await _taskPermissionService.CanEditTaskAsync(existing, user, dto.TimezoneOffsetMinutes);
            if (!canEdit)
            {
                return StatusCode(403, new { message = "Neturite teisiu atlikti sio veiksmo." });
            }

            var technicianExists = await _planService.TechnicianExistsAsync(dto.TechnicianId, cancellationToken);
            if (!technicianExists)
            {
                return UnprocessableEntity(new { message = "Technikas nerastas." });
            }

            // Validate working window and slot alignment in caller's local time
            var startLocal = dto.StartAt;
            var endLocal = dto.EndAt;
            if (startLocal.Date != endLocal.Date)
            {
                return UnprocessableEntity(new { message = "Uzduotis turi tilpti viena diena." });
            }

            var startMinutes = startLocal.Hour * 60 + startLocal.Minute;
            var endMinutes = endLocal.Hour * 60 + endLocal.Minute;
            if (startMinutes < ScheduleGrid.DayStartMinutes || endMinutes > ScheduleGrid.DayEndMinutes)
            {
                return UnprocessableEntity(new { message = "Uzduotis privalo tilpti i darbo laika." });
            }

            if (startMinutes % ScheduleGrid.SlotSizeMinutes != 0 || endMinutes % ScheduleGrid.SlotSizeMinutes != 0)
            {
                return UnprocessableEntity(new { message = "Laikas turi atitikti grafiko zingsni." });
            }

            var hasOverlap = await _planService.HasTaskOverlapAsync(id, dto.TechnicianId, utcStartAt, utcEndAt, cancellationToken);
            if (hasOverlap)
            {
                return Conflict(new { message = "Laiko langas uzimtas (persidengia uzduotys).", version = currentVersion });
            }

            // Preserve existing status - managers cannot change it, system manages status automatically
            var preservedStatus = existing.Status;

            var updateResult = await _planService.UpdateTaskAsync(id, task =>
            {
                task.Title = dto.Title;
                task.StartAt = utcStartAt;
                task.EndAt = utcEndAt;
                task.Priority = dto.Priority;
                task.Status = preservedStatus; // Ignore any status change from manager request
                task.SkillLevel = normalizedSkillLevel;
                task.Instructions = normalizedInstructions;
                if (dto.TechnicianComment != null)
                {
                    task.TechnicianComment = dto.TechnicianComment;
                }
            }, dto.TechnicianId, user, dto.TimezoneOffsetMinutes, normalizedObjectName, normalizedObjectAddress, cancellationToken);

            if (!updateResult.Ok)
            {
                return updateResult.Error switch
                {
                    TaskUpdateError.NotFound => NotFound(new { message = updateResult.Message }),
                    TaskUpdateError.NotEditable => StatusCode(403, new { message = updateResult.Message }),
                    TaskUpdateError.TechnicianNotFound => UnprocessableEntity(new { message = updateResult.Message }),
                    TaskUpdateError.TravelInfeasible => UnprocessableEntity(new { message = updateResult.Message }),
                    _ => UnprocessableEntity(new { message = updateResult.Message })
                };
            }

            var updated = updateResult.Task!;
            var version = await _planService.GetTaskVersionAsync(id, cancellationToken);

            var response = new TaskCreatedOrUpdatedResponseDto
            {
                Id = updated.Id,
                Version = version,
                Load = await _planService.GetWorkloadSnapshotAsync(updated.TechnicianId, updated.StartAt.Date, cancellationToken)
            };

            _logger.LogInformation("api.task.update id={Id} planId={PlanId} version={Version}", id, updated.PlanId, version);
            return Ok(response);
        }

        [HttpPatch("{id:long}/move")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> MoveTask(long id, [FromBody] MoveTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            _logger.LogInformation(
                "api.task.move.request id={Id} tz={Tz} targetDate={TargetDate}",
                id, dto.TimezoneOffsetMinutes, dto.TargetDate);

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.MoveTaskAsync(
                id,
                dto.TargetTechnicianId,
                targetDate,
                dto.TargetSlotStartMinutes,
                dto.Version,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                // Convert UTC times to local for response
                var timezoneOffset = dto.TimezoneOffsetMinutes;
                var localStartAt = _timeService.ConvertUtcToLocal(result.Task.StartAt, timezoneOffset);
                var localEndAt = _timeService.ConvertUtcToLocal(result.Task.EndAt, timezoneOffset);

                var response = new TaskMovedResponseDto
                {
                    Id = result.Task.Id,
                    Version = result.CurrentVersion ?? 0,
                    TechnicianId = result.Task.TechnicianId,
                    StartAt = localStartAt,
                    EndAt = localEndAt,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken)
                };

                _logger.LogInformation("api.task.move id={Id} techId={TechId} version={Version}", id, dto.TargetTechnicianId, result.CurrentVersion);
                return Ok(response);
            }

            return result.Error switch
            {
                TaskMoveError.NotFound => NotFound(new { message = result.Message }),
                TaskMoveError.TechnicianNotFound => UnprocessableEntity(new { message = result.Message }),
                TaskMoveError.InvalidWindow => UnprocessableEntity(new { message = result.Message }),
                TaskMoveError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskMoveError.Overlap => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                TaskMoveError.TravelInfeasible => UnprocessableEntity(new { message = result.Message }),
                TaskMoveError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => StatusCode(500, new { message = result.Message ?? "Unknown error" })
            };
        }

        [HttpPatch("{id:long}/swap")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> SwapTask(long id, [FromBody] SwapTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            _logger.LogInformation(
                "swap.start sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} sourceVersion={SourceVersion} targetVersion={TargetVersion}",
                id,
                dto.TargetTaskId,
                dto.SourceVersion,
                dto.TargetVersion);

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.SwapTasksAsync(
                id,
                dto.TargetTaskId,
                dto.SourceVersion,
                dto.TargetVersion,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.SourceTask != null && result.TargetTask != null)
            {
                var sourceLocalStart = _timeService.ConvertUtcToLocal(result.SourceTask.StartAt, dto.TimezoneOffsetMinutes);
                var sourceLocalEnd = _timeService.ConvertUtcToLocal(result.SourceTask.EndAt, dto.TimezoneOffsetMinutes);
                var targetLocalStart = _timeService.ConvertUtcToLocal(result.TargetTask.StartAt, dto.TimezoneOffsetMinutes);
                var targetLocalEnd = _timeService.ConvertUtcToLocal(result.TargetTask.EndAt, dto.TimezoneOffsetMinutes);

                return Ok(new TaskSwappedResponseDto
                {
                    SourceTaskId = result.SourceTask.Id,
                    SourceVersion = result.CurrentSourceVersion ?? 0,
                    SourceStartAt = sourceLocalStart,
                    SourceEndAt = sourceLocalEnd,
                    TargetTaskId = result.TargetTask.Id,
                    TargetVersion = result.CurrentTargetVersion ?? 0,
                    TargetStartAt = targetLocalStart,
                    TargetEndAt = targetLocalEnd,
                    TechnicianId = result.SourceTask.TechnicianId,
                    Load = await _planService.GetWorkloadSnapshotAsync(
                        result.SourceTask.TechnicianId,
                        result.SourceTask.StartAt.Date,
                        cancellationToken)
                });
            }

            _logger.LogWarning(
                "swap.fail sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} reason={Reason} message={Message}",
                id,
                dto.TargetTaskId,
                result.Error,
                result.Message ?? string.Empty);

            return result.Error switch
            {
                TaskSwapError.NotFound => NotFound(new { message = result.Message }),
                TaskSwapError.TargetNotFound => NotFound(new { message = result.Message }),
                TaskSwapError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskSwapError.Stale => Conflict(new
                {
                    message = result.Message,
                    sourceVersion = result.CurrentSourceVersion,
                    targetVersion = result.CurrentTargetVersion
                }),
                TaskSwapError.InvalidSwap => UnprocessableEntity(new { message = result.Message }),
                TaskSwapError.Overlap => Conflict(new { message = result.Message }),
                TaskSwapError.TravelInfeasible => UnprocessableEntity(new { message = result.Message }),
                _ => StatusCode(500, new { message = result.Message ?? "Unknown error" })
            };
        }

        [HttpPatch("{id:long}/swap-smart")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> SwapTaskSmart(long id, [FromBody] SwapTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            _logger.LogInformation(
                "swap-smart.start sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} sourceVersion={SourceVersion} targetVersion={TargetVersion}",
                id,
                dto.TargetTaskId,
                dto.SourceVersion,
                dto.TargetVersion);

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.SwapTasksSmartAsync(
                id,
                dto.TargetTaskId,
                dto.SourceVersion,
                dto.TargetVersion,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.SourceTask != null && result.TargetTask != null)
            {
                var sourceLocalStart = _timeService.ConvertUtcToLocal(result.SourceTask.StartAt, dto.TimezoneOffsetMinutes);
                var sourceLocalEnd = _timeService.ConvertUtcToLocal(result.SourceTask.EndAt, dto.TimezoneOffsetMinutes);
                var targetLocalStart = _timeService.ConvertUtcToLocal(result.TargetTask.StartAt, dto.TimezoneOffsetMinutes);
                var targetLocalEnd = _timeService.ConvertUtcToLocal(result.TargetTask.EndAt, dto.TimezoneOffsetMinutes);

                return Ok(new TaskSwappedResponseDto
                {
                    SourceTaskId = result.SourceTask.Id,
                    SourceVersion = result.CurrentSourceVersion ?? 0,
                    SourceStartAt = sourceLocalStart,
                    SourceEndAt = sourceLocalEnd,
                    TargetTaskId = result.TargetTask.Id,
                    TargetVersion = result.CurrentTargetVersion ?? 0,
                    TargetStartAt = targetLocalStart,
                    TargetEndAt = targetLocalEnd,
                    TechnicianId = result.SourceTask.TechnicianId,
                    Load = await _planService.GetWorkloadSnapshotAsync(
                        result.SourceTask.TechnicianId,
                        result.SourceTask.StartAt.Date,
                        cancellationToken)
                });
            }

            _logger.LogWarning(
                "swap-smart.fail sourceTaskId={SourceTaskId} targetTaskId={TargetTaskId} reason={Reason} message={Message}",
                id,
                dto.TargetTaskId,
                result.Error,
                result.Message ?? string.Empty);

            return result.Error switch
            {
                TaskSwapError.NotFound => NotFound(new { message = result.Message }),
                TaskSwapError.TargetNotFound => NotFound(new { message = result.Message }),
                TaskSwapError.NotEditable => StatusCode(403, new { message = result.Message }),
                TaskSwapError.Stale => Conflict(new
                {
                    message = result.Message,
                    sourceVersion = result.CurrentSourceVersion,
                    targetVersion = result.CurrentTargetVersion
                }),
                TaskSwapError.InvalidSwap => UnprocessableEntity(new { message = result.Message }),
                TaskSwapError.Overlap => Conflict(new { message = result.Message }),
                TaskSwapError.TravelInfeasible => UnprocessableEntity(new { message = result.Message }),
                _ => StatusCode(500, new { message = result.Message ?? "Unknown error" })
            };
        }

        [HttpPost("{id:long}/swap-smart/preview")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> PreviewSwapSmart(long id, [FromBody] SwapSmartPreviewRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            var timezoneOffset = dto.TimezoneOffsetMinutes != 0
                ? dto.TimezoneOffsetMinutes
                : _timeService.GetBrowserTimezoneOffset(Request);

            var result = await _planService.PreviewSwapSmartAsync(
                id, dto.TargetTaskId, timezoneOffset, cancellationToken);

            return Ok(new
            {
                feasible = result.Feasible,
                reason = result.Reason.ToString(),
                requiresSmartAdjustment = result.RequiresSmartAdjustment,
                message = result.Message
            });
        }

        [HttpPatch("{id:long}/technician-update")]
        [Authorize] // Allow any authenticated user (not just managers)
        public async Task<IActionResult> TechnicianUpdateTask(long id, [FromBody] TechnicianUpdateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var user = _userContextResolver.GetCurrentUser();
            if (user.IsManager)
            {
                return StatusCode(403, new { message = "Managers must use the main edit endpoint." });
            }

            var result = await _planService.TechnicianUpdateTaskAsync(
                id,
                dto.Status,
                dto.Comment,
                dto.Version,
                user,
                dto.TimezoneOffsetMinutes,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                _logger.LogInformation(
                    "api.task.technician-update.success userId={UserId} taskId={TaskId} status={Status}",
                    user.UserId, id, dto.Status);

                return Ok(new TaskCreatedOrUpdatedResponseDto
                {
                    Id = id,
                    Version = result.CurrentVersion ?? 0
                });
            }

            _logger.LogWarning(
                "api.task.technician-update.rejected userId={UserId} taskId={TaskId} reason={Reason}",
                user.UserId, id, result.Error);

            return result.Error switch
            {
                TechnicianUpdateError.TaskNotFound => NotFound(new { message = result.Message }),
                TechnicianUpdateError.NotOwner => StatusCode(403, new { message = result.Message }),
                TechnicianUpdateError.NotEditable => StatusCode(403, new { message = result.Message }),
                TechnicianUpdateError.InvalidStatus => UnprocessableEntity(new { message = result.Message }),
                TechnicianUpdateError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => StatusCode(500, new { message = "Nepavyko atnaujinti uzduoties." })
            };
        }

        private static bool TryParseDate(string value, out DateTime parsed)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                parsed = dt.Date;
                return true;
            }

            parsed = default;
            return false;
        }

    }
}

