using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authorization.Models;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.History;

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
        private readonly ITaskTravelFeasibilityService _travelFeasibilityService;

        public TasksController(
            IPlanService planService,
            ITaskPermissionService taskPermissionService,
            IScheduleTimeService timeService,
            IUserContextResolver userContextResolver,
            ITaskTravelFeasibilityService travelFeasibilityService,
            ILogger<TasksController> logger)
        {
            _planService = planService;
            _taskPermissionService = taskPermissionService;
            _timeService = timeService;
            _userContextResolver = userContextResolver;
            _travelFeasibilityService = travelFeasibilityService;
            _logger = logger;
        }

        [HttpPost]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequestDto dto, CancellationToken cancellationToken)
            => await CreateTaskInternalAsync(dto, useDraftPlan: false, cancellationToken);

        [HttpPost("draft")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> CreateDraftTask([FromBody] CreateTaskRequestDto dto, CancellationToken cancellationToken)
            => await CreateTaskInternalAsync(dto, useDraftPlan: true, cancellationToken);

        private async Task<IActionResult> CreateTaskInternalAsync(CreateTaskRequestDto dto, bool useDraftPlan, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            // System constraint: New tasks must always start with InProgress status
            if (dto.Status != ScheduleTaskStatus.InProgress)
            {
                return UnprocessableEntity(new { message = "Naujos uÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¾duotys turi prasidÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Âti su 'Vykdoma' statusu. Sistema nustato statusÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¾ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¦ automatiÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¦ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡kai." });
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
            var result = useDraftPlan
                ? await _planService.CreateDraftTaskAsync(
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
                    sourceBacklogItemId: dto.SourceBacklogItemId,
                    cancellationToken)
                : await _planService.CreateTaskAsync(
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
                sourceBacklogItemId: dto.SourceBacklogItemId,
                cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var (message, _) = TaskHistoryMessages.ForCreate(result, result.Task.Title);
                var response = new TaskCreatedOrUpdatedResponseDto
                {
                    Id = result.Task.Id,
                    Version = result.CurrentVersion ?? 0,
                    Message = message,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken),
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
                SourceTaskId = task.SourceTaskId,
                SourceBacklogItemId = task.SourceBacklogItemId,
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
            var result = await _planService.DeleteTaskAsync(id, dto.Version, dto.AutoCompact, user, dto.TimezoneOffsetMinutes, cancellationToken);

            if (result.Ok && result.Task != null)
            {
                var (message, _) = TaskHistoryMessages.ForDelete(result, result.Task.Title);
                var response = new TaskDeletedResponseDto
                {
                    DeletedTaskId = result.Task.Id,
                    Version = result.CurrentVersion ?? dto.Version,
                    Message = message,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken),
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
                var oldStart = result.OldStartAt;
                var oldEnd = result.OldEndAt;
                var newStart = result.Task.StartAt;
                var newEnd = result.Task.EndAt;
                
                var (message, _) = TaskHistoryMessages.ForReschedule(result, result.Task.Title, oldStart, oldEnd, newStart, newEnd, dto.TimezoneOffsetMinutes);
                var localStart = _timeService.ConvertUtcToLocal(result.Task.StartAt, dto.TimezoneOffsetMinutes);
                var localEnd = _timeService.ConvertUtcToLocal(result.Task.EndAt, dto.TimezoneOffsetMinutes);
                var response = new TaskMovedResponseDto
                {
                    Id = result.Task.Id,
                    Version = result.CurrentVersion ?? dto.Version,
                    TechnicianId = result.Task.TechnicianId,
                    StartAt = localStart,
                    EndAt = localEnd,
                    Message = message,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken),
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

            if (!TryParseDate(dto.TargetDate, out var targetDate))
            {
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });
            }

            if (dto.TargetSlotStartMinutes < 0 || dto.TargetSlotStartMinutes >= 24 * 60)
            {
                return UnprocessableEntity(new { message = "Netinkamas pradzios laikas." });
            }

            var timezoneOffset = dto.TimezoneOffsetMinutes;
            var user = _userContextResolver.GetCurrentUser();

            var result = await _planService.PreviewDragAsync(
                dto.TaskId,
                dto.TargetTechnicianId,
                targetDate,
                dto.TargetSlotStartMinutes,
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
                return ValidationProblem(ModelState);

            if (!TryParseDate(dto.TargetDate, out var targetDate))
                return UnprocessableEntity(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });

            if (dto.EndMinutes <= dto.StartMinutes)
                return UnprocessableEntity(new { message = "Pabaigos laikas turi buti veliau uz pradzios laika." });

            var result = await _travelFeasibilityService.BuildAssigneeTravelPreviewAsync(
                targetDate,
                dto.ObjectAddress,
                dto.StartMinutes,
                dto.EndMinutes,
                dto.TimezoneOffsetMinutes,
                dto.TaskId,
                cancellationToken);

            return Ok(result);
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
                return UnprocessableEntity(new { message = "Pabaigos laikas turi bÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â«ti vÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¾ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Âlenis uÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¾ pradÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¾ia." });
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
                    TaskUpdateError.Overlap => Conflict(new { message = updateResult.Message }),
                    TaskUpdateError.InvalidWindow => UnprocessableEntity(new { message = updateResult.Message }),
                    TaskUpdateError.TravelInfeasible => UnprocessableEntity(new { message = updateResult.Message }),
                    _ => UnprocessableEntity(new { message = updateResult.Message })
                };
            }

            var updated = updateResult.Task!;
            var version = await _planService.GetTaskVersionAsync(id, cancellationToken);

            var (message, _) = TaskHistoryMessages.ForUpdate(updateResult, updated.Title);
            var response = new TaskCreatedOrUpdatedResponseDto
            {
                Id = updated.Id,
                Version = version,
                Message = message,
                Load = await _planService.GetWorkloadSnapshotAsync(updated.TechnicianId, updated.StartAt.Date, cancellationToken),
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
                var oldStart = result.OldStartAt;
                var newStart = result.Task.StartAt;
                var techChanged = result.OldTechnicianId.HasValue && result.OldTechnicianId.Value != result.Task.TechnicianId;
                
                var (message, _) = TaskHistoryMessages.ForMove(result, result.Task.Title, oldStart, newStart, dto.TimezoneOffsetMinutes, techChanged, result.NewTechnicianName);
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
                    Message = message,
                    Load = await _planService.GetWorkloadSnapshotAsync(result.Task.TechnicianId, result.Task.StartAt.Date, cancellationToken),
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
                var (message, _) = TaskHistoryMessages.ForSwap(result, result.SourceTask.Title, result.TargetTask.Title);
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
                    Message = message,
                    Load = await _planService.GetWorkloadSnapshotAsync(
                        result.SourceTask.TechnicianId,
                        result.SourceTask.StartAt.Date,
                        cancellationToken),
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
                    targetVersion = result.CurrentTargetVersion,
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
                var (message, _) = TaskHistoryMessages.ForSwap(result, result.SourceTask.Title, result.TargetTask.Title);
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
                    Message = message,
                    Load = await _planService.GetWorkloadSnapshotAsync(
                        result.SourceTask.TechnicianId,
                        result.SourceTask.StartAt.Date,
                        cancellationToken),
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
                    targetVersion = result.CurrentTargetVersion,
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
                    Version = result.CurrentVersion ?? 0,
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

        [HttpPost("bulk/validate")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> BulkValidateTasks([FromBody] BulkCreateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (dto.Items == null || dto.Items.Count == 0)
                return UnprocessableEntity(new { message = "Nenurodyti elementai." });

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.BulkValidateTasksAsync(dto.Items, dto.PlanMode, user, cancellationToken);
            return Ok(result);
        }

        [HttpPost("bulk/preview-schedule")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> PreviewBulkSchedule([FromBody] BulkCreateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (dto.Items == null || dto.Items.Count == 0)
                return UnprocessableEntity(new { message = "Nenurodyti elementai." });

            var user = _userContextResolver.GetCurrentUser();
            var result = await _planService.PreviewBulkScheduleAsync(dto.Items, dto.PlanMode, user, cancellationToken);
            return Ok(result);
        }

        [HttpPost("bulk")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> BulkCreateTasks([FromBody] BulkCreateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            _logger.LogInformation("api.task.bulk.create.start planMode={PlanMode} itemCount={ItemCount}", dto.PlanMode ?? "(null)", dto.Items?.Count ?? 0);

            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (dto.Items == null || dto.Items.Count == 0)
                return UnprocessableEntity(new { message = "Nenurodyti elementai." });

            if (dto.Items.Any(item => item.Status != ScheduleTaskStatus.InProgress))
                return UnprocessableEntity(new { message = "Visi elementai turi tureti statusa Vykdoma." });

            var user = _userContextResolver.GetCurrentUser();

            var result = await _planService.BulkCreateTasksAsync(dto.Items, dto.PlanMode, user, cancellationToken);

            if (result.AllSucceeded)
            {
                _logger.LogInformation("api.task.bulk.create count={Count}", result.TaskIds.Count);
                return Ok(result);
            }

            return UnprocessableEntity(result);
        }


        [HttpGet("suggest-start")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> SuggestNextSlot(
            [FromQuery] long technicianId,
            [FromQuery] string targetDate,
            [FromQuery] int? afterEndMinutes,
            [FromQuery] long? previousServiceObjectId,
            [FromQuery] long? newServiceObjectId,
            [FromQuery] bool appendAfterLastTask = false,
            CancellationToken cancellationToken = default)
        {
            if (technicianId <= 0)
                return BadRequest(new { message = "Nenurodytas darbuotojas." });

            if (!TryParseDate(targetDate, out var parsedDate))
                return BadRequest(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });

            var tz = _timeService.GetBrowserTimezoneOffset(Request);
            var (effectiveDate, startMinutes, endMinutes) = await _planService.GetSuggestedNextSlotAsync(
                technicianId, parsedDate, tz,
                ScheduleGrid.SlotSizeMinutes, ScheduleGrid.DayStartMinutes,
                afterEndMinutes, previousServiceObjectId, newServiceObjectId, appendAfterLastTask, null, cancellationToken);

            var pad = (int n) => n.ToString().PadLeft(2, '0');
            var startTime = $"{pad(startMinutes / 60)}:{pad(startMinutes % 60)}";
            var endTime   = $"{pad(endMinutes   / 60)}:{pad(endMinutes   % 60)}";
            var dateStr   = effectiveDate.ToString("yyyy-MM-dd");

            return Ok(new { targetDate = dateStr, startAt = $"{dateStr}T{startTime}", endAt = $"{dateStr}T{endTime}" });
        }

        [HttpPost("suggest-start")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> SuggestNextSlot([FromBody] SuggestNextSlotRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (!TryParseDate(dto.TargetDate, out var parsedDate))
                return BadRequest(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });

            var (effectiveDate, startMinutes, endMinutes) = await _planService.GetSuggestedNextSlotAsync(
                dto.TechnicianId,
                parsedDate,
                dto.TimezoneOffsetMinutes,
                ScheduleGrid.SlotSizeMinutes,
                ScheduleGrid.DayStartMinutes,
                dto.AfterEndMinutes,
                dto.PreviousServiceObjectId,
                dto.NewServiceObjectId,
                dto.AppendAfterLastTask,
                dto.SimulatedAssignments,
                cancellationToken);

            var pad = (int n) => n.ToString().PadLeft(2, '0');
            var startTime = $"{pad(startMinutes / 60)}:{pad(startMinutes % 60)}";
            var endTime = $"{pad(endMinutes / 60)}:{pad(endMinutes % 60)}";
            var dateStr = effectiveDate.ToString("yyyy-MM-dd");

            return Ok(new { targetDate = dateStr, startAt = $"{dateStr}T{startTime}", endAt = $"{dateStr}T{endTime}" });
        }

        /// <summary>
        /// Suggests automatic technician and time assignment for all items in a bulk queue.
        /// Fired once in the background when the user clicks "Planuoti" so the "Automatinis"
        /// tab can display pre-computed results instantly without per-item API calls.
        /// Returns an index-aligned array of suggestions (null entries where no match was found).
        /// Zero writes.
        /// </summary>
        [HttpPost("suggest-auto-assignment/bulk")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> SuggestBulkAutoAssignment([FromBody] List<CreateTaskRequestDto> items, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (items == null || items.Count == 0)
                return BadRequest(new { message = "Nenurodyti elementai." });

            foreach (var item in items)
            {
                if (!TryParseDate(item.TargetDate, out _))
                    return BadRequest(new { message = $"Netinkamas datos formatas elementui '{item.Title}'. Naudokite yyyy-MM-dd." });
            }

            try
            {
                var suggestions = await _planService.SuggestBulkAutoAssignmentsAsync(items, cancellationToken);

                var pad = (int n) => n.ToString().PadLeft(2, '0');
                var result = suggestions.Select(s =>
                {
                    if (s == null) return (object?)null;
                    var dateStr = s.TargetDate.ToString("yyyy-MM-dd");
                    return new
                    {
                        technicianId = s.TechnicianId,
                        technicianName = s.TechnicianName,
                        targetDate = dateStr,
                        startAt = $"{dateStr}T{pad(s.StartMinutes / 60)}:{pad(s.StartMinutes % 60)}",
                        endAt = $"{dateStr}T{pad(s.EndMinutes / 60)}:{pad(s.EndMinutes % 60)}",
                        startMinutes = s.StartMinutes,
                        endMinutes = s.EndMinutes
                    };
                }).ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error suggesting bulk automatic assignments");
                return StatusCode(500, new { message = "Klaida sudarant automatinę užduočių priskyrimo sugestiją." });
            }
        }

        /// <summary>
        /// Suggests automatic technician and time assignment for a single task.
        /// Used by the frontend to preview automatic assignment before bulk submission.
        /// </summary>
        [HttpPost("suggest-auto-assignment")]
        [Authorize(Policy = "CanEditTask")]
        public async Task<IActionResult> SuggestAutoAssignment([FromBody] CreateTaskRequestDto dto, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);

            if (string.IsNullOrWhiteSpace(dto.ObjectName))
                return BadRequest(new { message = "Objektas yra privalomas." });

            if (!TryParseDate(dto.TargetDate, out var targetDate))
                return BadRequest(new { message = "Netinkamas datos formatas. Naudokite yyyy-MM-dd." });

            try
            {
                var suggestion = await _planService.SuggestAutoAssignmentAsync(
                    targetDate,
                    dto.EndMinutes - dto.StartMinutes,
                    dto.ObjectId,
                    dto.ObjectName,
                    dto.Priority,
                    dto.TimezoneOffsetMinutes,
                    dto.SimulatedAssignments,
                    null,
                    cancellationToken);

                if (suggestion == null)
                    return UnprocessableEntity(new { message = "Nepavyko rasti tinkamo automatinio priskyrimo šiai užduočiai." });

                var pad = (int n) => n.ToString().PadLeft(2, '0');
                var startTime = $"{pad(suggestion.StartMinutes / 60)}:{pad(suggestion.StartMinutes % 60)}";
                var endTime = $"{pad(suggestion.EndMinutes / 60)}:{pad(suggestion.EndMinutes % 60)}";
                var dateStr = suggestion.TargetDate.ToString("yyyy-MM-dd");

                return Ok(new
                {
                    technicianId = suggestion.TechnicianId,
                    technicianName = suggestion.TechnicianName,
                    targetDate = dateStr,
                    startAt = $"{dateStr}T{startTime}",
                    endAt = $"{dateStr}T{endTime}",
                    startMinutes = suggestion.StartMinutes,
                    endMinutes = suggestion.EndMinutes
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error suggesting automatic assignment");
                return StatusCode(500, new { message = "Klaida sudarant automatinę užduoties priskyrimo sugestija." });
            }
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
