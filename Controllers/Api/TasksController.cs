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

namespace ServiceTaskPlanningSystem.Controllers.Api
{
    [ApiController]
    [Route("api/tasks")]
    public class TasksController : ControllerBase
    {
        private readonly IPlanService _planService;
        private readonly ILogger<TasksController> _logger;
        private readonly ITaskPermissionService _taskPermissionService;
        private readonly IScheduleTimeService _timeService;
        private readonly IUserContextResolver _userContextResolver;

        public TasksController(IPlanService planService, ITaskPermissionService taskPermissionService, IScheduleTimeService timeService, IUserContextResolver userContextResolver, ILogger<TasksController> logger)
        {
            _planService = planService;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _userContextResolver = userContextResolver;
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
                return UnprocessableEntity(new { message = "Pabaigos laikas turi bÅ«ti vÄ—lesnis uÅ¾ pradÅ¾iÄ…." });
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
                return UnprocessableEntity(new { message = "Pabaigos laikas turi b?ti v?lesnis u? prad?i?." });
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
                TaskMoveError.Stale => Conflict(new { message = result.Message, version = result.CurrentVersion }),
                _ => StatusCode(500, new { message = result.Message ?? "Unknown error" })
            };
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

